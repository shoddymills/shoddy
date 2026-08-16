// MAINTAINER TOOL. Proves every mill.manifest still describes its mill.
//
//   node scripts/verify-manifests.js
//
// Needs bin/mill - the gate's build-mill step publishes it.
//
// A mill.manifest names the machines a mill is woven from, and a host reads
// it to know what it is embedding. It is GENERATED, which is exactly why it
// drifts: add a machine to a mill and the manifest goes on describing the
// mill as it was, silently, because nothing recompiles a JSON file.
//
// This was found the hard way. mills/pac-vt100/mill.manifest had lost
// julian from its machines section, and the only thing in the repository
// that noticed was the MAUI host lane's build - which nothing local ran and
// which CI only runs when hosts/maui/** changes. A file in mills/ was
// breaking a lane in hosts/, and the two were connected by nothing that
// would ever fire.
//
// A MANIFEST THAT EXISTS MUST BE TRUE; a mill with no manifest is a mill no
// host embeds, and that is a legitimate state rather than an omission. So
// this checks the ones that are there and reports the count of those that
// are not, without inventing a rule that every mill needs one.

const { spawnSync } = require("child_process");
const fs = require("fs");
const path = require("path");
const root = path.resolve(__dirname, "..");

const mill = path.join(root, "bin", process.platform === "win32" ? "mill.exe" : "mill");
if (!fs.existsSync(mill)) {
    console.log("bin/mill is not there - publish it first: "
        + "dotnet publish src/Shoddy.Mill -c Release -o bin");
    process.exit(1);
}

const millsDir = path.join(root, "mills");
let bad = 0, checked = 0, without = [];

for (const entry of fs.readdirSync(millsDir, { withFileTypes: true })) {
    if (!entry.isDirectory()) continue;
    const dir = path.join(millsDir, entry.name);
    if (!fs.existsSync(path.join(dir, "mill.manifest"))) { without.push(entry.name); continue; }

    checked++;
    // Judged on the exit code alone, and both streams are kept: the mill
    // writes its drift report to stderr, and under Windows PowerShell a
    // redirected native stderr is not evidence of anything by itself.
    const r = spawnSync(mill, ["manifest", dir], { cwd: root, encoding: "utf8" });
    if ((r.status ?? 1) !== 0) {
        console.log(`mills/${entry.name}/mill.manifest is stale:`);
        for (const line of `${r.stdout ?? ""}${r.stderr ?? ""}`.split(/\r?\n/)) {
            if (line.trim()) console.log(`    ${line.trim()}`);
        }
        console.log(`    regenerate it: bin/mill manifest --write mills/${entry.name}`);
        bad++;
    }
}

console.log(`${checked} manifest(s) checked; ${without.length} mill(s) carry none `
    + `(${without.join(", ") || "-"}) - a mill no host embeds needs no manifest`);
console.log(bad === 0 ? "EVERY MANIFEST DESCRIBES ITS MILL" : bad + " stale manifest(s)");
process.exit(bad === 0 ? 0 : 1);
