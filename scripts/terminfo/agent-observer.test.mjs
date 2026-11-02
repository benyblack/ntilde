import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createServer } from 'node:net';
import { once } from 'node:events';
import { chmodSync, mkdtempSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { agentRequest, observerEndpoint, OwnedCellObserver, screenSnapshot } from './agent-observer.mjs';

const paneId = '11111111-1111-1111-1111-111111111111', barrier = '\x1b[?62;4;22c';
const blank = () => ({ rows: 3, cols: 60, lines: ['', '', ''], cursorRow: 0, cursorCol: 0, cursorVisible: true });
function fixture() {
    let value = blank(), sessions = [{ paneId, profileName: 'fixture', kind: 'local', rows: 3, cols: 60 }];
    let calls = 0;
    const observer = new OwnedCellObserver({ appData: '/never/discover', ownerPid: 42, profileName: 'fixture', nonce: 'nonce',
        endpoint: () => 'fixture-only', rpc: async (_, method) => { calls++; return method === 'listSessions' ? { sessions } : value; } });
    return { observer, setView: next => { value = next; }, setSessions: next => { sessions = next; }, calls: () => calls };
}
async function bind(f) {
    await f.observer.handle('cell-start', () => 1000);
    f.setView({ ...blank(), lines: ['nonce', '', ''], cursorCol: 5 });
    await f.observer.handle('cell-bind', () => 1000, barrier);
}

test('binds exactly one nonce-bearing pane, copies snapshots and requires restoration', async () => {
    const f = fixture(); await bind(f);
    const data = { ...blank(), lines: ['Hello', '', ''], cursorCol: 5 };
    f.setView(data);
    await f.observer.handle('cell-read', () => 1000, barrier);
    data.lines[0] = 'later publication';
    assert.equal(f.observer.frames.at(-1).screen.lines[0], 'Hello');
    f.setView(blank());
    await f.observer.handle('cell-finish', () => 1000, barrier);
    assert.equal(f.observer.restored, true);
    await assert.rejects(f.observer.handle('cell-read', () => 1000, barrier), /already complete/);
});

test('unbound or unfenced reads cannot expose a snapshot', async () => {
    const f = fixture();
    await assert.rejects(f.observer.handle('cell-read', () => 1000, barrier), /not started/);
    assert.equal(f.calls(), 0);
    await f.observer.handle('cell-start', () => 1000);
    const calls = f.calls();
    await assert.rejects(f.observer.handle('cell-read', () => 1000, barrier), /not bound/);
    assert.equal(f.calls(), calls);
    await assert.rejects(f.observer.handle('cell-bind', () => 1000), /ordering barrier/);
    await assert.rejects(f.observer.handle('sendInput', () => 1000), /Unknown/);
});

for (const [name, sessions] of [
    ['multiple panes', [{ paneId, profileName: 'fixture', kind: 'local', rows: 3, cols: 60 }, {}]],
    ['another pane', [{ paneId: '22222222-2222-2222-2222-222222222222', profileName: 'fixture', kind: 'local', rows: 3, cols: 60 }]],
    ['another profile', [{ paneId, profileName: 'user profile', kind: 'local', rows: 3, cols: 60 }]],
    ['a remote pane', [{ paneId, profileName: 'fixture', kind: 'ssh', rows: 3, cols: 60 }]],
    ['a windowless session', [{ paneId, profileName: 'fixture', kind: 'local', rows: 3, cols: 60, windowless: true }]],
]) {
    test('refuses ' + name, async () => {
        const f = fixture(); await bind(f); f.setSessions(sessions);
        await assert.rejects(f.observer.handle('cell-read', () => 1000, barrier), /pane/);
        assert.equal(f.observer.frames.length, 2);
    });
}

test('wrong nonce, resize, restoration mismatch and exhausted sample budget fail closed', async () => {
    const f = fixture();
    await f.observer.handle('cell-start', () => 1000);
    await assert.rejects(f.observer.handle('cell-bind', () => 1000, barrier), /did not bind/);
    f.setView({ ...blank(), lines: ['nonce', '', ''], cursorCol: 5 });
    await f.observer.handle('cell-bind', () => 1000, barrier);
    f.setView({ ...blank(), cols: 61 });
    await assert.rejects(f.observer.handle('cell-read', () => 1000, barrier), /changed size/);
    f.setView({ ...blank(), lines: ['leftover', '', ''] });
    await assert.rejects(f.observer.handle('cell-finish', () => 1000, barrier), /restoration failed/);
    f.observer.snapshots = 64;
    await assert.rejects(f.observer.handle('cell-read', () => 1000, barrier), /budget exhausted/);
    assert.equal(f.observer.restored, undefined);
});

test('refuses malformed screen sizes, line counts and cursor coordinates', () => {
    for (const change of [{ rows: 2 }, { cols: 257 }, { lines: [] }, { cursorRow: 3 }, { cursorCol: -1 }, { cursorVisible: null }]) {
        assert.throws(() => screenSnapshot({ ...blank(), ...change }), /Invalid/);
    }
});

async function withServer(handler, action) {
    const sockets = new Set();
    const server = createServer(socket => { sockets.add(socket); socket.on('error', () => {}); socket.on('close', () => sockets.delete(socket)); handler(socket); });
    server.listen(0, '127.0.0.1'); await once(server, 'listening');
    try { await action({ host: '127.0.0.1', port: server.address().port }); }
    finally { for (const socket of sockets) socket.destroy(); server.close(); await once(server, 'close'); }
}

test('agent RPC accepts only read-only versioned correlated frames', async () => {
    await withServer(socket => socket.once('data', data => {
        const request = JSON.parse(data.toString());
        assert.equal(request.method, 'readScreen');
        socket.end(JSON.stringify({ v: 1, id: 1, result: { text: 'Unicode 🧪' } }) + '\n');
    }), async endpoint => {
        assert.deepEqual(await agentRequest(endpoint, 'readScreen', {}, 1000), { text: 'Unicode 🧪' });
        assert.throws(() => agentRequest(endpoint, 'sendInput', {}, 1000), /read-only/);
    });
});

for (const [name, data, error] of [
    ['wrong version', '{"v":2,"id":1,"result":{}}\n', /refused/],
    ['wrong request', '{"v":1,"id":2,"result":{}}\n', /refused/],
    ['an error', '{"v":1,"id":1,"error":{"code":"disabled"}}\n', /refused/],
    ['a partial frame', '{"v":1', /Incomplete/],
    ['oversized data', 'x'.repeat(512 * 1024 + 1), /Oversized/],
]) {
    test('RPC refuses ' + name, async () => withServer(socket => socket.once('data', () => socket.end(data)),
        endpoint => assert.rejects(agentRequest(endpoint, 'listSessions', {}, 1000), error)));
}

test('RPC uses a total deadline even when partial bytes keep arriving', async () => {
    await withServer(socket => {
        const timer = setInterval(() => socket.write(' '), 2);
        socket.on('close', () => clearInterval(timer));
    }, endpoint => assert.rejects(agentRequest(endpoint, 'listSessions', {}, 25), /timed out/));
});

test('discovery refuses another app, outside sockets, symlinks and shared permissions', { skip: !process.getuid }, async () => {
    const root = mkdtempSync(join(tmpdir(), 'nt-observer-'));
    const endpoint = join(root, 'agent.sock'), descriptor = join(root, 'agent-endpoint.json');
    const server = createServer(socket => socket.destroy());
    chmodSync(root, 0o700); server.listen(endpoint); await once(server, 'listening'); chmodSync(endpoint, 0o600);
    const write = value => writeFileSync(descriptor, JSON.stringify(value), { mode: 0o644 });
    try {
        write({ version: 1, pid: 42, endpoint });
        assert.equal(observerEndpoint(root, 42), endpoint);
        assert.throws(() => observerEndpoint(root, 43), /launched app/);
        write({ version: 1, pid: 42, endpoint: '/normal/user/agent.sock' });
        assert.throws(() => observerEndpoint(root, 42), /launched app/);
        rmSync(descriptor); symlinkSync(endpoint, descriptor);
        assert.throws(() => observerEndpoint(root, 42), /private observer/);
        rmSync(descriptor); write({ version: 1, pid: 42, endpoint }); chmodSync(endpoint, 0o666);
        assert.throws(() => observerEndpoint(root, 42), /private observer/);
        chmodSync(endpoint, 0o600); chmodSync(root, 0o755);
        assert.throws(() => observerEndpoint(root, 42), /private observer/);
    } finally { server.close(); await once(server, 'close'); rmSync(root, { recursive: true, force: true }); }
});
