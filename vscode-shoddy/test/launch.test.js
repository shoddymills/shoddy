// The debug launch's logic, tested without an editor:
//   node --test vscode-shoddy/test/launch.test.js
const test = require('node:test');
const assert = require('node:assert');
const path = require('path');
const launch = require('../launch');

const ws = path.resolve('/work/space');
const prog = path.join(ws, 'games', 'burler', 'burler.shoddy');

test('the program runs from its own folder by default', () => {
    const c = launch.resolveLaunch({ request: 'launch', program: prog }, ws);
    assert.strictEqual(c.cwd, path.dirname(prog));
});

test('a workspace opened on a parent folder changes nothing', () => {
    const atParent = launch.resolveLaunch({ request: 'launch', program: prog }, path.resolve('/work'));
    const atOwn = launch.resolveLaunch({ request: 'launch', program: prog }, path.dirname(prog));
    const noFolder = launch.resolveLaunch({ request: 'launch', program: prog }, undefined);
    assert.strictEqual(atParent.cwd, path.dirname(prog));
    assert.strictEqual(atOwn.cwd, path.dirname(prog));
    assert.strictEqual(noFolder.cwd, path.dirname(prog));
});

test('a relative program and cwd are taken against the workspace folder', () => {
    const c = launch.resolveLaunch(
        { request: 'launch', program: 'games/burler/burler.shoddy', cwd: 'data' }, ws);
    assert.strictEqual(c.program, prog);
    assert.strictEqual(c.cwd, path.join(ws, 'data'));
});

test('arguments pass through as strings, and default to none', () => {
    assert.deepStrictEqual(
        launch.resolveLaunch({ request: 'launch', program: prog }, ws).args, []);
    assert.deepStrictEqual(
        launch.resolveLaunch({ request: 'launch', program: prog, args: ['words.txt', 42] }, ws).args,
        ['words.txt', '42']);
    assert.throws(() => launch.resolveLaunch({ request: 'launch', program: prog, args: 'x' }, ws),
        /"args" must be a list/);
});

test('allowNet is off unless it is true', () => {
    assert.strictEqual(launch.resolveLaunch({ request: 'launch', program: prog }, ws).allowNet, false);
    assert.strictEqual(launch.resolveLaunch({ request: 'launch', program: prog, allowNet: true }, ws).allowNet, true);
});

test('a console setting is refused, whatever it names', () => {
    for (const value of ['integratedTerminal', 'internalConsole', 'externalTerminal'])
        assert.throws(() => launch.resolveLaunch({ request: 'launch', program: prog, console: value }, ws),
            /"console" is not a setting here/);
});

test('an attach configuration is left alone', () => {
    const attach = { request: 'attach', port: 4711 };
    assert.strictEqual(launch.resolveLaunch(attach, ws), attach);
});

test('the terminal runs the mill itself, serving the debugger on the port', () => {
    assert.deepStrictEqual(
        launch.debuggeeProcess({ cmd: '/x/mill', args: [] }, 5123),
        { cmd: '/x/mill', args: ['dap', '--listen', '5123'] });
    assert.deepStrictEqual(
        launch.debuggeeProcess({ cmd: 'dotnet', args: ['/ext/mill/mill.dll'] }, 5123),
        { cmd: 'dotnet', args: ['/ext/mill/mill.dll', 'dap', '--listen', '5123'] });
});

test('the terminal is named for the program, and says whether it is debugged', () => {
    assert.strictEqual(launch.terminalName(prog), 'shoddy debug: burler.shoddy');
    assert.strictEqual(launch.terminalName(prog, true), 'shoddy run: burler.shoddy');
});

test('without debugging, the window runs the program under mill run', () => {
    const c = launch.resolveLaunch(
        { request: 'launch', program: prog, noDebug: true, args: ['files/words.txt', '42'], allowNet: true }, ws);
    assert.deepStrictEqual(
        launch.terminalRequest(c, { cmd: '/x/mill', args: [] }, undefined, {}),
        {
            kind: 'external',
            title: 'shoddy run: burler.shoddy',
            cwd: path.dirname(prog),
            args: ['/x/mill', 'run', '--allow-net', prog, 'files/words.txt', '42'],
            env: {},
        });
    const plain = launch.resolveLaunch({ request: 'launch', program: prog, noDebug: true }, ws);
    assert.deepStrictEqual(
        launch.runProcess({ cmd: 'dotnet', args: ['/ext/mill/mill.dll'] }, plain),
        { cmd: 'dotnet', args: ['/ext/mill/mill.dll', 'run', prog] });
});

test('the window is an external terminal running the mill in the program folder', () => {
    const c = launch.resolveLaunch({ request: 'launch', program: prog }, ws);
    assert.deepStrictEqual(
        launch.terminalRequest(c, { cmd: 'dotnet', args: ['/ext/mill/mill.dll'] }, 5123, { SHODDYLIB: '/ext/machines' }),
        {
            kind: 'external',
            title: 'shoddy debug: burler.shoddy',
            cwd: path.dirname(prog),
            args: ['dotnet', '/ext/mill/mill.dll', 'dap', '--listen', '5123'],
            env: { SHODDYLIB: '/ext/machines' },
        });
});

test('a framed message carries its length in bytes, not characters', () => {
    const msg = { type: 'event', event: 'output', body: { output: 'café ✓' } };
    const bytes = launch.frame(msg);
    const text = bytes.toString('utf8');
    const body = Buffer.from(JSON.stringify(msg), 'utf8');
    assert.ok(text.startsWith(`Content-Length: ${body.length}\r\n\r\n`));
    assert.notStrictEqual(body.length, JSON.stringify(msg).length);
});

test('framed messages come back whole, however the bytes are split', () => {
    const msgs = [
        { seq: 1, type: 'request', command: 'initialize' },
        { seq: 2, type: 'event', event: 'output', body: { output: 'café ✓\n' } },
        { seq: 3, type: 'response', request_seq: 1, success: true },
    ];
    const all = Buffer.concat(msgs.map(launch.frame));
    // Every split point, including one inside a multi-byte character and
    // one inside the header.
    for (let cut = 1; cut < all.length; cut++) {
        const u = new launch.Unframer();
        const got = [...u.push(all.subarray(0, cut)), ...u.push(all.subarray(cut))];
        assert.deepStrictEqual(got, msgs, `split at byte ${cut}`);
    }
    // One byte at a time.
    const u = new launch.Unframer();
    const got = [];
    for (const b of all) got.push(...u.push(Buffer.from([b])));
    assert.deepStrictEqual(got, msgs);
});

test('a header with no length is refused, not guessed at', () => {
    const u = new launch.Unframer();
    assert.throws(() => u.push(Buffer.from('Content-Type: x\r\n\r\n{}')), /no Content-Length/);
});
