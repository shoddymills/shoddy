// MAINTAINER TOOL. Proves the headless mill still weaves, and still
// REFUSES BY NAME rather than crashing.
//
//   node scripts/verify-headless.js
//
// Expects the seeds library project to have been built in Release, which
// builds the headless mill and both machine libraries:
//   dotnet build machines/seeds/Shoddy.Machines.Seeds.csproj -c Release
//
// C3 and C8: the headless build exists so a machine with no window and no
// audio device can still weave - the kind of machine consumers' CI actually
// is. A build that cannot reference GLFW or OpenAL cannot load them, and
// `run`, which needs the full mill, has to say so in words instead of
// dying on a missing native library.
//
// WHAT THIS DOES AND DOES NOT PROVE. ci.yml runs this inside a bare
// dotnet/sdk container, where the absence of X11 and of an audio device is
// the real thing. Here there is a display and there are drivers, so this
// proves the WEAVE and the REFUSAL - which is the half that breaks when
// somebody edits the mill - and it does not prove the absence of a native
// dependency. The container job remains the authority on that, and this
// step exists so the refusal cannot rot unnoticed between pushes to main.

const { spawnSync } = require("child_process");
const path = require("path");
const fs = require("fs");
const os = require("os");
const root = path.resolve(__dirname, "..");

const dll = path.join(root, "artifacts", "bin", "Shoddy.Mill.Headless", "release", "mill.dll");
const libs = path.join(root, "artifacts", "bin", "Shoddy.Machines.Seeds", "release");

let bad = 0;
const fail = (msg) => { console.log(msg); bad++; };

if (!fs.existsSync(dll) || !fs.existsSync(path.join(libs, "Shoddy.Machines.Seeds.dll"))) {
    console.log("the headless mill or the machine library is not built - build them first: "
        + "dotnet build machines/seeds/Shoddy.Machines.Seeds.csproj -c Release");
    process.exit(1);
}

// The headless mill carries no library and no sources: it is the build
// tool, and a project tells it what to link. At a command line the two
// variables do the same - SHODDYLIB names the sources an Include resolves
// against, SHODDYLIBBIN the compiled libraries.
const env = { ...process.env, SHODDYLIB: path.join(root, "machines"), SHODDYLIBBIN: libs };
const out = fs.mkdtempSync(path.join(os.tmpdir(), "shoddy-headless-"));

function mill(args) {
    const r = spawnSync("dotnet", [dll, ...args], {
        cwd: root, env, encoding: "utf8",
    });
    // Judged on the exit code alone, and both streams are kept: a native
    // program writing to stderr is not a failure, and the refusal we are
    // looking for may arrive on either one.
    return { code: r.status ?? 1, out: `${r.stdout ?? ""}${r.stderr ?? ""}` };
}

{
    const r = mill(["weave", "mills/halifax/halifax.shoddy", "-o", out]);
    if (r.code !== 0) fail(`headless weave failed (exit ${r.code}):\n${r.out}`);
    else console.log("ok  weaves a real mill");
}

{
    const r = mill(["machine", "mills/halifax/halifax-core.shoddy", "-o", out]);
    if (r.code !== 0) fail(`headless machine-weave failed (exit ${r.code}):\n${r.out}`);
    else console.log("ok  machine-weaves its core");
}

{
    // THE ONE THAT MATTERS. Not merely non-zero: a crash is non-zero too,
    // and the whole point is that this is a refusal a person can read.
    const r = mill(["run", "mills/halifax/halifax.shoddy"]);
    if (r.code !== 1) {
        fail(`headless run exited ${r.code}, expected 1 - a crash and a refusal are `
            + `both non-zero, and only one of them is correct:\n${r.out}`);
    } else if (!/headless/i.test(r.out) || !/full mill/i.test(r.out)) {
        fail("headless run refused, but not by name: the message must say 'headless' "
            + `and 'full mill' so the reader knows which build they have.\n${r.out}`);
    } else {
        console.log("ok  run refuses by name, not by crashing");
    }
}

console.log(bad === 0 ? "THE HEADLESS MILL HOLDS ITS FLOOR" : bad + " problem(s)");
process.exit(bad === 0 ? 0 : 1);
