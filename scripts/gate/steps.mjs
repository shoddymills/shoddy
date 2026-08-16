// MAINTAINER TOOL - the release, as data.
//
// Every step is a row: an id, a title, the command, a TIMEOUT and a REMEDY.
// Keeping them declarative is what guarantees the properties in lib.mjs are
// universal - you cannot add a step that quietly has no time budget, and you
// cannot fail one without the message saying what to do next.
//
// Timeouts are generous multiples of the observed time, not guesses at it:
// the budget exists to turn a HANG into a named failure, not to police a
// slow machine.

import { join } from 'node:path';

const MILL = process.platform === 'win32' ? 'bin\\mill.exe' : 'bin/mill';

// The machine suites, by name rather than by glob: a suite that stops being
// listed is a suite that stops running, and that is how nine of them once
// stopped running unnoticed — and how seedterminaltest stopped again, by
// missing from every copy of the list at once. THIS LIST IS THE ONLY COPY
// NOW: build.ps1 and build.sh read it through node, and verify-suites.js
// (a preflight gate) proves every tst/*.shoddy is either run somewhere in
// this file or excluded below with its reason written down.
export const MACHINE_SUITES = [
    'csvtest', 'cuttletest', 'htmltest', 'jsontest', 'nettest', 'neuraltest',
    'randomtest', 'reckonertest', 'regextest', 'seedtest', 'seedbooltest',
    'seedbuiltintest',
    'seedengtest', 'seedbuzzertest', 'seedfintest', 'seedhttpstest',
    'seedisamtest', 'seedmiptest', 'seedneuraltest', 'seednettest',
    'seedreciotest', 'seedregextest', 'seedsimplextest', 'seedsparsetest',
    'seedterminaltest',
    'seedvt100test', 'shakertest', 'xmltest', 'seedgeotest',
    'seedjuliantest', 'seedephemeristest', 'seedsinqtest',
];

// tst/ files deliberately run by nothing, each with the reason. A name
// here is a decision on the record; a name in neither place fails the
// suites preflight gate.
export const TST_EXCLUSIONS = {
    'seedscribblertest': 'needs a real display: GLFW has no platform on hosted runners; run by hand',
    'seedturtletest': 'needs a real display: GLFW has no platform on hosted runners; run by hand',
    'seedplottertest': 'needs a real display: GLFW has no platform on hosted runners; run by hand',
    'buzzer-demo': 'interactive demo, not a suite',
    'html-demo': 'interactive demo, not a suite',
    'https-demo': 'interactive demo, not a suite',
    'plotter-demo': 'interactive demo, not a suite',
    'scribbler-demo': 'interactive demo, not a suite',
    'shaker-demo': 'interactive demo, not a suite',
    'turtle-demo': 'interactive demo, not a suite',
    'examples': 'a grab-bag of worked defs, not a suite',
    'simplex': 'a worked demo of the simplex machine — prints, asserts nothing',
    'gradebook': 'the guide\'s interactive worked example; Input blocks an unattended run',
    'isamfixture': 'shared record shape Included by the isam suites; no Main',
};

// The numerics and conformance suites that run before the machine sweep,
// in build.ps1's own order.
export const CORE_SUITES = [
    ['libtest', 'the golden library suite', 300],
    ['builtinsurfacetest', 'builtin dispatch against the seeded dictionary', 300],
    ['fin', 'finance, by known answers', 300],
    ['eng', 'engineering maths, by known answers', 300],
    ['lin', 'linear algebra, by residuals against the identities', 600],
    ['alg', 'symbolic algebra, by property', 300],
    ['bool', 'the binary layer, by identity', 300],
    ['sparse', 'sparse kernels against dense counterparts', 300],
    ['mip', 'branch and bound', 300],
    ['geo', 'the sphere, against independently worked distances', 300],
    ['julian', 'the calendar, against public epoch facts', 300],
    ['ephemeris', 'the sky, against the eclipse and the oppositions', 300],
    ['clock', 'the timing capability, by consistency not by moment', 300],
    ['sinq', 'querying, against answers a wrong sort cannot reach — and at a size that catches a stack-bound one', 300],
];

export const MILLS = [
    'demographics', 'devils-dust', 'emley-moor', 'halifax', 'invaders', 'iris',
    'mungo-caverns', 'oregon', 'pac-vt100', 'simplex-from-mps', 'sparky',
    'tally', 'weather-glass',
];

// ---- preflight ---------------------------------------------------------
// Read-only. Answers "would a release pass right now?" without building
// anything, so it is cheap enough to run on any ordinary day.

export function preflightSteps() {
    return [
        {
            id: 'docs', title: 'docs match the sources', timeoutSec: 300,
            cmd: ['node', 'scripts/verify-docs.js'],
            remedy: 'Read the page-vs-truth diff above. Machine pages are generated from '
                + 'Includes and word use; fix the page, not the check.',
        },
        {
            id: 'errors', title: 'every diagnostic is documented', timeoutSec: 300,
            cmd: ['node', 'scripts/verify-errors.js'],
            remedy: 'A new or reworded toolchain message needs a card on docs/errors.html.',
        },
        {
            id: 'permissions', title: 'executable bits on tracked scripts', timeoutSec: 300,
            cmd: ['node', 'scripts/verify-permissions.js'],
            remedy: 'FIXED N BITS means the correction is staged - commit it. '
                + 'Use git update-index --chmod=+x, never a filesystem chmod.',
        },
        {
            id: 'host-blind', title: 'the host stays blind to the mill', timeoutSec: 300,
            cmd: ['node', 'scripts/verify-host-blind.js'],
            remedy: 'A host project has reached into mill internals. See the script output.',
        },
        {
            id: 'suites', title: 'every tst suite and mill is run or excluded', timeoutSec: 60,
            cmd: ['node', 'scripts/verify-suites.js'],
            remedy: 'A tst/*.shoddy or a mills/* fell out of the roster. Add it to '
                + 'MACHINE_SUITES or MILLS in scripts/gate/steps.mjs, or to '
                + 'TST_EXCLUSIONS with the reason.',
        },
        {
            id: 'lanes', title: 'the lane boundaries hold', timeoutSec: 60,
            cmd: ['node', 'scripts/verify-lanes.js'],
            remedy: 'A lane has reached into another one, or fettler has taken a package '
                + 'that is not on R1.2\'s allowlist. The message names the rule and the file.',
        },
    ];
}

// ---- gate --------------------------------------------------------------
// The expensive proof. Everything build.ps1's Invoke-Test runs, in its
// order, plus the split that keeps `dotnet test` honest.

export function gateSteps({ splitDotnetTest }) {
    const steps = [
        // FIRST, AND CHEAP. Every result below this line is recorded through
        // the harness in lib.mjs, and a harness whose timeout or whose
        // receipt key had quietly stopped working would look exactly like
        // one that was never needed. `shoddy selftest` existed as a verb
        // nobody had a reason to type - which is the way an unrun check
        // rots. It takes four seconds; there is no argument for leaving it
        // to memory.
        {
            id: 'selftest', title: 'the harness keeps its own guarantees', timeoutSec: 120,
            cmd: ['node', 'scripts/gate/selftest.mjs'],
            remedy: 'The driver cannot keep its own promises, so nothing below this line '
                + 'means what it says. Read the log before trusting any gate.',
        },
        {
            id: 'build-mill', title: 'publish the mill', timeoutSec: 900,
            cmd: ['dotnet', 'publish', 'src/Shoddy.Mill', '-c', 'Release', '-o', 'bin'],
            remedy: 'A compile error, or bin/mill.exe is locked by a running process. '
                + 'Run `shoddy clean` and try again.',
        },
        // Straight after the mill, because it needs the mill and nothing
        // else, and because a stale manifest breaks a HOST lane twenty
        // minutes later - which is a long way from the file that is wrong.
        {
            id: 'manifests', title: 'every mill.manifest describes its mill', timeoutSec: 300,
            cmd: ['node', 'scripts/verify-manifests.js'],
            remedy: 'A generated file has drifted from the mill it describes. The message '
                + 'names the mill and the regeneration command.',
        },
    ];

    // WARM THE PROOF PROJECTS BEFORE THE TEST HOST NEEDS THEM.
    //
    // ProofTests runs `dotnet run --project proofs/console-cs` and friends as
    // child processes INSIDE dotnet test. On a warm cache that is a couple of
    // seconds; on a cold one it is a full restore and build of a project that
    // itself invokes the Shoddy toolchain - and it prints nothing to vstest
    // while it works, so blame's inactivity timer fires and the run is
    // reported as "Test host process crashed" with two thirds of the suite
    // never run. It reads like a crash and is really a stall.
    //
    // The repo already knew half of this: build.ps1 builds the mill before
    // `dotnet test` so ProofTests' own publish fallback is a no-op, with a
    // comment recording that the cold-cache version stalled a release runner
    // for half an hour in v2.0.0. The proofs need exactly the same courtesy,
    // and this gate makes it worse without it, because it clears stale
    // toolchain processes on the way in and so guarantees a cold cache.
    // EVERY project a test shells out to build, warmed here. The list mirrors
    // what the test classes actually invoke, and it has to stay in step with
    // them: ToolchainEquivalenceTests' static constructor builds both mill
    // entry points in Release, and ProofTests runs the two console proofs.
    // Warming only some of them fixes only some of the crashes, which is a
    // confusing place to stop.
    //
    // ORDER IS LOAD-BEARING, AND WARMING THE PROOFS FIRST UNDOES ITSELF.
    // `dotnet test src/Shoddy.Tests` builds the test project's DEBUG
    // dependency graph, and Shoddy.Tests and console-cs both ProjectReference
    // Shoddy.Hosting - in the same default configuration. So a proof project
    // warmed before `dotnet test` is invalidated BY `dotnet test`, and the
    // rebuild lands back inside the test host, silently, which is the exact
    // stall the warming exists to prevent. Observed on 2026-08-12: the run
    // that recompiled (analyser warnings in its log) stalled at
    // ProofTests.ConsoleCsProofsPass and was killed at the blame timeout; the
    // retry, which found everything up to date, passed all 296 in 1m51s. Same
    // commit, same code, and the only difference was whether a rebuild was
    // still owed.
    //
    // So the suite's own graph is built BEFORE the proofs are warmed. Then
    // the proofs are warmed on top of settled dependencies, and `dotnet test`
    // compiles nothing and invalidates nothing. The Release mill warms above
    // are a different configuration and do not take part in this.
    for (const [id, title, proj, extra] of [
        ['warm-mill-release', 'the full mill, Release', 'src/Shoddy.Mill', ['-c', 'Release']],
        ['warm-mill-headless', 'the headless mill, Release', 'src/Shoddy.Mill.Headless', ['-c', 'Release']],
        ['warm-tests', 'the C# suite and its Debug graph', 'src/Shoddy.Tests', []],
        ['warm-proof-cs', 'the C# proof project', 'proofs/console-cs/console-cs.csproj', []],
        ['warm-proof-fs', 'the F# proof project', 'proofs/console-fs/console-fs.fsproj', []],
    ]) {
        steps.push({
            id, title: `warm: ${title}`, timeoutSec: 1200,
            cmd: ['dotnet', 'build', proj, ...extra, '--nologo', '-v', 'q'],
            remedy: `${proj} failed to build. Run it directly: dotnet build ${proj}`,
        });
    }

    // The C# conformance suite. --blame-hang is permanent, not a debugging
    // aid: without it a crashed test host is indistinguishable from a slow
    // one, and the run sits until somebody notices.
    if (splitDotnetTest) {
        steps.push({
            id: 'dotnet-test-main', title: 'C# conformance, everything but the sockets',
            timeoutSec: 1200,
            cmd: ['dotnet', 'test', 'src/Shoddy.Tests',
                '--filter', 'FullyQualifiedName!~NetTests',
                '--blame-hang', '--blame-hang-timeout', '5m'],
            remedy: 'Read the failing test above. If it says the host CRASHED rather than '
                + 'a test failing, a test is taking the process down - see the hang dump path.',
        });
        steps.push({
            id: 'dotnet-test-net', title: 'C# conformance, the socket tests',
            timeoutSec: 900,
            cmd: ['dotnet', 'test', 'src/Shoddy.Tests',
                '--filter', 'FullyQualifiedName~NetTests',
                '--blame-hang', '--blame-hang-timeout', '3m'],
            remedy: 'The socket tests bind loopback ports. Check nothing else is holding them.',
        });
    } else {
        steps.push({
            id: 'dotnet-test', title: 'C# conformance suite', timeoutSec: 1800,
            // 8 minutes of SILENCE, not of runtime: the suite itself takes
            // under two. Generous on purpose - the budget exists to catch a
            // genuine stall, and a budget that false-fires teaches people to
            // ignore it.
            cmd: ['dotnet', 'test', 'src/Shoddy.Tests',
                '--blame-hang', '--blame-hang-timeout', '8m'],
            remedy: 'A "Test host process crashed" with a count well short of the total is NOT '
                + 'a failing test - it is a test taking the process down, and everything after '
                + 'it never ran. Two known causes, both fixed and both easy to reintroduce: a '
                + 'pipe deadlock from reading one child stream to the end before the other '
                + '(see ProcessRun.cs), and parallel tests sharing process-global state (see '
                + 'AssemblyInfo.cs). --split-tests isolates the socket tests for bisecting.',
        });
    }

    // Through the repo's own build target, not a reimplementation: the
    // dependency ordering there is load-bearing and has one home.
    steps.push({
        id: 'machines', title: 'build every machine DLL', timeoutSec: 1200,
        cmd: process.platform === 'win32'
            ? ['powershell', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
                '-File', 'build.ps1', 'machines']
            : ['./build.sh', 'machines'],
        remedy: 'Machines build in dependency order; an out-of-order build splices source '
            + 'and collides downstream. See the failing machine above.',
    });

    for (const [name, title, t] of CORE_SUITES) {
        steps.push({
            id: `tst-${name}`, title, timeoutSec: t,
            cmd: [MILL, 'run', `tst/${name}.shoddy`],
            remedy: `An assertion in tst/${name}.shoddy failed. The message names it.`,
        });
    }

    steps.push({
        id: 'tst-net-demo', title: 'the socket words end to end', timeoutSec: 300,
        cmd: [MILL, 'run', '--allow-net', 'tst/net-demo.shoddy'],
        remedy: 'This also proves the --allow-net gate opens. Check nothing blocks loopback.',
    });
    steps.push({
        id: 'tutorial-spiro', title: 'the tutorial still compiles and passes', timeoutSec: 300,
        cmd: [MILL, 'run', 'tutorials/spiro/test.shoddy'],
        remedy: 'The tutorial is documentation that has to keep working. Fix the code or the page.',
    });
    // The sinq demo asserts nothing — tst/sinq.shoddy grades the behaviour.
    // What this step proves is that the worked example reproduced on
    // docs/machines/sinq.html still COMPILES against the machine as it
    // stands, which is the half a page of prose cannot check for itself.
    steps.push({
        id: 'tst-sinq-demo', title: 'the sinq worked example still runs', timeoutSec: 300,
        cmd: [MILL, 'run', 'tst/sinq-demo.shoddy'],
        remedy: 'The example on docs/machines/sinq.html is this file. Fix whichever '
            + 'drifted, and check the printed output still matches the page.',
    });

    for (const suite of MACHINE_SUITES) {
        steps.push({
            id: `machine-${suite}`, title: `machine suite: ${suite}`, timeoutSec: 600,
            cmd: [MILL, 'run', `tst/${suite}.shoddy`],
            remedy: `An assertion in tst/${suite}.shoddy failed.`,
        });
    }
    // isamdump reopens what isamtest leaves behind: the round trip across two
    // processes is the thing being proved, and DELETE clears up after it.
    steps.push({
        id: 'machine-isamtest', title: 'machine suite: isam', timeoutSec: 600,
        cmd: [MILL, 'run', 'tst/isamtest.shoddy'],
        remedy: 'An assertion in tst/isamtest.shoddy failed.',
    });
    steps.push({
        id: 'machine-isamdump', title: 'isam round trip across processes', timeoutSec: 600,
        cmd: [MILL, 'run', 'tst/isamdump.shoddy', 'DELETE'],
        remedy: 'The index written by isamtest did not read back. Check both suites together.',
    });

    const millScript = process.platform === 'win32' ? 'build.ps1' : 'build.sh';
    for (const m of MILLS) {
        steps.push({
            id: `mill-${m}`, title: `mill suite: ${m}`, timeoutSec: 900,
            cmd: process.platform === 'win32'
                ? ['powershell', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
                    '-File', join('mills', m, millScript), 'test']
                : [join('.', 'mills', m, millScript), 'test'],
            remedy: `mills/${m}'s own suite failed. Run it directly: `
                + `mills/${m}/${millScript} test`,
        });
    }

    // ---- the lanes the gate used to walk straight past -----------------
    //
    // Four test projects live in this repository and this gate ran ONE of
    // them. Fettler's 288 tests, the MCP host's suite and the MAUI host's
    // suite were each proved only by a workflow, and every one of those
    // workflows is PATH-FILTERED: fettler.yml runs on fettler/**, mcp.yml
    // on hosts/mcp/**, maui.yml on hosts/maui/**.
    //
    // So a change OUTSIDE those paths triggered none of them. That is not
    // hypothetical - Directory.Build.props sets the version every one of
    // these binaries reports, and it matches no lane's path filter at all.
    // A green local gate plus a green CI run could both be true of a tree
    // where three of the four suites had never been executed.
    //
    // These run last because they are independent: nothing above depends on
    // them, so a failure here is read without unpicking anything else.
    const ps = (script, ...args) => ['powershell', '-NoProfile', '-NonInteractive',
        '-ExecutionPolicy', 'Bypass', '-File', script, ...args];
    const win = process.platform === 'win32';

    // Fettler needs NOTHING built first - R1.2 forbids it to reference any
    // project here, so there is no mill for it to wait on.
    steps.push({
        id: 'fettler-test', title: 'the fettler lane: 288 tests', timeoutSec: 900,
        cmd: win ? ps('fettler/build.ps1', 'test') : ['./fettler/build.sh', 'test'],
        remedy: 'Run it directly: fettler/build.ps1 test. Three skips are expected on '
            + 'Windows - a POSIX execute bit and two filename hazards have no meaning '
            + 'here, and they run on the macOS leg of fettler.yml.',
    });
    // fettler.yml builds and tests this lane in RELEASE; build.ps1 test is
    // Debug. Compiling the lane the way CI does is seconds, and a
    // configuration only ever built on a runner is one that can break there
    // and nowhere else.
    steps.push({
        id: 'fettler-release', title: 'the fettler lane builds for release', timeoutSec: 900,
        cmd: win ? ps('fettler/build.ps1', 'release') : ['./fettler/build.sh', 'release'],
        remedy: 'Run it directly: fettler/build.ps1 release.',
    });
    // THE ARTIFACT, NOT THE SOURCE. Everything above proves the code; these
    // two prove the file a person downloads. A self-contained single-file
    // publish fails in ways a build cannot - a trimmed assembly, an entry
    // point that never starts - and release.yml cuts these archives FROM
    // THE TAG, so without this the discovery happens after the tag is
    // public and the fix has to go forward.
    steps.push({
        id: 'fettler-publish', title: 'cut the fettle release archives', timeoutSec: 1800,
        cmd: win ? ps('fettler/build.ps1', 'publish') : ['./fettler/build.sh', 'publish'],
        remedy: 'Run it directly: fettler/build.ps1 publish.',
    });
    steps.push({
        id: 'fettler-shipped', title: 'the shipped fettle runs from its archive',
        timeoutSec: 600,
        cmd: ['node', 'scripts/verify-shipped.js', 'fettle'],
        remedy: 'The archive for this platform was unpacked outside the repository and '
            + 'driven. A failure here is in what ships, not in what builds.',
    });

    // Both host lanes need bin/mill.exe, which build-mill has already
    // published at the top of this list: ShoddyWeave drives it.
    //
    // AND BOTH NEED THEIR OWN `build` FIRST, which is not a formality. Each
    // lane's `test` verb runs `dotnet test <Lane>.Tests` and nothing else,
    // so it builds the TEST project's graph - while the suites read the
    // APP's build output to see what got linked. Skip the build and the
    // enumeration finds an empty directory and reports every machine as
    // missing, which reads like a catastrophic isolation failure and is
    // really an unbuilt app. Both workflows run build before test for this
    // reason; so does this.
    steps.push({
        id: 'mcp-build', title: 'the MCP host lane builds', timeoutSec: 900,
        cmd: win ? ps('hosts/mcp/build.ps1') : ['./hosts/mcp/build.sh'],
        remedy: 'Run it directly: hosts/mcp/build.ps1. If it stops at a missing '
            + 'bin/mill.exe, build-mill above did not leave one.',
    });
    steps.push({
        id: 'mcp-test', title: 'the MCP host lane: sparky\'s suite', timeoutSec: 900,
        cmd: win ? ps('hosts/mcp/build.ps1', 'test') : ['./hosts/mcp/build.sh', 'test'],
        remedy: 'Run it directly: hosts/mcp/build.ps1 test.',
    });
    steps.push({
        id: 'mcp-release', title: 'the MCP host lane builds for release', timeoutSec: 900,
        cmd: win ? ps('hosts/mcp/build.ps1', 'release') : ['./hosts/mcp/build.sh', 'release'],
        remedy: 'Run it directly: hosts/mcp/build.ps1 release.',
    });
    steps.push({
        id: 'mcp-closure', title: 'sparky links no window, audio or toolchain', timeoutSec: 120,
        cmd: ['node', 'scripts/verify-closure.js', 'sparky'],
        remedy: 'The server has linked past its floor, or an unfolded seed reached its '
            + 'closure. A csproj says what was asked for; this reads what arrived.',
    });
    steps.push({
        id: 'mcp-publish', title: 'cut the sparky release archives', timeoutSec: 1800,
        cmd: win ? ps('hosts/mcp/build.ps1', 'publish') : ['./hosts/mcp/build.sh', 'publish'],
        remedy: 'Run it directly: hosts/mcp/build.ps1 publish.',
    });
    steps.push({
        id: 'mcp-shipped', title: 'the shipped sparky answers on stdio', timeoutSec: 600,
        cmd: ['node', 'scripts/verify-shipped.js', 'sparky'],
        remedy: 'The archive was unpacked outside the repository and driven over stdio. '
            + 'It answers initialize and then computes, because a server carrying no '
            + 'woven core passes the first and fails the second.',
    });

    // MAUI is Windows-only and always has been - maui.yml runs on
    // windows-latest and the workload is maui-windows. On any other
    // platform there is nothing to run rather than something being
    // skipped, and saying so out loud beats a step that silently passes.
    if (win) {
        steps.push({
            id: 'maui-build', title: 'the MAUI host lane builds', timeoutSec: 1800,
            cmd: ps('hosts/maui/build.ps1'),
            remedy: 'Run it directly: hosts/maui/build.ps1. If the workload is missing: '
                + 'dotnet workload install maui-windows.',
        });
        steps.push({
            id: 'maui-test', title: 'the MAUI host lane: reckoner\'s suite', timeoutSec: 1800,
            cmd: ps('hosts/maui/build.ps1', 'test'),
            // The suite asks the environment which assertions have a device
            // behind them. Canvas and audio proofs need the desktop harness
            // and are not a hosted-runner gate, so the same variable CI sets
            // is set here - otherwise this machine would be asserting things
            // maui.yml never does, and the two would disagree about green.
            env: { SHODDY_HEADLESS_CI: '1' },
            remedy: 'Run it directly: hosts/maui/build.ps1 test. If the workload is '
                + 'missing: dotnet workload install maui-windows.',
        });
        steps.push({
            id: 'maui-release', title: 'the MAUI host lane builds for release', timeoutSec: 1800,
            cmd: ps('hosts/maui/build.ps1', 'release'),
            // OBSERVED FLAKE, WRITTEN DOWN RATHER THAN SLEPT THROUGH. This
            // step failed once with UnauthorizedAccessException from
            // Weaver.WeaveMachine moving Shoddy.Machines.Halifax-core.dll,
            // and passed on an immediate retry with nothing changed. A mill
            // writes its machine DLLs to mills/<name>/bin/, which is the
            // SAME path in Debug and Release - so a Release build starting
            // while the previous step's test host is still exiting contends
            // for one file. A sleep would hide it; naming it means the next
            // person reads a known race instead of hunting a phantom.
            remedy: 'Run it directly: hosts/maui/build.ps1 release. "Access to the path '
                + 'is denied" on a Shoddy.Machines.*.dll is the known race: mills/<name>/bin '
                + 'is shared across configurations, and a test host from the previous step '
                + 'may still hold it. Re-run this step alone to confirm before believing it.',
        });
        steps.push({
            id: 'maui-closure', title: 'the reckoner release carries no perch', timeoutSec: 120,
            cmd: ['node', 'scripts/verify-closure.js', 'reckoner'],
            remedy: 'B7.4: a release artifact must not carry the debug transport. '
                + 'Check what the release configuration excludes.',
        });
    }

    // C8, as much of it as a machine with a display can prove. The weave
    // and the refusal are the half that breaks when somebody edits the
    // mill; the absence of a native dependency needs the bare container,
    // and ci.yml remains the authority on that. The Release build it reads
    // is the one warm-mill-headless already made.
    steps.push({
        id: 'headless-floor', title: 'the headless mill weaves and refuses by name',
        timeoutSec: 600,
        cmd: ['node', 'scripts/verify-headless.js'],
        remedy: 'A crash and a refusal are both non-zero and only one is correct. The '
            + 'message must name "headless" and "full mill" so the reader knows which '
            + 'build they have.',
    });

    return steps;
}

// ---- package -----------------------------------------------------------

export function packageSteps(version) {
    return [
        {
            id: 'vsix', title: `package the extension at ${version}`, timeoutSec: 1200,
            cmd: process.platform === 'win32'
                ? ['powershell', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
                    '-File', 'build.ps1', 'vsix', version]
                : ['./build.sh', 'vsix', version],
            remedy: 'vsce is the one dependency outside this repo. `npm i -g @vscode/vsce` if missing.',
        },
    ];
}
