// MAINTAINER TOOL - the release, driven from one place.
//
//   node scripts/gate/driver.mjs <command> [version] [flags]
//
// Commands
//   doctor                is this machine fit to release?
//   preflight [version]   read-only: would a release pass right now?
//   gate                  the expensive proof: build + every test
//   package <version>     version bump, stage, .vsix
//   publish <version>     branch, commit, tag, push  (MUTATES HISTORY)
//   release <version>     all four, stopping at the first failure
//   status                what is green for the current commit
//   selftest              prove the harness still keeps its own guarantees
//   clean                 shut build servers down, clear orphans, remove litter
//                         DELIBERATE: it cold-starts the MSBuild cache, which
//                         the C# suite is sensitive to. Use when wedged.
//
// Flags
//   --resume        skip steps already green for THIS TREE - the commit plus
//                   whatever is uncommitted on top of it. Edit anything and
//                   there is nothing to resume; use --only or --from instead.
//   --only <id>     run one step by id
//   --from <id>     start at that step and continue
//   --split-tests   run the C# suite as two filtered passes. NOT the default:
//                   the suite passes as one command since the pipe deadlock in
//                   ProcessRun was fixed. Kept as an escape hatch for bisecting
//                   a future crash that takes the host down with it.
//   --json          machine-readable summary on stdout
//   --yes           do not prompt (required from a non-interactive shell)
//   --keep-litter   do not clean up afterwards (for debugging a failure)
//   --allow-dirty   let a release-strict check run on an uncommitted tree
//
// `preflight` on its own tolerates a dirty tree and says so; `preflight
// <version>` and `release` do not, because those are about a commit that is
// going to be tagged.
//
// There are two rules worth knowing before reading the code:
//
//   Receipts are keyed to the WORKING TREE - the commit, plus a digest of
//   anything uncommitted on top of it. `gate --resume` skips what is already
//   green for that exact source, and `publish` REFUSES without a green gate
//   for the clean commit it is about to tag. A release therefore cannot
//   discover a problem at tag time, when the tag is already public.
//
//   Nothing here judges a native program on anything but its exit code.

import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import {
    REPO, STATE, bad, branchName, git, head, headSha, info, keyIsClean,
    killStaleToolchain, mb, ok, printFailure, readReceipts, recordStep,
    removeLitter, runStep, secs, stageIsGreen, stepIsGreen, treeKey, warn,
} from './lib.mjs';
import { gateSteps, packageSteps, preflightSteps } from './steps.mjs';

const argv = process.argv.slice(2);
const cmd = argv[0] ?? 'help';

// Flags that consume the NEXT argument. Without this list a positional scan
// reads the value as the version - `preflight --only docs` tried to release
// a version called "docs".
const VALUED = new Set(['only', 'from']);

const flag = (name) => argv.includes(`--${name}`);
const flagVal = (name) => {
    const i = argv.indexOf(`--${name}`);
    return i >= 0 ? argv[i + 1] : undefined;
};

const positional = (() => {
    const out = [];
    for (let i = 1; i < argv.length; i++) {
        const a = argv[i];
        if (a.startsWith('--')) {
            if (VALUED.has(a.slice(2))) i++;
            continue;
        }
        out.push(a);
    }
    return out;
})();

const opts = {
    resume: flag('resume'),
    only: flagVal('only'),
    from: flagVal('from'),
    splitTests: flag('split-tests'),
    json: flag('json'),
    yes: flag('yes'),
    keepLitter: flag('keep-litter'),
    allowDirty: flag('allow-dirty'),
};

// ---- checks that need no subprocess ------------------------------------

// A dirty tree is only fatal when something downstream will TAG this commit.
// Bare `preflight` is meant to be runnable on an ordinary working day, and on
// an ordinary working day the tree is dirty - a gate you cannot run while
// working is a gate you run once, at the end, which is the habit this whole
// driver exists to break.
// The two files a release BUMPS on its way past. `package` rewrites them -
// vsce rewrites package.json, and shoddy-release rewrites the props - and
// then `shoddy-release` stages and commits exactly these two. So at publish
// time they are the release's own output, not somebody's uncommitted work.
//
// Without this, `shoddy release <version>` could not complete at all: its
// own package stage dirtied the tree that its own publish stage then
// refused. Preflight does NOT allow them, and must not - it runs before
// package, where a modified package.json really is a person's edit.
const RELEASE_BUMPS = ['vscode-shoddy/package.json', 'Directory.Build.props'];

function checkGitState({ strict, allow = [] }) {
    const problems = [], notes = [];
    const dirty = git('status', '--porcelain').out;
    if (dirty) {
        const lines = dirty.split('\n').filter(Boolean);
        const untrackedBuild = lines
            .filter((l) => l.startsWith('??') && /\.(dll|pdb|runtimeconfig\.json)$/.test(l));
        if (untrackedBuild.length) {
            problems.push(`untracked build output in the tree (${untrackedBuild.length} file(s)) `
                + '- run `shoddy clean`, or a git add -A sweeps it into a commit');
        }
        const real = lines
            .filter((l) => !l.startsWith('??'))
            .filter((l) => !allow.includes(l.slice(3).trim().replace(/\\/g, '/')));
        if (real.length) {
            const msg = `${real.length} uncommitted change(s)`;
            if (strict) problems.push(`${msg} - commit or stash before releasing`);
            else notes.push(`${msg} - fine for a check, not for a release`);
        }
    }
    if (git('rev-parse', '-q', '--verify', 'MERGE_HEAD').code === 0) {
        problems.push('a merge is in progress - finish or abort it');
    }

    // A NEW .sh THAT GIT HAS NEVER SEEN.
    //
    // verify-permissions.js is thorough about the executable bit and checks
    // only TRACKED files, so a shell script that has just been written is
    // invisible to it: the gate passes, the script is committed 100644, and
    // it fails on the first Linux runner that honours the bit literally -
    // which is how v1.8.0 shipped four of them and took down the Release
    // workflow with "Permission denied", exit 126.
    //
    // The window is between writing the file and adding it, and it is
    // exactly the moment nobody thinks about the bit. Named here with the
    // command that fixes it, because `git update-index --chmod=+x` does NOT
    // work on a path git does not yet have - it answers "cannot add to the
    // index - missing --add option?", which reads like a different problem.
    const untrackedSh = git('ls-files', '--others', '--exclude-standard').out
        .split('\n').map((s) => s.trim())
        .filter((f) => f.endsWith('.sh'));
    for (const f of untrackedSh) {
        notes.push(`${f} is a new shell script git has not seen - add it with the bit set: `
            + `git add --chmod=+x ${f}`);
    }

    return { problems, notes };
}

function checkVersion(version) {
    const problems = [];
    if (!version) return problems;
    if (!/^\d+\.\d+\.\d+$/.test(version)) {
        problems.push(`version must be exactly X.Y.Z (got '${version}')`);
        return problems;
    }
    const notes = join(REPO, 'release-notes', `v${version}.md`);
    if (!existsSync(notes)) {
        problems.push(`release-notes/v${version}.md is missing - the Release workflow reads only `
            + 'what the TAGGED commit contains, so notes written later are invisible to it');
    }
    if (git('rev-parse', '-q', '--verify', `refs/tags/v${version}`).code === 0) {
        problems.push(`tag v${version} already exists locally`);
    }
    const remoteTag = spawnSync('git', ['ls-remote', '--tags', 'origin', `refs/tags/v${version}`],
        { cwd: REPO, encoding: 'utf8' });
    if ((remoteTag.stdout ?? '').trim()) problems.push(`tag v${version} already exists on origin`);
    return problems;
}

function checkDoctor() {
    const problems = [];
    // shell:false deliberately - these are real executables on every platform,
    // and Node deprecates passing args through a shell because they are
    // concatenated rather than escaped.
    const need = (bin, args, why) => {
        const r = spawnSync(bin, args, { encoding: 'utf8' });
        if (r.status !== 0) problems.push(`${bin} not usable - ${why}`);
        return (r.stdout ?? '').trim();
    };
    const dotnet = need('dotnet', ['--version'], 'the toolchain cannot build');
    const node = process.version;
    need('git', ['--version'], 'nothing here works without it');

    try {
        const wanted = JSON.parse(readFileSync(join(REPO, 'global.json'), 'utf8')).sdk?.version;
        if (wanted && dotnet && dotnet.split('.')[0] !== wanted.split('.')[0]) {
            problems.push(`dotnet ${dotnet} does not match global.json's ${wanted} major`);
        }
        info(`dotnet ${dotnet}   node ${node}   global.json wants ${wanted}`);
    } catch { /* no global.json is not our problem to invent */ }

    // The gate now runs the MAUI host lane, which needs a workload rather
    // than just an SDK. Asked here so the answer arrives in three seconds
    // rather than twenty minutes into a gate.
    if (process.platform === 'win32') {
        const r = spawnSync('dotnet', ['workload', 'list'], { encoding: 'utf8' });
        if (!/maui-windows/.test(r.stdout ?? '')) {
            problems.push('the maui-windows workload is not installed - the gate runs the '
                + 'MAUI host lane. Install it: dotnet workload install maui-windows');
        } else {
            info('maui-windows workload present');
        }
    }

    const free = freeSpaceBytes(REPO);
    if (free !== null) {
        info(`free disk: ${mb(free)}`);
        if (free < 2 * 1024 * 1024 * 1024) {
            problems.push(`under 2 GB free - a hang dump alone can be 500 MB`);
        }
    }
    return problems;
}

function freeSpaceBytes(p) {
    try {
        if (process.platform === 'win32') {
            const drive = p.slice(0, 2);
            const r = spawnSync('powershell', ['-NoProfile', '-NonInteractive', '-Command',
                `(Get-PSDrive ${drive[0]}).Free`], { encoding: 'utf8' });
            const n = Number((r.stdout ?? '').trim());
            return Number.isFinite(n) ? n : null;
        }
        const r = spawnSync('df', ['-k', p], { encoding: 'utf8' });
        const line = (r.stdout ?? '').trim().split('\n').pop() ?? '';
        const n = Number(line.split(/\s+/)[3]) * 1024;
        return Number.isFinite(n) ? n : null;
    } catch { return null; }
}

// ---- running a list of steps -------------------------------------------

async function runSteps(steps, stage, key) {
    let list = steps;
    // REFUSED, NOT RECONCILED. --from used to overwrite the list --only had
    // just built, so `--only x --from y` ran everything from y and silently
    // ignored --only entirely: the caller asked for one step and got sixty,
    // with nothing said. Two contradictory instructions are a question the
    // tool cannot answer, and guessing at one of them is the worst of the
    // available answers.
    if (opts.only && opts.from) {
        bad('--only and --from contradict each other: one names a single step, the '
            + 'other names all of them from a point onwards. Pass one.');
        process.exit(2);
    }
    if (opts.only) {
        list = steps.filter((s) => s.id === opts.only);
        if (list.length === 0) { bad(`no step called '${opts.only}'`); process.exit(2); }
    }
    if (opts.from) {
        const i = steps.findIndex((s) => s.id === opts.from);
        if (i < 0) { bad(`no step called '${opts.from}'`); process.exit(2); }
        list = steps.slice(i);
    }

    const results = [];
    let skipped = 0;
    for (const step of list) {
        if (opts.resume && stepIsGreen(key, step.id)) {
            skipped++;
            continue;
        }
        const r = await runStep(step);
        recordStep(key, stage, step.id, r.status, r.durationMs);
        results.push({ id: step.id, status: r.status, durationMs: r.durationMs });
        if (r.status !== 'pass') {
            printFailure(r, step);
            return { results, skipped, failed: step.id };
        }
    }
    if (skipped) info(`${skipped} step(s) already green for this tree, skipped`);
    return { results, skipped, failed: null };
}

// ---- stages ------------------------------------------------------------

// Every key starts with the full SHA, so the first seven characters are the
// short commit whether or not a dirty-tree digest is appended to it.
const shortSha = (key) => key.slice(0, 7);
const treeNote = (key) => (keyIsClean(key) ? '' : ', plus uncommitted changes');

async function doPreflight(version, key, { strict }) {
    head(`preflight  (commit ${shortSha(key)}, branch ${branchName()}${treeNote(key)})`);

    const gitState = checkGitState({ strict: strict && !opts.allowDirty });
    const problems = [...checkDoctor(), ...gitState.problems, ...checkVersion(version)];
    for (const n of gitState.notes) warn(n);
    if (problems.length) {
        console.log('');
        for (const p of problems) bad(p);
        console.log('');
        return { ok: false, failed: 'preconditions' };
    }
    ok(version
        ? `environment, git state and v${version} are coherent`
        : 'environment and git state are sound');

    const r = await runSteps(preflightSteps(), 'preflight', key);
    return { ok: !r.failed, failed: r.failed, results: r.results, skipped: r.skipped };
}

async function doGate(key) {
    head(`gate  (commit ${shortSha(key)}${treeNote(key)})`);

    // The gate says the same thing about the tree that preflight does, and
    // for the same reason: a gate is meant to be runnable mid-morning on a
    // dirty tree. What it may never do is leave the reader thinking a green
    // gate on uncommitted work is the green gate `publish` will ask for. It
    // is not - that one is keyed to the clean commit.
    for (const n of checkGitState({ strict: false }).notes) warn(n);

    // THE GATE DOES NOT TOUCH THE BUILD SERVERS, and that is the opposite of
    // what it did first. Clearing them on the way in seemed obviously right -
    // an abandoned run leaves workers behind - and it reliably broke the very
    // tests the gate exists to run.
    //
    // Several suites shell out to `dotnet build` and `dotnet run`. With the
    // servers warm those are seconds; with them cold they are a full restore
    // and build that prints nothing to vstest while it works, so blame's
    // inactivity timer fires and the host is reported as CRASHED with two
    // thirds of the suite never run. Measured, not assumed: `dotnet test`
    // passed 294/294 three times in a row, and the same command run straight
    // after clearing the servers died at 46 tests. A forced kill and a polite
    // `build-server shutdown` both did it, so it is the cold start and not
    // the manner of it.
    //
    // `shoddy clean` still exists for when something really is wedged. It is
    // a deliberate act with a known cost, not a courtesy performed silently
    // before every run.
    const steps = gateSteps({ splitDotnetTest: opts.splitTests });
    info(`${steps.length} steps`);
    const r = await runSteps(steps, 'gate', key);
    if (!opts.keepLitter) {
        const { count, freedBytes } = removeLitter({ quiet: true });
        if (count) info(`cleaned ${count} build artefact(s), ${mb(freedBytes)}`);
    }
    return {
        ok: !r.failed, failed: r.failed, results: r.results,
        skipped: r.skipped, stepIds: steps.map((s) => s.id),
    };
}

async function doPackage(version, key) {
    head(`package  ${version}`);
    const r = await runSteps(packageSteps(version), 'package', key);
    if (r.failed) return { ok: false, failed: r.failed };
    const vsix = join(REPO, 'vscode-shoddy', `vscode-shoddy-${version}.vsix`);
    if (!existsSync(vsix)) {
        bad(`build reported success but ${vsix} is not there`);
        return { ok: false, failed: 'vsix-missing' };
    }
    ok(`vscode-shoddy-${version}.vsix`);
    return { ok: true, results: r.results, skipped: r.skipped };
}

// The only stage that mutates history. It does NOT reimplement the tag/push/
// merge sequence: shoddy-release.* already owns that, it is tested, and a
// second copy of a routine that pushes tags is exactly the kind of duplicate
// this driver exists to avoid. What publish adds is the guard in FRONT of it.
async function doPublish(version) {
    head(`publish  v${version}`);

    // THE TREE IS CHECKED BEFORE THE RECEIPT, and the order is load-bearing.
    // A receipt is keyed to the source it was earned against, so on a dirty
    // tree it is keyed to the uncommitted work - never to the commit about to
    // be tagged. Asking for the receipt first would therefore answer "no green
    // gate", sending the reader off to run a gate that cannot fix the real
    // problem. Say what is actually wrong: commit or stash.
    const strict = checkGitState({ strict: true, allow: RELEASE_BUMPS });
    if (strict.problems.length) {
        for (const p of strict.problems) bad(p);
        return { ok: false, failed: 'dirty-tree' };
    }

    // Clean but for the version bump, so the tree key is the commit - which
    // is what makes the receipt below a statement about the code that is
    // going to carry the tag. If package HAS bumped, the key has moved and
    // the receipt is looked up under the commit deliberately: the gate
    // proved the source, and a version string is not the source.
    const sha = headSha();
    const gateIds = gateSteps({ splitDotnetTest: opts.splitTests }).map((s) => s.id);
    if (!stageIsGreen(sha, gateIds)) {
        bad(`no green gate for commit ${sha.slice(0, 7)}`);
        info('publish will not tag code that has not been proved at this exact commit.');
        info('Run:  shoddy gate --resume');
        return { ok: false, failed: 'no-receipt' };
    }
    ok(`gate is green for ${sha.slice(0, 7)}`);

    console.log('');
    info('This will, through scripts/shoddy-release:');
    info(`  1. cut release/V${version} from an up-to-date main`);
    info(`  2. build and test from clean`);
    info(`  3. commit the version bump and push the release branch`);
    info(`  4. tag v${version} and push it  <-- the release ships here`);
    info(`  5. merge the release branch --no-ff into main and push`);
    console.log('');

    if (!opts.yes) {
        bad('publish pushes and tags, so it needs --yes said out loud');
        info(`Re-run:  shoddy publish ${version} --yes`);
        return { ok: false, failed: 'unconfirmed' };
    }

    const script = process.platform === 'win32'
        ? ['powershell', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
            '-File', 'scripts/shoddy-release.ps1', version, '-Yes']
        : ['./scripts/shoddy-release.sh', version, '-y'];

    const r = await runStep({
        id: 'publish', title: `release v${version}`, timeoutSec: 3600, cmd: script,
        remedy: 'The tag may already be public. Fix forward with a new patch version '
            + 'rather than moving a tag anyone may have fetched.',
    });
    recordStep(sha, 'publish', 'publish', r.status, r.durationMs);
    if (r.status !== 'pass') {
        printFailure(r, { id: 'publish', title: `release v${version}`, timeoutSec: 3600 });
        return { ok: false, failed: 'publish' };
    }
    ok(`v${version} tagged and pushed - watch Actions -> Release`);
    return { ok: true, results: [{ id: 'publish', status: 'pass', durationMs: r.durationMs }] };
}

// ---- reporting ---------------------------------------------------------

function summarise(stages, startedAt) {
    const total = Date.now() - startedAt;
    head('summary');
    for (const [name, res] of stages) {
        const n = res.results?.length ?? 0;
        const skipped = res.skipped ?? 0;
        // A stage that ran NOTHING does not get to say PASS. It passed
        // earlier, against this same tree, and that is a different sentence -
        // the one `--resume` was reading out as though it had just proved it.
        const mark = res.ok ? (n === 0 && skipped > 0 ? 'SKIP' : 'PASS') : 'FAIL';
        const detail = res.failed ? `  first failure: ${res.failed}`
            : skipped ? `  (${skipped} already green for this tree)` : '';
        console.log(`   ${mark.padEnd(5)} ${name.padEnd(10)} ${n} step(s) run${detail}`);
    }
    info(`total ${secs(total)}`);
    if (opts.json) {
        console.log(JSON.stringify({
            sha: headSha(), branch: branchName(), totalMs: total,
            stages: Object.fromEntries(stages.map(([n, r]) => [n, { ok: r.ok, failed: r.failed ?? null }])),
        }, null, 2));
    }
}

function doStatus() {
    const key = treeKey();
    head(`status  (commit ${shortSha(key)}, branch ${branchName()}${treeNote(key)})`);
    const r = readReceipts()[key];
    if (!r) {
        info(keyIsClean(key)
            ? 'nothing recorded for this commit yet'
            : 'nothing recorded for this tree yet - it has changed since the last gate');
        return;
    }
    const byStage = {};
    for (const [id, v] of Object.entries(r)) {
        byStage[v.stage] ??= { pass: 0, fail: 0 };
        byStage[v.stage][v.status === 'pass' ? 'pass' : 'fail']++;
    }
    for (const [stage, c] of Object.entries(byStage)) {
        console.log(`   ${stage.padEnd(10)} ${c.pass} green${c.fail ? `, ${c.fail} not` : ''}`);
    }
    const gateIds = gateSteps({ splitDotnetTest: opts.splitTests }).map((s) => s.id);
    console.log('');
    if (!stageIsGreen(key, gateIds)) {
        info('gate is not fully green for this tree - publish would refuse');
    } else if (keyIsClean(key)) {
        ok('gate is green - publish would be allowed');
    } else {
        // Green, and genuinely so - but earned against uncommitted work, which
        // is not a thing that can be tagged. Saying "publish would be allowed"
        // here would be the old lie wearing a new key.
        ok('gate is green for the tree as it stands, including uncommitted work');
        info('publish still refuses: it tags a commit, so commit first and gate that.');
    }
}

// ---- main --------------------------------------------------------------

async function main() {
    const startedAt = Date.now();
    const version = positional[0];
    if (!headSha()) { bad('not inside a git repository'); process.exit(2); }
    // Taken ONCE, before any step runs. A step that writes into the tree -
    // `package` bumps versions - would otherwise move the key underneath its
    // own run and record its receipts against a source that never existed.
    const key = treeKey();

    switch (cmd) {
        case 'doctor': {
            head('doctor');
            const problems = checkDoctor();
            if (problems.length) { for (const p of problems) bad(p); process.exit(1); }
            ok('this machine can build and release');
            break;
        }
        case 'clean': {
            head('clean');
            killStaleToolchain();
            const { count, freedBytes } = removeLitter();
            ok(count ? `removed ${count} item(s), ${mb(freedBytes)} freed` : 'nothing to clean');
            break;
        }
        case 'status': doStatus(); break;
        case 'selftest': {
            // Proves the harness's own promises - a timeout that quietly
            // stopped working would look just like one never needed.
            const r = await runStep({
                id: 'selftest', title: 'release harness self-test', timeoutSec: 120,
                cmd: [process.execPath, 'scripts/gate/selftest.mjs'],
                remedy: 'The driver cannot keep its own guarantees. Read the log before trusting a gate.',
            });
            if (r.status !== 'pass') { console.log(r.tail.join('\n')); process.exit(1); }
            ok('the harness keeps its guarantees');
            break;
        }

        case 'preflight': {
            const r = await doPreflight(version, key, { strict: Boolean(version) });
            summarise([['preflight', r]], startedAt);
            process.exit(r.ok ? 0 : 1);
        }
        case 'gate': {
            const r = await doGate(key);
            summarise([['gate', r]], startedAt);
            process.exit(r.ok ? 0 : 1);
        }
        case 'package': {
            if (!version) { bad('package needs a version: shoddy package 2.0.3'); process.exit(2); }
            const r = await doPackage(version, key);
            summarise([['package', r]], startedAt);
            process.exit(r.ok ? 0 : 1);
        }
        case 'publish': {
            if (!version) { bad('publish needs a version: shoddy publish 2.0.3'); process.exit(2); }
            const r = await doPublish(version);
            summarise([['publish', r]], startedAt);
            process.exit(r.ok ? 0 : 1);
        }
        case 'release': {
            if (!version) { bad('release needs a version: shoddy release 2.0.3'); process.exit(2); }
            const stages = [];
            const pre = await doPreflight(version, key, { strict: true });
            stages.push(['preflight', pre]);
            if (!pre.ok) { summarise(stages, startedAt); process.exit(1); }
            const gate = await doGate(key);
            stages.push(['gate', gate]);
            if (!gate.ok) { summarise(stages, startedAt); process.exit(1); }
            const pkg = await doPackage(version, key);
            stages.push(['package', pkg]);
            if (!pkg.ok) { summarise(stages, startedAt); process.exit(1); }
            const pub = await doPublish(version);
            stages.push(['publish', pub]);
            summarise(stages, startedAt);
            process.exit(pub.ok ? 0 : 1);
        }
        default: {
            // The LEADING comment block only. Filtering the whole file for
            // '//' printed every internal section header too, which buried
            // the usage in commentary about the code.
            const lines = readFileSync(new URL(import.meta.url), 'utf8').split('\n');
            const usage = [];
            for (const l of lines) {
                if (!l.startsWith('//')) break;
                usage.push(l.replace(/^\/\/ ?/, ''));
            }
            console.log(usage.join('\n'));
            if (cmd !== 'help') bad(`unknown command '${cmd}'`);
            process.exit(cmd === 'help' ? 0 : 2);
        }
    }
}

main().catch((e) => { bad(String(e?.stack ?? e)); process.exit(1); });
