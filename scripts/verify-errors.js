// MAINTAINER TOOL - read-only. Checks the docs against the sources.
//
//   node scripts/verify-errors.js
//
// Harvests every diagnostic string the toolchain can raise (Parser, Lexer,
// Engine, Compiler, the linter, the capability lint and the manifest verbs)
// and asserts each appears on the errors page, so a new or reworded message
// cannot ship undocumented. ALL MESSAGES COVERED is the pass; missing
// entries print and exit 1.

const fs = require("fs");
const path = require("path");
const root = path.resolve(__dirname, "..");
// The docs are ASCII (verify-docs holds them to it), so a message that
// carries an em dash is written on the page as &mdash;. Decode the handful
// of entities a message can contain before comparing, built from code
// points so this file stays ASCII too.
const ENT = { mdash: 0x2014, ndash: 0x2013, hellip: 0x2026, rsquo: 0x2019,
              lsquo: 0x2018, ldquo: 0x201c, rdquo: 0x201d, middot: 0xb7,
              rarr: 0x2192, larr: 0x2190, nbsp: 0xa0 };
const unescaped = f => fs.readFileSync(root + f, "utf8")
  .replace(/&amp;/g, "&").replace(/&lt;/g, "<").replace(/&gt;/g, ">")
  .replace(/&quot;/g, '"')
  .replace(/&([a-z]+);/g, (m, n) => (n in ENT ? String.fromCharCode(ENT[n]) : m));

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

// The linter, the capability lint and the manifest verbs build a message
// from several literals joined with +, often across lines, and a hole in
// an interpolated string may hold a quoted string of its own
// ({n.File ?? "?"}). The one-literal patterns above cannot read that, and
// for want of it these three files were never harvested: the build page
// promised every lint message was on the errors page, and the capability
// lint's two and the gate's five were not. So these files are tokenized:
// each literal is read whole with its holes as {}, a run of literals
// joined by + is one message, and a run counts when it is what a
// diagnostic call is handed: a warning or error list's Add, stderr's
// WriteLine, or a message builder's return. stdout carries the build's
// plan lines, not faults, and "mill: wrote ..." reports success. The
// page shows a lint message without the FILE:LINE: warning: its print
// carries, so that prefix is dropped.
function literalRuns(file) {
  const t = fs.readFileSync(root + "/" + file, "utf8");
  function read(at, interp) {          // at: the opening quote
    let s = "", k = at + 1;
    while (k < t.length) {
      const c = t[k];
      if (c === "\\") { s += c + t[k + 1]; k += 2; continue; }
      if (c === '"') return { s, end: k + 1 };
      if (interp && c === "{") {
        if (t[k + 1] === "{") { s += "{"; k += 2; continue; }
        let depth = 1;
        k++;
        while (k < t.length && depth > 0) {
          if (t[k] === '"') { k = read(k, false).end; continue; }
          if (t[k] === "{") depth++;
          else if (t[k] === "}") depth--;
          k++;
        }
        s += "{}";
        continue;
      }
      if (interp && c === "}" && t[k + 1] === "}") { s += "}"; k += 2; continue; }
      s += c;
      k++;
    }
    return { s, end: k };
  }
  function verbatimEnd(at) {           // at: the opening quote of @"..."
    let k = at + 1;
    while (k < t.length) {
      if (t[k] === '"') { if (t[k + 1] === '"') { k += 2; continue; } return k + 1; }
      k++;
    }
    return k;
  }
  const out = [];
  let i = 0;
  while (i < t.length) {
    if (t.startsWith("//", i)) { const nl = t.indexOf("\n", i); i = nl < 0 ? t.length : nl; continue; }
    if (t[i] === "'") { i += t[i + 1] === "\\" ? 4 : 3; continue; }          // a char literal
    if (t[i] === "@" && t[i + 1] === '"') { i = verbatimEnd(i + 1); continue; }
    const interp = t[i] === "$" && t[i + 1] === '"';
    if (!interp && t[i] !== '"') { i++; continue; }
    const start = i;
    let msg = "", k = i;
    for (;;) {
      const isInterp = t[k] === "$";
      const lit = read(isInterp ? k + 1 : k, isInterp);
      msg += lit.s;
      let j = lit.end;
      while (/\s/.test(t[j])) j++;
      if (t[j] !== "+") { i = lit.end; break; }
      j++;
      while (/\s/.test(t[j])) j++;
      if (t[j] === '"' || (t[j] === "$" && t[j + 1] === '"')) { k = j; continue; }
      i = lit.end;
      break;
    }
    const before = t.slice(Math.max(0, start - 60), start).replace(/\s+$/, "");
    if (/((?:[Ww]arnings|Verbose|errors|hits|UnknownWords)\.Add\(\(?|Console\.Error\.WriteLine\(|return)$/.test(before) &&
        !msg.startsWith("mill: wrote"))
      out.push(msg.replace(/^(\{\}:\{\}: )?(warning|note): /, ""));
  }
  return out;
}
msgs.push(...literalRuns("src/Shoddy.Compiler/Lint.cs"));
msgs.push(...literalRuns("src/Shoddy.Compiler/CapabilityLint.cs"));
msgs.push(...literalRuns("src/Shoddy.Compiler/Manifest.cs"));

// ShoddyWeave's own refusals are MSBuild <Error> items in the targets, with
// MSBuild's %(...) and $(...) where a message names the mill or the mode.
for (const f of ["src/Shoddy.Build/build/Shoddy.Build.targets", "src/Shoddy.Build/build/Shoddy.Library.targets"])
  for (const m of fs.readFileSync(root + "/" + f, "utf8").matchAll(/<Error\b[^>]*?\bCode="([^"]+)"[^>]*?\bText="([^"]*)"/g))
    msgs.push(m[1] + ": " + m[2].replace(/&quot;/g, '\\"').replace(/[%$]\([^)]*\)/g, "{}"));

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

if (uniq.length > 0) {
  console.log("MISSING from docs/errors.html (" + uniq.length + "):");
  for (const m of uniq) console.log(" -", m);
}

if (uniq.length === 0) console.log("ALL MESSAGES COVERED");
process.exit(uniq.length === 0 ? 0 : 1);
