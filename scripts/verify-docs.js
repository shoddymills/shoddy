// MAINTAINER TOOL - read-only. Checks the docs against the sources.
//
//   node scripts/verify-docs.js        (run from anywhere; finds the repo from its own path)
//
// Rebuilds ground truth from the tree on every run and diffs it against the
// machine pages, both directions:
//
//   "Who Uses It"          - machines that Include this machine, and mills that
//                            call its words (mill-local Defs, Type variants and
//                            record fields excluded so shared names don't lie)
//   "The Machines It Uses" - the machine's own direct Includes
//
// Exit 0 and "ALL PAGES MATCH GROUND TRUTH" is the pass. Any mismatch prints
// page vs truth and exits 1. Run it before cutting a release, and whenever
// machines gain or lose an Include or a mill starts using a new machine.

const fs = require("fs");
const path = require("path");
const root = path.resolve(__dirname, "..");

// ---- ground truth: machine -> machines it includes ----
const includes = {};
const words = {};
for (const f of fs.readdirSync(path.join(root, "machines"))) {
  if (!f.endsWith(".shoddy")) continue;
  const name = f.replace(".shoddy", "");
  const txt = fs.readFileSync(path.join(root, "machines", f), "utf8");
  includes[name] = [...txt.matchAll(/^Include "([a-z0-9-]+)\.shoddy"/gm)].map(m => m[1]);
  words[name] = [...txt.matchAll(/^Def ([A-Za-z0-9]+)/gm)].map(m => m[1]);
}

// ---- ground truth: machine -> machines that include it ----
const usedByMachines = {};
for (const m of Object.keys(includes)) usedByMachines[m] = [];
for (const [m, incs] of Object.entries(includes))
  for (const dep of incs) usedByMachines[dep].push(m);

// ---- ground truth: machine -> mills that call its words ----
const usedByMills = {};
for (const m of Object.keys(includes)) usedByMills[m] = [];
const millsDir = path.join(root, "mills");
const mills = fs.readdirSync(millsDir).filter(d => fs.statSync(path.join(millsDir, d)).isDirectory());
for (const mill of mills) {
  const dir = path.join(millsDir, mill);
  const srcs = fs.readdirSync(dir).filter(f => f.endsWith(".shoddy")).map(f =>
    fs.readFileSync(path.join(dir, f), "utf8").split("\n")
      .filter(l => !/^\s*Rem\b/i.test(l) && !/^\s*Include\b/i.test(l)).join("\n"));
  const local = new Set();
  for (const t of srcs) {
    for (const m2 of t.matchAll(/^Def ([A-Za-z0-9]+)/gm)) local.add(m2[1]);
    for (const m2 of t.matchAll(/^Type +[A-Za-z0-9]+ *= *(.+)$/gm))
      for (const v of m2[1].split("|")) local.add(v.trim().split("(")[0].trim());
    for (const m2 of t.matchAll(/^\s+([A-Za-z0-9]+) +As +/gm)) local.add(m2[1]);
  }
  const all = srcs.join("\n");
  for (const [mach, ws] of Object.entries(words)) {
    let hit = false;
    for (const w of ws) {
      if (local.has(w)) continue;
      if (w.length <= 1) continue;               // single letters match prose
      if (new RegExp("\\b" + w + "\\b").test(all)) { hit = true; break; }
    }
    if (hit) usedByMills[mach].push(mill);
  }
}

// Hand-verified exclusions for string-literal false positives the filters
// above cannot see. Re-check by eye if either side changes.
usedByMills["buzzer"] = usedByMills["buzzer"].filter(m => m !== "pac-vt100"); // "P - Play" menu text

// ---- extract page sections and diff ----
function extract(page, sectionId) {
  const html = fs.readFileSync(path.join(root, "docs", "machines", page + ".html"), "utf8");
  const i = html.indexOf('id="' + sectionId + '"');
  if (i < 0) return null;
  const ends = [html.indexOf("</table>", i), html.indexOf("</p>", i)].filter(e => e > 0);
  const sec = html.slice(i, ends.length ? Math.min(...ends) : html.length);
  return {
    machines: [...sec.matchAll(/<td><a href="([a-z0-9-]+)\.html">/g)].map(m => m[1]),
    mills: [...sec.matchAll(/<td><a href="\.\.\/mills\/([a-z0-9-]+)\.html">/g)].map(m => m[1]),
  };
}

const sorted = a => [...a].sort().join(" ");
let bad = 0;
for (const m of Object.keys(includes).sort()) {
  const ub = extract(m, "usedby");
  if (!ub) { console.log(m + ": MISSING usedby section"); bad++; continue; }
  if (sorted(ub.machines) !== sorted(usedByMachines[m]))
    { console.log(m + " usedby machines: page[" + sorted(ub.machines) + "] truth[" + sorted(usedByMachines[m]) + "]"); bad++; }
  if (sorted(ub.mills) !== sorted(usedByMills[m]))
    { console.log(m + " usedby mills: page[" + sorted(ub.mills) + "] truth[" + sorted(usedByMills[m]) + "]"); bad++; }
  const us = extract(m, "uses");
  if (us === null) { console.log(m + ": MISSING uses section"); bad++; continue; }
  if (sorted(us.machines) !== sorted(includes[m]))
    { console.log(m + " uses: page[" + sorted(us.machines) + "] truth[" + sorted(includes[m]) + "]"); bad++; }
}
console.log(bad === 0 ? "ALL PAGES MATCH GROUND TRUTH" : bad + " mismatches");
process.exit(bad === 0 ? 0 : 1);
