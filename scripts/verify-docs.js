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
// Then five structural checks, so the docs can't quietly fall behind the tree
// as Shoddy grows:
//
//   catalogs   - every machine and mill has a page, is listed in its catalog,
//                the docs home and the README; and no page is an orphan
//   navigation - every docs page carries the same nav bar (adding a page means
//                adding it everywhere, and this is what says you missed one)
//   grounding  - the copyable instruction blocks on the AI pages still carry
//                every fact an assistant gets wrong without being told
//   runtime    - the .NET version quoted in the docs matches what the mill
//                actually targets
//   encoding   - no tracked text file carries UTF-8 that has been round-tripped
//                through CP1252. v1.7.0 shipped ~400 mangled characters past
//                this gate because it had no such check (issue #36). A file
//                that is legitimately about the fault opts out with a marker;
//                see the check itself for the exact string.
//
// Exit 0 and "ALL PAGES MATCH GROUND TRUTH" is the pass. Any mismatch prints
// page vs truth and exits 1. Run it before cutting a release, and whenever
// machines gain or lose an Include or a mill starts using a new machine.
//
// Deliberately NOT checked: counts in prose. Docs should not say "twenty-two
// machines" at all - a number that has to be revised every time the tree grows
// is a divergence waiting to happen, so the prose says "every machine" instead.

const fs = require("fs");
const path = require("path");
const root = path.resolve(__dirname, "..");

// ---- ground truth: machine -> machines it includes ----
// The reckoner seeds live one level deeper, in machines/seeds/, so both
// directories are walked into the same flat machine-name space.
const includes = {};
const words = {};
const machineDirs = ["machines", path.join("machines", "seeds")];
for (const dir of machineDirs)
  for (const f of fs.readdirSync(path.join(root, dir))) {
    if (!f.endsWith(".shoddy")) continue;
    const name = f.replace(".shoddy", "");
    const txt = fs.readFileSync(path.join(root, dir, f), "utf8");
    includes[name] = [...txt.matchAll(/^Include "([a-z0-9-]+)\.shoddy"/gm)].map(m => m[1]);
    words[name] = [...txt.matchAll(/^Def ([A-Za-z0-9]+)/gm)].map(m => m[1]);
  }

// ---- ground truth: machine -> machines that include it ----
const usedByMachines = {};
for (const m of Object.keys(includes)) usedByMachines[m] = [];
for (const [m, incs] of Object.entries(includes))
  for (const dep of incs) usedByMachines[dep].push(m);
// Every reckoner seed includes both cuttle and reckoner, which would make
// their usedby tables just the thirteen seeds again - already listed, with
// more detail, in the "The Reckoner Seeds" section those two pages carry
// instead. Strip seeds from just these two so the tables aren't a duplicate.
usedByMachines["cuttle"] = usedByMachines["cuttle"].filter(m => !m.startsWith("seed"));
usedByMachines["reckoner"] = usedByMachines["reckoner"].filter(m => !m.startsWith("seed"));

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
// emley-moor closes SOCKETS. Close is net's and turtle's alike - the very
// collision net.shoddy's header documents - and the scan cannot tell which
// one a bare call meant. The mill includes net and not turtle.
usedByMills["turtle"] = usedByMills["turtle"].filter(m => m !== "emley-moor");

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
// ---- spec.html's machine catalogue: its Includes trailers ----
// Prose, and prose drifts. Every machine bullet in section 13 states what
// that machine includes; nothing checked it, so the list went stale twice
// over — silently, while this script reported green. It is ground truth
// like any other now.
{
  const spec = fs.readFileSync(path.join(root, "docs", "spec.html"), "utf8");
  for (const m of [...spec.matchAll(/<li><strong>([a-z0-9-]+)\.shoddy<\/strong>([\s\S]*?)<\/li>/g)]) {
    const name = m[1];
    if (!(name in includes)) continue;
    // Only the run of <code> names directly after "Includes" — net's
    // trailer carries prose after its list, and that is not a claim.
    const run = m[2].match(/Includes((?:\s*<code>[a-z0-9-]+<\/code>|\s*,|\s*and)*)/);
    const claimed = run
      ? [...run[1].matchAll(/<code>([a-z0-9-]+)<\/code>/g)].map(x => x[1])
      : [];
    if (sorted(claimed) !== sorted(includes[name])) {
      console.log("spec.html " + name + " Includes: page[" + sorted(claimed) +
                  "] truth[" + sorted(includes[name]) + "]");
      bad++;
    }
  }
}

// ---- catalogs: nothing added to the tree may go unlisted ----
// Exception: the reckoner seeds ("seed*"). They have no bearing outside
// cuttle/reckoner, so they are deliberately not tiled in the general
// catalogs - reckoner's own page lists all twelve instead. They still need
// a page (the orphan check below still runs over every machine) and their
// usedby/uses sections are still ground-truth-checked as normal.
const machineNames = Object.keys(includes).sort();
const catalogedNames = machineNames.filter(n => !n.startsWith("seed"));
const read = f => fs.readFileSync(path.join(root, f), "utf8");
const listings = [
  ["machine", catalogedNames, "docs/machines/index.html", n => n + ".html"],
  ["machine", catalogedNames, "docs/index.html", n => "machines/" + n + ".html"],
  ["machine", catalogedNames, "README.md", n => "machines/" + n + ".html"],
  ["mill", mills, "docs/mills/index.html", n => n + ".html"],
  ["mill", mills, "docs/index.html", n => "mills/" + n + ".html"],
  ["mill", mills, "README.md", n => "mills/" + n + ".html"],
];
for (const [kind, names, file, link] of listings) {
  const txt = read(file);
  for (const n of names)
    if (!txt.includes(link(n))) { console.log(file + ": " + kind + " " + n + " is not listed"); bad++; }
}
for (const [dir, names] of [["machines", machineNames], ["mills", mills]])
  for (const f of fs.readdirSync(path.join(root, "docs", dir)))
    if (f.endsWith(".html") && f !== "index.html" && !names.includes(f.replace(".html", "")))
      { console.log("docs/" + dir + "/" + f + ": page with no " + dir.replace(/s$/, "") + " behind it"); bad++; }

// ---- navigation: one nav bar, copied into every page ----
// The bar is two <nav class="docnav"> rows — "meta" (Story, Authorship)
// beside the wordmark, "main" beneath — so every row is collected and the
// items compared as one sequence. Checking only the first would leave the
// twelve items in the second row unguarded.
const pages = [];
(function walk(d) {
  for (const f of fs.readdirSync(d)) {
    const p = path.join(d, f);
    if (fs.statSync(p).isDirectory()) { if (f !== "media") walk(p); }
    else if (f.endsWith(".html") && f !== "404.html") pages.push(p);
  }
})(path.join(root, "docs"));
const navs = {};
for (const p of pages) {
  const rows = [...read(path.relative(root, p)).matchAll(/<nav class="docnav[^"]*"[^>]*>([\s\S]*?)<\/nav>/g)];
  const rel = path.relative(root, p).replace(/\\/g, "/");
  if (!rows.length) { console.log(rel + ": no nav bar"); bad++; continue; }
  const key = rows
    .flatMap(r => [...r[1].matchAll(/<(?:a href="[^"]+"|span class="here")>([^<]+)</g)])
    .map(x => x[1].trim()).join(" | ");
  (navs[key] = navs[key] || []).push(rel);
}
if (Object.keys(navs).length > 1) {
  const majority = Object.entries(navs).sort((a, b) => b[1].length - a[1].length)[0][0];
  for (const [key, where] of Object.entries(navs))
    if (key !== majority)
      { console.log("nav differs on " + where.join(", ") + ":\n  has  " + key + "\n  want " + majority); bad++; }
}

// ---- grounding: the copyable blocks handed to an assistant must agree ----
// Three pages carry a block of Shoddy facts meant to be pasted into a session.
// They are deliberately separate artifacts (each has to work alone), so they
// cannot be deduplicated - but they must not contradict each other. Every fact
// below is one an assistant gets wrong without being told, and dropping it from
// one block while the others keep it is exactly the drift this catches.
const grounding = [
  ["assistants.html", "docs/assistants.html"],
  ["with-ai.html", "docs/tutorials/with-ai.html"],
  ["backgammon.html", "docs/tutorials/backgammon.html"],
];
const facts = [
  ["only numeric type", /only\s+numeric\s+type/i],
  ["no bitwise operators", /no\s+bitwise\s+operators/i],
  ["arrays vs lists", /recurse\s+over\s+lists/i],
  ["structural record equality", /compare\s+structurally/i],
  ["mutual recursion costs stack", /mutual\s+recursion/i],
  ["no exceptions", /no\s+exceptions/i],
  ["1-based indexing", /1-based/i],
  // A secured socket breaks the never-blocks promise the rest of the family
  // keeps, and an assistant told only "sockets never block" writes a poll
  // loop that aborts.
  ["a secured socket blocks", /secured\s+socket/i],
  // The three trees an assistant should read rather than guess from.
  ["docs/* as a source", /`docs\/\*`/],
  ["machines/* as a source", /`machines\/\*`/],
  ["mills/* as a source", /`mills\/\*`/],
];
for (const [label, file] of grounding) {
  if (!fs.existsSync(path.join(root, file))) continue;   // page not present yet
  const txt = read(file);
  for (const [name, re] of facts)
    if (!re.test(txt)) { console.log(file + ": grounding block has lost \"" + name + "\""); bad++; }
}

// ---- grounding, second half: the claims must still be TRUE of the tree ----
// The check above asks whether a fact is PRESENT. That catches a deletion and
// nothing else - a block can keep every phrase and still describe a tree that
// has moved underneath it, which is exactly how these pages went stale before.
// So the specifics are checked against the sources they describe.
//
// The pages are deliberately written to name as few specifics as possible -
// pointers rather than payload, because a copied word list is wrong the moment
// a machine gains a word. What little they DO name has to be real, and this is
// what says so.
{
  // Every `name.shoddy` an INSTRUCTIONS page mentions must be a machine that
  // exists. This is the one that bites: the old blocks listed a dozen machines
  // by name as "the common ones", and a rename or a removal made the advice
  // wrong with nothing to notice. The rewritten blocks name no machine list at
  // all - they point at the directory - so what is left to check is the
  // handful named in passing as examples, and this is what keeps them real.
  //
  // Scoped to the two pages whose whole job is grounding. backgammon.html is a
  // build-a-program tutorial and legitimately names the files it is teaching
  // you to write (dice.shoddy, board.shoddy, ...), which are not machines and
  // never will be; it stays under the phrase check above only.
  const instructionPages = ["docs/assistants.html", "docs/tutorials/with-ai.html"];
  // The pages' own worked-example filenames, spelled as they appear in the
  // templates. Everything else must resolve.
  const exampleFiles = /^(test|core|readings)$|-core$/;
  for (const file of instructionPages) {
    if (!fs.existsSync(path.join(root, file))) continue;
    const named = new Set([...read(file).matchAll(/\b([a-z][a-z0-9-]*)\.shoddy\b/g)].map(m => m[1]));
    for (const n of named) {
      if (exampleFiles.test(n)) continue;
      if (!(n in includes)) {
        console.log(file + ': names "' + n + '.shoddy", which is not a machine');
        bad++;
      }
    }
  }

  // Published URLs are the fallback for an assistant with no checkout, so a
  // page that offers one has to offer a real one. Checked against the docs
  // tree itself: the path after the site root must be a page that exists.
  const SITE = "https://shoddymills.github.io/shoddy/";
  for (const [label, file] of grounding) {
    if (!fs.existsSync(path.join(root, file))) continue;
    for (const m of read(file).matchAll(/https:\/\/shoddymills\.github\.io\/shoddy\/([A-Za-z0-9/._-]*)/g)) {
      const rel = m[1].replace(/[.,)]+$/, "");
      if (rel && !fs.existsSync(path.join(root, "docs", rel))) {
        console.log(file + ": published link " + SITE + rel + " has no page behind it");
        bad++;
      }
    }
  }

  // The house-style claim the pages now make is a number, and a number is the
  // most driftable thing a page can carry - so it is derived here rather than
  // trusted. Mean Def body across machines/, counting neither blank lines nor
  // comments. If the library's shape moves, the pages have to say so.
  {
    let lines = 0, defs = 0;
    for (const dir of machineDirs)
      for (const f of fs.readdirSync(path.join(root, dir))) {
        if (!f.endsWith(".shoddy")) continue;
        let open = false, n = 0;
        for (const l of fs.readFileSync(path.join(root, dir, f), "utf8").split("\n")) {
          if (/^Def /.test(l)) { if (open) { lines += n; defs++; } open = true; n = 0; }
          else if (open) {
            if (/^\S/.test(l) && l.trim() !== "") { lines += n; defs++; open = false; }
            else if (l.trim() && !/^\s*Rem\b/i.test(l)) n++;
          }
        }
        if (open) { lines += n; defs++; }
      }
    const mean = Math.round(lines / defs);
    const WORDS = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight"];
    for (const [label, file] of grounding) {
      if (!fs.existsSync(path.join(root, file))) continue;
      const txt = read(file);
      // Only pages that make the claim are held to it.
      const claim = txt.match(/mean body across the standard library is (\w+)/i);
      if (claim && claim[1].toLowerCase() !== WORDS[mean]) {
        console.log(file + ': says the mean Def body is "' + claim[1] +
                    '" lines; the tree measures ' + WORDS[mean] + " (" + mean + ")");
        bad++;
      }
    }
  }
}

// ---- runtime: the version the docs quote is the one the mill targets ----
const tfm = read("src/Shoddy.Mill/Shoddy.Mill.csproj").match(/<TargetFramework>net([0-9]+)\./);
if (!tfm) { console.log("cannot read TargetFramework from the mill's csproj"); bad++; }
else for (const f of [...pages.map(p => path.relative(root, p).replace(/\\/g, "/")), "README.md"])
  for (const m of read(f).matchAll(/\.NET(?:&nbsp;|\s)([0-9]+)\b/g))
    if (m[1] !== tfm[1]) { console.log(f + ': says ".NET ' + m[1] + '", the mill targets net' + tfm[1] + ".0"); bad++; }

// ---- encoding: no file may carry UTF-8 that was mangled through CP1252 ----
// v1.7.0 shipped ~400 mangled characters across eighteen files - the README's
// own headers among them - and this gate passed on every one of them, twice.
// Nothing else in the toolchain has any reason to notice: the corruption sits
// in comments and prose, so the compiler, the tests and verify-errors are all
// happy. See issue #36.
//
// The signature is a lead byte from a multi-byte UTF-8 sequence that has been
// decoded as CP1252 and re-encoded: an A-circumflex, A-tilde or a-circumflex
// immediately followed by CP1252's high punctuation or a Latin-1 character.
// That pairing does not occur in real prose in any language this repo uses.
//
// Both patterns are built from code points rather than written literally, so
// this file does not trip its own check - which is the same problem the
// exemption below exists to solve, met one line earlier.
const MOJI_LEAD = "[\\u00C2\\u00C3\\u00E2]";
const MOJI_NEXT = "[\\u00A0-\\u00BF\\u0152\\u0153\\u0160\\u0161\\u0178\\u017D\\u017E"
                + "\\u0192\\u02C6\\u02DC\\u2013\\u2014\\u2018-\\u201A\\u201C-\\u201E"
                + "\\u2020-\\u2022\\u2026\\u2030\\u2039\\u203A\\u20AC\\u2122]";
const MOJI = new RegExp(MOJI_LEAD + MOJI_NEXT);

// A file that is ABOUT this fault must be able to quote it. Carry this marker
// anywhere in the file and its mangled sequences are treated as evidence
// rather than damage. Named in RELEASING.md so a writer finds it before they
// reach for deleting the check.
const MOJI_OPT_OUT = "verify-docs: mojibake is the subject here";

const TEXT_EXT = new Set([".md", ".html", ".shoddy", ".js", ".json", ".xml",
                          ".svg", ".cs", ".sh", ".ps1", ".txt", ".yml"]);
const SKIP_DIR = new Set([".git", "bin", "obj", "artifacts", "node_modules"]);

function textFiles(dir, out = []) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    if (e.isDirectory()) { if (!SKIP_DIR.has(e.name)) textFiles(path.join(dir, e.name), out); }
    else if (TEXT_EXT.has(path.extname(e.name).toLowerCase())) out.push(path.join(dir, e.name));
  }
  return out;
}

for (const file of textFiles(root)) {
  let text;
  try { text = fs.readFileSync(file, "utf8"); } catch { continue; }
  if (text.includes(MOJI_OPT_OUT)) continue;
  const rel = path.relative(root, file).replace(/\\/g, "/");
  text.split("\n").forEach((line, i) => {
    if (MOJI.test(line)) {
      console.log(rel + ":" + (i + 1) + ": UTF-8 mangled through CP1252 - "
                  + line.trim().slice(0, 72));
      bad++;
    }
  });
}

console.log(bad === 0 ? "ALL PAGES MATCH GROUND TRUTH" : bad + " mismatches");
process.exit(bad === 0 ? 0 : 1);
