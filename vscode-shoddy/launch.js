// Shoddy for VS Code — the debug launch, as plain logic.
// No `vscode` here, deliberately: everything this module decides is
// tested with `node --test vscode-shoddy/test/launch.test.js`, with no
// editor running.
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

/** The process a launch starts: the mill command (as millCommand answers
 *  it, {cmd, args}) serving the protocol on `port`. */
function debuggeeProcess(mill, port) {
    return { cmd: mill.cmd, args: [...mill.args, 'dap', '--listen', String(port)] };
}

/** The process a launch without debugging starts (Run File, or Run →
 *  Run Without Debugging): `mill run` on the program, exactly as at a
 *  command line, with --allow-net before the file when it is armed and
 *  the program's arguments after it. */
function runProcess(mill, config) {
    const net = config.allowNet ? ['--allow-net'] : [];
    return { cmd: mill.cmd, args: [...mill.args, 'run', ...net, config.program, ...config.args] };
}

/** The window's title: which program is running, and whether it is
 *  being debugged. */
function terminalName(program, noDebug) {
    return `shoddy ${noDebug ? 'run' : 'debug'}: ${path.basename(program)}`;
}

/** The arguments of the runInTerminal request that starts a launch, in
 *  a terminal window of the operating system's own (kind "external"),
 *  started in the program's folder with `env` added to its environment.
 *  Debugging, the window runs the mill serving the protocol on `port`;
 *  without debugging, it runs the program under `mill run`. */
function terminalRequest(config, mill, port, env) {
    const run = config.noDebug ? runProcess(mill, config) : debuggeeProcess(mill, port);
    return {
        kind: 'external',
        title: terminalName(config.program, config.noDebug),
        cwd: config.cwd,
        args: [run.cmd, ...run.args],
        env,
    };
}

/** One protocol message as the mill reads it: a Content-Length header,
 *  a blank line, then the JSON body. The length counts bytes, not
 *  characters. */
function frame(message) {
    const body = Buffer.from(JSON.stringify(message), 'utf8');
    return Buffer.concat([Buffer.from(`Content-Length: ${body.length}\r\n\r\n`, 'ascii'), body]);
}

/** The reverse of frame, over a stream: bytes go in as they arrive, in
 *  pieces of any size, and whole messages come out. */
class Unframer {
    constructor() {
        this.buf = Buffer.alloc(0);
    }

    /** Add the bytes in `chunk`; answer every message they complete. */
    push(chunk) {
        this.buf = Buffer.concat([this.buf, chunk]);
        const out = [];
        for (;;) {
            const end = this.buf.indexOf('\r\n\r\n');
            if (end < 0) break;
            const header = this.buf.subarray(0, end).toString('ascii');
            const m = /^Content-Length:\s*(\d+)\s*$/im.exec(header);
            if (!m) throw new Error(`a message header with no Content-Length: ${JSON.stringify(header)}`);
            const start = end + 4;
            const stop = start + Number(m[1]);
            if (this.buf.length < stop) break;
            out.push(JSON.parse(this.buf.subarray(start, stop).toString('utf8')));
            this.buf = this.buf.subarray(stop);
        }
        return out;
    }
}

module.exports = { resolveLaunch, debuggeeProcess, runProcess, terminalName, terminalRequest, frame, Unframer };
