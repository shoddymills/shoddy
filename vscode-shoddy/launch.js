// Shoddy for VS Code — the debug launch's configuration, as plain logic.
// No `vscode` here, deliberately: everything this module decides is
// tested with `node --test vscode-shoddy/test/launch.test.js`, with no
// editor running. The launch itself is the mill's: `mill dap` is the
// adapter VS Code talks to, and it asks VS Code for the terminal window
// the program runs in (src/Shoddy.Perch/PerchAdapter.cs).
const path = require('path');

/** Fill in a launch configuration after VS Code has substituted its
 *  variables: the program and the folder it runs from, both absolute, and
 *  its arguments as strings. A relative path is taken against the
 *  workspace folder, as VS Code takes its own; with no `cwd`, the program
 *  runs from its own folder. Throws on a configuration that cannot be
 *  launched, with a message that says what to fix.
 *  `folder` is the workspace folder's path, or undefined. */
function resolveLaunch(config, folder) {
    if (config.request !== 'launch' || !config.program) return config;
    const out = { ...config };
    const base = folder || path.dirname(out.program);
    if (!path.isAbsolute(out.program)) out.program = path.resolve(base, out.program);
    out.cwd = out.cwd ? path.resolve(base, out.cwd) : path.dirname(out.program);
    if (out.args === undefined) out.args = [];
    if (!Array.isArray(out.args)) throw new Error('"args" must be a list of strings, as in ["a", "b"]');
    out.args = out.args.map((a) => String(a));
    out.allowNet = out.allowNet === true;
    // There is one place a program runs: a terminal window of the
    // operating system's own. A "console" setting, copied from another
    // language's launch.json, is refused rather than ignored, so nobody
    // believes they chose something they did not get.
    if (out.console !== undefined)
        throw new Error('"console" is not a setting here: a Shoddy program always runs in a terminal window of its own. Remove it.');
    return out;
}

module.exports = { resolveLaunch };
