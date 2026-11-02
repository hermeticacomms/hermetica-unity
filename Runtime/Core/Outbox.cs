#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Hermetica.Mobile
{
    /// <summary>
    /// The offline queue, persisted in the store so nothing is lost when the app is killed or offline. Items keep their order:
    /// identify calls go one by one, runs of events go in batches. Same design as the Android SDK's Outbox.
    /// </summary>
    internal sealed class Outbox
    {
        public const string Key = "hm_outbox";
        public const string EventKind = "event";
        public const string IdentifyKind = "identify";
        private const int ServerMaxBatch = 50;
        private readonly object _lock = new object();
        private readonly IKeyValueStore _store;
        private readonly Transport _http;
        private readonly HermeticaOptions _o;
        private readonly List<JObject> _items;

        public Outbox(IKeyValueStore store, Transport http, HermeticaOptions o)
        {
            _store = store;
            _http = http;
            _o = o;
            _items = Load();
        }

        private List<JObject> Load()
        {
            try { return (JArray.Parse(_store.Get(Key) ?? "[]")).OfType<JObject>().ToList(); }
            catch (JsonException) { return new List<JObject>(); }
        }

        private void Save() => _store.Set(Key, new JArray(_items).ToString(Formatting.None));

        /// <summary>Returns the queue length after adding; the oldest items go once the queue is full.</summary>
        public int Add(string kind, JObject body)
        {
            lock (_lock)
            {
                _items.Add(new JObject { ["id"] = Guid.NewGuid().ToString(), ["kind"] = kind, ["body"] = body });
                if (_items.Count > _o.MaxQueue) _items.RemoveRange(0, _items.Count - _o.MaxQueue);
                Save();
                return _items.Count;
            }
        }

        public int Count { get { lock (_lock) return _items.Count; } }

        public void Clear() { lock (_lock) { _items.Clear(); Save(); } }

        /// <summary>Sends everything queued; stops at the first failure and keeps the rest. True when the queue is empty.</summary>
        public async Task<bool> FlushAsync(JObject? consent)
        {
            while (true)
            {
                List<JObject> batch;
                lock (_lock) batch = Head();
                if (batch.Count == 0) return true;
                var first = batch[0];
                var firstId = (string)first["id"]!;
                int code;
                if ((string?)first["kind"] == IdentifyKind)
                {
                    code = await _http.PostAsync("/ingest/v1/identify", WithConsent((JObject)first["body"]!.DeepClone(), consent), firstId).ConfigureAwait(false);
                }
                else
                {
                    var body = new JObject { ["events"] = new JArray(batch.Select(i => i["body"]!.DeepClone())) };
                    // Same head and size means the same items, so a retry after a restart reuses the key.
                    code = await _http.PostAsync("/ingest/v1/batch", WithConsent(body, consent), "batch-" + firstId + "-" + batch.Count).ConfigureAwait(false);
                }
                // 4xx will not get better on retry: drop those items rather than block the queue forever.
                if (code < 200 || code >= 500 || code == 429) return false;
                var sent = new HashSet<string>(batch.Select(i => (string)i["id"]!));
                lock (_lock) { _items.RemoveAll(i => sent.Contains((string)i["id"]!)); Save(); }
            }
        }

        private List<JObject> Head()
        {
            if (_items.Count == 0) return new List<JObject>();
            if ((string?)_items[0]["kind"] == IdentifyKind) return new List<JObject> { _items[0] };
            return _items.TakeWhile(i => (string?)i["kind"] == EventKind).Take(Math.Min(_o.BatchSize, ServerMaxBatch)).ToList();
        }

        private static JObject WithConsent(JObject o, JObject? consent)
        {
            if (consent != null) o["consent"] = consent.DeepClone();
            return o;
        }
    }
}
