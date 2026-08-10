// CI GATE - read-only. Asserts host-blindness mechanically (A3.6).
//
//   node scripts/verify-host-blind.js    (run from anywhere; finds the repo from its own path)
//
// No host name — "maui", "azure", "ios", "android", … — may appear in the
// manifest schema, anything under src/, the host-blind templates, or the
// extension. The mill contract, the toolchain, Shoddy.Build and the
// host-blind templates are host-independent BY CONSTRUCTION, and this check
// is what lets that boundary be enforced rather than remembered: the moment
// a host concept leaks into a host-blind tree, CI names the line.
//
// Word-boundary, case-insensitive: "ios" flags and "scenarios" does not.
// A file with a legitimate mention — documentation ABOUT the boundary, a
// comment quoting a host to say why it is excluded — opts out with the
// marker string below, the encoding check's pattern.
//
// This is the one verify-* script wired into ci.yml; the others stay
// manual pre-release gates.

const fs = require("fs");
const path = require("path");
const root = path.resolve(__dirname, "..");

// The host-name list. Extend it when a new host exists to name; the cost
// of a stale entry is a false negative, never a false positive.
const HOSTS = [
  "maui", "azure", "ios", "android", "maccatalyst", "mac catalyst", "winui",
  "xamarin", "uwp", "tizen", "watchos", "tvos", "iphone", "ipad",
];
const HOST_RE = new RegExp("\\b(" + HOSTS.join("|") + ")\\b", "i");

// Carry this marker anywhere in a file and its mentions are treated as
// deliberate rather than leaks.
const OPT_OUT = "verify-host-blind: host names are the subject here";

// What the check covers (A3.6): the schema, everything under src/ — the
// toolchain, Shoddy.Build when it lands — the host-blind templates, and
// the extension's own sources. Missing roots are fine: templates/ arrives
// later and the check must not wait for it.
const ROOTS = [
  path.join("docs", "schemas"),
  "src",
  "templates",
  "vscode-shoddy",
];

const TEXT_EXT = new Set([".md", ".html", ".shoddy", ".js", ".json", ".ts",
                          ".cs", ".csproj", ".slnx", ".props", ".targets",
                          ".sh", ".ps1", ".txt", ".yml", ".xml", ".svg"]);
// bin/obj/artifacts are build output; the extension's mill/ and machines/
// are the staged toolchain (build output too, and full of runtime RIDs
// that legitimately name platforms); out/ is the compiled extension.
const SKIP_DIR = new Set([".git", "bin", "obj", "artifacts", "node_modules",
                          "mill", "machines", "out", "dist"]);

function textFiles(dir, out = []) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    if (e.isDirectory()) { if (!SKIP_DIR.has(e.name)) textFiles(path.join(dir, e.name), out); }
    else if (TEXT_EXT.has(path.extname(e.name).toLowerCase())) out.push(path.join(dir, e.name));
  }
  return out;
}

let bad = 0;
for (const rootDir of ROOTS) {
  const abs = path.join(root, rootDir);
  if (!fs.existsSync(abs)) continue;
  for (const file of textFiles(abs)) {
    let text;
    try { text = fs.readFileSync(file, "utf8"); } catch { continue; }
    if (text.includes(OPT_OUT)) continue;
    const rel = path.relative(root, file).replace(/\\/g, "/");
    text.split("\n").forEach((line, i) => {
      const m = HOST_RE.exec(line);
      if (m) {
        console.log(rel + ":" + (i + 1) + ": host name \"" + m[1] + "\" in a "
                    + "host-blind tree - " + line.trim().slice(0, 72));
        bad++;
      }
    });
  }
}

console.log(bad === 0 ? "HOST-BLIND" : bad + " host-name leaks");
process.exit(bad === 0 ? 0 : 1);
