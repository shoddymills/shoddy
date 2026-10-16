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
//         or Shoddy.Compiler                           ci.yml (mcp, maui jobs)
//
// They live HERE now and only here: `build check` runs this, and so does
// ci.yml's checks job. The workflow's own grep copies are gone - a rule
// with two copies is a rule whose copies disagree.
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

// ANY mention outside an XML comment counts, not only a ProjectReference:
// an Import, a Reference or a property naming the project reaches the
// toolchain just as surely. That is what the workflow's grep used to
// catch; comments are stripped so a note explaining the rule is not a
// breach of it.
for (const proj of projectsUnder("hosts")) {
    const text = fs.readFileSync(proj, "utf8").replace(/<!--[\s\S]*?-->/g, "");
    for (const m of text.matchAll(/[\w./\\-]*\b(Shoddy\.Mill|Shoddy\.Compiler)\.csproj/g)) {
        fail(`${rel(proj)} names ${m[1]}.csproj - hosts/ must not reference the `
            + "toolchain (B2.4); the weave arrives through ShoddyWeave, driven by the headless mill");
    }
}

// The lanes' twin scripts are verify-twins.js's job: it compares the verbs
// each header documents, which a text search for the verb's name cannot.

console.log(bad === 0 ? "ALL LANE BOUNDARIES HOLD" : bad + " problem(s)");
process.exit(bad === 0 ? 0 : 1);
