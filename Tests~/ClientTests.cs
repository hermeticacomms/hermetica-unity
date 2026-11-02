#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Hermetica.Mobile;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hermetica.Mobile.Tests
{
    /// <summary>Records each request and answers with the next queued status (200 once the queue is empty).</summary>
    internal sealed class FakeServer : HttpMessageHandler
    {
        public readonly Queue<int> Statuses = new Queue<int>();
        public readonly List<(string Path, string Query, string? Key, JObject? Body)> Seen = new List<(string, string, string?, JObject?)>();
        public string Reply = "{}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = r.Content == null ? null : JObject.Parse(await r.Content.ReadAsStringAsync());
            lock (Seen) Seen.Add((r.RequestUri!.AbsolutePath, r.RequestUri.Query, r.Headers.TryGetValues("Idempotency-Key", out var k) ? k.First() : null, body));
            var status = Statuses.Count > 0 ? Statuses.Dequeue() : 200;
            if (status == 0) throw new HttpRequestException("dropped");
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(Reply) };
        }

        public List<(string Path, string Query, string? Key, JObject? Body)> To(string path) { lock (Seen) return Seen.Where(s => s.Path == path).ToList(); }
    }

    public class ClientTests
    {
        private static (HermeticaClient, FakeServer, MemoryStore) Start(MemoryStore? store = null)
        {
            var server = new FakeServer();
            var s = store ?? new MemoryStore();
            var o = new HermeticaOptions { Platform = "ios", RetryBase = TimeSpan.Zero, FlushInterval = TimeSpan.FromHours(1), Device = new DeviceMetadata { AppName = "shop" } };
            return (new HermeticaClient("pk_test", "https://api.test/", s, o, new HttpClient(server)), server, s);
        }

        [Fact]
        public async Task BatchesEventsAndRetriesWithTheSameIdempotencyKey()
        {
            var (hm, server, _) = Start();
            hm.Track("added_to_cart", new Dictionary<string, object?> { ["sku"] = "A1" }, 1999, "USD");
            hm.Screen("cart");
            server.Statuses.Enqueue(503);
            server.Statuses.Enqueue(0);
            Assert.True(await hm.FlushAsync());
            var sent = server.To("/ingest/v1/batch");
            Assert.Equal(3, sent.Count);
            Assert.All(sent, s => Assert.Equal(sent[0].Key, s.Key));
            var events = (JArray)sent[2].Body!["events"]!;
            Assert.Equal(new[] { "added_to_cart", "page_view" }, events.Select(e => (string)e["name"]!));
            Assert.Equal(1999, (long)events[0]["value"]!);
            Assert.Equal("cart", (string)events[1]["properties"]!["screen"]!);
            Assert.Equal(hm.AnonymousId, (string)events[0]["anonymousId"]!);
        }

        [Fact]
        public async Task KeepsTheQueueOfflineAndSendsItOnTheNextStart()
        {
            var store = new MemoryStore();
            var (hm, server, _) = Start(store);
            for (var i = 0; i < 5; i++) server.Statuses.Enqueue(503);
            hm.Track("a");
            Assert.False(await hm.FlushAsync());
            var (hm2, server2, _) = Start(store);
            Assert.True(await hm2.FlushAsync());
            Assert.Equal("a", (string)((JArray)server2.To("/ingest/v1/batch").Last().Body!["events"]!)[0]["name"]!);
            Assert.Equal(server.To("/ingest/v1/batch")[0].Key, server2.To("/ingest/v1/batch").Last().Key); // same batch, same key
        }

        [Fact]
        public async Task OptOutDropsTheQueueAndConsentTravelsWithBatches()
        {
            var (hm, server, _) = Start();
            hm.Track("a");
            hm.SetTrackingOptOut(true);
            hm.Track("b");
            Assert.True(await hm.FlushAsync());
            Assert.Empty(server.To("/ingest/v1/batch"));
            hm.SetConsent(analytics: true, marketing: false);
            hm.Track("c");
            await hm.FlushAsync();
            var body = server.To("/ingest/v1/batch").Last().Body!;
            Assert.Equal("c", (string)body["events"]![0]!["name"]!);
            Assert.False((bool)body["consent"]!["marketing"]!);
        }

        [Fact]
        public async Task IdentifyWaitsForAnIdentifierAndThePushTokenFollowsTheProfile()
        {
            var (hm, server, _) = Start();
            hm.SetProfileAttributes(new Dictionary<string, object?> { ["plan"] = "pro" });
            Assert.True(await hm.SetPushTokenAsync(new PushToken("apns-1", "apns") { Environment = "debug" }));
            hm.Identify("user-1", email: "a@b.co");
            await hm.FlushAsync();
            var id = server.To("/ingest/v1/identify").Single().Body!;
            Assert.Equal("user-1", (string)id["identifiers"]!["externalId"]!);
            Assert.Equal("pro", (string)id["traits"]!["plan"]!);

            // the re-registration runs in the background
            for (var i = 0; i < 200 && server.To("/client/push-tokens").Count < 2; i++) await Task.Delay(10);
            var push = server.To("/client/push-tokens");
            Assert.Equal(2, push.Count);
            var a = push[1].Body!["data"]!["attributes"]!;
            Assert.Equal("apns", (string)a["vendor"]!);
            Assert.Equal("debug", (string)a["device_metadata"]!["environment"]!);
            Assert.Equal("user-1", (string)a["profile"]!["data"]!["attributes"]!["external_id"]!);
            Assert.Contains("company_id=pk_test", push[0].Query);

            Assert.True(await hm.UnregisterPushTokenAsync());
            Assert.Single(server.To("/client/push-token-unregister"));
        }

        [Fact]
        public async Task InAppMessagesAndPushOpens()
        {
            var (hm, server, _) = Start();
            server.Reply = "{\"items\":[{\"id\":\"m1\",\"variant\":\"b\",\"layout\":\"banner_top\",\"content\":{\"blocks\":[]},\"style\":{}}]}";
            var items = await hm.FetchInAppMessagesAsync(InAppTrigger.Screen("cart"));
            Assert.Equal("m1", items.Single().Id);
            Assert.Contains("trigger=screen_view", server.To("/client/in-app-messages").Single().Query);
            Assert.Contains("platform=ios", server.To("/client/in-app-messages").Single().Query);
            server.Reply = "{}";
            Assert.True(await hm.ReportInAppEventAsync(items[0], "click", "Shop"));
            Assert.Equal("Shop", (string)server.To("/client/in-app-events").Single().Body!["events"]![0]!["button"]!);

            var link = hm.HandlePushOpened(new Dictionary<string, object?> { ["hermetica_message_id"] = "p1", ["deep_link"] = "shop://sale" });
            Assert.Equal("shop://sale", link);
            await hm.FlushAsync();
            Assert.Equal("$opened_push", (string)server.To("/ingest/v1/batch").Single().Body!["events"]![0]!["name"]!);
        }

        [Fact]
        public void RefusesASecretKey()
        {
            Assert.Throws<ArgumentException>(() => new HermeticaClient("hm_sk_live", "https://api.test", new MemoryStore()));
        }
    }
}
