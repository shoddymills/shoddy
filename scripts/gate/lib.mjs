// MAINTAINER TOOL - the plumbing every release step runs on.
//
// This file exists so that a release step cannot be written without the
// five properties that make a failure diagnosable by the person running it,
// rather than by whoever wrote the script:
//
//   a timeout        no step can hang; it fails, by name, with its elapsed
//   a log            per step, on disk, with the tail printed on failure
//   progress         a line while it runs, and a heartbeat when it is quiet
//   exit-code truth  native stderr is NEVER fatal - only the code is judged
//   a remedy         the failure message says what to run next
//
// The exit-code rule is not a preference. Windows PowerShell wraps every
// stderr line a native program writes in a NativeCommandError the moment a
// caller captures the stream, and under $ErrorActionPreference='Stop' that
// terminates a run which had exited 0. Judging the code and nothing else,
// on every platform, is what makes the two shells behave identically.
//
// Everything here is async: a hard timeout has to be able to fire WHILE a
// child runs, which a synchronous wait cannot do.

import { spawn, spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import {
    appendFileSync, existsSync, mkdirSync, readdirSync, readFileSync,
    rmSync, statSync, writeFileSync,
} from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

export const REPO = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
export const STATE = join(REPO, '.release-state');
export const LOGS = join(STATE, 'logs');

const isWin = process.platform === 'win32';
const tty = process.stdout.isTTY === true;

// ---- formatting -------------------------------------------------------

const C = {
    reset: tty ? '\x1b[0m' : '', dim: tty ? '\x1b[2m' : '',
    red: tty ? '\x1b[31m' : '', green: tty ? '\x1b[32m' : '',
    yellow: tty ? '\x1b[33m' : '', cyan: tty ? '\x1b[36m' : '',
    bold: tty ? '\x1b[1m' : '',
};

export function head(s) { console.log(`\n${C.bold}${C.cyan}== ${s}${C.reset}`); }
export function info(s) { console.log(`   ${s}`); }
export function warn(s) { console.log(`${C.yellow}   ! ${s}${C.reset}`); }
export function ok(s) { console.log(`${C.green}   OK ${s}${C.reset}`); }
export function bad(s) { console.log(`${C.red}   FAILED ${s}${C.reset}`); }
export function secs(ms) { return `${(ms / 1000).toFixed(1)}s`; }
export function mb(bytes) { return `${(bytes / 1024 / 1024).toFixed(0)} MB`; }

// ---- running one external command -------------------------------------

// Judged on its exit code alone. Output is teed to a log file and nothing is
// printed unless the step fails: a green release should be quiet enough to
// read at a glance.
export async function runStep(step) {
    mkdirSync(LOGS, { recursive: true });
    const logPath = join(LOGS, `${step.id}.log`);
    writeFileSync(logPath, `$ ${step.cmd.join(' ')}\n\n`);

    const started = Date.now();
    process.stdout.write(`   ${step.id.padEnd(34)} `);

    const res = await spawnBounded(step, logPath);
    const took = Date.now() - started;

    let status = 'pass';
    if (res.timedOut) {
        status = 'timeout';
        console.log(`${C.red}TIMED OUT${C.reset} after ${secs(took)}`);
    } else if (res.code !== 0) {
        status = 'fail';
        console.log(`${C.red}FAILED${C.reset} (exit ${res.code}, ${secs(took)})`);
    } else {
        console.log(`${C.green}ok${C.reset} ${C.dim}${secs(took)}${C.reset}`);
    }
    return { ...res, id: step.id, durationMs: took, status, logPath };
}

// spawn + hard timeout + kill the WHOLE TREE. A plain child.kill() leaves
// dotnet's MSBuild workers and test hosts alive, and those hold the locks
// that make the next attempt hang too - the exact failure this file exists
// to stop repeating.
function spawnBounded(step, logPath) {
    const timeoutMs = (step.timeoutSec ?? 600) * 1000;
    return new Promise((resolvePromise) => {
        const child = spawn(step.cmd[0], step.cmd.slice(1), {
            cwd: step.cwd ? join(REPO, step.cwd) : REPO,
            env: { ...process.env, ...(step.env ?? {}) },
            // stdin is INHERITED, not 'ignore'. Parts of this suite read the
            // console - InKeyTests is entirely about keystrokes - and a child
            // handed a closed stdin behaves differently from one holding the
            // terminal. Only the OUTPUT streams are piped, which is all the
            // logging needs.
            stdio: ['inherit', 'pipe', 'pipe'],
            detached: !isWin,
            shell: false,
        });

        let timedOut = false;
        const tail = [];
        const capture = (buf) => {
            const s = buf.toString();
            try { appendFileSync(logPath, s); } catch { /* log is best effort */ }
            for (const line of s.split(/\r?\n/)) {
                if (line.trim() === '') continue;
                tail.push(line);
                if (tail.length > 60) tail.shift();
            }
        };
        child.stdout.on('data', capture);
        child.stderr.on('data', capture);

        const timer = setTimeout(() => { timedOut = true; killTree(child); }, timeoutMs);
        // A quiet step that is still alive says so, so a long stage never
        // looks like the hang it is specifically built to rule out.
        const beat = setInterval(() => process.stdout.write('.'), 30_000);

        const finish = (code) => {
            clearTimeout(timer);
            clearInterval(beat);
            resolvePromise({ code: code ?? 1, timedOut, tail });
        };
        child.on('error', (e) => { capture(Buffer.from(String(e))); finish(127); });
        child.on('close', (code) => finish(code));
    });
}

function killTree(child) {
    try {
        if (isWin) spawnSync('taskkill', ['/pid', String(child.pid), '/T', '/F'], { stdio: 'ignore' });
        else process.kill(-child.pid, 'SIGKILL');
    } catch { /* already gone */ }
}

export function printFailure(result, step) {
    console.log('');
    bad(`${step.id} - ${step.title}`);
    if (result.status === 'timeout') {
        info(`Ran past its ${step.timeoutSec}s budget and was killed with its whole process tree.`);
    }
    info(`log: ${result.logPath}`);
    if (result.tail?.length) {
        console.log(`${C.dim}   --- last lines ---${C.reset}`);
        for (const l of result.tail.slice(-25)) console.log(`   ${l}`);
    }
    if (step.remedy) {
        console.log('');
        console.log(`${C.yellow}   what to do: ${step.remedy}${C.reset}`);
    }
    console.log('');
}

// ---- git ---------------------------------------------------------------

export function git(...args) {
    const r = spawnSync('git', args, { cwd: REPO, encoding: 'utf8' });
    return { code: r.status ?? 1, out: (r.stdout ?? '').trim(), err: (r.stderr ?? '').trim() };
}

export function headSha() { return git('rev-parse', 'HEAD').out; }
export function branchName() { return git('rev-parse', '--abbrev-ref', 'HEAD').out; }

// ---- receipts ----------------------------------------------------------
//
// A receipt says "these steps passed against THIS SOURCE". So the key has to
// name the source, and the commit SHA alone does not: it names the last thing
// you committed, not the thing on disk that the steps actually ran against.
//
// This file used to key on the SHA and say, right here, that a timestamp
// "does not know you edited a file". Correct, and one step short - neither
// does the SHA. `gate --resume` on a dirty tree skipped all 72 steps and
// printed a pass in a tenth of a second, describing a tree from before the
// morning's work. It is a lie a proof harness cannot afford to tell.
//
// So: the key is the SHA when the tree is clean, and the SHA plus a digest of
// everything that differs from it when it is not.
//
//   clean tree   ->  <sha>                     unchanged from before, which
//                                              is what keeps `publish` and
//                                              every existing receipt working
//   dirty tree   ->  <sha>+<12 hex of digest>  its own key; edit anything and
//                                              it moves, so resume re-runs
//
// The cost is real and is the point: after an edit, `--resume` has nothing to
// resume and runs the lot. When you know only one thing changed, say so
// yourself with `--only` or `--from`. That is a caller's claim, made by
// someone who can be right about it; `--resume` is the harness's own claim,
// and it may only make claims it can prove.
//
// `.release-state/` is gitignored, so the receipts cannot appear in the
// status that keys them - a fingerprint that changed every time it was
// written would invalidate itself on every step.

export function treeKey() {
    const sha = headSha();
    // -uall lists untracked FILES rather than their directories, so a new
    // file cannot hide behind an unchanged folder name.
    const status = git('status', '--porcelain', '--untracked-files=all').out;
    // Tracked edits: the content, not merely the fact that a path is modified.
    const diff = status ? git('diff', 'HEAD').out : '';
    return treeKeyFrom(sha, status, diff, (rel) => {
        try { return readFileSync(join(REPO, rel)); } catch { return null; }
    });
}

// The key computed from its inputs rather than from the repository, so the
// self-test can prove the property that matters - CHANGE ANYTHING AND THE KEY
// MOVES - without editing a file to do it. A guarantee this file cannot
// demonstrate is a guarantee it is only asserting.
export function treeKeyFrom(sha, status, diff, readUntracked) {
    if (!status) return sha;

    const h = createHash('sha256');
    h.update(status).update('\0').update(diff).update('\0');
    // Untracked files appear in no diff, so hash them by hand. Ignored paths
    // are absent from --porcelain, which keeps build output out of this.
    for (const line of status.split('\n')) {
        if (!line.startsWith('?? ')) continue;
        // A path git had to C-quote will not round-trip through this strip,
        // and neither will a file deleted between the status and the read.
        // The path itself is in the digest regardless, so a NEW file always
        // moves the key; only an edit to an oddly-named untracked file could
        // slip past, which is a far smaller hole than the one being closed.
        const rel = line.slice(3).trim().replace(/^"(.*)"$/, '$1');
        h.update(rel).update('\0');
        const bytes = readUntracked(rel);
        if (bytes) h.update(bytes);
    }
    return `${sha}+${h.digest('hex').slice(0, 12)}`;
}

// True when the key names a commit rather than a commit plus uncommitted
// work - which is the only state `publish` will tag.
export function keyIsClean(key) { return !key.includes('+'); }

const receiptFile = () => join(STATE, 'receipts.json');

export function readReceipts() {
    try { return JSON.parse(readFileSync(receiptFile(), 'utf8')); } catch { return {}; }
}

export function recordStep(key, stage, id, status, durationMs) {
    mkdirSync(STATE, { recursive: true });
    const all = readReceipts();
    all[key] ??= {};
    all[key][id] = { stage, status, durationMs, at: new Date().toISOString() };
    writeFileSync(receiptFile(), JSON.stringify(all, null, 2));
}

export function stepIsGreen(key, id) {
    return readReceipts()[key]?.[id]?.status === 'pass';
}

export function stageIsGreen(key, ids) {
    const r = readReceipts()[key] ?? {};
    return ids.length > 0 && ids.every((id) => r[id]?.status === 'pass');
}

// ---- cleanup -----------------------------------------------------------

// Build litter this repo's own tooling leaves behind, all of it regenerable.
// Named explicitly rather than globbed from the root, so `clean` can never
// reach something that is actually source.
const LITTER = [
    'src/Shoddy.Tests/TestResults',
    '_isamtest.tmp.idx',
];

export function removeLitter({ quiet = false } = {}) {
    let freed = 0, count = 0;
    const drop = (rel) => {
        const p = join(REPO, rel);
        if (!existsSync(p)) return;
        freed += sizeOf(p);
        rmSync(p, { recursive: true, force: true });
        count++;
        if (!quiet) info(`removed ${rel}`);
    };
    for (const rel of LITTER) drop(rel);
    // Whatever a weave dropped beside a source: untracked build output only,
    // taken from git so a tracked file can never match.
    const untracked = git('ls-files', '--others', '--exclude-standard', 'tst/', 'src/').out;
    for (const rel of untracked.split('\n').map((s) => s.trim()).filter(Boolean)) {
        if (/\.(dll|pdb|runtimeconfig\.json|deps\.json)$/.test(rel)) drop(rel);
    }
    return { count, freedBytes: freed };
}

function sizeOf(p) {
    try {
        const st = statSync(p);
        if (!st.isDirectory()) return st.size;
        let total = 0;
        for (const e of readdirSync(p)) total += sizeOf(join(p, e));
        return total;
    } catch { return 0; }
}

// Clear the toolchain's leftovers - GENTLY, and NEVER by force-killing
// MSBuild.
//
// This function used to `taskkill /T /F` every dotnet.exe that was not the
// editor's, on the theory that an abandoned run leaves workers holding locks.
// It does, but the cure was far worse: MSBuild's reusable worker nodes talk
// over NAMED PIPES, and a node killed outright leaves its pipe behind with
// nothing on the other end. The next build finds the dead endpoint, waits on
// it, and the caller sits there forever.
//
// That turned the tests into a coin toss. `dotnet test` passed 294/294 three
// times running; `shoddy clean` immediately followed by the SAME command
// crashed the test host at 46 tests, because several suites shell out to
// `dotnet build` and `dotnet run` and those nested builds met the dead pipes.
// The gate was calling this on the way in, so the gate reliably broke the
// tests it existed to run — a cleanup step that manufactured the failure it
// was there to prevent.
//
// `dotnet build-server shutdown` is the supported way: the servers close
// their own pipes and exit. Only genuine ORPHANS with nothing to clean up
// after them — a test host or a mill left behind by a killed run — are still
// forced, and MSBuild workers are never touched.
export function killStaleToolchain({ quiet = false } = {}) {
    // Graceful first, and this alone is usually the whole job.
    spawnSync('dotnet', ['build-server', 'shutdown'], { stdio: 'ignore', cwd: REPO });

    const killed = [];
    if (isWin) {
        // testhost/mill ONLY. Not dotnet.exe: that is where the MSBuild
        // workers live, and killing those is the fault described above.
        const ps = `Get-CimInstance Win32_Process -Filter "Name='testhost.exe' OR Name='mill.exe'"`
            + ` | Where-Object { $_.CommandLine -notlike '*vscode*' -and $_.CommandLine -notlike '*devenv*' }`
            + ` | ForEach-Object { $_.ProcessId }`;
        const r = spawnSync('powershell', ['-NoProfile', '-NonInteractive', '-Command', ps], { encoding: 'utf8' });
        for (const pid of (r.stdout ?? '').split(/\r?\n/).map((s) => s.trim()).filter(Boolean)) {
            spawnSync('taskkill', ['/pid', pid, '/T', '/F'], { stdio: 'ignore' });
            killed.push(pid);
        }
    } else {
        const r = spawnSync('pgrep', ['-f', 'testhost|bin/mill'], { encoding: 'utf8' });
        for (const pid of (r.stdout ?? '').split('\n').map((s) => s.trim()).filter(Boolean)) {
            if (Number(pid) === process.pid) continue;
            try { process.kill(Number(pid), 'SIGKILL'); killed.push(pid); } catch { /* gone */ }
        }
    }
    if (!quiet) {
        info(killed.length
            ? `build servers shut down; ${killed.length} orphan(s) cleared`
            : 'build servers shut down');
    }
    return killed.length;
}
