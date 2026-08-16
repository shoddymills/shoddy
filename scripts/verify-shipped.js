// MAINTAINER TOOL. Unpacks a release archive and DRIVES THE BINARY INSIDE
// IT - no repository, no installed runtime, no project reference.
//
//   node scripts/verify-shipped.js fettle
//   node scripts/verify-shipped.js sparky
//
// Run after that lane's `build.ps1 publish`.
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
let bad = 0;
const fail = (msg) => { console.log(msg); bad++; };
const ok = (msg) => console.log(`ok  ${msg}`);

const isWin = process.platform === "win32";
const RID = isWin ? "win-x64"
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
    // Unversioned first: the gate publishes without a version, and a stale
    // versioned archive from an earlier release must never be mistaken for
    // what was just built.
    const exact = `${prefix}-${RID}${isWin ? ".zip" : ".tar.gz"}`;
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

if (lane === "fettle") {
    const archive = findArchive("fettle");
    if (!archive) { console.log("1 problem(s)"); process.exit(1); }
    const dir = scratch("fettle");
    if (!unpack(archive, dir)) { console.log("1 problem(s)"); process.exit(1); }

    const exe = path.join(dir, isWin ? "fettle.exe" : "fettle");
    if (!fs.existsSync(exe)) fail(`no executable in ${path.basename(archive)}`);
    // MIT wants its notice in every copy, and an archive is a copy.
    for (const f of ["NOTICE", "LICENSE"]) {
        if (fs.existsSync(path.join(dir, f))) ok(`${f} travelled with the binary`);
        else fail(`${f} is not in ${path.basename(archive)}`);
    }

    if (fs.existsSync(exe)) {
        if (!isWin) fs.chmodSync(exe, 0o755);
        const tree = scratch("fettle-tree");
        fs.mkdirSync(path.join(tree, "src"));
        fs.writeFileSync(path.join(tree, "src", "A.cs"), "public sealed class A\n");

        const v = drive(exe, ["--version"]);
        if (v.code === 0 && /^fettle \d+\.\d+\.\d+/.test(v.out.trim())) {
            ok(`it reports itself: ${v.out.trim()}`);
        } else {
            fail(`--version answered '${v.out.trim()}' (exit ${v.code})`);
        }

        const found = drive(exe, ["--root", tree, "find", "**/*.cs", "--json"]);
        if (found.code === 0 && found.out.includes("A.cs")) ok("it finds a file in a tree it was given");
        else fail(`find failed (exit ${found.code}): ${found.out}${found.err}`);

        // R3.7: a failure arrives COMPLETE ON STDOUT. Reading it must not
        // need a stderr redirection anywhere - which is the clause that
        // exists because Windows PowerShell makes a redirected native
        // stderr fatal on its own.
        const missing = drive(exe, ["--root", tree, "read", "absent.cs", "--json"]);
        if (missing.code !== 3) fail(`expected exit 3 for a missing file, got ${missing.code}`);
        else if (!missing.out.includes('"outcome":"not-found"')) {
            fail(`the failure did not arrive on stdout: '${missing.out}'`);
        } else ok("a failure arrives complete on stdout, exit 3");

        // R3.2: the MCP front end speaks on stdio and stdout carries the
        // protocol and nothing else.
        const rpc = '{"jsonrpc":"2.0","id":1,"method":"initialize",'
            + '"params":{"protocolVersion":"2025-06-18"}}\n';
        const served = drive(exe, ["serve", "--root", tree], { input: rpc });
        if (!served.out.includes('"serverInfo"')) {
            fail(`the MCP front end did not answer initialize: '${served.out}'`);
        } else if (served.out.trimStart()[0] !== "{") {
            fail("stdout did not start with the message - something else is on the protocol stream");
        } else ok("the MCP front end answers on stdio, and stdout carries the protocol alone");
    }

} else if (lane === "sparky") {
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
        if (!r.out.includes("x: 12500")) {
            fail(`the shipped server did not compute over its woven core: '${r.out.slice(0, 400)}'`);
        } else ok("and computes over its woven core");
    }

} else {
    console.log("usage: node scripts/verify-shipped.js <fettle|sparky>");
    process.exit(2);
}

console.log(bad === 0 ? `THE SHIPPED ${lane.toUpperCase()} WORKS` : bad + " problem(s)");
process.exit(bad === 0 ? 0 : 1);
