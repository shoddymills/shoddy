# hosts/maui — the MAUI host and Shoddy Reckoner

The MAUI lane of the hosting initiative (Part B): `Shoddy.Maui` holds
the reusable host pieces (the calculator session loop, the transcript
view, the canvas view and the audio sinks), `shoddy-reckoner` is the
app, `Shoddy.Maui.Tests` proves both without a window.

This folder has its own solution and depends one-way on `src/` (B2.2).
Nothing here may reference `Shoddy.Mill` or `Shoddy.Compiler` (B2.4);
`scripts/verify-lanes.js` checks that. The weave arrives through
`ShoddyWeave` (`src/Shoddy.Build`), driven by the headless mill, which
the lane's projects build themselves:

```
cd hosts/maui
./build.ps1            # restore + build (Debug)
./build.ps1 test       # Shoddy.Maui.Tests
./build.ps1 release    # Release build, and its closure (verify-closure.js)
```

The repo root's `build.ps1 prove` runs all three on Windows. ci.yml's
`maui` job runs the same verbs on `windows-latest` with
`SHODDY_HEADLESS_CI=1`, which skips the assertions that need an audio
device; a workstation runs them.

## Lanes and workstations

| Lane | Builds on | State |
|---|---|---|
| Windows (WinUI 3) | this Windows workstation, `windows-latest` CI | active |
| Mac Catalyst, iOS, Android | the Mac workstation (rollout plan §3) | TFMs not yet added |

The `TargetFrameworks` lists in `Shoddy.Maui` and `shoddy-reckoner`
carry only `net10.0-windows10.0.19041.0` today. The Mac workstation
adds its frameworks by extending those lists — the `Platforms/`
folders already hold all four heads.

## Toolchain pins (B7.1)

| Item | Pinned where | Value this lane was built with |
|---|---|---|
| .NET SDK | `global.json` (repo root) | 10.0.302 |
| MAUI workload | this table; re-install after every SDK bump | `maui-windows` 10.0.20 (workload set 10.0.302.1) |
| SkiaSharp | `Shoddy.Maui.csproj` | see the csproj |

`global.json` states the SDK this lane was built with, and every CI job
and the Release workflow read it through `setup-dotnet`'s
`global-json-file` - so the runner installs that exact SDK and the
workload above pairs with the SDK it was tested against. Bump the version
here when you bump the workload, and re-run
`dotnet workload install maui-windows`.

Workloads live inside the SDK directory and vanish with it: after any
SDK bump, re-run `dotnet workload install maui-windows` (and on the
Mac, the full `maui` workload) and update this table.
