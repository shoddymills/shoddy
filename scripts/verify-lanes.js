// MAINTAINER TOOL - read-only. Proves the lane boundaries still hold.
//
//   node scripts/verify-lanes.js
//
// This repository is three lanes that are allowed to know very little about
// each other, and every one of those rules was enforced ONLY inside a
// GitHub workflow:
//
//   B2.2  nothing in src/ references hosts/            ci.yml
//   B2.4  nothing in hosts/ references Shoddy.Mill
//         or Shoddy.Compiler                           mcp.yml, maui.yml
//
// Each of those workflows is PATH-FILTERED, which is the problem. mcp.yml
// runs when hosts/mcp/** changes; maui.yml when hosts/maui/** changes. A
// commit that breaks a boundary from the OTHER side - or a maintainer
// running the full local gate before pushing - met none of them. The gate
// could be entirely green on a tree that CI would reject, which makes the
// gate's greenness worth less than it looks.
//
// So the rules live here, run locally in preflight, and the workflows stay
// as they are: CI is the backstop, not the only copy.
//
// Pure text over the project files, deliberately. Reading the csproj
// catches a reference in the same commit that adds it, before anything
// has been built.

const fs = require("fs");
const path = require("path");
const root = path.resolve(__dirname, "..");

let bad = 0;
function fail(msg) { console.log(msg); bad++; }

function projectsUnder(rel) {
    const out = [];
    const walk = (dir) => {
        let entries;
        try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch { return; }
        for (const e of entries) {
            const full = path.join(dir, e.name);
            // artifacts/ and bin/ hold build output; a copy of a csproj there
            // is not a source of truth and would report the same fault twice.
            if (e.isDirectory()) {
                if (e.name === "artifacts" || e.name === "bin" || e.name === "obj") continue;
                walk(full);
            } else if (e.name.endsWith(".csproj") || e.name.endsWith(".fsproj")) {
                out.push(full);
            }
        }
    };
    walk(path.join(root, rel));
    return out;
}

const rel = (p) => path.relative(root, p).replace(/\\/g, "/");

// ---- B2.2: src/ knows nothing about hosts/ ----------------------------

for (const proj of projectsUnder("src")) {
    const text = fs.readFileSync(proj, "utf8");
    for (const m of text.matchAll(/<ProjectReference[^>]*Include="([^"]+)"/g)) {
        if (/hosts[/\\]/i.test(m[1])) {
            fail(`${rel(proj)} references ${m[1]} - src/ must never reference hosts/ (B2.2)`);
        }
    }
}

// ---- B2.4: hosts/ carries no toolchain --------------------------------
//
// Shoddy.Mill.Headless is a DIFFERENT project and is reached as a tool
// rather than as a reference, so the pattern is anchored to the exact file
// names the rule names.

for (const proj of projectsUnder("hosts")) {
    const text = fs.readFileSync(proj, "utf8");
    for (const m of text.matchAll(/<ProjectReference[^>]*Include="([^"]+)"/g)) {
        const base = m[1].replace(/\\/g, "/").split("/").pop();
        if (base === "Shoddy.Mill.csproj" || base === "Shoddy.Compiler.csproj") {
            fail(`${rel(proj)} references ${base} - hosts/ must not reference the `
                + "toolchain (B2.4); the weave arrives through ShoddyWeave, driven by bin/mill");
        }
    }
}

// ---- the twins have not drifted ---------------------------------------
//
// Every lane ships its build script twice, and this machine runs the .ps1
// while CI runs the .sh. A verb present in one and not the other is a
// target that is only ever exercised on one platform - which is exactly
// how a bug sits in the untested twin for release after release.

for (const [lane, verbs] of [
    ["hosts/mcp", ["build", "test", "release", "publish"]],
    ["hosts/maui", ["build", "test", "release"]],
]) {
    for (const verb of verbs) {
        for (const twin of ["build.ps1", "build.sh"]) {
            const p = path.join(root, lane, twin);
            if (!fs.existsSync(p)) { fail(`${lane}/${twin} is missing`); continue; }
            if (!fs.readFileSync(p, "utf8").includes(verb)) {
                fail(`${lane}/${twin} has lost the '${verb}' verb - its twin still has it, `
                    + "so that target is now proved on one platform only");
            }
        }
    }
}

console.log(bad === 0 ? "ALL LANE BOUNDARIES HOLD" : bad + " problem(s)");
process.exit(bad === 0 ? 0 : 1);
