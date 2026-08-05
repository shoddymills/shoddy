// MAINTAINER TOOL - checks (and fixes) the executable bit on every tracked
// script, using git's own index rather than the filesystem.
//
//   node scripts/verify-permissions.js
//
// A script committed without +x runs fine on Windows, where NTFS has no
// execute bit to check, and only fails once something honors the bit
// literally - the Linux CI runner, WSL, a Mac. v1.8.0 shipped four scripts
// this way; one of them broke the Release workflow (mills/devils-dust/build.sh,
// "Permission denied", exit 126) before the rest were found by hand.
//
// Finds every tracked file that opens with a shebang line and is missing
// +x, then fixes it directly in the index: `git update-index --chmod=+x`,
// not a filesystem chmod, because a checkout with core.filemode=false (the
// default on Windows) makes a raw chmod invisible to git regardless of
// what the filesystem actually did.
//
// ALL EXECUTABLE BITS OK is the pass (nothing to fix). FIXED N EXECUTABLE
// BIT(S) means the correction is now staged - `git status` will show it;
// commit it like any other change. Either way this exits 0: once it has
// run, the tree is correct.

const { execSync } = require("child_process");
const fs = require("fs");
const path = require("path");
const root = path.resolve(__dirname, "..");

const lines = execSync("git ls-files -s", { cwd: root, encoding: "utf8" })
  .trim().split("\n").filter(Boolean);
const fixed = [];

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
  execSync(`git update-index --chmod=+x -- "${file}"`, { cwd: root });
  fixed.push(file);
}

console.log(fixed.length === 0 ? "ALL EXECUTABLE BITS OK"
  : "FIXED " + fixed.length + " EXECUTABLE BIT(S):");
for (const f of fixed) console.log(" - " + f);
process.exit(0);
