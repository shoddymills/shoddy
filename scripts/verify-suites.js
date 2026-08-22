// MAINTAINER TOOL - read-only. Proves the suite roster is complete.
//
//   node scripts/verify-suites.js
//
// Every tst/*.shoddy must be named by scripts/suites.mjs - CORE_SUITES,
// MACHINE_SUITES or DEDICATED, all of which build.ps1/build.sh run - or
// sit in TST_EXCLUSIONS with its reason written down. Both directions are
// checked: a listed suite whose file is gone is as much a lie as a file no
// list runs. This check exists because suites keep falling out of
// hand-kept lists silently - nine at once historically, then
// seedterminaltest again in 2026 - and a suite that stops being run proves
// nothing while looking like it does.
//
// The mills need no roster: build.ps1/build.sh walk every directory under
// mills/ and run its own build script's `test` verb, so a new mill cannot
// fall out of a list that does not exist. What CAN still go wrong is a
// mill shipping only one of its build-script twins - the walk runs
// build.ps1 on Windows and build.sh everywhere else, so a mill with one
// twin is a mill that fails for half the maintainers. That is checked
// here.

const fs = require("fs");
const path = require("path");
const { pathToFileURL } = require("url");
const root = path.resolve(__dirname, "..");

(async () => {
  const rosterPath = path.join(root, "scripts", "suites.mjs");
  const roster = await import(pathToFileURL(rosterPath).href);

  const files = fs.readdirSync(path.join(root, "tst"))
    .filter(f => f.endsWith(".shoddy"))
    .map(f => f.slice(0, -".shoddy".length));
  const present = new Set(files);

  const core = new Set(roster.CORE_SUITES);
  const machine = new Set(roster.MACHINE_SUITES);
  const dedicated = new Set(roster.DEDICATED);
  const excl = roster.TST_EXCLUSIONS;

  let bad = 0;
  for (const f of files) {
    const runs = machine.has(f) || core.has(f) || dedicated.has(f);
    const excluded = Object.prototype.hasOwnProperty.call(excl, f);
    if (runs && excluded) {
      console.log(f + ": both run and excluded — pick one");
      bad++;
    } else if (!runs && !excluded) {
      console.log("tst/" + f + ".shoddy: nothing runs it and no exclusion names it — "
        + "add it to MACHINE_SUITES in scripts/suites.mjs, or to "
        + "TST_EXCLUSIONS with the reason");
      bad++;
    }
  }
  for (const name of [...core, ...machine, ...dedicated, ...Object.keys(excl)])
    if (!present.has(name)) {
      console.log(name + ": listed but tst/" + name + ".shoddy does not exist");
      bad++;
    }

  // Both twins, or the suite passes on one platform and not the other.
  const millsDir = path.join(root, "mills");
  const onDisk = fs.readdirSync(millsDir, { withFileTypes: true })
    .filter(e => e.isDirectory())
    .map(e => e.name)
    .filter(n => fs.existsSync(path.join(millsDir, n, "build.ps1"))
              || fs.existsSync(path.join(millsDir, n, "build.sh")));

  for (const m of onDisk)
    for (const twin of ["build.ps1", "build.sh"])
      if (!fs.existsSync(path.join(millsDir, m, twin))) {
        console.log("mills/" + m + "/" + twin + " is missing — the suite walk runs "
          + "build.ps1 on Windows and build.sh elsewhere, so this mill is "
          + "unprovable on one of them");
        bad++;
      }

  console.log(bad === 0
    ? "ALL SUITES AND MILLS ACCOUNTED FOR"
    : bad + " problem(s)");
  process.exit(bad === 0 ? 0 : 1);
})();
