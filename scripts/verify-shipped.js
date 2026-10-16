// MAINTAINER TOOL. Unpacks a release archive and DRIVES THE BINARY INSIDE
// IT - no repository, no installed runtime, no project reference.
//
//   node scripts/verify-shipped.js sparky [VERSION]
//
// The lane's own `publish` verb runs it last, with the version it
// published under (none for a local or CI publish, the tag's on release).
//
// Everything else in this gate proves the source. This proves the artifact:
// the exact file a person downloads, unpacked into a directory that knows
// nothing about this checkout and run there. A single-file publish can fail
// in ways a build cannot - a trimmed-away assembly, a missing native asset,
// an entry point that never starts - and none of those show up until
// somebody runs the thing.
//
// It belongs in the gate rather than only in a workflow because of WHEN the
// alternative finds out. release.yml builds these archives from the TAG. A
// publish that is broken is therefore discovered after the tag is public,
// which is the one failure this whole harness exists to make impossible.
//
// The archive for THIS platform only. The others are cut here for
// inspection and are proved on their own runners; an osx-arm64 binary
// cannot be executed on Windows to ask it anything.

const { spawnSync } = require("child_process");
const fs = require("fs");
const os = require("os");
const path = require("path");
const root = path.resolve(__dirname, "..");

const lane = process.argv[2];
const version = process.argv[3] || "";
let bad = 0;
const fail = (msg) => { console.log(msg); bad++; };
const ok = (msg) => console.log(`ok  ${msg}`);

const isWin = process.platform === "win32";
// process.arch is node's own architecture: an x64 node under emulation on
// an ARM64 machine proves the win-x64 archive, which is still a real proof.
const RID = isWin
    ? (process.arch === "arm64" ? "win-arm64" : "win-x64")
    : process.platform === "darwin"
        ? (process.arch === "arm64" ? "osx-arm64" : "osx-x64")
        : "linux-x64";

// A fresh directory OUTSIDE the repository, so nothing the binary finds can
// have come from the checkout. That is half of what is being proved.
function scratch(name) {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), `shoddy-${name}-`));
    return dir;
}

function unpack(archive, into) {
    // bsdtar ships with Windows 10+ and reads zip as well as tar.gz, so one
    // command serves every platform and no third-party unzip is needed.
    const r = spawnSync("tar", ["-xf", archive, "-C", into], { encoding: "utf8" });
    if ((r.status ?? 1) !== 0) {
        fail(`could not unpack ${path.basename(archive)}: ${r.stderr ?? ""}`);
        return false;
    }
    return true;
}

function findArchive(prefix) {
    const pub = path.join(root, "artifacts", "publish");
    let names;
    try { names = fs.readdirSync(pub); } catch {
        fail(`artifacts/publish is not there - run the lane's \`build.ps1 publish\` first`);
        return null;
    }
    // Exactly the name just published: a stale archive from another
    // version must never be mistaken for what was just built.
    const exact = `${prefix}${version ? "-" + version : ""}-${RID}${isWin ? ".zip" : ".tar.gz"}`;
    if (names.includes(exact)) return path.join(pub, exact);
    fail(`no ${exact} in artifacts/publish - run the lane's \`build.ps1 publish\` first`);
    return null;
}

// Judged on the exit code alone, both streams kept. `input` is written to
// stdin and the stream then closed, which is how a stdio server is told the
// conversation is over.
function drive(exe, args, { input } = {}) {
    const r = spawnSync(exe, args, { encoding: "utf8", input: input ?? "" });
    return { code: r.status ?? 1, out: r.stdout ?? "", err: r.stderr ?? "" };
}

// THE EXECUTABLE BIT. It only survives a tar made on a filesystem that has
// one, and a sparky that arrives without it does not run. Every unix
// archive of this version is listed and its entry must read -rwx. Off
// Windows only: an archive cut on Windows cannot record the bit at all,
// which is why a release is cut on the Linux runner.
function executableBits(prefix) {
    if (isWin) return;
    const pub = path.join(root, "artifacts", "publish");
    for (const rid of ["linux-x64", "osx-x64", "osx-arm64"]) {
        const name = `${prefix}${version ? "-" + version : ""}-${rid}.tar.gz`;
        const archive = path.join(pub, name);
        if (!fs.existsSync(archive)) { fail(`no ${name} in artifacts/publish`); continue; }
        const r = spawnSync("tar", ["-tvzf", archive], { encoding: "utf8" });
        const entry = (r.stdout ?? "").split("\n").find((l) => /\s(\.\/)?sparky$/.test(l.trim()) || l.trim().endsWith(" sparky"));
        if (!entry) fail(`${name} has no sparky entry`);
        else if (!/^-rwx/.test(entry.trim())) fail(`${name} carries sparky without its execute bit: ${entry.trim()}`);
        else ok(`${name} carries sparky executable`);
    }
}

if (lane === "sparky") {
    executableBits("sparky");
    const archive = findArchive("sparky");
    if (!archive) { console.log("1 problem(s)"); process.exit(1); }
    const dir = scratch("sparky");
    if (!unpack(archive, dir)) { console.log("1 problem(s)"); process.exit(1); }

    const exe = path.join(dir, isWin ? "sparky.exe" : "sparky");
    if (!fs.existsSync(exe)) {
        fail(`no executable in ${path.basename(archive)}`);
    } else {
        if (!isWin) fs.chmodSync(exe, 0o755);
        const home = scratch("sparky-root");
        // Initialize, then ask it to compute something only the woven core
        // can answer. A server that starts but carries no mill answers the
        // first message and fails the second, so both are sent.
        const lines = [
            '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}',
            '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"eval",'
                + '"arguments":{"lines":["{ 12500 13100 11900 } MEAN"]}}}',
        ].join("\n") + "\n";
        const r = drive(exe, ["--root", home], { input: lines });
        if (!r.out.includes('"serverInfo"')) {
            fail(`the shipped server did not answer initialize: '${r.out.slice(0, 400)}'`);
        } else ok("the shipped server answers initialize on stdio");
        // The version is the tag's, stamped by the publish (-p:Version):
        // what a user sees in serverInfo must be the number on the archive.
        if (version && !r.out.includes(`"version":"${version}"`)) {
            fail(`the shipped server does not report version ${version}: '${r.out.slice(0, 400)}'`);
        } else if (version) ok(`and reports version ${version}`);
        if (!r.out.includes("x: 12500")) {
            fail(`the shipped server did not compute over its woven core: '${r.out.slice(0, 400)}'`);
        } else ok("and computes over its woven core");
    }

} else {
    console.log("usage: node scripts/verify-shipped.js <sparky> [VERSION]");
    process.exit(2);
}

console.log(bad === 0 ? `THE SHIPPED ${lane.toUpperCase()} WORKS` : bad + " problem(s)");
process.exit(bad === 0 ? 0 : 1);
