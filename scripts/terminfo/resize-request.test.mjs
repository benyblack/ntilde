import test from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:net';
import { once } from 'node:events';
import { agentRequest, ownedResizeRequest } from './agent-observer.mjs';
const paneId = '11111111-1111-1111-1111-111111111111';

async function withServer(handler, action) {
    const sockets = new Set();
    const server = createServer(socket => {
        sockets.add(socket); socket.on('error', () => {}); socket.on('close', () => sockets.delete(socket));
        handler(socket);
    });
    server.listen(0, '127.0.0.1'); await once(server, 'listening');
    try { await action({ host: '127.0.0.1', port: server.address().port }); }
    finally { for (const socket of sockets) socket.destroy(); server.close(); await once(server, 'close'); }
}
test('the action sends only resizePane and retains actual partial geometry without echoing the target', async () => {
    const partial = { rows: 21, cols: 71, layoutSettled: false, exact: false };
    await withServer(socket => socket.once('data', data => {
        assert.deepEqual(JSON.parse(data), { v: 1, id: 1, method: 'resizePane', params: { paneId, rows: 20, cols: 70 } });
        socket.end(JSON.stringify({ v: 1, id: 1, result: partial }) + '\n');
    }), async endpoint => {
        assert.deepEqual(await ownedResizeRequest(endpoint, paneId, 20, 70, 1000), partial);
        assert.throws(() => agentRequest(endpoint, 'resizePane', {}, 1000), /read-only/);
    });
});
for (const [rows, cols, id] of [[0, 70, paneId], [129, 70, paneId], [20, 0, paneId], [20, 257, paneId], [20, 70, 'another pane']]) {
    test(`invalid resize ${rows}x${cols} ${id} is refused before connecting`, () => {
        assert.throws(() => ownedResizeRequest('/never/discover', id, rows, cols, 1000), /Invalid owned resize/);
    });
}
test('the action has a small response budget independent of screen-capture payloads', async () => {
    await withServer(socket => socket.once('data', () => socket.end('x'.repeat(16385))),
        endpoint => assert.rejects(ownedResizeRequest(endpoint, paneId, 20, 70, 1000), /Oversized/));
});
