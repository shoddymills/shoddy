// MAINTAINER TOOL - read-only. Proves a lane's RELEASE OUTPUT carries what
// it should and nothing it should not.
//
//   node scripts/verify-closure.js sparky
//   node scripts/verify-closure.js reckoner
//
// The lanes' own `release` verb runs it after the build, so a local
// release and CI's release are graded by this one copy of the lists.
//
// A dependency floor is a claim about what is linked, and the only place a
// claim like that can be checked is the closure itself - a csproj says what
// was asked for, not what arrived transitively. Both of these lived only in
// a path-filtered workflow, so the local gate could be green while the
// shipped artifact carried a debug transport or a window toolkit.
//
// THE ONLY COPY of these lists. ci.yml used to carry its own inline
// version beside this one; now it calls the lane's `release` verb, which
// calls this, so there is nothing left to drift.

const fs = require("fs");
const path = require("path");
const root = path.resolve(__dirname, "..");

const lane = process.argv[2];
let bad = 0;
const fail = (msg) => { console.log(msg); bad++; };

// The release output directory carries the target framework in its name on
// some lanes and not others, so it is found rather than spelled.
function releaseDir(project) {
    const base = path.join(root, "artifacts", "bin", project);
    let entries;
    try { entries = fs.readdirSync(base); } catch {
        return null;
    }
    const hit = entries.find((e) => e === "release" || e.startsWith("release_"));
    return hit ? path.join(base, hit) : null;
}

function check(project, { must, banned, why }) {
    const dir = releaseDir(project);
    if (!dir) {
        fail(`no release output under artifacts/bin/${project} - run the lane's `
            + "`build.ps1 release` before this check");
        return;
    }
    console.log(`reading ${path.relative(root, dir).replace(/\\/g, "/")}`);
    const files = fs.readdirSync(dir);

    for (const name of must) {
        if (!files.some((f) => f.toLowerCase() === name.toLowerCase())) {
            fail(`${project}: ${name} is missing from the release output`);
        }
    }
    for (const pattern of banned) {
        const hit = files.find((f) => f.toLowerCase().includes(pattern.toLowerCase()));
        if (hit) fail(`${project}: ${hit} is in the release output - ${why}`);
    }
    console.log(`${files.length} file(s) in the closure`);
}

switch (lane) {
    case "sparky":
        check("Sparky", {
            must: ["sparky.dll", "Shoddy.Machines.Sparky-core.dll",
                   "Shoddy.Machines.dll", "Shoddy.Machines.Seeds.dll"],
            // No window, no audio, no toolchain, and no third-party protocol
            // package: the JSON-RPC layer is hand-rolled over
            // System.Text.Json, and this is what still says so in a year.
            banned: [
                "Shoddy.Compiler", "Shoddy.Devil", "Shoddy.Mill",
                "Silk.NET", "OpenAL", "glfw", "Microsoft.CodeAnalysis", "SkiaSharp",
                // The three unfolded seeds. The fold is the mechanism; this
                // is the mechanism observed from outside it.
                "Shoddy.Machines.Seedbuzzer", "Shoddy.Machines.Seedkeys",
                "Shoddy.Machines.Seedvt100", "Shoddy.Machines.Buzzer.dll",
                "Shoddy.Machines.Keys.dll", "Shoddy.Machines.Vt100.dll",
            ],
            why: "the server links past its floor",
        });
        break;

    case "reckoner":
        check("ShoddyReckoner", {
            must: ["Shoddy.Machines.Halifax-core.dll", "Shoddy.Machines.dll"],
            banned: ["Shoddy.Perch"],
            why: "a release artifact must not carry the debug transport (B7.4)",
        });
        break;

    default:
        console.log("usage: node scripts/verify-closure.js <sparky|reckoner>");
        process.exit(2);
}

console.log(bad === 0 ? `THE ${lane.toUpperCase()} CLOSURE HOLDS` : bad + " problem(s)");
process.exit(bad === 0 ? 0 : 1);
