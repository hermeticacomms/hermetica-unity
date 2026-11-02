#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Hermetica.Mobile
{
    /// <summary>
    /// The Hermetica app client shared by the .NET MAUI and Unity SDKs: identity, events, consent, push tokens and in-app
    /// messages. Every call returns at once and is safe from any thread; network work runs in the background. Events are queued
    /// on the device, sent in batches, retried with backoff (one Idempotency-Key per batch), and kept while offline.
    /// </summary>
    public sealed class HermeticaClient : IDisposable
    {
        private const string KAid = "hm_aid", KExt = "hm_external_id", KEmail = "hm_email", KPhone = "hm_phone", KTraits = "hm_pending_traits";
        private const string KOptOut = "hm_opt_out", KConsent = "hm_consent", KToken = "hm_push_token";
        private readonly IKeyValueStore _store;
        private readonly HermeticaOptions _o;
        private readonly Transport _http;
        private readonly Outbox _outbox;
        private readonly SemaphoreSlim _flushing = new SemaphoreSlim(1, 1);
        private readonly object _lock = new object();
        private Timer? _timer;
        private volatile bool _optedOut;

        public HermeticaClient(string siteKey, string baseUrl, IKeyValueStore store, HermeticaOptions? options = null, HttpClient? http = null)
        {
            if (string.IsNullOrWhiteSpace(siteKey)) throw new ArgumentException("siteKey is required", nameof(siteKey));
            if (siteKey.StartsWith("hm_sk_", StringComparison.Ordinal)) throw new ArgumentException("use the public site key, never a secret hm_sk_ key, in an app", nameof(siteKey));
            if (!(baseUrl.StartsWith("https://", StringComparison.Ordinal) || baseUrl.StartsWith("http://", StringComparison.Ordinal))) throw new ArgumentException("baseUrl must be an http(s) URL", nameof(baseUrl));
            _store = store;
            _o = options ?? new HermeticaOptions();
            _optedOut = store.Get(KOptOut) == "1";
            _http = new Transport(baseUrl, siteKey, http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) }, _o, () => _optedOut);
            _outbox = new Outbox(store, _http, _o);
            if (_outbox.Count > 0) _ = FlushAsync(); // whatever was left from the last run
        }

        /// <summary>Sent with in-app requests so per-session frequency caps hold.</summary>
        public string SessionId { get; } = "s" + Guid.NewGuid().ToString("N").Substring(0, 16);

        /// <summary>Raised on <see cref="Screen"/> and <see cref="Track"/>: moments that can show an in-app message. The platform layer fetches and shows.</summary>
        public event Action<InAppTrigger>? InAppTriggered;

        public string AnonymousId
        {
            get
            {
                lock (_lock)
                {
                    var id = _store.Get(KAid);
                    if (id != null) return id;
                    id = Guid.NewGuid().ToString();
                    _store.Set(KAid, id);
                    return id;
                }
            }
        }

        public bool IsTrackingOptedOut => _optedOut;

        // ---- identity and events ----

        /// <summary>Names the profile (any of external id, email, phone) and links this device's anonymous history to it.</summary>
        public void Identify(string? externalId, string? email = null, string? phone = null, IDictionary<string, object?>? traits = null)
        {
            if (!string.IsNullOrEmpty(externalId)) _store.Set(KExt, externalId!);
            if (!string.IsNullOrEmpty(email)) _store.Set(KEmail, email!);
            if (!string.IsNullOrEmpty(phone)) _store.Set(KPhone, phone!);
            SetProfileAttributes(traits ?? new Dictionary<string, object?>());
            ReRegisterPush();
        }

        /// <summary>Profile properties; held until the first <see cref="Identify"/> when no identifier is known yet.</summary>
        public void SetProfileAttributes(IDictionary<string, object?> traits)
        {
            if (_optedOut) return;
            var all = Parse(_store.Get(KTraits)) ?? new JObject();
            foreach (var kv in traits) all[kv.Key] = kv.Value == null ? JValue.CreateNull() : JToken.FromObject(kv.Value);
            var ids = new JObject();
            if (_store.Get(KExt) is string ext) ids["externalId"] = ext;
            if (_store.Get(KEmail) is string mail) ids["email"] = mail;
            if (_store.Get(KPhone) is string ph) ids["phone"] = ph;
            if (!ids.HasValues) { _store.Set(KTraits, all.ToString(Formatting.None)); return; }
            _store.Remove(KTraits);
            _outbox.Add(Outbox.IdentifyKind, new JObject { ["anonymousId"] = AnonymousId, ["identifiers"] = ids, ["traits"] = all });
            _ = FlushAsync();
        }

        /// <summary><paramref name="value"/> is in minor currency units (cents) with an ISO 4217 <paramref name="currency"/>.</summary>
        public void Track(string name, IDictionary<string, object?>? properties = null, long? value = null, string? currency = null)
        {
            Enqueue(name, properties, value, currency);
            InAppTriggered?.Invoke(InAppTrigger.Event(name, properties));
        }

        /// <summary>A screen view: a page_view with the screen name, and a screen trigger for in-app messages.</summary>
        public void Screen(string name, IDictionary<string, object?>? properties = null)
        {
            var p = new Dictionary<string, object?>(properties ?? new Dictionary<string, object?>()) { ["screen"] = name };
            Enqueue("page_view", p, null, null);
            InAppTriggered?.Invoke(InAppTrigger.Screen(name));
        }

        private void Enqueue(string name, IDictionary<string, object?>? properties, long? value, string? currency)
        {
            if (_optedOut) return;
            var props = properties == null ? new JObject() : JObject.FromObject(properties);
            props["hm_sdk"] = _o.SdkVersion;
            var ev = new JObject
            {
                ["name"] = name, ["properties"] = props, ["dedupeKey"] = Guid.NewGuid().ToString(),
                ["occurredAt"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture), ["anonymousId"] = AnonymousId,
            };
            if (value != null) ev["value"] = value;
            if (currency != null) ev["currency"] = currency;
            if (_outbox.Add(Outbox.EventKind, ev) >= _o.BatchSize) _ = FlushAsync();
            else ScheduleFlush();
        }

        /// <summary>Logout: forget the profile and start a new anonymous id. The push token moves to the new anonymous visitor.</summary>
        public void Reset()
        {
            foreach (var k in new[] { KExt, KEmail, KPhone, KAid, KTraits }) _store.Remove(k);
            ReRegisterPush();
        }

        /// <summary>Opted out: nothing is tracked or sent and queued events are dropped. Push tokens still register.</summary>
        public void SetTrackingOptOut(bool optOut)
        {
            _optedOut = optOut;
            _store.Set(KOptOut, optOut ? "1" : "0");
            if (optOut) _outbox.Clear();
        }

        /// <summary>For workspaces that require consent. analytics = false is the same as opting out.</summary>
        public void SetConsent(bool analytics, bool marketing)
        {
            _store.Set(KConsent, new JObject { ["analytics"] = analytics, ["marketing"] = marketing }.ToString(Formatting.None));
            SetTrackingOptOut(!analytics);
            _ = FlushAsync();
        }

        /// <summary>Sends the queue now (also sent every few seconds, at the batch size, and when the app goes to the background).</summary>
        public async Task<bool> FlushAsync()
        {
            if (_optedOut) return true;
            await _flushing.WaitAsync().ConfigureAwait(false);
            try { return await _outbox.FlushAsync(Parse(_store.Get(KConsent))).ConfigureAwait(false); }
            finally { _flushing.Release(); }
        }

        private void ScheduleFlush()
        {
            lock (_lock)
            {
                if (_timer != null) return;
                _timer = new Timer(_ => { lock (_lock) { _timer?.Dispose(); _timer = null; } _ = FlushAsync(); }, null, _o.FlushInterval, Timeout.InfiniteTimeSpan);
            }
        }

        // ---- push ----

        private JObject Who(bool camel)
        {
            var who = new JObject();
            void Put(string snake, string camelName, string? v) { if (v != null) who[camel ? camelName : snake] = v; }
            Put("external_id", "externalId", _store.Get(KExt));
            Put("email", "email", _store.Get(KEmail));
            Put("phone_number", "phoneNumber", _store.Get(KPhone));
            Put("anonymous_id", "anonymousId", AnonymousId);
            return who;
        }

        internal JObject PushBody(PushToken t, string type)
        {
            var d = _o.Device;
            return new JObject
            {
                ["data"] = new JObject
                {
                    ["type"] = type,
                    ["attributes"] = new JObject
                    {
                        ["token"] = t.Token, ["platform"] = _o.Platform, ["vendor"] = t.Vendor, ["enablement_status"] = t.EnablementStatus, ["background"] = t.Background,
                        ["device_metadata"] = new JObject
                        {
                            ["device_id"] = AnonymousId, ["sdk_version"] = _o.SdkVersion, ["device_model"] = d.DeviceModel ?? "", ["os_name"] = d.OsName ?? _o.Platform,
                            ["os_version"] = d.OsVersion ?? "", ["app_name"] = d.AppName ?? "", ["app_version"] = d.AppVersion ?? "", ["app_build"] = d.AppBuild ?? "",
                            ["app_id"] = d.AppId ?? "", ["environment"] = t.Environment,
                        },
                        ["profile"] = new JObject { ["data"] = new JObject { ["type"] = "profile", ["attributes"] = Who(false) } },
                    },
                },
            };
        }

        /// <summary>Registers the device token with the profile; true when the server took it. Re-sent on identify and reset.</summary>
        public async Task<bool> SetPushTokenAsync(PushToken token)
        {
            var code = await _http.PostAsync("/client/push-tokens", PushBody(token, "push-token"), Guid.NewGuid().ToString()).ConfigureAwait(false);
            var ok = code >= 200 && code < 300;
            if (ok) _store.Set(KToken, JsonConvert.SerializeObject(token));
            return ok;
        }

        /// <summary>Stops pushes to this device (call on logout before <see cref="Reset"/>).</summary>
        public async Task<bool> UnregisterPushTokenAsync()
        {
            var t = StoredToken();
            if (t == null) return true;
            var code = await _http.PostAsync("/client/push-token-unregister", PushBody(t, "push-token-unregister"), Guid.NewGuid().ToString()).ConfigureAwait(false);
            var ok = code >= 200 && code < 300;
            if (ok) _store.Remove(KToken);
            return ok;
        }

        private PushToken? StoredToken()
        {
            var o = Parse(_store.Get(KToken));
            var token = (string?)o?["Token"];
            var vendor = (string?)o?["Vendor"];
            if (o == null || token == null || vendor == null) return null;
            return new PushToken(token, vendor)
            {
                Environment = (string?)o["Environment"] ?? "release", EnablementStatus = (string?)o["EnablementStatus"] ?? "AUTHORIZED", Background = (string?)o["Background"] ?? "AVAILABLE",
            };
        }

        private void ReRegisterPush()
        {
            var t = StoredToken();
            if (t != null) _ = SetPushTokenAsync(t);
        }

        private static string? Str(IDictionary<string, object?> d, string k) => d.TryGetValue(k, out var v) && v is string s && s.Length > 0 ? s : null;

        /// <summary>A notification was tapped, with its data: tracks $opened_push for Hermetica messages and returns the deep link, or null.</summary>
        public string? HandlePushOpened(IDictionary<string, object?> data)
        {
            var id = Str(data, "hermetica_message_id");
            if (id != null) Enqueue("$opened_push", new Dictionary<string, object?> { ["hermetica_message_id"] = id }, null, null);
            return Str(data, "deep_link") ?? Str(data, "url") ?? Str(data, "hermetica_url");
        }

        /// <summary>A notification arrived: tracks $received_push (delivered). True when it was a Hermetica message.</summary>
        public bool HandlePushReceived(IDictionary<string, object?> data)
        {
            var id = Str(data, "hermetica_message_id");
            if (id != null) Enqueue("$received_push", new Dictionary<string, object?> { ["hermetica_message_id"] = id }, null, null);
            return id != null;
        }

        // ---- in-app ----

        /// <summary>The messages that fit this moment, best first. Empty on any failure: a missing message must never break the app.</summary>
        public async Task<IReadOnlyList<InAppMessage>> FetchInAppMessagesAsync(InAppTrigger trigger)
        {
            if (_optedOut) return Array.Empty<InAppMessage>();
            var who = Who(false);
            var q = new Dictionary<string, string?>
            {
                ["platform"] = _o.Platform, ["trigger"] = trigger.Type, ["session_id"] = SessionId, ["name"] = trigger.Name,
                ["properties"] = trigger.Properties != null && trigger.Properties.Count > 0 ? JsonConvert.SerializeObject(trigger.Properties) : null,
            };
            foreach (var p in who.Properties()) q[p.Name] = (string?)p.Value;
            var body = await _http.GetAsync("/client/in-app-messages", q).ConfigureAwait(false);
            return (body?["items"] as JArray)?.OfType<JObject>().Select(o => new InAppMessage(o)).ToList() ?? (IReadOnlyList<InAppMessage>)Array.Empty<InAppMessage>();
        }

        /// <summary><paramref name="type"/> is impression, click or dismiss; <paramref name="button"/> the tapped button's label.</summary>
        public async Task<bool> ReportInAppEventAsync(InAppMessage message, string type, string? button = null)
        {
            if (_optedOut) return false;
            var ev = new JObject { ["messageId"] = message.Id, ["variant"] = message.Variant, ["type"] = type, ["platform"] = _o.Platform, ["sessionId"] = SessionId };
            if (button != null) ev["button"] = button;
            var code = await _http.PostAsync("/client/in-app-events", new JObject { ["events"] = new JArray(ev), ["profile"] = Who(true) }, Guid.NewGuid().ToString()).ConfigureAwait(false);
            return code >= 200 && code < 300;
        }

        private static JObject? Parse(string? s)
        {
            if (s == null) return null;
            try { return JObject.Parse(s); } catch (JsonException) { return null; }
        }

        public void Dispose()
        {
            lock (_lock) { _timer?.Dispose(); _timer = null; }
        }
    }
}
