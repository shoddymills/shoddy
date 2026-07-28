#!/bin/sh
# ====================================================================
# run.sh -- replay the C original's transcripts against Mungo Caverns.
#
# The C original's tests/ directory holds ~107 recorded games: a .log of
# commands and a .chk of the exact output they produce.  Ninety-two of
# them pin the LCG with an in-band "seed" line, which makes them a far
# stronger oracle than golden files snapshotted from today's build --
# they catch a refactor that draws one extra random number, which is the
# specific way this port can break silently.
#
# Three adjustments make them comparable:
#
#   1. Mungo Caverns renamed the dwarves to curmudgeons.  The rename is
#      total and reversible, so this script substitutes in both
#      directions -- curmudgeon into the commands going in, dwarf back
#      out of the output.  Order matters: the longer forms first, or
#      "curmudgeons" becomes "dwarfs".
#
#   2. The game itself was renamed to Mungo Caverns, and the old names
#      appear in recorded output ("Welcome to ...!!", the cave the game
#      says is nearby, the suspend and resume messages, the version
#      line).  Two old names collapse into one new one, so this rename
#      is mapped forward on the expected side (to_mungo below) rather
#      than backward out of the output.  "Adventurer" and "Adventuredom"
#      are role words, not the name, and are shielded from the mapping.
#      Typed commands never contain the name, so the input side is
#      untouched.
#
#   3. "curmudgeon" is five characters longer than "dwarf", and the new
#      name is longer than either old one, so stored message text wraps
#      at different columns.  Both sides are reflowed
#      paragraph-by-paragraph before diffing.  See reflow.awk.
#
# Usage:
#   ./run.sh                 all transcripts
#   ./run.sh --smoke         the fast subset, for use between edits
#   ./run.sh axebear pirate_carry     named transcripts only
#   ./run.sh --keep          leave the work directory for inspection
#
# Set OPENADV to the C source tree if it is not beside the repo.
# ====================================================================

set -u

here=$(cd "$(dirname "$0")" && pwd)
mill="$here/../../../bin/mill.exe"
[ -x "$mill" ] || mill="$here/../../../bin/mill"
game="$here/../mungo-caverns.shoddy"

: "${OPENADV:=$here/../../../../open-adventure-master}"
src="$OPENADV/tests"

if [ ! -d "$src" ]; then
    echo "run.sh: no transcripts at $src" >&2
    echo "        set OPENADV to the open-adventure source tree" >&2
    exit 2
fi

# ---- the fast subset -------------------------------------------------
# Chosen to touch each subsystem a refactor is likely to disturb: the
# random stream (seeded curmudgeon and pirate games), travel conditions,
# the parser, hints, scoring, the endgame, and save/resume.
SMOKE="axebear dwarf wakedwarves2 ogre_no_dwarves pirate_carry pitfall
       plover magicwords illformed intransitivecarry hint_grate
       turnpenalties win430 endgame428 saveresume.1 saveresume.2"

keep=no
case "${1:-}" in
    --keep) keep=yes; shift ;;
esac

case "${1:-}" in
    --smoke) tests=$SMOKE; shift ;;
    "")      tests=$(ls "$src"/*.log | sed -e 's|.*/||' -e 's|\.log$||' | sort) ;;
    *)       tests="$*" ;;
esac

# ---- work directory --------------------------------------------------
# Tests run with the C's own working directory layout because badmagic
# feeds the game "../main.o" and expects to find a real file that is not
# a save.  A nested tests/ directory with a junk file beside it
# reproduces that without writing into the reference tree, which the
# save tests would otherwise litter with .adv files.
#
# The directory is per-run, keyed on the process id, so that two sweeps
# started at once cannot overwrite each other's input file.  They can:
# a full sweep takes eighteen minutes, which is long enough to start a
# second one while forgetting the first, and the failures that produces
# look like real divergence -- a game replying to somebody else's
# commands, seed line and all -- rather than like interference.
work=${TMPDIR:-/tmp}/mungo-caverns-transcripts.$$
rm -rf "$work"
mkdir -p "$work/tests"
if [ -f "$OPENADV/main.o" ]; then
    cp "$OPENADV/main.o" "$work/main.o"
else
    printf 'not a save file, and deliberately so\n' > "$work/main.o"
fi

# ---- tampered save fixtures -----------------------------------------
# The C ships a second binary, cheat, that writes a save file with one
# field forced to an impossible value; four transcripts resume from its
# output to check how the game reacts.  mungo-caverns has the same generator
# behind -cheat, so the fixtures are written here in mungo-caverns's own
# save format rather than copied from the C -- the two formats are not
# byte-compatible and were never meant to be.
cheat() {
    ( cd "$work/tests" && "$mill" run "$game" -cheat "$@" >/dev/null 2>&1 )
}
cheat -d -900   -o cheat_numdie.adv
cheat -d -1000  -o cheat_numdie1000.adv
cheat -d 2000   -o cheat_savetamper.adv
cheat -v -1337  -o resume_badversion.adv
cheat -s -1000  -o thousand_saves.adv
cheat -t -1000  -o thousand_turns.adv
cheat -l -1000  -o thousand_limit.adv

# ---- the rename, both directions ------------------------------------
to_curmudgeon() {
    sed -e 's/Dwarvish/Curmudgeonish/g'   -e 's/dwarvish/curmudgeonish/g' \
        -e 's/DWARVES/CURMUDGEONS/g'      -e 's/Dwarves/Curmudgeons/g' \
        -e 's/dwarves/curmudgeons/g'      -e 's/DWARF/CURMUDGEON/g' \
        -e 's/Dwarf/Curmudgeon/g'         -e 's/dwarf/curmudgeon/g'
}
to_dwarf() {
    sed -e 's/Curmudgeonish/Dwarvish/g'   -e 's/curmudgeonish/dwarvish/g' \
        -e 's/CURMUDGEONS/DWARVES/g'      -e 's/Curmudgeons/Dwarves/g' \
        -e 's/curmudgeons/dwarves/g'      -e 's/CURMUDGEON/DWARF/g' \
        -e 's/Curmudgeon/Dwarf/g'         -e 's/curmudgeon/dwarf/g'
}

# The game-name rename, applied forward to the expected text.  Shield the
# role words first or "Adventurer" becomes "Mungo Cavernsr"; the version
# line loses its trailing URL because the port no longer prints one.
to_mungo() {
    sed -e 's/Adventurer/@@ROLE@@/g'      -e 's/Adventuredom/@@REALM@@/g' \
        -e 's|Open Adventure \(.*\) - http://www\.catb\.org/esr/open-adventure/|Mungo Caverns \1|' \
        -e 's/Open Adventure/Mungo Caverns/g' \
        -e 's/Colossal Cave/Mungo Caverns/g' \
        -e 's/Adventure/Mungo Caverns/g' \
        -e 's/@@ROLE@@/Adventurer/g'      -e 's/@@REALM@@/Adventuredom/g'
}
normalise() { awk -f "$here/reflow.awk" | sed 's/  */ /g'; }

# ---- replay ----------------------------------------------------------
pass=0; fail=0; failed=''
for t in $tests; do
    log="$src/$t.log"
    chk="$src/$t.chk"
    if [ ! -f "$log" ] || [ ! -f "$chk" ]; then
        echo "?? $t (no such transcript)"
        fail=$((fail + 1)); failed="$failed $t"
        continue
    fi

    # The C's Makefile reads the game's command-line options out of the
    # log itself, so -o, -r and -l travel with the transcript that needs
    # them.  mungo-caverns accepts the same three.
    opts=$(sed -n '/^#options:/s///p' "$log")

    to_curmudgeon < "$log" > "$work/tests/$t.input"
    ( cd "$work/tests" && "$mill" run "$game" $opts < "$t.input" 2>&1 ) \
        | to_dwarf | normalise > "$work/$t.got"
    to_mungo < "$chk" | normalise > "$work/$t.exp"

    if diff -q "$work/$t.exp" "$work/$t.got" >/dev/null 2>&1; then
        pass=$((pass + 1))
    else
        fail=$((fail + 1)); failed="$failed $t"
        echo "FAIL $t"
    fi
done

echo
echo "passed $pass, failed $fail"
if [ $fail -gt 0 ]; then
    echo "failing:$failed"
    echo "inspect: diff $work/NAME.exp $work/NAME.got"
    keep=yes
fi
[ "$keep" = yes ] || rm -rf "$work"
[ $fail -eq 0 ]
