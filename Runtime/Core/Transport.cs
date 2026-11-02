#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Hermetica.Mobile
{
    /// <summary>HTTP to the public client and ingest endpoints, with the site key as bearer.</summary>
    internal sealed class Transport
    {
        private static readonly Random Jitter = new Random();
        private readonly string _baseUrl;
        private readonly string _siteKey;
        private readonly HttpClient _http;
        private readonly HermeticaOptions _o;
        private readonly Func<bool> _optedOut;

        public Transport(string baseUrl, string siteKey, HttpClient http, HermeticaOptions o, Func<bool> optedOut)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _siteKey = siteKey;
            _http = http;
            _o = o;
            _optedOut = optedOut;
        }

        private string Url(string path, IDictionary<string, string?>? query)
        {
            // The /client endpoints take the public key as company_id too (Klaviyo-compatible).
            var q = new List<KeyValuePair<string, string?>>();
            if (path.StartsWith("/client/", StringComparison.Ordinal)) q.Add(new KeyValuePair<string, string?>("company_id", _siteKey));
            if (query != null) q.AddRange(query);
            var parts = q.Where(kv => !string.IsNullOrEmpty(kv.Value)).Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value!)).ToList();
            return _baseUrl + path + (parts.Count > 0 ? "?" + string.Join("&", parts) : "");
        }

        private HttpRequestMessage Request(HttpMethod method, string url)
        {
            var req = new HttpRequestMessage(method, url);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _siteKey);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            // The server drops everything from a visitor who sends Global Privacy Control.
            if (_optedOut()) req.Headers.TryAddWithoutValidation("Sec-GPC", "1");
            return req;
        }

        /// <summary>The parsed 2xx JSON object, or null on any failure.</summary>
        public async Task<JObject?> GetAsync(string path, IDictionary<string, string?>? query = null)
        {
            try
            {
                using var req = Request(HttpMethod.Get, Url(path, query));
                using var res = await _http.SendAsync(req).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode) return null;
                return JObject.Parse(await res.Content.ReadAsStringAsync().ConfigureAwait(false));
            }
            catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException || e is Newtonsoft.Json.JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Retries network errors, 429 and 5xx with exponential backoff, always with the same Idempotency-Key. Returns the last
        /// status code, or -1 when the network never answered.
        /// </summary>
        public async Task<int> PostAsync(string path, JObject body, string idempotencyKey)
        {
            var json = body.ToString(Newtonsoft.Json.Formatting.None);
            var code = -1;
            for (var attempt = 0; attempt < _o.MaxRetries; attempt++)
            {
                try
                {
                    using var req = Request(HttpMethod.Post, Url(path, null));
                    req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    req.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
                    using var res = await _http.SendAsync(req).ConfigureAwait(false);
                    code = (int)res.StatusCode;
                }
                catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException)
                {
                    code = -1;
                }
                if (code >= 200 && code < 500 && code != 429) return code;
                if (attempt < _o.MaxRetries - 1)
                {
                    int jitter;
                    lock (Jitter) jitter = Jitter.Next((int)_o.RetryBase.TotalMilliseconds + 1);
                    await Task.Delay(TimeSpan.FromMilliseconds(_o.RetryBase.TotalMilliseconds * (1 << attempt) + jitter)).ConfigureAwait(false);
                }
            }
            return code;
        }
    }
}
