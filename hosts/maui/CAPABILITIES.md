# The platform capability map (B6 step 5, B4.10)

What each Shoddy capability needs from each platform Reckoner targets.
Reckoner grants everything halifax's manifest declares and supplies a
real resource behind every entry — so this map's content is
*permissions and entitlements*, not a matrix of refusals. The app build
reads the grant from `ShoddyReckoner.csproj`; this file records what
each platform's manifest must say for the grant to be honest.

| Capability | Resource the app supplies | Windows (WinUI 3) | Android | iOS / Mac Catalyst |
|---|---|---|---|---|
| `file` | app-storage root (`AppDataDirectory/halifax`) | nothing — app container | nothing — app-private storage | nothing — sandbox container |
| `terminal` | the transcript itself | — | — | — |
| `clock` | acknowledgement only (C2.4a) | — | — | — |
| `random` | acknowledgement only | — | — | — |
| `keys` | for halifax, nothing — pure functions over key codes; for a Mode T game, the terminal's key capture feeding `InKey` through the input pipe | — | — | — |
| `vt100` | nothing — pure (`VTEVALKEY`) | — | — | — |
| `scribbler` | `ScribblerCanvasView` in a `CanvasPage` | — | — | — |
| `buzzer` | platform sink behind the shared engine (D18) | nothing — output only, `AudioGraph` | nothing — output only, `AudioTrack` | nothing — output only, `AVAudioEngine`; Catalyst: no microphone entitlement, output needs none |
| `net` | outbound socket (`NETGET`/`NETREQUEST`, user-named URLs — B4.6) | packaged builds: `internetClient` capability in `Package.appxmanifest` | `android.permission.INTERNET` in `AndroidManifest.xml` | Catalyst App Sandbox: `com.apple.security.network.client` in `Entitlements.plist`; iOS: nothing |

What the table says in one line: **only `net` costs anything anywhere,
and only a manifest line** — audio is output-only on every target,
files never leave the app container, and five of the nine capabilities
are pure or acknowledgement-only (B4.9). No API key ships because
there is no built-in service to authenticate to (B4.6).

The exclusion (B4.11) needs no platform entry at all: `seednet`
registers `NETGET` and `NETREQUEST` only, so no listen, accept or
blocking-wait word is reachable from the app's dictionary on any
platform — asserted by the every-seed test, not policed by this table.

Per-lane to-dos as the Mac workstation adds heads:

- **Android**: add `INTERNET` to `Platforms/Android/AndroidManifest.xml`
  when the TFM lands (the template's manifest carries it already).
- **Mac Catalyst**: add `com.apple.security.network.client` to
  `Platforms/MacCatalyst/Entitlements.plist`.
- **Windows packaged**: `internetClient` is a default capability in the
  template's `Package.appxmanifest`; today's `WindowsPackageType=None`
  build needs nothing.
