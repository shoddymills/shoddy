// Shoddy for VS Code — mill commands.
// Plain JavaScript, no build step: the folder is the extension.
const vscode = require('vscode');
const cp = require('child_process');
const fs = require('fs');
const net = require('net');
const path = require('path');
const launch = require('./launch');

// This extension ships a mill and the machines under its own directory
// (see `build.ps1 stage`), so installing the .vsix is the whole install.
// A workspace that builds its own mill still wins: a Shoddy hacker's
// bin/mill is the one they just changed, and the bundled copy is only
// the fallback for everyone else.
let bundleRoot = null;

/** How to launch the mill bundled with this extension — a command and
 * the arguments that must precede the subcommand — or null when it
 * wasn't staged.
 *
 * The staged build is framework-dependent and built without a runtime
 * identifier, so the only native launcher in it is the Windows
 * mill.exe. Everywhere else the portable mill.dll is run through the
 * `dotnet` muxer, which the .NET runtime this extension already
 * requires puts on PATH. */
function bundledMill() {
    if (!bundleRoot) return null;
    const dir = path.join(bundleRoot, 'mill');
    const exe = path.join(dir, process.platform === 'win32' ? 'mill.exe' : 'mill');
    if (fs.existsSync(exe)) return { cmd: exe, args: [] };
    const dll = path.join(dir, 'mill.dll');
    return fs.existsSync(dll) ? { cmd: 'dotnet', args: [dll] } : null;
}

/** The machines bundled with this extension, or null when not staged. */
function bundledLib() {
    if (!bundleRoot) return null;
    const dir = path.join(bundleRoot, 'machines');
    return fs.existsSync(dir) ? dir : null;
}

/** How to launch the mill: the shoddy.millPath setting, else the
 * workspace's bin/mill (bin/mill.exe on Windows), else the bundled
 * copy, else `mill` on PATH. Returns {cmd, args} — args is empty for
 * every route but the bundled `dotnet mill.dll` one. */
function millCommand() {
    const configured = vscode.workspace.getConfiguration('shoddy').get('millPath');
    if (configured) return { cmd: configured, args: [] };
    for (const folder of vscode.workspace.workspaceFolders || []) {
        const candidate = path.join(folder.uri.fsPath, 'bin', 'mill');
        if (fs.existsSync(candidate)) return { cmd: candidate, args: [] };
        if (process.platform === 'win32' && fs.existsSync(candidate + '.exe'))
            return { cmd: candidate + '.exe', args: [] };
    }
    return bundledMill() || { cmd: 'mill', args: [] };
}

/** The environment for a mill we spawn ourselves. An Include the mill
 * can't find beside its source falls back to $SHODDYLIB, so pointing
 * that at the bundled machines/ makes `Include "seq.shoddy"` work in a
 * scratch file anywhere. A SHODDYLIB the user set is theirs — we never
 * override it, since it's how a checkout is pinned to its own machines. */
function millEnv() {
    const lib = bundledLib();
    return (lib && !process.env.SHODDYLIB) ? { SHODDYLIB: lib } : {};
}

// A program runs from its own folder, wherever the workspace is opened,
// so a relative path in it (`files/words.txt`) means the same thing in
// VS Code as it does from the mill's build wrapper, which changes into
// the mill folder first. That is one terminal per folder: a terminal's
// starting folder is fixed when it is created, and moving one with a cd
// would mean writing a different command for every shell.
const terms = new Map();
function terminal(dir) {
    let t = terms.get(dir);
    if (!t || t.exitStatus !== undefined) {
        t = vscode.window.createTerminal({ name: `shoddy mill: ${path.basename(dir)}`, cwd: dir });
        terms.set(dir, t);
    }
    return t;
}

/** A free loopback port: bound, read and released. */
function freePort() {
    return new Promise((resolve, reject) => {
        const srv = net.createServer();
        srv.once('error', reject);
        srv.listen(0, '127.0.0.1', () => {
            const { port } = srv.address();
            srv.close(() => resolve(port));
        });
    });
}

/** True while process `pid` exists. EPERM means it exists and belongs to
 * someone else. */
function alive(pid) {
    try { process.kill(pid, 0); return true; }
    catch (e) { return e.code === 'EPERM'; }
}

/** How long the mill has to start serving the debugger after its window
 * opens. The first launch after an install weaves the instrumented
 * machines the program includes, which takes seconds, not minutes. */
const START_MS = 60000;

/** The debug adapter for a launch, running inside the extension.
 *
 * A program being debugged runs in a terminal window of the operating
 * system's own, never inside VS Code: it has that window's keyboard and
 * screen, at that window's size, exactly as under `mill run`. VS Code
 * opens the window when its debug adapter asks it to (runInTerminal, kind
 * "external"), and the user's terminal.external.* settings choose which
 * terminal that is. The mill in the window is not that adapter: it serves
 * the protocol on a loopback port. So this class is the adapter VS Code
 * talks to. It asks for the window, waits for the mill to answer on the
 * port, then relays every message both ways unchanged. */
class ExternalLaunch {
    constructor(config) {
        this.config = config;
        this.emitter = new vscode.EventEmitter();
        this.onDidSendMessage = this.emitter.event;
        this.pending = [];      // VS Code's messages, held until the mill is reached
        this.socket = null;
        this.started = false;
        this.ended = false;     // no mill to relay to, now or ever
        this.told = false;      // VS Code has had the terminated event
        this.asked = null;      // the seq of the runInTerminal request, until answered
    }

    handleMessage(message) {
        if (message.type === 'response' && message.request_seq === this.asked) {
            this.asked = null;
            if (message.success && this.config.noDebug) this.ran();
            else if (message.success) this.connect(message.body || {});
            else this.fail(`VS Code could not open a terminal window for the program: ${message.message || 'it gave no reason'}`);
            return;
        }
        if (this.socket && !this.socket.destroyed) { this.socket.write(launch.frame(message)); return; }
        if (this.ended) { this.answerAfterEnd(message); return; }
        this.pending.push(message);
        if (!this.started) this.start(message);
    }

    /** A request that arrives with no mill left to answer it. VS Code
     * ends every session with disconnect and waits for the reply, so that
     * one succeeds. Without debugging there was never a mill to answer,
     * and the program is running in its window, so launch succeeds too
     * (refusing it would put an error in front of a program that started
     * fine). Anything else is refused. */
    answerAfterEnd(m) {
        if (m.type !== 'request') return;
        const ok = m.command === 'disconnect' || this.config.noDebug === true;
        this.emitter.fire({
            type: 'response', seq: 0, request_seq: m.seq, command: m.command,
            success: ok, message: ok ? undefined : 'the program has ended',
        });
    }

    /** On VS Code's first message, which is always initialize: ask for the
     * window. */
    async start(first) {
        this.started = true;
        const args = first.arguments || {};
        if (first.command !== 'initialize' || !args.supportsRunInTerminalRequest) {
            this.fail('this editor cannot open a terminal window, and a Shoddy program being debugged runs in one');
            return;
        }
        // A port only when debugging: without, nothing listens on one.
        let port;
        if (!this.config.noDebug) {
            try { port = await freePort(); }
            catch (e) { this.fail(`no loopback port is free for the debugger: ${e.message}`); return; }
            if (this.ended) return;
            this.port = port;
        }
        // The mill numbers its own messages from 1 once it is reached; this
        // request is answered before then, so the two never overlap.
        this.asked = 1;
        this.emitter.fire({
            type: 'request', seq: this.asked, command: 'runInTerminal',
            arguments: launch.terminalRequest(this.config, millCommand(), port, millEnv()),
        });
    }

    /** Without debugging, the window is the whole of the job: the program
     * runs there under `mill run`, and there is no debugger to talk to.
     * Answer initialize, then end the session; the window stays. */
    ran() {
        for (const m of this.pending) {
            if (m.type !== 'request') continue;
            this.emitter.fire({
                type: 'response', seq: 0, request_seq: m.seq, command: m.command,
                success: true, body: {},
            });
        }
        this.pending = [];
        this.end();
    }

    /** The window is open: connect to the mill as soon as it listens. A
     * refused connection means it has not started yet, so try again,
     * until the process is gone or START_MS has passed. */
    connect(body) {
        const pid = body.processId;
        const deadline = Date.now() + START_MS;
        const attempt = () => {
            if (this.ended) return;
            const sock = net.connect(this.port, '127.0.0.1');
            sock.once('connect', () => {
                sock.removeAllListeners('error');
                this.relay(sock);
            });
            sock.once('error', () => {
                sock.destroy();
                if (pid && !alive(pid)) this.fail('the mill ended before its debugger started; its window says why');
                else if (Date.now() > deadline) this.fail(`the mill did not start its debugger within ${START_MS / 1000} seconds`);
                else setTimeout(attempt, 100);
            });
        };
        attempt();
    }

    relay(sock) {
        if (this.ended) { sock.destroy(); return; }
        this.socket = sock;
        const unframer = new launch.Unframer();
        sock.on('data', (chunk) => {
            let messages;
            try { messages = unframer.push(chunk); }
            catch (e) { this.fail(`the mill sent a message that could not be read: ${e.message}`); return; }
            for (const m of messages) {
                if (m.type === 'event' && m.event === 'terminated') this.told = true;
                this.emitter.fire(m);
            }
        });
        // An error is always followed by close, which ends the session.
        sock.on('error', () => {});
        sock.on('close', () => this.end());
        for (const m of this.pending) sock.write(launch.frame(m));
        this.pending = [];
    }

    /** The session is over. If the mill did not say so itself (it was
     * killed, or its window was closed), say so for it, or VS Code would
     * wait for it forever. */
    end() {
        if (this.ended) return;
        this.ended = true;
        if (this.told) return;
        this.told = true;
        this.emitter.fire({ type: 'event', seq: 0, event: 'terminated' });
    }

    /** Refuse every request still waiting, with `reason`, and end. */
    fail(reason) {
        if (this.ended) return;
        for (const m of this.pending) {
            if (m.type !== 'request') continue;
            this.emitter.fire({
                type: 'response', seq: 0, request_seq: m.seq, command: m.command,
                success: false, message: reason,
            });
        }
        this.pending = [];
        if (this.socket) this.socket.destroy();
        this.end();
    }

    dispose() {
        this.ended = true;
        if (this.socket) this.socket.destroy();
        this.emitter.dispose();
    }
}

/** Save and return the active editor's file path, or null. */
async function activeFile() {
    const editor = vscode.window.activeTextEditor;
    if (!editor || editor.document.isUntitled) {
        vscode.window.showErrorMessage('Shoddy: no saved .shoddy file is active.');
        return null;
    }
    await editor.document.save();
    return editor.document.fileName;
}

/** Run File: the program in a terminal window of its own, under `mill
 * run`, from its own folder. It goes through a launch without debugging,
 * the same route as Run → Run Without Debugging, because that is how VS
 * Code opens a terminal window outside itself. */
async function runFile() {
    const file = await activeFile();
    if (!file) return;
    const uri = vscode.Uri.file(file);
    await vscode.debug.startDebugging(vscode.workspace.getWorkspaceFolder(uri), {
        type: 'shoddy',
        request: 'launch',
        name: `Shoddy: run ${path.basename(file)}`,
        program: file,
        noDebug: true,
    });
}

function millInTerminal(subcommand) {
    return async () => {
        const file = await activeFile();
        if (!file) return;
        const t = terminal(path.dirname(file));
        t.show(true);
        // PowerShell needs the call operator to run a quoted path;
        // cmd and POSIX shells choke on it.
        const call = /powershell|pwsh/i.test(vscode.env.shell || '') ? '& ' : '';
        const { cmd, args } = millCommand();
        const lead = [cmd, ...args].map((a) => `"${a}"`).join(' ');
        t.sendText(`${call}${lead} ${subcommand} "${file}"`);
    };
}

/** mill gen: capture the generated C# and open it as a C# document. */
async function showGenerated() {
    const file = await activeFile();
    if (!file) return;
    const { cmd, args } = millCommand();
    cp.execFile(cmd, [...args, 'gen', file],
        { maxBuffer: 64 * 1024 * 1024, env: { ...process.env, ...millEnv() }, cwd: path.dirname(file) },
        async (err, stdout, stderr) => {
            if (err || !stdout) {
                vscode.window.showErrorMessage(
                    'Shoddy: mill gen failed: ' + (stderr || (err && err.message) || 'no output'));
                return;
            }
            const doc = await vscode.workspace.openTextDocument(
                { language: 'csharp', content: stdout });
            vscode.window.showTextDocument(doc, { preview: true });
        });
}

exports.activate = (ctx) => {
    bundleRoot = ctx.extensionPath;
    // A .vsix is a zip, and the executable bit doesn't survive every route
    // through one. Restoring it costs nothing when it was already set, and
    // there is nothing to restore on the `dotnet mill.dll` route.
    const mill = bundledMill();
    if (mill && mill.cmd !== 'dotnet' && process.platform !== 'win32') {
        try { fs.chmodSync(mill.cmd, 0o755); } catch { /* read-only install */ }
    }
    // Terminals are launched by VS Code, not by us, so the terminal
    // commands get SHODDYLIB through the environment collection instead.
    // The collection is persisted across restarts — clear it when the
    // user has their own SHODDYLIB, or a stale one would shadow it.
    const lib = millEnv().SHODDYLIB;
    if (lib) ctx.environmentVariableCollection.replace('SHODDYLIB', lib);
    else ctx.environmentVariableCollection.delete('SHODDYLIB');

    ctx.subscriptions.push(
        vscode.commands.registerCommand('shoddy.run', runFile),
        vscode.commands.registerCommand('shoddy.weave', millInTerminal('weave')),
        vscode.commands.registerCommand('shoddy.machine', millInTerminal('machine')),
        vscode.commands.registerCommand('shoddy.gen', showGenerated),

        // The perch: a launch runs `mill dap --listen PORT` in a terminal
        // window of its own, reached through ExternalLaunch; an attach
        // names a transport — never a host kind — and connects straight
        // to the debuggee's perch service, no mill process in the path.
        vscode.debug.registerDebugAdapterDescriptorFactory('shoddy', {
            createDebugAdapterDescriptor: (session) => {
                if (session.configuration.request === 'attach') {
                    return new vscode.DebugAdapterServer(
                        session.configuration.port,
                        session.configuration.host || '127.0.0.1');
                }
                return new vscode.DebugAdapterInlineImplementation(new ExternalLaunch(session.configuration));
            },
        }),
        // Zero-config F5: debug the current .shoddy file. We resolve
        // "${file}" ourselves (active editor, else any visible .shoddy
        // editor) so F5 works even when focus is on a panel — VS Code's
        // own substitution errors when no file editor has focus.
        vscode.debug.registerDebugConfigurationProvider('shoddy', {
            resolveDebugConfiguration(_folder, config) {
                const shoddyDoc = () => {
                    const active = vscode.window.activeTextEditor;
                    if (active && active.document.languageId === 'shoddy') return active.document;
                    const visible = vscode.window.visibleTextEditors
                        .find((e) => e.document.languageId === 'shoddy');
                    return visible ? visible.document : null;
                };
                if (!config.type && !config.request && !config.name) {
                    config = {
                        type: 'shoddy',
                        request: 'launch',
                        name: 'Perch: debug current file',
                        program: '${file}',
                        stopOnEntry: true,
                    };
                }
                if (!config.program || config.program === '${file}') {
                    const doc = shoddyDoc();
                    if (!doc) {
                        vscode.window.showErrorMessage(
                            'Shoddy debugger: open a .shoddy file, or click into one, then press F5.');
                        return undefined;
                    }
                    config.program = doc.fileName;
                }
                return config;
            },
            // After VS Code has filled in ${workspaceFolder} and the rest:
            // the program and the folder it runs from, both absolute. A
            // relative path in launch.json is taken against the workspace
            // folder, as VS Code takes its own. With no "cwd", the program
            // runs from its own folder.
            resolveDebugConfigurationWithSubstitutedVariables(folder, config) {
                try {
                    return launch.resolveLaunch(config, folder ? folder.uri.fsPath : undefined);
                } catch (e) {
                    vscode.window.showErrorMessage(`Shoddy debugger: ${e.message}`);
                    return undefined;
                }
            },
        }),
    );
};

exports.deactivate = () => {
    for (const t of terms.values()) t.dispose();
};
