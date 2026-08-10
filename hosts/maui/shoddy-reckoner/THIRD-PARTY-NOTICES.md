# Third-party notices — Shoddy Reckoner

What the app bundles beyond this repository's own MIT-licensed code
(the Shoddy runtime, the woven halifax mill and its machines).

| Component | Why it is here | License |
|---|---|---|
| .NET MAUI (Microsoft.Maui.*) | the UI toolkit — every screen is its controls (B1.4) | MIT |
| Windows App SDK / WinUI 3 | the Windows head MAUI targets | MIT (with Windows SDK license for OS components) |
| SkiaSharp (+ Views.Maui) | the scribbler canvas blit, and only that (D10): `plotter` charts, `turtle` drawings | MIT |
| Besley (semibold) | the brand face, on the app's own chrome — converted from the docs' woff2, license at `Resources/Fonts/Besley-OFL.txt` | SIL OFL 1.1 |

Recorded deliberately, per B7.7's asymmetry note: **the canvas costs a
third-party SDK; audio costs none.** The buzzer backend uses each
platform's own bound API (`AudioGraph` on Windows — D18), so no audio
entry appears above, and its absence is by design rather than
omission. SkiaSharp additionally carries its own privacy manifest
(`PrivacyInfo.xcprivacy`) on Apple platforms — an item for the Mac
lane when it lands.

Game licenses (mungo-caverns's BSD-2-Clause among them) join this file
with the shelf (B5.8); no game ships in this build.
