// Shoddy for VS Code — mill commands and the debugger.
// Plain JavaScript, no build step: the folder is the extension.
const vscode = require('vscode');
const cp = require('child_process');
const fs = require('fs');
const path = require('path');
const launch = require('./launch');

// This extension ships a mill under its own directory (see `build.sh
// stage`), with the machine library in lib/ and the machine sources in
// machines/ beside it, so installing the .vsix is the whole install. A
// workspace that builds its own mill still wins: a Shoddy hacker's
// freshly built mill is the one they just changed, and the bundled copy
// is only the fallback for everyone else.
let bundleRoot = null;

/** How to launch the mill bundled with this extension — a command, the
 * arguments that must precede the subcommand, and the machine sources
 * beside it — or null when it wasn't staged.
 *
 * The staged build is framework-dependent and built without a runtime
 * identifier, so the only native launcher in it is the Windows
 * mill.exe. Everywhere else the portable mill.dll is run through the
 * `dotnet` muxer, which the .NET runtime this extension already
 * requires puts on PATH. */
function bundledMill() {
    if (!bundleRoot) return null;
    const dir = path.join(bundleRoot, 'mill');
    const lib = path.join(dir, 'machines');
    const exe = path.join(dir, process.platform === 'win32' ? 'mill.exe' : 'mill');
    if (fs.existsSync(exe)) return { cmd: exe, args: [], lib };
    const dll = path.join(dir, 'mill.dll');
    return fs.existsSync(dll) ? { cmd: 'dotnet', args: [dll], lib } : null;
}

/** How to launch the mill: the shoddy.millPath setting, else the mill a
 * workspace has built (where `dotnet build src/Shoddy.Mill -c Release`
 * puts it under artifacts/), else the bundled copy, else `mill` on
 * PATH. Returns {cmd, args, lib}: args is empty for every route but the
 * bundled `dotnet mill.dll` one, and lib is the folder of machine
 * sources that goes with that mill — a checkout's own machines/ for the
 * mill it built, the bundle's for the bundled mill — or null when the
 * route says nothing about one. */
function millCommand() {
    const configured = vscode.workspace.getConfiguration('shoddy').get('millPath');
    if (configured) return { cmd: configured, args: [], lib: null };
    for (const folder of vscode.workspace.workspaceFolders || []) {
        const root = folder.uri.fsPath;
        const candidate = path.join(root, 'artifacts', 'bin', 'Shoddy.Mill', 'release', 'mill');
        const machines = path.join(root, 'machines');
        const lib = fs.existsSync(machines) ? machines : null;
        if (fs.existsSync(candidate)) return { cmd: candidate, args: [], lib };
        if (process.platform === 'win32' && fs.existsSync(candidate + '.exe'))
            return { cmd: candidate + '.exe', args: [], lib };
    }
    return bundledMill() || { cmd: 'mill', args: [], lib: null };
}

/** The environment for a mill we spawn ourselves, or ask VS Code to.
 * SHODDYLIB names the machine library's sources: a file reached there
 * is a machine rather than source to splice, and an Include the mill
 * can't find beside its source is looked for there. It is pointed at
 * the sources that go with the mill in use, so `Include "seq.shoddy"`
 * works in a scratch file anywhere, and a mill in a checkout links the
 * checkout's library exactly as the checkout's build wrapper has it. A
 * SHODDYLIB the user set is theirs — we never override it. */
function millEnv() {
    const { lib } = millCommand();
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

        // The debugger. A launch runs `mill dap` as the adapter: a child
        // of this extension host speaking the protocol over its stdin and
        // stdout, which asks VS Code for a terminal window of the
        // operating system's own and runs the program there under
        // `mill run --perch`, so the program has that window's keyboard
        // and screen exactly as under `mill run`. The window inherits the
        // library environment through the adapter. An attach names a
        // transport — never a host kind — and connects straight to the
        // debuggee's perch service, no mill process in the path.
        vscode.debug.registerDebugAdapterDescriptorFactory('shoddy', {
            createDebugAdapterDescriptor: (session) => {
                if (session.configuration.request === 'attach') {
                    return new vscode.DebugAdapterServer(
                        session.configuration.port,
                        session.configuration.host || '127.0.0.1');
                }
                const { cmd, args } = millCommand();
                return new vscode.DebugAdapterExecutable(cmd, [...args, 'dap'], { env: millEnv() });
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
