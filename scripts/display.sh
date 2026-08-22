#!/usr/bin/env bash
# MAINTAINER TOOL - the suites CI cannot run.
#
#   scripts/display.sh           run them
#   scripts/display.sh --list    name them and stop
#
# The twin of display.ps1 and equivalent to it. See that file for why
# these three are excluded from CI: they open a real window, GLFW needs
# a platform to create one on, and a hosted runner has none - not even under
# Xvfb, which v1.10.1 shipped a gate for and which failed the same way.
#
# THE LIST IS NOT WRITTEN HERE. It is read out of TST_EXCLUSIONS in
# scripts/suites.mjs, so a fourth windowed suite cannot be added there
# and quietly forgotten here.
set -eu

cd "$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"

MILL=bin/mill
[ -x "$MILL" ] || MILL=bin/mill.exe

READ='const m = await import("./scripts/suites.mjs");
console.log(Object.entries(m.TST_EXCLUSIONS)
  .filter(([, why]) => why.startsWith("needs a real display"))
  .map(([name]) => name).join(" "))'

SUITES=$(node --input-type=module -e "$READ") \
    || { echo 'STOPPED: could not read TST_EXCLUSIONS from scripts/suites.mjs.' >&2; exit 1; }

[ -n "$SUITES" ] \
    || { echo 'STOPPED: no display suites are listed - has the reason wording changed?' >&2; exit 1; }

if [ "${1:-}" = "--list" ] || [ "${1:-}" = "-List" ]; then
    for s in $SUITES; do echo "tst/$s.shoddy"; done
    exit 0
fi

# SAID OUT LOUD RATHER THAN FAILING OBSCURELY. Without a display these do not
# fail on an assertion, they fail somewhere inside GLFW with a message that
# explains nothing. Refusing up front is also what should stop anyone wiring
# this into a workflow: it CANNOT pass on a hosted runner, and a red step
# somebody later disables is worse than a gap that is written down.
# macOS always has a window server, so only the X11/Wayland case is checked.
if [ "$(uname -s)" != "Darwin" ] && [ -z "${DISPLAY:-}" ] && [ -z "${WAYLAND_DISPLAY:-}" ]; then
    echo 'STOPPED: no DISPLAY or WAYLAND_DISPLAY, and every suite here opens a window.' >&2
    echo '         Run it on a machine with a real display. It cannot pass in CI,' >&2
    echo '         which is why CI excludes these three by name.' >&2
    exit 1
fi

[ -x "$MILL" ] || { echo "STOPPED: $MILL is not built. Run: ./build.sh build" >&2; exit 1; }

FAILED=
for suite in $SUITES; do
    echo "==> tst/$suite.shoddy"
    "$MILL" --no-window run "tst/$suite.shoddy" || FAILED="$FAILED $suite"
done

echo
if [ -n "$FAILED" ]; then
    echo "FAILED:$FAILED" >&2
    exit 1
fi
echo "OK - all display suites passed."
