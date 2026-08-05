// MAINTAINER TOOL - read-only. Surveys zero-arg Defs whose bodies are
// constants, which are the ones a Let should be holding.
//
//   node scripts/survey-constants.js            the table
//   node scripts/survey-constants.js --list      every convertible, by file
//   node scripts/survey-constants.js --check     exit 1 if any remain
//
// A machine may carry a top-level Let that binds a constant, so a zero-arg
// Def whose body is a list, an array or a record is a value rebuilt on
// every call - and, for a list, a value that is never equal to itself,
// since a List compares by identity. Those become a private Let with the
// Def as its one-line reader. Mills, tutorials and test programs go
// further: their Lets have always been legal and may call anything, so
// every constant-bodied zero-arg Def there becomes a plain Let.
//
// What stays: a machine Def returning a bare Number, String or Boolean
// (a scalar compares by value and costs nothing to push), and anything
// whose body calls a word, everywhere, because that is a computation.
//
// The classification is textual and deliberately simple. A body counts as
// constant when every name in it is a type constructor, ToArray or Dim -
// the two words the compiler folds - and everything else is a literal or
// an operator. Arithmetic inside a constructor's arguments does not make
// a fourth category: EngUnit("KNOT", "SPEED", 1852 / 3600, 0) is one
// constructor constant.

const fs = require("fs");
const path = require("path");
const root = path.resolve(__dirname, "..");

// ---- gathering the files ---------------------------------------------

function walk(dir, out) {
  if (!fs.existsSync(dir)) return out;
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) {
      if (e.name === "bin" || e.name === "obj" || e.name === "dat") continue;
      walk(p, out);
    } else if (e.name.endsWith(".shoddy")) out.push(p);
  }
  return out;
}

const rel = p => path.relative(root, p).replace(/\\/g, "/");

function areaOf(p) {
  const r = rel(p);
  if (r.startsWith("machines/")) return "machines";
  if (r.startsWith("mills/")) {
    return /(^|\/)tests?\//.test(r) || /\/test[^/]*\.shoddy$/.test(r)
      ? "mill tests" : "mill sources";
  }
  if (r.startsWith("tutorials/")) return "tutorials";
  if (r.startsWith("tst/")) return "tst";
  return null;
}

const files = [];
for (const d of ["machines", "mills", "tutorials", "tst"]) walk(path.join(root, d), files);

// ---- every type name in the tree, because a constructor call looks
// exactly like a word call until you know which names are types --------

const types = new Set(["Some", "None", "Ok", "Err"]);   // the prelude's own
for (const f of files)
  for (const m of fs.readFileSync(f, "utf8").matchAll(/^Type\s+([A-Za-z_][\w]*)/gm))
    types.add(m[1]);

// The two words a constant initializer may name: the compiler folds both
// at codegen, and there is no array literal in the language, so without
// them the array case could not be written at all.
const folded = new Set(["ToArray", "Dim"]);

// ---- classifying one body --------------------------------------------

const OPERATOR_WORDS = new Set(["Mod", "And", "Or", "Not", "True", "False"]);

// Number, String and Boolean literals, operators, brackets and commas are
// what is left once the names are accounted for.
function bodyKind(body) {
  const stripped = body.replace(/"(?:[^"]|"")*"/g, '""');
  const names = [...stripped.matchAll(/[A-Za-z_][\w]*/g)].map(m => m[0])
    .filter(n => !OPERATOR_WORDS.has(n));
  const calls = names.filter(n => !types.has(n) && !folded.has(n));
  if (calls.length > 0) return { kind: "calls a word", why: calls[0] };
  if (/^\s*\{/.test(body)) return { kind: "list literal" };
  if (names.length > 0) return { kind: "constructor over constants" };
  if (/[^\s]/.test(stripped)) return { kind: "scalar literal" };
  return { kind: "calls a word", why: "(empty)" };   // a void Def, not a constant
}

// ---- reading the zero-arg Defs out of a file -------------------------

function defsIn(file) {
  const lines = fs.readFileSync(file, "utf8").split(/\r?\n/);
  const out = [];
  for (let i = 0; i < lines.length; i++) {
    const m = /^Def\s+([A-Za-z_][\w]*)\s*\(\s*\)/.exec(lines[i]);
    if (!m || m[1].toUpperCase() === "MAIN") continue;
    // The body runs to the next line that starts in column one. Rem
    // lines inside it are commentary, not code.
    const body = [];
    for (let j = i + 1; j < lines.length; j++) {
      if (/^\S/.test(lines[j])) break;
      const t = lines[j].trim();
      if (t === "" || /^Rem\b/.test(t)) continue;
      body.push(t);
    }
    out.push({ name: m[1], line: i + 1, body: body.join(" ") });
  }
  return out;
}

// ---- the survey -------------------------------------------------------

const KINDS = ["scalar literal", "list literal", "constructor over constants", "calls a word"];
const AREAS = ["machines", "mill sources", "mill tests", "tutorials", "tst"];
const counts = {};
for (const a of AREAS) counts[a] = Object.fromEntries(KINDS.map(k => [k, 0]));

const convertible = [];
for (const f of files) {
  const area = areaOf(f);
  if (!area) continue;
  for (const d of defsIn(f)) {
    const { kind } = bodyKind(d.body);
    counts[area][kind]++;
    // D9: in a machine only composite constants convert, because that is
    // where identity and per-call allocation bite. D10: everywhere else
    // every constant does, because a mill's Let has always been legal.
    const composite = kind === "list literal" || kind === "constructor over constants";
    if (area === "machines" ? composite : kind !== "calls a word")
      convertible.push({ file: rel(f), line: d.line, name: d.name, kind });
  }
}

const argv = process.argv.slice(2);
if (argv.includes("--list")) {
  for (const c of convertible)
    console.log(`${c.file}:${c.line}  ${c.name}()  ${c.kind}`);
  console.log(`${convertible.length} convertible`);
} else {
  const w = 30;
  const head = ["body is".padEnd(w), ...AREAS.map(a => a.padStart(13))].join("");
  console.log(head);
  console.log("-".repeat(head.length));
  for (const k of KINDS)
    console.log([k.padEnd(w), ...AREAS.map(a => String(counts[a][k]).padStart(13))].join(""));
  console.log("-".repeat(head.length));
  console.log(["total".padEnd(w),
    ...AREAS.map(a => String(KINDS.reduce((s, k) => s + counts[a][k], 0)).padStart(13))].join(""));
  console.log("");
  console.log(convertible.length === 0
    ? "NO CONVERTIBLES REMAIN"
    : `${convertible.length} CONVERTIBLE (--list to name them)`);
}

if (argv.includes("--check")) process.exit(convertible.length === 0 ? 0 : 1);
