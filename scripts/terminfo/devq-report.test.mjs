import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { test } from 'node:test';
import { parseDevqFrame, parseDevqReply, validateDevqExchange, devqReplyLimit } from './devq-report.mjs';
import { terminalFence } from './terminal-fence.mjs';
import { DevqObserver, validateDevqReadExtras } from './devq-capture.mjs';

const da = '\x1b[?62;4;22c';
const xres = '\x1bP1+R7465726d4e616d65=41\x1b\\';
const refusal = '\x1bP0+R666f6f\x1b\\';
const osc = '\x1b]11;rgb:1111/2222/3333\x1b\\';
const title = '\x1b]ltest-title\x1b\\';
const palette = '\x1b[?1;2#Q';

class Input extends EventEmitter {
    isRaw = false; paused = true;
    isPaused() { return this.paused; }
    setRawMode(value) { this.isRaw = value; }
    pause() { this.paused = true; }
    resume() { this.paused = false; }
}

test('one exchange is a list of raw DCS, OSC and palette-stack replies (or their explicit absence) ordered by the DA barrier', () => {
    assert.deepEqual(parseDevqFrame(xres + da), { barrier: da, replies: [xres] });
    assert.deepEqual(parseDevqFrame(palette + osc + title + refusal + da), { barrier: da, replies: [palette, osc, title, refusal] }, 'mixed reply kinds, all retained in order');
    assert.deepEqual(parseDevqFrame(da), { barrier: da, replies: [] }, 'a complete DA alone is an explicitly absent reply');
    for (const partial of ['', '\x1b', '\x1bP', '\x1bP1+R', xres.slice(0, -1), xres, xres + '\x1b[?6', '\x1b[?62;4;22', '\x1b[', '\x1b[?', '\x1b[?1;', '\x1b[?1;2#', palette,
        osc.slice(0, -1), '\x1b]11;x', title + '\x1b']) {
        assert.equal(parseDevqFrame(partial), null, JSON.stringify(partial));
    }
});

test('anything but bounded replies before the barrier is refused', () => {
    for (const bytes of ['user input', '\x1b[1;1R' + da, '\x1bP1+R\x1bX' + da, '\x1bP' + 'x'.repeat(129) + '\x1b\\' + da, '\x1b]' + 'x'.repeat(129) + '\x1b\\' + da,
        xres + 'x', 'x'.repeat(1025), xres + '\x1b[?1;1R' + da, '\x1b[?123;1#Q' + da, '\x1b[?1;2#QX' + da]) {
        assert.throws(() => parseDevqFrame(bytes), /device-query/, JSON.stringify(bytes).slice(0, 40));
    }
    const tooMany = Array.from({ length: devqReplyLimit + 1 }, () => refusal).join('') + da;
    assert.throws(() => parseDevqFrame(tooMany), /Too many device-query/);
    const tooManyReports = Array.from({ length: devqReplyLimit + 1 }, () => palette).join('') + da;
    assert.throws(() => parseDevqFrame(tooManyReports), /Too many device-query/);
    assert.throws(() => parseDevqReply('\x1bP0+R'), /Unexpected device-query reply/);
    assert.throws(() => parseDevqReply(xres + 'x'), /Unexpected device-query reply/);
    assert.throws(() => parseDevqReply('\x1b[?1;2R'), /Unexpected device-query reply/);
    assert.throws(() => parseDevqReply(7), /Unexpected device-query reply/);
});

test('a retained exchange is revalidated from its raw bytes', () => {
    assert.deepEqual(validateDevqExchange({ barrier: da, replies: [xres, palette] }), { barrier: da, replies: [xres, palette] });
    assert.deepEqual(validateDevqExchange({ barrier: da, replies: [] }), { barrier: da, replies: [] });
    for (const value of [null, {}, { barrier: da }, { barrier: 'x', replies: [] }, { barrier: da, replies: 'x' }, { barrier: da, replies: [xres, null] },
        { barrier: da, replies: [xres], extra: 1 }, { barrier: da, replies: Array.from({ length: devqReplyLimit + 1 }, () => xres) }]) {
        assert.throws(() => validateDevqExchange(value), /device-query/, JSON.stringify(value));
    }
});

test('the fence sends the exact request, then DA, and resolves the replies with their barrier', async () => {
    const input = new Input(); let written;
    const fence = terminalFence(input, { write(value, callback) { written = value; callback(); } });
    const exchange = fence.exchange('\x1bP+Q7465726d4e616d65\x1b\\', parseDevqFrame);
    assert.equal(written, '\x1bP+Q7465726d4e616d65\x1b\\\x1b[c');
    input.emit('data', Buffer.from(xres.slice(0, 3)));
    input.emit('data', Buffer.from(xres.slice(3) + da));
    assert.deepEqual(await exchange, { barrier: da, replies: [xres] });
    const report = fence.exchange('\x1b[#R', parseDevqFrame);
    input.emit('data', Buffer.from(palette + da));
    assert.deepEqual(await report, { barrier: da, replies: [palette] });
    const silent = fence.exchange('\x1b[?#R', parseDevqFrame);
    input.emit('data', Buffer.from(da));
    assert.deepEqual(await silent, { barrier: da, replies: [] });
    const broken = fence.exchange('\x1b[#R', parseDevqFrame);
    input.emit('data', Buffer.from('\x1b[2;1R'));
    await assert.rejects(broken, /device-query/);
    fence.dispose();
});

test('the parent observer accepts a report only on a read ordered by its own barrier', () => {
    assert.deepEqual(validateDevqReadExtras({ kind: 'dvq-read', barrier: da }), {});
    assert.deepEqual(validateDevqReadExtras({ kind: 'dvq-read', barrier: da, report: { barrier: da, replies: [xres] } }), { report: { barrier: da, replies: [xres] } });
    assert.throws(() => validateDevqReadExtras({ kind: 'dvq-bind', barrier: da, report: { barrier: da, replies: [] } }), /Only reads carry/);
    assert.throws(() => validateDevqReadExtras({ kind: 'dvq-read', barrier: '\x1b[?1c', report: { barrier: da, replies: [] } }), /not ordered by this read barrier/);
    assert.throws(() => validateDevqReadExtras({ kind: 'dvq-read', barrier: da, report: { barrier: da, replies: ['x'] } }), /device-query/);
});

test('the parent observer journals the replies beside the cell frame and refuses overlapping requests', async () => {
    const observer = new DevqObserver({ appData: '/tmp/none', ownerPid: 2, profileName: 'p', nonce: 'NTILDE_DQ_x', expectedPaneId: 'p' });
    let release;
    observer.cells = { restored: false, frames: [], handle: (kind, budget, barrier) => new Promise(resolve => {
        release = () => { observer.cells.frames.push({ index: observer.cells.frames.length, kind, barrier }); resolve({ screen: {} }); };
    }) };
    const pending = observer.handle({ kind: 'dvq-read', barrier: da, report: { barrier: da, replies: [xres] } }, () => 1000);
    await assert.rejects(observer.handle({ kind: 'dvq-read', barrier: da }, () => 1000), /busy/);
    release(); await pending;
    assert.deepEqual(observer.frames[0], { index: 0, kind: 'dvq-read', barrier: da, report: { barrier: da, replies: [xres] } });
    const next = observer.handle({ kind: 'dvq-read', barrier: da }, () => 1000);
    release(); await next;
    assert.equal(observer.frames[1].report, undefined, 'a cells read carries no report');
});
