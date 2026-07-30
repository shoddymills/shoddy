// Shoddy for VS Code — mill commands.
// Plain JavaScript, no build step: the folder is the extension.
const vscode = require('vscode');
const cp = require('child_process');
const fs = require('fs');
const path = require('path');

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

let term;
function terminal() {
    if (!term || term.exitStatus !== undefined) {
        term = vscode.window.createTerminal('shoddy mill');
    }
    return term;
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

function millInTerminal(subcommand) {
    return async () => {
        const file = await activeFile();
        if (!file) return;
        const t = terminal();
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
        { maxBuffer: 64 * 1024 * 1024, env: { ...process.env, ...millEnv() } },
        async (err, stdout, stderr) => {
            if (err || !stdout) {
                vscode.window.showErrorMessage(
                    'Shoddy: mill gen failed — ' + (stderr || (err && err.message) || 'no output'));
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
        vscode.commands.registerCommand('shoddy.run', millInTerminal('run')),
        vscode.commands.registerCommand('shoddy.weave', millInTerminal('weave')),
        vscode.commands.registerCommand('shoddy.machine', millInTerminal('machine')),
        vscode.commands.registerCommand('shoddy.gen', showGenerated),

        // The perch: `mill dap` speaks the Debug Adapter Protocol.
        vscode.debug.registerDebugAdapterDescriptorFactory('shoddy', {
            createDebugAdapterDescriptor: () => {
                const { cmd, args } = millCommand();
                return new vscode.DebugAdapterExecutable(cmd, [...args, 'dap'],
                    { env: millEnv() });
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
                            'Shoddy perch: open (or click into) a .shoddy file, then press F5.');
                        return undefined;
                    }
                    config.program = doc.fileName;
                }
                return config;
            },
        }),
    );
};

exports.deactivate = () => {
    if (term) term.dispose();
};
