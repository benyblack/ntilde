import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createServer } from 'node:net';
import { once } from 'node:events';
import { chmodSync, mkdtempSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import * as observerApi from './agent-observer.mjs';
import { agentRequest, observerEndpoint, OwnedCellObserver, screenSnapshot } from './agent-observer.mjs';

const paneId = '11111111-1111-1111-1111-111111111111', barrier = '\x1b[?62;4;22c';
const blank = () => ({ rows: 3, cols: 60, lines: ['', '', ''], cursorRow: 0, cursorCol: 0, cursorVisible: true });

test('observer preserves a correlated App refusal code without trusting an uncorrelated refusal', async () => {
    let frame = { v: 1, id: 1, error: { code: 'screenDetailsUnavailable', message: 'budget' } };
    const server = createServer(socket => { socket.once('data', () => socket.end(JSON.stringify(frame) + '\n')); });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    try {
        const endpoint = { host: '127.0.0.1', port: server.address().port };
        await assert.rejects(agentRequest(endpoint, 'readScreen', {}, 1000), error => error.code === 'screenDetailsUnavailable');
        frame = { ...frame, id: 2 };
        await assert.rejects(agentRequest(endpoint, 'readScreen', {}, 1000), error => error.code === undefined && /Uncorrelated/.test(error.message));
    } finally { await new Promise(resolve => server.close(resolve)); }
});
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

test('SGR observer requests complete attributes, binds the prior pane and retains independent flag runs', async () => {
    const initial = { ...blank(), attributeLines: Array(3).fill('0300:-1/-1|'.repeat(60)) };
    let value = structuredClone(initial);
    const observer = new OwnedCellObserver({ appData: '/never/discover', ownerPid: 42, profileName: 'fixture', nonce: 'nonce',
        includeAttributes: true, prefix: 'sgr', expectedPaneId: paneId, endpoint: () => 'fixture-only',
        rpc: async (_, method, params) => {
            if (method === 'listSessions') return { sessions: [{ paneId, profileName: 'fixture', kind: 'local', rows: 3, cols: 60 }] };
            assert.equal(params.includeAttributes, true); return value;
        } });
    await observer.handle('sgr-start', () => 1000);
    value = { ...structuredClone(initial), lines: ['nonce', '', ''], cursorCol: 5 };
    await observer.handle('sgr-bind', () => 1000, barrier);
    value.lines[0] = 'A'; value.cursorCol = 1;
    value.attributeLines[0] = '0301:-1/-1|' + '0300:-1/-1|'.repeat(59);
    await observer.handle('sgr-read', () => 1000, barrier);
    value.attributeLines[0] = initial.attributeLines[0];
    assert.equal(observer.frames.at(-1).screen.attributeRuns[0].flags, 769);
    await assert.rejects(observer.handle('cell-read', () => 1000, barrier), /Unknown/);
    value = { ...blank() };
    await assert.rejects(observer.handle('sgr-read', () => 1000, barrier), /attribute rows/);
    value = structuredClone(initial);
    value.attributeLines[0] = '0301:-1/-1|' + '0300:-1/-1|'.repeat(59);
    await assert.rejects(observer.handle('sgr-finish', () => 1000, barrier), /restoration failed/);
    assert.equal(observer.restored, undefined);
    value = structuredClone(initial);
    await observer.handle('sgr-finish', () => 1000, barrier);
    assert.equal(observer.restored, true);
});

test('SGR refuses a different pane before reading any attributes', async () => {
    let reads = 0;
    const observer = new OwnedCellObserver({ appData: '/never/discover', ownerPid: 42, profileName: 'fixture', nonce: 'nonce',
        includeAttributes: true, prefix: 'sgr', expectedPaneId: '22222222-2222-2222-2222-222222222222', endpoint: () => 'fixture-only',
        rpc: async (_, method) => {
            if (method === 'readScreen') reads++;
            return { sessions: [{ paneId, profileName: 'fixture', kind: 'local', rows: 3, cols: 60 }] };
        } });
    await assert.rejects(observer.handle('sgr-start', () => 1000), /pane changed/);
    assert.equal(reads, 0);
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
    ['wrong version', '{"v":2,"id":1,"result":{}}\n', /Uncorrelated/],
    ['wrong request', '{"v":1,"id":2,"result":{}}\n', /Uncorrelated/],
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


test('RGB observer opts into details, copies complete runs and refuses missing or unrestored metadata', async () => {
    const initial = { ...blank(), cellDetails: [{ count: 180, flags: 768, fg: -1, bg: -1, underlineStyle: 0, underlineColorIndex: -1 }] };
    let value = structuredClone(initial);
    const observer = new OwnedCellObserver({ appData: '/never/discover', ownerPid: 42, profileName: 'fixture', nonce: 'nonce',
        includeCellDetails: true, prefix: 'rgb', expectedPaneId: paneId, endpoint: () => 'fixture-only',
        rpc: async (_, method, params) => {
            if (method === 'listSessions') return { sessions: [{ paneId, profileName: 'fixture', kind: 'local', rows: 3, cols: 60 }] };
            assert.equal(params.includeCellDetails, true);
            assert.equal(params.includeAttributes, false);
            return value;
        } });
    await observer.handle('rgb-start', () => 1000);
    value = { ...structuredClone(initial), lines: ['nonce', '', ''], cursorCol: 5 };
    await observer.handle('rgb-bind', () => 1000, barrier);
    value = { ...blank(), cellDetails: [{ count: 180, flags: 520, fg: -1, bg: -1,
        foregroundArgb: 0xff010203, underlineStyle: 3, underlineColorIndex: 123 }] };
    await observer.handle('rgb-read', () => 1000, barrier);
    value.cellDetails[0].foregroundArgb = 0xff040506;
    assert.equal(observer.frames.at(-1).screen.cellDetails[0].foregroundArgb, 0xff010203);
    await assert.rejects(observer.handle('rgb-finish', () => 1000, barrier), /restoration failed/);
    value = blank();
    await assert.rejects(observer.handle('rgb-read', () => 1000, barrier), /budget/);
    value = initial;
    await observer.handle('rgb-finish', () => 1000, barrier);
    assert.equal(observer.restored, true);
});


test('cursor mode projection is opt-in, validates all stored fields and detaches returned state', () => {
    const raw = { ...blank(), cursorMode: { style: 2, blink: true, isDefault: true } };
    assert.equal(screenSnapshot(raw).cursorMode, undefined);
    const snapshot = screenSnapshot(raw, false, false, true);
    raw.cursorMode.style = 0;
    assert.equal(snapshot.cursorMode.style, 2);
    for (const cursorMode of [null, {}, { style: 3, blink: true, isDefault: true },
        { style: 1, blink: null, isDefault: false }, { style: 1, blink: false, isDefault: null }])
        assert.throws(() => screenSnapshot({ ...blank(), cursorMode }, false, false, true), /cursor mode/);
});

test('cursor observer requests mode state and refuses restoration that changes only a stored mode', async () => {
    const initial = { ...blank(), cellDetails: [{ count: 180, flags: 768, fg: -1, bg: -1, underlineStyle: 0, underlineColorIndex: -1 }],
        cursorMode: { style: 2, blink: true, isDefault: true } };
    let value = structuredClone(initial);
    const observer = new OwnedCellObserver({ appData: '/never/discover', ownerPid: 42, profileName: 'fixture', nonce: 'nonce',
        includeCellDetails: true, includeCursorMode: true, prefix: 'cursor', expectedPaneId: paneId, endpoint: () => 'fixture-only',
        rpc: async (_, method, params) => {
            if (method === 'listSessions') return { sessions: [{ paneId, profileName: 'fixture', kind: 'local', rows: 3, cols: 60 }] };
            assert.equal(params.includeCursorMode, true); return value;
        } });
    await observer.handle('cursor-start', () => 1000);
    value = { ...structuredClone(initial), lines: ['nonce', '', ''], cursorCol: 5 };
    await observer.handle('cursor-bind', () => 1000, barrier);
    value = { ...structuredClone(initial), cursorMode: { style: 2, blink: true, isDefault: false } };
    await assert.rejects(observer.handle('cursor-finish', () => 1000, barrier), /restoration failed/);
    value = initial;
    await observer.handle('cursor-finish', () => 1000, barrier);
});


test('overline observer opts in explicitly, retains booleans and includes them in restoration proof', async () => {
    const initial = { ...blank(), cellDetails: [{ count: 180, flags: 768, fg: -1, bg: -1, underlineStyle: 0, underlineColorIndex: -1, overline: false }] };
    let value = structuredClone(initial);
    const observer = new OwnedCellObserver({ appData: '/never/discover', ownerPid: 42, profileName: 'fixture', nonce: 'nonce',
        includeCellDetails: true, includeOverline: true, prefix: 'rendition', expectedPaneId: paneId, endpoint: () => 'fixture-only',
        rpc: async (_, method, params) => {
            if (method === 'listSessions') return { sessions: [{ paneId, profileName: 'fixture', kind: 'local', rows: 3, cols: 60 }] };
            assert.equal(params.includeOverline, true);
            assert.equal(params.includeCellDetails, true);
            return value;
        } });
    await observer.handle('rendition-start', () => 1000);
    value = { ...structuredClone(initial), lines: ['nonce', '', ''], cursorCol: 5 };
    await observer.handle('rendition-bind', () => 1000, barrier);
    value = structuredClone(initial);
    value.cellDetails[0].overline = true;
    await observer.handle('rendition-read', () => 1000, barrier);
    value.cellDetails[0].overline = false;
    assert.equal(observer.frames.at(-1).screen.cellDetails[0].overline, true);
    value.cellDetails[0].overline = true;
    await assert.rejects(observer.handle('rendition-finish', () => 1000, barrier), /restoration failed/);
    value = initial;
    await observer.handle('rendition-finish', () => 1000, barrier);
    assert.equal(observer.restored, true);
});

test('protection observer opts in explicitly, journals the stored booleans and includes them in restoration proof', async () => {
    const initial = { ...blank(), cellDetails: [{ count: 180, flags: 768, fg: -1, bg: -1, underlineStyle: 0, underlineColorIndex: -1, protected: false }] };
    let value = structuredClone(initial);
    const requests = [];
    const observer = new OwnedCellObserver({ appData: '/never/discover', ownerPid: 42, profileName: 'fixture', nonce: 'nonce',
        includeCellDetails: true, includeProtection: true, prefix: 'selective', expectedPaneId: paneId, endpoint: () => 'fixture-only',
        rpc: async (_, method, params) => {
            if (method === 'listSessions') return { sessions: [{ paneId, profileName: 'fixture', kind: 'local', rows: 3, cols: 60 }] };
            requests.push(params);
            assert.equal(params.includeProtection, true);
            assert.equal(params.includeOverline, undefined);
            return value;
        } });
    await observer.handle('selective-start', () => 1000);
    value = { ...structuredClone(initial), lines: ['nonce', '', ''], cursorCol: 5 };
    await observer.handle('selective-bind', () => 1000, barrier);
    value = structuredClone(initial);
    value.cellDetails[0].protected = true;
    await observer.handle('selective-read', () => 1000, barrier);
    value.cellDetails[0].protected = false;
    assert.equal(observer.frames.at(-1).screen.cellDetails[0].protected, true);
    value.cellDetails[0].protected = true;
    await assert.rejects(observer.handle('selective-finish', () => 1000, barrier), /restoration failed/);
    value = initial;
    await observer.handle('selective-finish', () => 1000, barrier);
    assert.equal(observer.restored, true);
    // an observer that never asked for protection sends no such parameter
    const plain = new OwnedCellObserver({ appData: '/never/discover', ownerPid: 42, profileName: 'fixture', nonce: 'nonce',
        includeCellDetails: true, prefix: 'cell', expectedPaneId: paneId, endpoint: () => 'fixture-only',
        rpc: async (_, method, params) => {
            if (method === 'listSessions') return { sessions: [{ paneId, profileName: 'fixture', kind: 'local', rows: 3, cols: 60 }] };
            assert.equal('includeProtection' in params, false);
            return { ...blank(), cellDetails: [{ count: 180, flags: 768, fg: -1, bg: -1, underlineStyle: 0, underlineColorIndex: -1 }] };
        } });
    await plain.handle('cell-start', () => 1000);
});

test('only the image-specific capture wrapper opts in while default render request bytes stay unchanged', async () => {
    assert.equal(typeof observerApi.imageCaptureRequest, 'function');
    const params = [];
    await withServer(socket => socket.once('data', data => {
        const request = JSON.parse(data); params.push(request.params);
        socket.end(JSON.stringify({ v: 1, id: 1, result: {} }) + '\n');
    }), async endpoint => {
        await observerApi.captureRequest(endpoint, paneId, 0, 1000);
        await observerApi.imageCaptureRequest(endpoint, paneId, 0, 1000);
        await observerApi.colorCaptureRequest(endpoint, paneId, null, 1000);
    });
    assert.deepEqual(params[0], { paneId, mode: 'render', inline: true, maxWidth: 0, scale: 1, includeRenderDetails: true, presentationTimeMs: 0 });
    assert.deepEqual(params[1], { ...params[0], includeImageDetails: true });
    assert.deepEqual(params[2], params[0]);
});
