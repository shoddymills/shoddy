// MAINTAINER TOOL - read-only. Proves the lane boundaries still hold.
//
//   node scripts/verify-lanes.js
//
// This repository is four lanes that are allowed to know very little about
// each other, and every one of those rules was enforced ONLY inside a
// GitHub workflow:
//
//   B2.2  nothing in src/ references hosts/            ci.yml
//   B2.4  nothing in hosts/ references Shoddy.Mill
//         or Shoddy.Compiler                           mcp.yml, maui.yml
//   R1.2  nothing in fettler/ references any project
//         outside its own lane, and its packages come
//         from an allowlist                            fettler.yml
//
// Each of those workflows is PATH-FILTERED, which is the problem. mcp.yml
// runs when hosts/mcp/** changes; fettler.yml when fettler/** changes. A
// commit that breaks a boundary from the OTHER side - or a maintainer
// running the full local gate before pushing - met none of them. The gate
// could be entirely green on a tree that CI would reject, which makes the
// gate's greenness worth less than it looks.
//
// So the rules live here, run locally in preflight, and the workflows stay
// as they are: CI is the backstop, not the only copy.
//
// Pure text over the project files, deliberately. The compiled-assembly
// version of this check already exists inside Fettler.Tests; reading the
// csproj catches a reference in the same commit that adds it, before
// anything has been built.

const fs = require("fs");
const path = require("path");
const root = path.resolve(__dirname, "..");

// Every package fettler/ is permitted to carry into what it ships. Adding
// one is a decision, not a drift: it must also reach the csproj comment,
// FrontEndTests.Allowed, THIRD-PARTY-NOTICES.md, and NOTICE if its licence
// asks - NOTICE being the only one of those a downloader ever sees.
const FETTLER_PACKAGES = ["PdfPig"];

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

// ---- R1.2: fettler/ is its own island ---------------------------------

for (const proj of projectsUnder("fettler")) {
    const text = fs.readFileSync(proj, "utf8");
    for (const m of text.matchAll(/<ProjectReference[^>]*Include="([^"]+)"/g)) {
        // Anything climbing two levels has left fettler/ entirely.
        if (/\.\.[/\\]\.\.[/\\]/.test(m[1])) {
            fail(`${rel(proj)} references ${m[1]} - fettler must not reference any `
                + "project outside its own lane (R1.2)");
        }
    }
}

{
    const shipped = ["fettler/Fettler/Fettler.csproj", "fettler/fettle/fettle.csproj"];
    const found = new Set();
    for (const p of shipped) {
        const text = fs.readFileSync(path.join(root, p), "utf8");
        for (const m of text.matchAll(/<PackageReference[^>]*Include="([^"]+)"/g)) {
            found.add(m[1]);
        }
    }
    for (const pkg of found) {
        if (!FETTLER_PACKAGES.includes(pkg)) {
            fail(`fettler/ takes ${pkg}, which is not on R1.2's allowlist. Adding one is `
                + "a decision: put it in FETTLER_PACKAGES here, in the workflow's allowed "
                + "list, in the csproj comment, in FrontEndTests.Allowed, in "
                + "THIRD-PARTY-NOTICES.md, and in NOTICE if its licence asks");
        }
    }
    console.log(`fettler packages: ${[...found].sort().join(" ") || "none"}`);
}

// ---- the obligations that travel with a binary ------------------------
//
// Apache-2.0 wants PdfPig's notice alongside the single file that bundles
// it; MIT wants Shoddy's own notice in every copy, and an archive is a
// copy. Either one left behind in the repository has not reached the
// person who downloaded a release.

{
    const notice = fs.readFileSync(path.join(root, "NOTICE"), "utf8");
    if (!notice.includes("PdfPig")) fail("NOTICE does not mention PdfPig");
    // The scope sentence: PdfPig is in fettle and in nothing else built
    // here, and a NOTICE that stopped saying so would describe the
    // extension and sparky as carrying a PDF component neither has had.
    if (!notice.includes("fettle alone")) {
        fail("NOTICE no longer scopes PdfPig to fettle alone - the extension and sparky "
            + "would read as carrying a PDF component neither has ever had");
    }
    for (const twin of ["fettler/build.ps1", "fettler/build.sh"]) {
        const text = fs.readFileSync(path.join(root, twin), "utf8");
        for (const file of ["NOTICE", "LICENSE"]) {
            if (!text.includes(file)) fail(`${twin} does not put ${file} in the archive`);
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
    ["fettler", ["build", "test", "release", "publish"]],
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
