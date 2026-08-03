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

Everything above except the test-only rows is **redistributed in binary form**
inside the packaged VS Code extension (`vscode-shoddy-*.vsix`), which carries a
copy of the mill under `extension/mill/`. This notices file ships in that
package for the same reason.

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

### Colossal Cave Adventure (`mills/mungo-caverns/`)

The mungo-caverns mill is a Shoddy forward-port of **Open Adventure** by Eric S.
Raymond (https://gitlab.com/esr/open-adventure), itself a forward-port of
*Adventure* 2.5 (1995) by Will Crowther and Don Woods — the last version of
Colossal Cave Adventure in the main line of development by the game's original
authors, released under the **BSD 2-Clause License** with their permission and
encouragement. Adventure was originally written by Will Crowther; most of the
features of the game as it stands were added by Don Woods.

Because it derives from that work, the entire mill is licensed **BSD-2-Clause**
rather than MIT. The full terms and copyright lines — Crowther & Woods
(1977, 2005), Eric S. Raymond (Open Adventure), and Stephen Vincent Foster
(the Shoddy port, 2026) — are in `mills/mungo-caverns/LICENSE` in the source
repository.

The port's data tables — the rooms, objects, messages, vocabulary and travel
rules — derive from Open Adventure's dungeon database. They were generated from
it once and are now hand-edited Shoddy, maintained against the upstream C,
which remains the reference for every rule. The text they carry is Crowther and
Woods' and Eric Raymond's, which is why the whole mill is BSD-2-Clause.
**Neither that database nor the C sources are redistributed here.**

The port is **modified** from upstream: the game itself is called Mungo
Caverns, and the cave's knife-throwing folk are called curmudgeons
rather than by their upstream name, which renames one vocabulary word, the
symbols built from it, and the messages and room descriptions that mention
them. Both renames are mechanical, and the harness the port was graded with
mapped them back so that upstream's recorded games could grade it byte for
byte. **Those recorded games are not redistributed here either.** It plays
identically otherwise.

### Pac-Man homage (`mills/pac-vt100/`)

The `pac*.shoddy` files are an original reimplementation, ported by hand from a
compact C terminal rendition. **The original C source is not redistributed in
this repository** (its license could not be established). *PAC-MAN* is a
trademark of Bandai Namco Entertainment Inc. This project is an educational
homage and is not affiliated with, endorsed by, or sponsored by the trademark
owner.

### Space Invaders homage (`mills/invaders/`)

The `invaders*.shoddy` files are an original reimplementation. *SPACE INVADERS*
is a trademark of Taito Corporation / Square Enix. This project is an
educational homage and is not affiliated with, endorsed by, or sponsored by the
trademark owner.

### Boids (`mills/devils-dust/`)

The flocking simulation in the `devils-dust*.shoddy` files implements
**boids**, the distributed behavioral model created by **Craig W. Reynolds**
and published as *Flocks, Herds, and Schools: A Distributed Behavioral
Model*, Computer Graphics 21(4) (SIGGRAPH '87 Conference Proceedings),
pp. 25–34, ACM, 1987 (https://doi.org/10.1145/37402.37406). The three
steering rules — separation, alignment, cohesion — and the additional
steering-force composition the mill uses are Reynolds's; the term "boids"
is his coinage. His reference page is https://www.red3d.com/cwr/boids/.
**No third-party code is included** — the Shoddy implementation is original,
and the algorithm and model are credited to Reynolds.

The mill's "eleven" lever position is a nod to a scene in *This Is Spinal
Tap* (1984); no material from the film is included. The sound workbench's
cowbell recipe (`sound-lab.shoddy`) follows the widely documented two-square
voicing of the Roland TR-808 rhythm machine; no Roland material is included.

### Neural-network regression reference (`mills/demographics/`)

The `demographics*.shoddy` files are an original port of James D. McCaffrey's
walkthrough *Neural Network Regression from Scratch with Batch Training*
(https://jamesmccaffreyblog.com/2023/09/04/neural-network-regression-from-scratch-with-batch-training-using-csharp/).
McCaffrey's reference **C# source is not redistributed in this repository** (its
license could not be established); only the Shoddy port and its data files ship
here. The algorithm and architecture are credited to McCaffrey.

### Iris data set (`mills/iris/dat/`)

The measurements in `iris-train.dat` and `iris-test.dat` are R. A. Fisher's,
from *The Use of Multiple Measurements in Taxonomic Problems*, Annals of
Eugenics 7(2):179–188 (1936), as distributed by the UCI Machine Learning
Repository (https://archive.ics.uci.edu/dataset/53/iris). The data is in the
public domain and is reproduced here **as data only** — no third-party code is
included, and the `iris*.shoddy` programs are original. Two rows (35 and 38)
are known to differ between Fisher's paper and the raw UCI file; they are
carried here in their corrected form, which is what R's `datasets::iris` and
scikit-learn's `load_iris` ship and what the widely published summary
statistics reflect.

### Besley (`docs/media/font/`)

The documentation site sets its page titles in **Besley SemiBold**, a Clarendon
revival by Owen Earl / Indestructible Type. It is licensed under the **SIL Open
Font License 1.1**; the full licence travels with the font at
[`docs/media/font/OFL.txt`](docs/media/font/OFL.txt). Only a latin subset ships
(`besley-semibold-latin.woff2`, ~17 KB), which the OFL permits — subsetted and
converted copies remain covered by the same licence and are not sold on their
own. The wordmark and lockup SVGs under `docs/media/brand/` were set in the
same face and **converted to outlines**, so they carry no font dependency and
embed no font data.
