// MAINTAINER TOOL - read-only. Checks the docs against the sources.
//
//   node scripts/verify-errors.js
//
// Harvests every diagnostic string the toolchain can raise (Parser, Lexer,
// Engine, Compiler) and asserts each appears on the errors page, so a new
// or reworded message cannot ship undocumented. ALL MESSAGES COVERED is
// the pass; missing entries print and exit 1.
//
// SECOND SET, SECOND PAGE: Fettler's screening refusals, against
// docs/fettler.html. The screen is the one part of Fettler a person meets
// ONLY as a refusal - there is no successful output that shows what it did -
// so a message it can raise and nobody wrote down is a dead end: somebody
// reads it, searches the documentation, and finds nothing at all.
//
// Only the SCREENING refusals, deliberately. Fettler raises hundreds of
// messages and documenting every one would be a second errors.html that
// nobody would keep up. These are the ones a screened tree produces, and
// their whole purpose is to be acted on by a person who has just been
// refused something they expected to get.

const fs = require("fs");
const path = require("path");
const root = path.resolve(__dirname, "..");
const unescaped = f => fs.readFileSync(root + f, "utf8")
  .replace(/&amp;/g, "&").replace(/&lt;/g, "<").replace(/&gt;/g, ">")
  .replace(/&quot;/g, '"');

const page = unescaped("/docs/errors.html");

function statics(file, re) {
  const t = fs.readFileSync(root + "/" + file, "utf8");
  return [...t.matchAll(re)].map(m => m[1]);
}
const msgs = [];
msgs.push(...statics("src/Shoddy.Devil/Parser.cs", /Die\([^,]+, ?\$?"((?:[^"\\]|\\.)+)"/g));
msgs.push(...statics("src/Shoddy.Devil/Lexer.cs", /ShoddyError\([^;]*?\$?"((?:[^"\\]|\\.)+)"/g));
msgs.push(...statics("src/Shoddy.Runtime/Engine.cs", /Die\(line, ?\$?"((?:[^"\\]|\\.)+)"/g));
msgs.push(...statics("src/Shoddy.Compiler/Machines.cs", /ShoddyError\([^;]*?\$?"((?:[^"\\]|\\.)+)"/g));
msgs.push(...statics("src/Shoddy.Compiler/CodeGen.cs", /ShoddyError\([^;]*?\$?"((?:[^"\\]|\\.)+)"/g));
msgs.push(...statics("src/Shoddy.Compiler/Constants.cs", /ShoddyError\([^;]*?\$?"((?:[^"\\]|\\.)+)"/g));
msgs.push(...statics("src/Shoddy.Compiler/Weaver.cs", /ShoddyError\([^;]*?\$?"((?:[^"\\]|\\.)+)"/g));

const pageNorm = page.replace(/\s+/g, " ");
const missing = [];
for (const raw of msgs) {
  const parts = raw.split(/\{[^}]*\}/).map(s => s.trim()).filter(s => s.length >= 8);
  if (parts.length === 0) continue;
  const frag = parts.reduce((a, b) => (a.length >= b.length ? a : b));
  const clean = frag.replace(/\\"/g, '"').replace(/\\\\/g, "\u005c").replace(/\s+/g, " ");
  if (!pageNorm.includes(clean)) missing.push(clean);
}
const uniq = [...new Set(missing)];

// ---- Fettler's screening refusals ----

const screenSources = ["fettler/Fettler/Core/Sidecar.cs",
                       "fettler/Fettler/Core/Disclosure.cs",
                       "fettler/Fettler/Core/Bench.cs"];

// A literal handed straight to a Screened failure. Died() is matched too:
// it composes a sentence AROUND the literal, so the literal is still the
// part a person reads and then searches the docs for.
const direct = [];
for (const f of screenSources)
  direct.push(...statics(f, /Outcome\.Screened,\s*(?:Died\()?\$?"((?:[^"\\]|\\.)+)"/g));

// A REFUSAL SITE THE PATTERN CANNOT READ is worse than an undocumented one,
// because it passes silently and goes on passing. So the sites are counted
// independently and the two numbers compared: every `Outcome.Screened,` in
// these files either yields a literal above or is one of the composed forms
// handled below, and a refusal written in some third shape makes this fail
// rather than quietly shrinking what the gate covers.
let sites = 0;
let composed = 0;
for (const f of screenSources) {
  const text = fs.readFileSync(root + "/" + f, "utf8");
  sites += (text.match(/Outcome\.Screened,/g) || []).length;
  composed += (text.match(/Outcome\.Screened, Screen\.Describe/g) || []).length;
}

if (direct.length !== sites - composed) {
  console.log("MISSING: " + (sites - composed) + " screening refusal(s) raise a message and "
            + "verify-errors.js could only read " + direct.length + " of them. One is written "
            + "in a shape the pattern does not match, so nothing is checking its wording.");
  process.exit(1);
}

const screen = [...direct];

// The two sentences composed rather than passed: Died's own wrapper, and
// the refusal Describe builds out of the findings - which is the one nearly
// every screened tree actually produces.
screen.push(...statics("fettler/Fettler/Core/Sidecar.cs",
                       /\$"(the screening sidecar failed:[^"\\]*)"/g));
screen.push(...statics("fettler/Fettler/Core/Screen.cs",
                       /return \$"(this response would disclose[^"\\]*)"/g));

// A harvest that quietly found nothing would pass this gate for ever, and
// would go on passing it as messages were added. The count is not asserted
// - it moves whenever one is - but zero is the one number that can never be
// right, and it means the patterns above have stopped matching the source.
if (screen.length === 0) {
  console.log("MISSING: no screening refusal was harvested at all - the patterns in "
            + "verify-errors.js no longer match Fettler's sources");
  process.exit(1);
}

// The same rule as above: the longest literal run between interpolations is
// what has to appear on the page. Matching the whole message would fail on
// every message carrying a value, and matching the shortest would pass on
// almost anything.
//
// No backslash unescaping here, unlike the loop above. A C# escape has never
// appeared inside one of these, and a step for a case that does not exist is
// how a check acquires a bug nobody can reach to fix.
const fettlerNorm = unescaped("/docs/fettler.html").replace(/\s+/g, " ");
const undocumented = [];
for (const raw of screen) {
  const parts = raw.split(/\{[^}]*\}/).map(s => s.trim()).filter(s => s.length >= 8);
  if (parts.length === 0) continue;
  const frag = parts.reduce((a, b) => (a.length >= b.length ? a : b)).replace(/\s+/g, " ");
  if (!fettlerNorm.includes(frag)) undocumented.push(frag);
}
const alsoMissing = [...new Set(undocumented)];

for (const [where, list] of [["docs/errors.html", uniq],
                             ["docs/fettler.html", alsoMissing]]) {
  if (list.length === 0) continue;
  console.log("MISSING from " + where + " (" + list.length + "):");
  for (const m of list) console.log(" -", m);
}

const total = uniq.length + alsoMissing.length;
if (total === 0) console.log("ALL MESSAGES COVERED");
process.exit(total === 0 ? 0 : 1);
