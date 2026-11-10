import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { test } from 'node:test';
import { parseChecksumReportFrame, parseChecksumReply, validateChecksumReport } from './checksum-report.mjs';
import { terminalFence } from './terminal-fence.mjs';
import { RectChecksumObserver, validateChecksumReadExtras } from './rect-checksum-capture.mjs';

const da = '\x1b[?62;4;22c';
const reply = '\x1bP1!~FEB1\x1b\\';

class Input extends EventEmitter {
    isRaw = false; paused = true;
    isPaused() { return this.paused; }
    setRawMode(value) { this.isRaw = value; }
    pause() { this.paused = true; }
    resume() { this.paused = false; }
}

test('one exchange is a raw DCS reply (or its explicit absence) ordered by the DA barrier', () => {
    assert.deepEqual(parseChecksumReportFrame(reply + da), { barrier: da, reply: { raw: reply } });
    assert.deepEqual(parseChecksumReportFrame(da), { barrier: da, reply: null }, 'a complete DA alone is an explicitly absent reply');
    assert.deepEqual(parseChecksumReportFrame('\x1bP0$r\x1b\\' + da), { barrier: da, reply: { raw: '\x1bP0$r\x1b\\' } }, 'any bounded DCS is retained for the grader');
    for (const partial of ['', '\x1b', '\x1bP1!~FE', reply.slice(0, -1), reply, reply + '\x1b[?6', '\x1b[?62;4;22']) {
        assert.equal(parseChecksumReportFrame(partial), null, JSON.stringify(partial));
    }
});

test('anything but one bounded DCS before the barrier is refused', () => {
    for (const bytes of ['user input', '\x1b[1;1R' + da, '\x1bP1\x1bX', '\x1bP' + 'x'.repeat(49) + '\x1b\\' + da, reply + reply + da, reply + 'x',
        'x'.repeat(193)]) {
        assert.throws(() => parseChecksumReportFrame(bytes), /rectangle checksum/, JSON.stringify(bytes).slice(0, 40));
    }
    assert.throws(() => parseChecksumReply('\x1bP1!~FEB1'), /Unexpected rectangle checksum reply/);
});

test('a retained exchange is revalidated from its raw bytes', () => {
    assert.deepEqual(validateChecksumReport({ barrier: da, reply: { raw: reply } }), { barrier: da, reply: { raw: reply } });
    assert.deepEqual(validateChecksumReport({ barrier: da, reply: null }), { barrier: da, reply: null });
    for (const value of [null, {}, { barrier: da }, { barrier: 'x', reply: null }, { barrier: da, reply: { raw: 'x' } },
        { barrier: da, reply: { raw: reply, extra: 1 } }, { barrier: da, reply: null, extra: 1 }]) {
        assert.throws(() => validateChecksumReport(value), /rectangle checksum/, JSON.stringify(value));
    }
});

test('the fence sends the exact request, then DA, and resolves the reply with its barrier', async () => {
    const input = new Input(); let written;
    const fence = terminalFence(input, { write(value, callback) { written = value; callback(); } });
    const exchange = fence.checksumReport('\x1b[1;1;1;1;1;5*y');
    assert.equal(written, '\x1b[1;1;1;1;1;5*y\x1b[c');
    input.emit('data', Buffer.from(reply.slice(0, 4)));
    input.emit('data', Buffer.from(reply.slice(4) + da));
    assert.deepEqual(await exchange, { barrier: da, reply: { raw: reply } });
    const silent = fence.checksumReport('\x1b[?1;1;1;1;1;5*y');
    input.emit('data', Buffer.from(da));
    assert.deepEqual(await silent, { barrier: da, reply: null });
    await assert.rejects(fence.checksumReport(''), /Invalid rectangle checksum request/);
    const broken = fence.checksumReport('\x1b[7*y');
    input.emit('data', Buffer.from('\x1b[2;1R'));
    await assert.rejects(broken, /rectangle checksum/);
    fence.dispose();
});

test('the parent observer accepts a report only on a read ordered by its own barrier', () => {
    assert.deepEqual(validateChecksumReadExtras({ kind: 'csum-read', barrier: da }), {});
    assert.deepEqual(validateChecksumReadExtras({ kind: 'csum-read', barrier: da, report: { barrier: da, reply: { raw: reply } } }),
        { report: { barrier: da, reply: { raw: reply } } });
    assert.throws(() => validateChecksumReadExtras({ kind: 'csum-bind', barrier: da, report: { barrier: da, reply: null } }), /Only reads carry/);
    assert.throws(() => validateChecksumReadExtras({ kind: 'csum-read', barrier: '\x1b[?1c', report: { barrier: da, reply: null } }), /not ordered by this read barrier/);
    assert.throws(() => validateChecksumReadExtras({ kind: 'csum-read', barrier: da, report: { barrier: da, reply: { raw: 'x' } } }), /rectangle checksum/);
});

test('the parent observer journals the reply beside the cell frame and refuses overlapping requests', async () => {
    const observer = new RectChecksumObserver({ appData: '/tmp/none', ownerPid: 2, profileName: 'p', nonce: 'NTILDE_CK_x', expectedPaneId: 'p' });
    let release;
    observer.cells = { restored: false, frames: [], handle: (kind, budget, barrier) => new Promise(resolve => {
        release = () => { observer.cells.frames.push({ index: observer.cells.frames.length, kind, barrier }); resolve({ screen: {} }); };
    }) };
    const pending = observer.handle({ kind: 'csum-read', barrier: da, report: { barrier: da, reply: { raw: reply } } }, () => 1000);
    await assert.rejects(observer.handle({ kind: 'csum-read', barrier: da }, () => 1000), /busy/);
    release(); await pending;
    assert.deepEqual(observer.frames[0], { index: 0, kind: 'csum-read', barrier: da, report: { barrier: da, reply: { raw: reply } } });
    const next = observer.handle({ kind: 'csum-read', barrier: da }, () => 1000);
    release(); await next;
    assert.equal(observer.frames[1].report, undefined, 'a cells read carries no report');
});
