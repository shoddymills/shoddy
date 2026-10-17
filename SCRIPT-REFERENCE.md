# Script Reference

Every shell script in this repository, with the commands each one accepts and the arguments those commands take. Compiled from the scripts themselves on 2026-10-16.

Scope: the `.sh` files. Each has a PowerShell twin (`.ps1`) that takes the same commands and flags, spelled the same way. `scripts/verify-twins.js` checks that every script has its twin and that both headers list the same verbs; it does not compare flags or logic. ci.yml's `windows` job runs the root `build.ps1` and every mill's `build.ps1`. The twins are not listed separately here.

Conventions shared by every script:

- A command is the first positional argument. Where a default is shown, running the script with no arguments runs that command.
- `mill` below means the built toolchain at `artifacts/bin/Shoddy.Mill/release/mill`.
- Every mill and tutorial wrapper `cd`s into its own folder first, so relative paths in the table are relative to the script's folder unless the row says otherwise.
- An unknown command prints a usage line to stderr and exits 2 (the two `hosts/` lanes exit 1).

## All scripts

| Script | Role | Commands |
| --- | --- | --- |
| [build.sh](build.sh) | Root build wrapper for the toolchain, suites and VS Code extension | `all`, `build`, `machines`, `test`, `check`, `prove`, `reproducible`, `run`, `weave`, `stage`, `vsix`, `install`, `clean`, `help` |
| [scripts/shoddy-branch.sh](scripts/shoddy-branch.sh) | Maintainer: cut, sync and land branches | `feature`, `bug`, `sync`, `land` |
| [scripts/shoddy-commit.sh](scripts/shoddy-commit.sh) | Maintainer: stage everything and commit | flags only: `-Message`, `-Force` |
| [scripts/shoddy-display.sh](scripts/shoddy-display.sh) | Maintainer: run the windowed suites CI cannot | (run), `--list` |
| [scripts/shoddy-make-sitemap.sh](scripts/shoddy-make-sitemap.sh) | Regenerate the docs sitemap | positional: `[DOCSDIR] [BASEURL]` |
| [scripts/shoddy-mill-common.sh](scripts/shoddy-mill-common.sh) | Library sourced by every mill wrapper; not run directly | functions: `ensure_mill`, `run_live`, `weave_and_move` |
| [scripts/shoddy-pr.sh](scripts/shoddy-pr.sh) | Maintainer: push the branch and open a pull request | flags only: `-Draft`, `-Title`, `-Web` |
| [scripts/shoddy-ship.sh](scripts/shoddy-ship.sh) | Maintainer: tag a release on main and push the tag | positional: `X.Y.Z [-Yes]` |
| [scripts/shoddy-status.sh](scripts/shoddy-status.sh) | Maintainer: read-only report of where the work is | positional: `[X.Y.Z]` |
| [hosts/maui/build.sh](hosts/maui/build.sh) | MAUI lane | `build`, `test`, `run`, `release` |
| [hosts/mcp/build.sh](hosts/mcp/build.sh) | MCP lane (Sparky server) | `build`, `test`, `run`, `release`, `publish`, `install` |
| [mills/demographics/build.sh](mills/demographics/build.sh) | Demographics mill | `build`, `train`, `run`, `test`, `clean` |
| [mills/devils-dust/build.sh](mills/devils-dust/build.sh) | Devil's Dust mill | `run`, `test` |
| [mills/emley-moor/build.sh](mills/emley-moor/build.sh) | Emley Moor web server mill | `run`, `test`, `build`, `clean` |
| [mills/halifax/build.sh](mills/halifax/build.sh) | Halifax calculator mill | `run`, `test`, `demo`, `build` |
| [mills/invaders/build.sh](mills/invaders/build.sh) | Invaders mill | `run`, `test` |
| [mills/iris/build.sh](mills/iris/build.sh) | Iris mill | `build`, `train`, `run`, `test`, `clean` |
| [mills/mungo-caverns/build.sh](mills/mungo-caverns/build.sh) | Mungo Caverns mill | `run`, `test` |
| [mills/oregon/build.sh](mills/oregon/build.sh) | Oregon Trail mill | `run`, `test` |
| [mills/pac-vt100/build.sh](mills/pac-vt100/build.sh) | Pac VT100 mill | `run`, `test` |
| [mills/simplex-from-mps/build.sh](mills/simplex-from-mps/build.sh) | Simplex from MPS mill | `build`, `run`, `test`, `clean` |
| [mills/sparky/build.sh](mills/sparky/build.sh) | Sparky mill (the dictionary under the MCP server) | `run`, `test`, `build` |
| [mills/tally/build.sh](mills/tally/build.sh) | Tally mill | `run`, `capture`, `test`, `build`, `clean` |
| [mills/thornle/build.sh](mills/thornle/build.sh) | Thornle mill | `run`, `short`, `test` |
| [mills/weather-glass/build.sh](mills/weather-glass/build.sh) | Weather Glass mill | `run`, `test`, `build`, `clean` |
| [tutorials/spiro/build.sh](tutorials/spiro/build.sh) | Spirograph tutorial | `run`, `test` |

## build.sh (repository root)

Unix wrapper around `dotnet build`. Exports `SHODDYLIB=<repo>/machines` so every mill resolves machines from the checkout. Default command: `help`.

| Command | Arguments | What it does |
| --- | --- | --- |
| `help`, `-h`, `--help` | | Prints the usage block from the top of the script. |
| `all` | `[patch]`, `[minor]`, `[major]` or `[X.Y.Z]` | Re-invokes the script as `clean`, then `test`, then `vsix` with the same optional argument. Stops at the first failure. release.yml runs `all X.Y.Z` with the tag's version. |
| `build`, `machines` | | `dotnet build src/Shoddy.Mill -c Release`. Builds the toolchain and the machine library in one go. |
| `test` | | Runs `build`, then: `dotnet test src/Shoddy.Tests` with a `.trx` under `artifacts/test-results`; every `CORE_SUITES` suite from `scripts/suites.mjs`; `tst/net-demo.shoddy` with `--allow-net`; `tutorials/spiro/test.shoddy`; `tst/sinq-demo.shoddy`; every `MACHINE_SUITES` suite plus `tst/isamtest.shoddy` and `tst/isamdump.shoddy DELETE` in a scratch folder; then `./build.sh test` in every `mills/*/` folder; then `node scripts/verify-manifests.js` and `node scripts/verify-headless.js`. The tutorial runs through `tutorials/spiro/build.sh test`. Every suite runs with stdin closed. This is CI's core job. |
| `check` | | Read-only. Runs `node scripts/<gate>.js` for `verify-docs`, `verify-errors`, `verify-permissions --check`, `verify-host-blind`, `verify-suites`, `verify-twins`, `verify-lanes`, then `node --test vscode-shoddy/test/launch.test.js`. Exactly CI's checks job. To stage a missing execute bit, run `node scripts/verify-permissions.js` without `--check`. |
| `prove` | | **The release proof.** Refuses under Git Bash on Windows (use `build.ps1 prove`). Needs `vsce`. Sets `SHODDY_CI=1`: locked restore and CI build settings, as on the runner. Then, in CI's order: `check`, `test`, `reproducible`, the MCP lane's `build`, `test`, `release` and `publish 0.0.1`, `vsix 0.0.1` (the package is deleted afterwards), and `scripts/shoddy-display.sh` where there is a display. Ends with a `PROVED` line and a `NOT PROVED ON THIS MACHINE` line for each lane it cannot run. If the tree was clean before and after, it writes `artifacts/proved/<commit>`, which `shoddy-pr` reads. If the run itself dirtied the tree, it lists the changes and exits 1. |
| `reproducible` | | Two clean builds from an empty `artifacts/`, then compares SHA-256 sums of every DLL under `artifacts/bin`. Fails with the diff if they differ. CI's core job calls this. |
| `run` | `[--allow-net] [--no-window] [--no-lint] [--lint-verbose] FILE.shoddy [ARGS...]` | Runs `build`, then `mill run` with the switches and file. Switches must come before the file. Anything after the file reaches the program through `Args`. Any other `--` switch is refused with exit 2. Missing file: usage, exit 2. |
| `weave` | `FILE.shoddy` | Runs `build`, then `mill weave FILE.shoddy`. Missing file: usage, exit 2. |
| `stage` | `[X.Y.Z]` | Removes `vscode-shoddy/mill` and `vscode-shoddy/machines`, then `dotnet publish src/Shoddy.Mill -c Release -o vscode-shoddy/mill -p:SatelliteResourceLanguages=en`, adding `-p:Version=X.Y.Z` when given. Copies `THIRD-PARTY-NOTICES.md` and `AUTHORSHIP.md` into `vscode-shoddy/`. |
| `vsix` | `[patch]`, `[minor]`, `[major]` or `[X.Y.Z]` | Needs `vsce` on PATH (reported, never installed; CI pins 3.2.1). Runs `stage`, then in `vscode-shoddy/`: no argument, `vsce package`; a bump word, `vsce package <word> --no-git-tag-version`; an exact version, `stage X.Y.Z` then package with `package.json` temporarily stamped to that version and restored on exit. With no argument or an exact version, it then checks `vscode-shoddy/vscode-shoddy-<version>.vsix` exists, and exits 1 if not. |
| `install` | `[X.Y.Z]` | Needs the `code` CLI on PATH. Installs `vscode-shoddy/vscode-shoddy-X.Y.Z.vsix` with `code --install-extension --force`. With no version, reads it from `vscode-shoddy/package.json`. Refuses if that exact package has not been built. |
| `clean` | | Removes `artifacts/`, `vscode-shoddy/mill`, `vscode-shoddy/machines`, and every `bin/` folder under `machines/`, `mills/`, `proofs/`, `tst/` and `tutorials/` (where `mill weave` and `mill machine` write). |

## scripts/shoddy-branch.sh

Creates and deletes branches, locally and on origin. Never merges. Runs from anywhere inside the repo. Before any command it checks: inside a git repository, no merge in progress, no unstaged changes, no staged changes.

| Command | Arguments | What it does |
| --- | --- | --- |
| `feature` | `NAME` | Fetches with `--prune`; refuses if `feature/NAME` exists locally or on origin; checks out `main`, `pull --ff-only origin main`, `checkout -b feature/NAME`, `push -u origin feature/NAME`. A leading `feature/` on NAME is stripped. NAME may use letters, digits, `.`, `_`, `-`. |
| `bug` | `NAMENN` | Same cut as `feature`, for `bug/NAMENN`. NAME must end in two digits, numbered per origin feature (for example `pudsey01`). A leading `bug/` is stripped. |
| `sync` | | From a `feature/*` or `bug/*` branch only. Fetches, then `git merge origin/main -m "Merge main into <branch>"`. Exits 0 with a message if nothing is behind. |
| `land` | `[-Yes]` | From a `feature/*` or `bug/*` branch only. Fetches, then asks `gh pr view <branch>` for the PR state: `MERGED` proceeds, `OPEN` or `CLOSED` refuses. Without gh or a PR, treats the branch as merged if every commit is reachable from `origin/main`. If unconfirmed, prompts `y/N`; under `-Yes` it refuses instead. Then `checkout main`, `pull --ff-only`, `branch -D <branch>`, `push origin --delete <branch>`. |

The `-Yes` flag is accepted as either the second or third argument.

## scripts/shoddy-commit.sh

Stages everything and commits. Pushes nothing. Runs from anywhere inside the repo and `cd`s to the repository root.

| Flag | Arguments | What it does |
| --- | --- | --- |
| `-Message` | `"what changed"` | Required. The commit message. A blank message is refused. |
| `-Force` | | Allows a commit on `main` or `master`, which is otherwise refused. |

Behaviour: refuses when a merge is in progress or when the tree is clean. Before `git add -A` it runs `git add --chmod=+x` on every untracked file whose first two bytes are `#!`, so new scripts are committed executable. Then `git commit -m "<message>"`. Any other argument exits 2.

## scripts/shoddy-display.sh

Runs the suites excluded from CI because they open a real window. The list is read from `TST_EXCLUSIONS` in `scripts/suites.mjs`, taking every entry whose reason starts with "needs a real display".

| Command | Arguments | What it does |
| --- | --- | --- |
| (none) | | Exports `SHODDYLIB=<repo>/machines`, as `build.sh` does. For each suite, `mill --no-window run tst/<suite>.shoddy`. Continues past failures and lists them at the end, exiting 1 if any failed. Refuses up front on Linux when neither `DISPLAY` nor `WAYLAND_DISPLAY` is set, and when the mill is not built. |
| `--list`, `-List` | | Prints `tst/<suite>.shoddy` for each display suite and exits 0. |

## scripts/shoddy-make-sitemap.sh

POSIX `sh` script. Writes `<DOCSDIR>/sitemap.xml` with one `<url>` per `*.html` under DOCSDIR except `404.html`. `index.html` becomes its directory URL. `<lastmod>` is the file's last git commit date, falling back to the file's mtime when it is dirty or untracked.

| Argument | Default | What it is |
| --- | --- | --- |
| `DOCSDIR` (position 1) | `docs` | The folder to scan and write the sitemap into. |
| `BASEURL` (position 2) | `https://shoddymills.github.io/shoddy/` | The prefix for every `<loc>`. |

## scripts/shoddy-mill-common.sh

Not executable and has no shebang. Sourced by every `mills/*/build.sh` after that script has `cd`ed into its own folder. Callers that use `run_live` set `ORIG_DIR=$(pwd)` before the `cd`.

| Provides | Arguments | What it does |
| --- | --- | --- |
| `MILL_DIR` | | The mill's own absolute directory. |
| `REPO` | | `MILL_DIR/../..`, the repository root. |
| `MILL` | | `REPO/artifacts/bin/Shoddy.Mill/release/mill`. |
| `SHODDYLIB` | | Exported as `REPO/machines`. |
| `ensure_mill` | | Runs `REPO/build.sh build`. |
| `run_live` | `CMD...` | Runs CMD from `ORIG_DIR`, the folder the user invoked the wrapper from. |
| `weave_and_move` | `SRC...` | Runs `ensure_mill`, then `mill weave SRC` for each source. Output lands in the mill's `bin/`. |

## scripts/shoddy-pr.sh

Pushes the current branch and opens a pull request. Never merges. Requires `gh` installed and authenticated, a clean tree, a `feature/*` or `bug/*` branch, and at least one commit beyond `origin/main`.

| Flag | Arguments | What it does |
| --- | --- | --- |
| `-Draft` | | Adds `--draft` to `gh pr create`. |
| `-Title` | `"..."` | Sets the PR title. Default: the subject of the first commit on the branch, or the branch name. |
| `-Web` | | Opens the pull request in a browser afterwards with `gh pr view --web`. |

Behaviour: fetches, warns if `main` has moved, lists the commits, then `git push -u origin <branch>`. If an OPEN PR exists for the branch it prints that PR and exits 0; a MERGED or CLOSED one is ignored. Otherwise `gh pr create --base main --head <branch> --title <title> --body-file <generated body>`. The body lists the commits and states whether `build prove` passed on HEAD, read from `artifacts/proved/<commit>`. Any other argument exits 2.

## scripts/shoddy-ship.sh

Cuts a release by tagging `main` and pushing the tag. Those are its only two mutating operations; everything else is a refusal.

| Argument | Required | What it is |
| --- | --- | --- |
| `X.Y.Z` (position 1) | yes | An exact three-part version. Pre-release and build suffixes are refused. The tag is `vX.Y.Z`. |
| `-Yes` (position 2) | no | Skips the `Tag and push? (y/N)` prompt. |

Checks in order: valid version; inside a repository with no merge in progress; `git fetch --prune --tags` succeeds; on `main`; clean tree; level with `origin/main`; `vX.Y.Z` does not exist locally or on origin; `release-notes/vX.Y.Z.md` exists and is committed; `gh` is installed; `gh run list --workflow ci.yml --commit HEAD` answers, has at least one run, and every run is completed with conclusion `success`. Any failed check stops the script. Then `git tag -a vX.Y.Z -m vX.Y.Z` and `git push origin vX.Y.Z`.

## scripts/shoddy-status.sh

Read-only report of where the work sits in the release procedure. The only thing it writes is remote-tracking refs, because it fetches. Always exits 0 once it starts reporting. Does not use `set -e`.

| Argument | Required | What it is |
| --- | --- | --- |
| `X.Y.Z` (position 1) | no | When given, also reports whether `release-notes/vX.Y.Z.md` is committed and whether tag `vX.Y.Z` is free, and whether the version is ready to ship. |

Rows reported: `branch`, `commits` (ahead and behind `origin/main`), `pushed`, `review` (PR state via gh), `proven` (ci.yml's runs on HEAD, by the same rule `shoddy-ship` refuses by), `notes`, `tag`, `released` (latest reachable tag), and `next`, the first unmet step. Exits 1 only when not inside a repository or the version is malformed.

## hosts/maui/build.sh

Default command: `build`. Unknown command exits 1.

| Command | Arguments | What it does |
| --- | --- | --- |
| `build` | | `dotnet build ShoddyMaui.slnx` (Debug). |
| `test` | | `dotnet test Shoddy.Maui.Tests`. |
| `run` | | `dotnet build shoddy-reckoner`, then `dotnet run --project shoddy-reckoner --no-build`. Launches the Reckoner app. |
| `release` | | `dotnet build ShoddyMaui.slnx -c Release`, then `node scripts/verify-closure.js reckoner`: no perch, and the woven core and machine library present. |

## hosts/mcp/build.sh

Always runs the root `../../build.sh build` first, so the mill's own suite has a mill. Default command: `build`. Unknown command exits 1.

| Command | Arguments | What it does |
| --- | --- | --- |
| `build` | | `dotnet build Sparky.slnx` (Debug). |
| `test` | | `dotnet test Shoddy.Mcp.Tests`. |
| `run` | | `dotnet build sparky`, then `dotnet run --project sparky --no-build`. Starts the MCP server on stdin/stdout; it waits for a JSON-RPC message and looks idle. |
| `release` | | `dotnet build Sparky.slnx -c Release`, then `node scripts/verify-closure.js sparky`: no window, audio, toolchain or third-party protocol package in the closure. |
| `publish` | `[V]` | For each of `win-x64`, `win-arm64`, `linux-x64`, `osx-x64`, `osx-arm64`: `dotnet publish sparky -c Release -r <rid> --self-contained -p:ShoddyRidPublish=true [-p:Version=V] -o ../../artifacts/publish/sparky/<rid>`. Single-file comes from `Sparky.csproj`; `ShoddyRidPublish` keeps these restores' lock files under `artifacts/`. Windows RIDs are zipped to `sparky[-V]-<rid>.zip`; the others are tarred to `sparky[-V]-<rid>.tar.gz` with the executable bit set. Then `node scripts/verify-shipped.js sparky [V]` checks the execute bit in every unix archive (off Windows), unpacks this platform's archive outside the repo, drives it over stdio, and with V checks `serverInfo` reports V. |
| `install` | `[V]` | Publishes the single file for this machine's RID (`osx-arm64`, `osx-x64`, `linux-x64` or `linux-arm64`) to `artifacts/publish/sparky-install/<rid>`, drives it over stdio and stops if it does not answer, then copies it to `$SPARKY_HOME` (default `~/.local/share/shoddy/sparky/sparky`) by copy-then-rename. If `claude` is on PATH, runs `claude mcp remove --scope user sparky`, then in the folder install was run from `claude mcp add --scope project sparky -- <path> --root .`, which writes its `.mcp.json`. An absolute `SPARKY_ROOT` registers `--scope user` with `--root $SPARKY_ROOT` instead (and creates it). Otherwise prints the command to register by hand. |

## Mill wrappers (mills/*/build.sh)

Every mill wrapper `cd`s into its own folder, sources `scripts/shoddy-mill-common.sh`, and calls `ensure_mill` (the root `build.sh build`) before any `mill run`. Targets marked **live** run from the folder the user invoked the script from, so paths typed at a prompt or passed on the command line resolve there. `test`, `build` and `clean` always run from the mill's folder.

### mills/demographics/build.sh

Default command: `build`.

| Command | Arguments | What it does |
| --- | --- | --- |
| `build` | | Weaves `demographics-train.shoddy` and `demographics.shoddy` into `bin/`. |
| `train` | | Builds, then `dotnet bin/demographics-train.dll`. Writes `dat/people-model.bin`; about nine minutes. |
| `run` | | Builds, then `dotnet bin/demographics.dll`. Predicts interactively. |
| `test` | | `mill run test.shoddy < /dev/null`. Grades the shipped model from source; does not train. |
| `clean` | | `rm -f bin/*.dll bin/*.json`. |

### mills/devils-dust/build.sh

Default command: `run`.

| Command | Arguments | What it does |
| --- | --- | --- |
| `run` | | `mill run devils-dust.shoddy`. Opens a scribbler window; Q or Escape quits. |
| `test` | | `mill run test.shoddy < /dev/null`. Headless model checks. |

### mills/emley-moor/build.sh

Default command: `run`.

| Command | Arguments | What it does |
| --- | --- | --- |
| `run` | | `mill run --allow-net emley-moor.shoddy`. Serves on `http://127.0.0.1:8080/`, loopback only. |
| `test` | | `mill run test.shoddy < /dev/null`. Routing tests; no network. |
| `build` | | Weaves `emley-moor.shoddy` into `bin/`. The woven program needs `SHODDY_ALLOW_NET=1` to run. |
| `clean` | | `rm -rf bin`. |

### mills/halifax/build.sh

Default command: `run`.

| Command | Arguments | What it does |
| --- | --- | --- |
| `run` | | **live**: `mill run halifax.shoddy`. The calculator at a prompt. |
| `test` | | `mill run test.shoddy < /dev/null`. |
| `demo` | | `mill run halifax.shoddy < files/demo.halifax`. The golden session. |
| `build` | | Weaves `halifax.shoddy` into `bin/`. |

### mills/invaders/build.sh

Default command: `run`.

| Command | Arguments | What it does |
| --- | --- | --- |
| `run` | | `mill run invaders.shoddy`. Opens a scribbler window. Left/Right move, Space fires, Q or Escape quits. |
| `test` | | `mill run test.shoddy < /dev/null`. Headless simulation smoke check. |

### mills/iris/build.sh

Default command: `build`.

| Command | Arguments | What it does |
| --- | --- | --- |
| `build` | | Weaves `iris-train.shoddy` and `iris.shoddy` into `bin/`. |
| `train` | | Builds, then `dotnet bin/iris-train.dll`. Writes `dat/iris-model.bin`; a few seconds. |
| `run` | | Builds, then `dotnet bin/iris.dll`. Classifies interactively. |
| `test` | | `mill run test.shoddy < /dev/null`. Grades the shipped model against the held-out rows. |
| `clean` | | `rm -f bin/*.dll bin/*.json`. |

### mills/mungo-caverns/build.sh

Default command: `run`.

| Command | Arguments | What it does |
| --- | --- | --- |
| `run` | | **live**: `mill run mungo-caverns.shoddy`. Console game. |
| `test` | | `mill run <suite> < /dev/null` for `test.shoddy` and every `tests/test-*.shoddy`, stopping at the first failure. |

### mills/oregon/build.sh

Default command: `run`.

| Command | Arguments | What it does |
| --- | --- | --- |
| `run` | | `mill run oregon.shoddy`. Console game. |
| `test` | | `mill run test.shoddy < /dev/null`. Headless model checks. |

### mills/pac-vt100/build.sh

Default command: `run`.

| Command | Arguments | What it does |
| --- | --- | --- |
| `run` | | `mill run pac.shoddy`. VT100 terminal game; wants an ANSI terminal of at least 80x26. WASD, arrows or keypad move, Q or Escape quits. |
| `test` | | `mill run test.shoddy < /dev/null`. Headless simulation smoke check. |

### mills/simplex-from-mps/build.sh

Default command: `build`.

| Command | Arguments | What it does |
| --- | --- | --- |
| `build` | | Weaves `simplex-mps.shoddy` into `bin/`. |
| `run` | `FILE.mps [-z]` | Builds, then **live**: `dotnet bin/simplex-mps.dll FILE.mps [-z]`. `-z` or `--zero-lower` forces `x >= 0` on every variable. Missing FILE: usage, exit 2. |
| `test` | | `mill run test.shoddy < /dev/null`. Solves both fixtures and checks the answers. |
| `clean` | | `rm -f bin/*.dll bin/*.json`. |

### mills/sparky/build.sh

Default command: `run`.

| Command | Arguments | What it does |
| --- | --- | --- |
| `run` | | **live**: `mill run sparky.shoddy`. The dictionary at a prompt. |
| `test` | | `mill run test.shoddy < /dev/null`. Writes no files. |
| `build` | | Weaves `sparky.shoddy` into `bin/`. |

### mills/tally/build.sh

Default command: `run`. `SPEC` is the second argument and defaults to `files/grades.spec`; it resolves against the mill's folder.

| Command | Arguments | What it does |
| --- | --- | --- |
| `run` | `[SPEC]` | `mill run tally.shoddy SPEC`. Reads the spec, prints the report, shows the chart. |
| `capture` | `[SPEC]` | `mill run --no-window tally.shoddy SPEC`. Nothing on screen; the PNG named in the spec is the output. |
| `test` | | `mill run test.shoddy < /dev/null`. |
| `build` | | Weaves `tally.shoddy` into `bin/`. A woven program has no window backend, so charts still need `mill run`. |
| `clean` | | `rm -rf bin`. |

### mills/thornle/build.sh

Default command: `run`.

| Command | Arguments | What it does |
| --- | --- | --- |
| `run` | | `mill run thornle.shoddy`. VT100 terminal game on `files/words.txt`; wants an ANSI terminal of at least 80x24. A typed word guesses, Q or QUIT leaves the game. |
| `short` | | `mill run thornle.shoddy files/short-list.txt`. The same game on six words. |
| `test` | | `mill run test.shoddy < /dev/null`. Headless model checks. |

### mills/weather-glass/build.sh

Default command: `run`.

| Command | Arguments | What it does |
| --- | --- | --- |
| `run` | `[ZIP]` | `mill run --allow-net weather-glass.shoddy ZIP`. ZIP defaults to `63011`. Four HTTPS GETs to zippopotam.us and api.weather.gov. |
| `test` | | `mill run test.shoddy < /dev/null`. Offline, against captured responses in `files/`. |
| `build` | | Weaves `weather-glass.shoddy` into `bin/`. The woven program needs `SHODDY_ALLOW_NET=1` to run. |
| `clean` | | `rm -rf bin`. |

## tutorials/spiro/build.sh

Default command: `run`. Does not source the common library. Finds a mill in this order: the `MILL` environment variable if set, `../../artifacts/bin/Shoddy.Mill/release/mill` (or `mill.exe`), then `mill` on PATH. Exits 1 if none is found.

| Command | Arguments | What it does |
| --- | --- | --- |
| `run` | | `mill run spiro.shoddy`. Draws the spiral in a window. |
| `test` | | `mill run test.shoddy < /dev/null`. Headless checks; works in CI. The root `build.sh test` runs this. |

| Environment | What it does |
| --- | --- |
| `MILL` | Path to the mill to use, overriding the search. |
