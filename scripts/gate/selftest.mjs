// MAINTAINER TOOL - proves the release harness's own guarantees.
//
//   node scripts/gate/selftest.mjs
//
// The driver's whole value is four promises: a hung step is killed and
// NAMED, a failed step prints its tail and its remedy, a passing step is
// quiet, and a receipt follows the SOURCE IT WAS EARNED AGAINST. A harness
// whose timeout quietly stopped working would look exactly like a harness
// that never needed it, so the guarantees are asserted here rather than
// assumed.
//
// The last of the four went unasserted while this header claimed it, and it
// was the one that broke: receipts were keyed to the commit, so `gate
// --resume` on a dirty tree skipped all 72 steps and reported a pass in a
// tenth of a second. Section 6 is that promise, tested.
//
// Runs in a few seconds and touches nothing in the tree.

import { runStep, printFailure, secs, treeKey, treeKeyFrom, keyIsClean } from './lib.mjs';

let failures = 0;
function check(name, cond, detail = '') {
    if (cond) console.log(`  ok    ${name}`);
    else { console.log(`  FAIL  ${name}${detail ? ` - ${detail}` : ''}`); failures++; }
}

const node = process.execPath;

console.log('\n== release harness self-test\n');

// 1. A step that hangs must be KILLED at its budget, not waited on.
//    This is the one that matters: an unbounded step is indistinguishable
//    from a slow one, and that is how a stall costs an afternoon.
{
    const started = Date.now();
    const r = await runStep({
        id: 'selftest-hang',
        title: 'a step that never returns',
        timeoutSec: 3,
        cmd: [node, '-e', 'setInterval(() => {}, 1000)'],
    });
    const took = Date.now() - started;
    check('a hung step times out', r.status === 'timeout', `got '${r.status}'`);
    check('and is killed near its budget, not left running',
        took < 15_000, `took ${secs(took)}`);
}

// 2. A failing step reports the code and keeps the tail for the message.
{
    const r = await runStep({
        id: 'selftest-fail',
        title: 'a step that exits non-zero',
        timeoutSec: 30,
        cmd: [node, '-e', 'console.log("the useful line"); process.exit(3)'],
    });
    check('a failing step is a failure', r.status === 'fail', `got '${r.status}'`);
    check('its exit code is preserved', r.code === 3, `got ${r.code}`);
    check('its output is kept for the report',
        r.tail.some((l) => l.includes('the useful line')));
}

// 3. stderr alone is NOT a failure. This is the rule that broke a release:
//    PowerShell treats a native program's stderr as fatal when the caller
//    captures it, and perfectly ordinary tools write to stderr all the time.
{
    const r = await runStep({
        id: 'selftest-stderr',
        title: 'a step that writes to stderr and exits 0',
        timeoutSec: 30,
        cmd: [node, '-e', 'console.error("machine seq.shoddy is not built - building"); process.exit(0)'],
    });
    check('stderr on a zero exit is still a pass', r.status === 'pass', `got '${r.status}'`);
}

// 4. A passing step says so and nothing else.
{
    const r = await runStep({
        id: 'selftest-pass',
        title: 'a step that succeeds',
        timeoutSec: 30,
        cmd: [node, '-e', 'process.exit(0)'],
    });
    check('a passing step passes', r.status === 'pass', `got '${r.status}'`);
}

// 5. The failure report carries the remedy, because a message that only says
//    what broke leaves the reader where they started.
{
    const step = {
        id: 'selftest-remedy', title: 'a failure with a remedy', timeoutSec: 30,
        cmd: [node, '-e', 'process.exit(1)'],
        remedy: 'THE-REMEDY-TEXT',
    };
    const r = await runStep(step);
    const written = [];
    const real = console.log;
    console.log = (s = '') => written.push(String(s));
    printFailure(r, step);
    console.log = real;
    check('the failure report prints the remedy',
        written.some((l) => l.includes('THE-REMEDY-TEXT')));
    check('and points at the log file',
        written.some((l) => l.includes('selftest-remedy.log')));
}

// 6. A receipt key follows the SOURCE, not the commit. Computed from its
//    inputs so the property can be proved without editing a file to do it -
//    which also means these hold whether the tree is clean or dirty today.
{
    const sha = 'a'.repeat(40);
    const none = () => null;
    const clean = treeKeyFrom(sha, '', '', none);
    check('a clean tree keys to the bare commit', clean === sha, `got '${clean}'`);
    check('and reads as clean', keyIsClean(clean));

    const dirty = treeKeyFrom(sha, ' M src/a.cs', '@@ -1 +1 @@\n-one\n+two', none);
    check('an uncommitted change keys to something else', dirty !== sha);
    check('which still names the commit it sits on', dirty.startsWith(sha));
    check('and does NOT read as clean, so publish cannot take it',
        !keyIsClean(dirty), `got '${dirty}'`);

    // THE ONE THAT MATTERS. Same commit, same set of modified paths, one
    // different byte of content: the old SHA-only key called these identical
    // and skipped every step.
    const edited = treeKeyFrom(sha, ' M src/a.cs', '@@ -1 +1 @@\n-one\n+three', none);
    check('editing a tracked file moves the key', edited !== dirty);

    // An untracked file is in no diff at all, so it has to be hashed by hand.
    const status = '?? src/new.cs';
    const before = treeKeyFrom(sha, status, '', () => Buffer.from('first'));
    const after = treeKeyFrom(sha, status, '', () => Buffer.from('second'));
    check('editing an UNTRACKED file moves the key too', before !== after);
    check('an unreadable untracked file is survivable, not fatal',
        typeof treeKeyFrom(sha, status, '', none) === 'string');

    check('the same source twice gives the same key',
        treeKeyFrom(sha, ' M src/a.cs', 'diff', none) === treeKeyFrom(sha, ' M src/a.cs', 'diff', none));

    // And the real repository agrees with itself, which is what the driver
    // actually calls.
    check('the live key is stable between calls', treeKey() === treeKey());
}

console.log('');
if (failures) {
    console.log(`SELF-TEST FAILED: ${failures} guarantee(s) not met\n`);
    process.exit(1);
}
console.log('ALL HARNESS GUARANTEES HOLD\n');
