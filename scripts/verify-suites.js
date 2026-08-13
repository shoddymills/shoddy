// MAINTAINER TOOL - read-only. Proves the suite roster is complete.
//
//   node scripts/verify-suites.js
//
// Every tst/*.shoddy must be run by something in scripts/gate/steps.mjs —
// the MACHINE_SUITES or CORE_SUITES lists, or a dedicated step naming
// tst/<name>.shoddy — or sit in TST_EXCLUSIONS with its reason written
// down. Both directions are checked: a listed suite whose file is gone is
// as much a lie as a file no list runs. This check exists because suites
// keep falling out of hand-kept lists silently — nine at once historically,
// then seedterminaltest again in 2026 — and a suite that stops being run
// proves nothing while looking like it does.

const fs = require("fs");
const path = require("path");
const { pathToFileURL } = require("url");
const root = path.resolve(__dirname, "..");

(async () => {
  const stepsPath = path.join(root, "scripts", "gate", "steps.mjs");
  const steps = await import(pathToFileURL(stepsPath).href);

  // Dedicated steps run suites by literal path; harvest those spellings.
  const src = fs.readFileSync(stepsPath, "utf8");
  const dedicated = new Set(
    [...src.matchAll(/tst\/([a-z0-9-]+)\.shoddy/g)].map(m => m[1]));

  const files = fs.readdirSync(path.join(root, "tst"))
    .filter(f => f.endsWith(".shoddy"))
    .map(f => f.slice(0, -".shoddy".length));
  const present = new Set(files);

  const machine = new Set(steps.MACHINE_SUITES);
  const core = new Set(steps.CORE_SUITES.map(row => row[0]));
  const excl = steps.TST_EXCLUSIONS;

  let bad = 0;
  for (const f of files) {
    const runs = machine.has(f) || core.has(f) || dedicated.has(f);
    const excluded = Object.prototype.hasOwnProperty.call(excl, f);
    if (runs && excluded) {
      console.log(f + ": both run and excluded — pick one");
      bad++;
    } else if (!runs && !excluded) {
      console.log("tst/" + f + ".shoddy: nothing runs it and no exclusion names it — "
        + "add it to MACHINE_SUITES in scripts/gate/steps.mjs, or to "
        + "TST_EXCLUSIONS with the reason");
      bad++;
    }
  }
  for (const name of [...machine, ...core, ...Object.keys(excl)])
    if (!present.has(name)) {
      console.log(name + ": listed but tst/" + name + ".shoddy does not exist");
      bad++;
    }

  console.log(bad === 0 ? "ALL SUITES ACCOUNTED FOR" : bad + " problem(s)");
  process.exit(bad === 0 ? 0 : 1);
})();
