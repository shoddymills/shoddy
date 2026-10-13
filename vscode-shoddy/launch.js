// Shoddy for VS Code — the debug launch, as plain logic.
// No `vscode` here, deliberately: everything this module decides is
// tested with `node --test vscode-shoddy/test/launch.test.js`, with no
// editor running.
const path = require('path');

/** The two places a debugged program can have its console:
 *  integratedTerminal — the default: a terminal panel whose process is the
 *    mill itself (`mill dap --listen PORT`). The program reads that
 *    terminal's keyboard and draws on its screen, exactly as under
 *    `mill run`; the editor talks to the debugger over a loopback socket.
 *  internalConsole — the Debug Console: output arrives as text, and the
 *    program has no keyboard (every Input reads end of input). */
const CONSOLES = ['integratedTerminal', 'internalConsole'];

/** Fill in a launch configuration after VS Code has substituted its
 *  variables: the program and the folder it runs from, both absolute, its
 *  arguments as strings, and which console it gets. A relative path is
 *  taken against the workspace folder, as VS Code takes its own; with no
 *  `cwd`, the program runs from its own folder. Throws on a configuration
 *  that cannot be launched, with a message that says what to fix.
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
    if (out.console === undefined) out.console = 'integratedTerminal';
    if (!CONSOLES.includes(out.console))
        throw new Error(`"console" must be one of ${CONSOLES.map((c) => `"${c}"`).join(', ')}`);
    return out;
}

/** The process an integrated-terminal launch starts: the mill command (as
 *  millCommand answers it, {cmd, args}) serving the protocol on `port`. */
function debuggeeProcess(mill, port) {
    return { cmd: mill.cmd, args: [...mill.args, 'dap', '--listen', String(port)] };
}

/** The terminal's title: which program is being debugged. */
function terminalName(program) {
    return `shoddy debug: ${path.basename(program)}`;
}

module.exports = { CONSOLES, resolveLaunch, debuggeeProcess, terminalName };
