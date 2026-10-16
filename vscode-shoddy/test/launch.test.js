// The debug launch's configuration logic, tested without an editor:
//   node --test vscode-shoddy/test/launch.test.js
// The launch itself — the window, the relay — is the mill's, and is
// tested with it (src/Shoddy.Tests/PerchAdapterTests.cs).
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
