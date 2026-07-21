// Shoddy for VS Code — mill commands.
// Plain JavaScript, no build step: the folder is the extension.
const vscode = require('vscode');
const cp = require('child_process');
const fs = require('fs');
const path = require('path');

/** The mill executable: the shoddy.millPath setting, else the
 * workspace's bin/mill (bin/mill.exe on Windows), else `mill` on PATH. */
function millPath() {
    const configured = vscode.workspace.getConfiguration('shoddy').get('millPath');
    if (configured) return configured;
    for (const folder of vscode.workspace.workspaceFolders || []) {
        const candidate = path.join(folder.uri.fsPath, 'bin', 'mill');
        if (fs.existsSync(candidate)) return candidate;
        if (process.platform === 'win32' && fs.existsSync(candidate + '.exe'))
            return candidate + '.exe';
    }
    return 'mill';
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
        t.sendText(`${call}"${millPath()}" ${subcommand} "${file}"`);
    };
}

/** mill gen: capture the generated C# and open it as a C# document. */
async function showGenerated() {
    const file = await activeFile();
    if (!file) return;
    cp.execFile(millPath(), ['gen', file], { maxBuffer: 64 * 1024 * 1024 },
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
    ctx.subscriptions.push(
        vscode.commands.registerCommand('shoddy.run', millInTerminal('run')),
        vscode.commands.registerCommand('shoddy.weave', millInTerminal('weave')),
        vscode.commands.registerCommand('shoddy.machine', millInTerminal('machine')),
        vscode.commands.registerCommand('shoddy.gen', showGenerated),

        // The perch: `mill dap` speaks the Debug Adapter Protocol.
        vscode.debug.registerDebugAdapterDescriptorFactory('shoddy', {
            createDebugAdapterDescriptor: () =>
                new vscode.DebugAdapterExecutable(millPath(), ['dap']),
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
