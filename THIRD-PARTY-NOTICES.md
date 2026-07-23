# Third-Party Notices

Shoddy itself is licensed under the [MIT License](LICENSE). It builds on,
depends on, or includes homages to the third-party works listed below, which
remain under their own licenses and the copyrights of their respective owners.

## NuGet dependencies

| Package | License | Used by |
|---------|---------|---------|
| `Microsoft.CodeAnalysis.CSharp` (Roslyn) | MIT | compiler — weaving generated C# |
| `Silk.NET.Windowing`, `Silk.NET.Input`, `Silk.NET.OpenGL` | MIT | mill — the scribbler window |
| `Silk.NET.OpenAL` | MIT | mill — the buzzer audio bindings |
| `Silk.NET.OpenAL.Soft.Native` | see **OpenAL Soft** below | mill — bundled native audio library |
| `xunit`, `xunit.runner.visualstudio` | Apache-2.0 / MIT | tests only (not distributed) |
| `Microsoft.NET.Test.Sdk`, `coverlet.collector` | MIT | tests only (not distributed) |

### OpenAL Soft (LGPL)

`Silk.NET.OpenAL.Soft.Native` redistributes **OpenAL Soft**, which is licensed
under the **GNU Lesser General Public License, version 2.1 (LGPL-2.1)**.
OpenAL Soft is dynamically loaded by the mill; it is not statically linked into
Shoddy's own assemblies. Under the LGPL you may relink the mill against your own
build of OpenAL Soft. Source and license for OpenAL Soft are available at
https://github.com/kcat/openal-soft.

### GLFW

The Silk.NET windowing native dependency (GLFW) is licensed under the
**zlib/libpng license**. See https://www.glfw.org/license.html.

## Included and adapted works

### OREGON (The Oregon Trail)

`mills/oregon/oregon.bas` is the historical 1971 BASIC listing of *OREGON* by
Bill Heinemann, developed for the Minnesota Educational Computing Consortium
(MECC). The `oregon*.shoddy` files are a port of that program, retained for
educational and historical purposes. *The Oregon Trail* is a trademark of
Houghton Mifflin Harcourt. This project is not affiliated with, endorsed by, or
sponsored by the trademark owner.

### Pac-Man homage (`mills/pacman-vt100/`)

The `pac*.shoddy` files are an original reimplementation, ported by hand from a
compact C terminal rendition. **The original C source is not redistributed in
this repository** (its license could not be established). *PAC-MAN* is a
trademark of Bandai Namco Entertainment Inc. This project is an educational
homage and is not affiliated with, endorsed by, or sponsored by the trademark
owner.

### Space Invaders homage (`mills/space-invaders/`)

The `invaders*.shoddy` files are an original reimplementation. *SPACE INVADERS*
is a trademark of Taito Corporation / Square Enix. This project is an
educational homage and is not affiliated with, endorsed by, or sponsored by the
trademark owner.

### Neural-network regression reference (`mills/demographics/`)

The `demographics*.shoddy` files are an original port of James D. McCaffrey's
walkthrough *Neural Network Regression from Scratch with Batch Training*
(https://jamesmccaffreyblog.com/2023/09/04/neural-network-regression-from-scratch-with-batch-training-using-csharp/).
McCaffrey's reference **C# source is not redistributed in this repository** (its
license could not be established); only the Shoddy port and its data files ship
here. The algorithm and architecture are credited to McCaffrey.
