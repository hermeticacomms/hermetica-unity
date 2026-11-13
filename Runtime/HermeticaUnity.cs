#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Hermetica.Mobile;
using Newtonsoft.Json;
using UnityEngine;

namespace Hermetica.Unity
{
    /// <summary>
    /// A JSON file in Application.persistentDataPath. Not PlayerPrefs: the queue is saved from background threads, and PlayerPrefs
    /// may only be used on the main thread.
    /// </summary>
    public sealed class FileStore : IKeyValueStore
    {
        private readonly string _path;
        private readonly Dictionary<string, string> _d;

        public FileStore(string path)
        {
            _path = path;
            try { _d = File.Exists(path) ? JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path)) ?? new Dictionary<string, string>() : new Dictionary<string, string>(); }
            catch (Exception e) when (e is IOException || e is JsonException) { _d = new Dictionary<string, string>(); }
        }

        public string? Get(string key) { lock (_d) return _d.TryGetValue(key, out var v) ? v : null; }
        public void Set(string key, string value) { lock (_d) { _d[key] = value; Save(); } }
        public void Remove(string key) { lock (_d) { if (_d.Remove(key)) Save(); } }

        private void Save()
        {
            try
            {
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(_d));
                if (File.Exists(_path)) File.Delete(_path);
                File.Move(tmp, _path); // whole-file swap: a crash mid-write never leaves half a queue
            }
            catch (IOException e) { Debug.LogWarning("[hermetica] could not save state: " + e.Message); }
        }
    }

    /// <summary>
    /// Hermetica for Unity. Call <see cref="Initialize"/> once (for example from your first scene); then use <see cref="Client"/>.
    /// The queue is flushed when the app is paused or quits.
    /// </summary>
    public static class HermeticaSdk
    {
        private static HermeticaClient? _client;

        public static HermeticaClient Client => _client ?? throw new InvalidOperationException("Call HermeticaSdk.Initialize first");

        /// <param name="siteKey">The public site key (never a secret key).</param>
        /// <param name="baseUrl">Your Hermetica API URL.</param>
        public static HermeticaClient Initialize(string siteKey, string baseUrl, Action<HermeticaOptions>? configure = null)
        {
            if (_client != null) return _client;
            var ios = Application.platform == RuntimePlatform.IPhonePlayer;
            var o = new HermeticaOptions
            {
                Platform = ios ? "ios" : "android",
                SdkVersion = "unity-0.4.0",
                Device = new DeviceMetadata
                {
                    DeviceModel = SystemInfo.deviceModel, OsName = ios ? "ios" : Application.platform == RuntimePlatform.Android ? "android" : Application.platform.ToString(),
                    OsVersion = SystemInfo.operatingSystem, AppName = Application.productName, AppVersion = Application.version, AppId = Application.identifier,
                },
            };
            configure?.Invoke(o);
            _client = new HermeticaClient(siteKey, baseUrl, new FileStore(Path.Combine(Application.persistentDataPath, "hermetica.json")), o);
            var go = new GameObject("Hermetica") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<Lifecycle>();
            return _client;
        }

        /// <summary>Registers a token from Firebase Messaging (vendor fcm) or Mobile Notifications' DeviceToken on iOS (vendor apns).</summary>
        public static void SetPushToken(string token, string vendor, bool debugBuild = false) =>
            _ = Client.SetPushTokenAsync(new PushToken(token, vendor) { Environment = debugBuild ? "debug" : "release" });

        private sealed class Lifecycle : MonoBehaviour
        {
            private void OnApplicationPause(bool paused) { if (paused) _ = _client?.FlushAsync(); }
            private void OnApplicationQuit() => _ = _client?.FlushAsync();
            private void Start() => _ = ShowFor(InAppTrigger.AppOpen);

            private async System.Threading.Tasks.Task ShowFor(InAppTrigger t)
            {
                if (_client == null || InAppHandler == null) return;
                var items = await _client.FetchInAppMessagesAsync(t);
                if (items.Count > 0) InAppHandler(items[0]); // awaited from Start: resumes on the main thread
            }

            private void OnEnable() { if (_client != null) _client.InAppTriggered += OnTrigger; }
            private void OnDisable() { if (_client != null) _client.InAppTriggered -= OnTrigger; }
            private void OnTrigger(InAppTrigger t) => _ = ShowFor(t);
        }

        /// <summary>
        /// Shows in-app messages with your own UI: called on the main thread with the best message for app open, every Screen and
        /// every Track. Report what happened with <c>Client.ReportInAppEventAsync(message, "impression" | "click" | "dismiss")</c>.
        /// </summary>
        public static Action<InAppMessage>? InAppHandler { get; set; }
    }
}
