import test from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:net';
import { mkdtempSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { ownedInputRequest } from './input-request.mjs';
import { agentRequest } from './agent-observer.mjs';
const request = { paneId: '11111111-1111-1111-1111-111111111111', kind: 'keyDown', key: '7', modifiers: 0 };
async function fixture(respond, action) {
    const root = mkdtempSync(join(tmpdir(), 'nti-')), path = process.platform === 'win32' ? `\\\\.\\pipe\\nti-${process.pid}-${Date.now()}` : join(root, 's');
    const sockets = new Set(), server = createServer(socket => {
        sockets.add(socket); socket.on('error', () => {}); socket.on('close', () => sockets.delete(socket));
        socket.on('data', bytes => respond(socket, JSON.parse(bytes.toString())));
    });
    await new Promise(resolve => server.listen(path, resolve));
    try { await action(path); }
    finally { for (const socket of sockets) socket.destroy(); await new Promise(resolve => server.close(resolve)); rmSync(root, { recursive: true, force: true }); }
}
test('acting RPC is isolated from the read-only observer and preserves the original partial result', async () => {
    assert.throws(() => agentRequest('unused', 'generatePaneInput', request, 100), /read-only/);
    const partial = { eventDispatched: true, inputSubmitted: true, observationAvailable: false };
    await fixture((socket, frame) => {
        assert.deepEqual(frame, { v: 1, id: 1, method: 'generatePaneInput', params: request });
        socket.end(JSON.stringify({ v: 1, id: 1, result: partial }) + '\n');
    }, async path => assert.deepEqual(await ownedInputRequest(path, request, 1000), partial));
});
for (const [name, response] of [['mismatch', { v: 1, id: 2, result: {} }], ['refusal', { v: 1, id: 1, error: { code: 'denied' } }],
    ['oversized', 'x'.repeat(16385)], ['disconnect', ''], ['timeout', null]]) test(name + ' remains incomplete without retries', async () => {
    let calls = 0;
    await fixture((socket) => { calls++; if (response !== null) socket.end(typeof response === 'string' ? response : JSON.stringify(response) + '\n'); },
        async path => assert.rejects(ownedInputRequest(path, request, name === 'timeout' ? 20 : 1000)));
    assert.equal(calls, 1);
});
