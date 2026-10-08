// MAINTAINER TOOL - read-only. Checks the docs against the sources.
//
//   node scripts/verify-errors.js
//
// Harvests every diagnostic string the toolchain can raise (Parser, Lexer,
// Engine, Compiler) and asserts each appears on the errors page, so a new
// or reworded message cannot ship undocumented. ALL MESSAGES COVERED is
// the pass; missing entries print and exit 1.

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
