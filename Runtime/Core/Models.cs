#nullable enable
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

// Shared by the .NET MAUI and Unity SDKs (the Unity export copies this folder into Runtime/Core): keep it C# 9 and free of
// MAUI and UnityEngine types.
namespace Hermetica.Mobile
{
    /// <summary>Where the SDK keeps its state (anonymous id, profile, consent, the offline queue). MAUI: Preferences; Unity: PlayerPrefs.</summary>
    public interface IKeyValueStore
    {
        string? Get(string key);
        void Set(string key, string value);
        void Remove(string key);
    }

    /// <summary>A store that forgets everything when the process ends (tests, or when nothing may be written to disk).</summary>
    public sealed class MemoryStore : IKeyValueStore
    {
        private readonly Dictionary<string, string> _d = new Dictionary<string, string>();
        public string? Get(string key) { lock (_d) return _d.TryGetValue(key, out var v) ? v : null; }
        public void Set(string key, string value) { lock (_d) _d[key] = value; }
        public void Remove(string key) { lock (_d) _d.Remove(key); }
    }

    /// <summary>Optional description of the app and handset, sent with push tokens.</summary>
    public sealed class DeviceMetadata
    {
        public string? DeviceModel { get; set; }
        public string? OsName { get; set; }
        public string? OsVersion { get; set; }
        public string? AppName { get; set; }
        public string? AppVersion { get; set; }
        public string? AppBuild { get; set; }
        public string? AppId { get; set; }
    }

    public sealed class HermeticaOptions
    {
        /// <summary>ios or android: which in-app messages to fetch and the push token's platform.</summary>
        public string Platform { get; set; } = "android";
        public string SdkVersion { get; set; } = "dotnet-mobile-0.4.0";
        public DeviceMetadata Device { get; set; } = new DeviceMetadata();
        public int BatchSize { get; set; } = 20;
        public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(2);
        public int MaxQueue { get; set; } = 200;
        public int MaxRetries { get; set; } = 5;
        public TimeSpan RetryBase { get; set; } = TimeSpan.FromMilliseconds(500);
    }

    /// <summary>A device token and what the OS allows. Values are the server's wire values.</summary>
    public sealed class PushToken
    {
        public PushToken(string token, string vendor) { Token = token; Vendor = vendor; }
        public string Token { get; }
        /// <summary>apns or fcm.</summary>
        public string Vendor { get; }
        /// <summary>debug or release.</summary>
        public string Environment { get; set; } = "release";
        /// <summary>AUTHORIZED, DENIED, NOT_DETERMINED, PROVISIONAL or UNAUTHORIZED.</summary>
        public string EnablementStatus { get; set; } = "AUTHORIZED";
        /// <summary>AVAILABLE, DENIED or RESTRICTED.</summary>
        public string Background { get; set; } = "AVAILABLE";
    }

    /// <summary>A moment that can show an in-app message: app open, a screen, or a tracked event.</summary>
    public sealed class InAppTrigger
    {
        private InAppTrigger(string type, string? name, IDictionary<string, object?>? properties) { Type = type; Name = name; Properties = properties; }
        public static readonly InAppTrigger AppOpen = new InAppTrigger("app_open", null, null);
        public static InAppTrigger Screen(string name) => new InAppTrigger("screen_view", name, null);
        public static InAppTrigger Event(string name, IDictionary<string, object?>? properties = null) => new InAppTrigger("event", name, properties);
        public string Type { get; }
        public string? Name { get; }
        public IDictionary<string, object?>? Properties { get; }
    }

    /// <summary>An in-app message the server picked. <see cref="Content"/> holds blocks (heading, text, image, buttons) and <see cref="Style"/> the colours.</summary>
    public sealed class InAppMessage
    {
        public InAppMessage(JObject raw)
        {
            Raw = raw;
            Id = (string?)raw["id"] ?? "";
            Variant = (string?)raw["variant"] ?? "a";
            Layout = (string?)raw["layout"] ?? "modal";
            Content = raw["content"] as JObject ?? new JObject();
            Style = raw["style"] as JObject ?? new JObject();
        }
        public string Id { get; }
        public string Variant { get; }
        /// <summary>modal, banner_top, banner_bottom, fullscreen or bottom_sheet.</summary>
        public string Layout { get; }
        public JObject Content { get; }
        public JObject Style { get; }
        public JObject Raw { get; }
    }
}
