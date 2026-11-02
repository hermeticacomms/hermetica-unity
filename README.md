# Hermetica for Unity

Events, identity, consent, push tokens and in-app messages for Unity games and apps (Unity 2021.3+, Android and iOS). Events are
queued on the device, sent in batches, retried with backoff using one `Idempotency-Key` per batch, kept while offline, and
flushed when the app is paused.

Install with the Package Manager: **Add package from git URL** →
`https://github.com/hermeticacomms/hermetica-unity.git#v0.1.0`

```csharp
using Hermetica.Unity;

var hm = HermeticaSdk.Initialize("hm_pk_...", "https://api.example.com");
hm.SetConsent(analytics: true, marketing: true);
hm.Identify("player-42", email: "ada@example.com");
hm.Screen("Shop");
hm.Track("level_complete", new Dictionary<string, object?> { ["level"] = 3 });
hm.Track("purchase", new Dictionary<string, object?> { ["sku"] = "gems_100" }, value: 499, currency: "USD");
hm.Reset(); // logout
```

## Push

```csharp
// Firebase Messaging
FirebaseMessaging.TokenReceived += (_, e) => HermeticaSdk.SetPushToken(e.Token, "fcm");
// or Unity Mobile Notifications on iOS: the APNs token
HermeticaSdk.SetPushToken(request.DeviceToken, "apns", debugBuild: Debug.isDebugBuild);

var link = hm.HandlePushOpened(data); // the notification's data as IDictionary<string, object?>; tracks $opened_push
```

## In-app messages

```csharp
HermeticaSdk.InAppHandler = m =>
{
    ShowMyPanel(m.Layout, m.Content["blocks"], m.Style); // your UI
    _ = hm.ReportInAppEventAsync(m, "impression");
};
```

The handler runs for app open, every `Screen` and every `Track`; targeting and frequency caps are applied by the server.

## Consent

`SetTrackingOptOut(true)` (or `SetConsent(analytics: false, ...)`) drops the queue and stops tracking until opted back in.
Consent and suppression are enforced by the server too. Use the public site key only; a secret `hm_sk_` key is refused.

The client core (`Runtime/Core`) is shared with the .NET MAUI SDK; `dotnet test Tests~` runs its tests outside Unity.
