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

if (uniq.length > 0) {
  console.log("MISSING from docs/errors.html (" + uniq.length + "):");
  for (const m of uniq) console.log(" -", m);
}

if (uniq.length === 0) console.log("ALL MESSAGES COVERED");
process.exit(uniq.length === 0 ? 0 : 1);
