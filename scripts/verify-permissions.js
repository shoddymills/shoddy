// MAINTAINER TOOL - checks (and fixes) the executable bit on every tracked
// script, using git's own index rather than the filesystem.
//
//   node scripts/verify-permissions.js            fix: stage any missing bit
//   node scripts/verify-permissions.js --check    report and fail, fix nothing
//
// A script committed without +x runs fine on Windows, where NTFS has no
// execute bit to check, and only fails once something honors the bit
// literally - the Linux CI runner, WSL, a Mac. v1.8.0 shipped four scripts
// this way; one of them broke the Release workflow (mills/devils-dust/build.sh,
// "Permission denied", exit 126) before the rest were found by hand.
//
// Finds every tracked file that opens with a shebang line and is missing
// +x. LOCALLY it fixes the bit directly in the index - `git update-index
// --chmod=+x`, not a filesystem chmod, because a checkout with
// core.filemode=false (the default on Windows) makes a raw chmod invisible
// to git regardless of what the filesystem actually did - and exits 0:
// once it has run, the tree is correct, and `git status` shows the staged
// fix to commit like any other change.
//
// IN CI it runs with --check, which fixes nothing and FAILS instead. A
// runner's index is thrown away at the end of the job, so a fixing run
// there would repair the bit, print a cheerful line, exit 0, and let the
// fault sail through to the release that honors it literally. A gate that
// repairs the evidence is not a gate.

const { execSync } = require("child_process");
const fs = require("fs");
const path = require("path");
const root = path.resolve(__dirname, "..");
const checkOnly = process.argv.includes("--check");

const lines = execSync("git ls-files -s", { cwd: root, encoding: "utf8" })
  .trim().split("\n").filter(Boolean);
const found = [];

for (const line of lines) {
  const m = line.match(/^(\d+) [0-9a-f]+ \d+\t(.+)$/);
  if (!m) continue;
  const [, mode, file] = m;
  if (mode === "120000") continue; // symlink - no exec bit to check
  let head;
  try {
    const fd = fs.openSync(path.join(root, file), "r");
    const buf = Buffer.alloc(2);
    fs.readSync(fd, buf, 0, 2, 0);
    fs.closeSync(fd);
    head = buf.toString("utf8");
  } catch { continue; } // unreadable in the working tree - not this check's problem
  if (head !== "#!" || mode === "100755") continue;
  if (!checkOnly) execSync(`git update-index --chmod=+x -- "${file}"`, { cwd: root });
  found.push(file);
}

if (found.length === 0) {
  console.log("ALL EXECUTABLE BITS OK");
  process.exit(0);
}
if (checkOnly) {
  console.log("MISSING " + found.length + " EXECUTABLE BIT(S):");
  for (const f of found) console.log(" - " + f);
  console.log("fix locally: node scripts/verify-permissions.js (stages the fix), then commit");
  process.exit(1);
}
console.log("FIXED " + found.length + " EXECUTABLE BIT(S):");
for (const f of found) console.log(" - " + f);
process.exit(0);
