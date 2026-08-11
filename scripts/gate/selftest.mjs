// MAINTAINER TOOL - proves the release harness's own guarantees.
//
//   node scripts/gate/selftest.mjs
//
// The driver's whole value is four promises: a hung step is killed and
// NAMED, a failed step prints its tail and its remedy, a passing step is
// quiet, and receipts follow the commit. A harness whose timeout quietly
// stopped working would look exactly like a harness that never needed it,
// so the guarantees are asserted here rather than assumed.
//
// Runs in a few seconds and touches nothing in the tree.

import { runStep, printFailure, secs } from './lib.mjs';

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

console.log('');
if (failures) {
    console.log(`SELF-TEST FAILED: ${failures} guarantee(s) not met\n`);
    process.exit(1);
}
console.log('ALL HARNESS GUARANTEES HOLD\n');
