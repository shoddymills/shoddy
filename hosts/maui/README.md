# hosts/maui — the MAUI host and Shoddy Reckoner

The MAUI lane of the hosting initiative (Part B): `Shoddy.Maui` holds
the reusable host pieces (the calculator session loop, the transcript
view, the canvas view and the audio sinks), `shoddy-reckoner` is the
app, `Shoddy.Maui.Tests` proves both without a window.

This folder has its own solution and workflow and depends one-way on
`src/` (B2.2). Nothing here may reference `Shoddy.Mill`; no mobile
target may reference `Shoddy.Compiler` (B2.4). The weave arrives
through `ShoddyWeave` (`src/Shoddy.Build`), driven by the repo's
published `bin/mill.exe` — build that first:

```
./build.ps1            # from the repo root: publishes bin/mill
cd hosts/maui
./build.ps1            # restore + build + test the Windows lane
```

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

Workloads live inside the SDK directory and vanish with it: after any
SDK bump, re-run `dotnet workload install maui-windows` (and on the
Mac, the full `maui` workload) and update this table.
