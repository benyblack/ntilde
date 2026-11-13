import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { test } from 'node:test';
import { parseXtgettcapFrame, parseXtgettcapReply, validateXtgettcapExchange, xtgettcapReplyLimit } from './xtgettcap-report.mjs';
import { terminalFence } from './terminal-fence.mjs';
import { XtgettcapObserver, validateXtgettcapReadExtras } from './xtgettcap-capture.mjs';

const da = '\x1b[?62;4;22c';
const refusal = '\x1bP0+r\x1b\\';
const coValue = '\x1bP1+r436f=323536\x1b\\';
const tcFlag = '\x1bP1+r5463\x1b\\';

class Input extends EventEmitter {
    isRaw = false; paused = true;
    isPaused() { return this.paused; }
    setRawMode(value) { this.isRaw = value; }
    pause() { this.paused = true; }
    resume() { this.paused = false; }
}

test('one exchange is a list of raw DCS replies (or their explicit absence) ordered by the DA barrier', () => {
    assert.deepEqual(parseXtgettcapFrame(refusal + da), { barrier: da, replies: [refusal] });
    assert.deepEqual(parseXtgettcapFrame(coValue + refusal + tcFlag + da), { barrier: da, replies: [coValue, refusal, tcFlag] },
        'a multi-name request earns one reply per name, all retained');
    assert.deepEqual(parseXtgettcapFrame(da), { barrier: da, replies: [] }, 'a complete DA alone is an explicitly absent reply');
    for (const partial of ['', '\x1b', '\x1bP', '\x1bP0+r', refusal.slice(0, -1), refusal, refusal + '\x1b[?6', '\x1b[?62;4;22', coValue + '\x1b']) {
        assert.equal(parseXtgettcapFrame(partial), null, JSON.stringify(partial));
    }
});

test('anything but bounded DCS replies before the barrier is refused', () => {
    for (const bytes of ['user input', '\x1b[1;1R' + da, '\x1bP1+r\x1bX' + da, '\x1bP' + 'x'.repeat(129) + '\x1b\\' + da,
        refusal + 'x', 'x'.repeat(513), refusal + '\x1b[?1;1R' + da]) {
        assert.throws(() => parseXtgettcapFrame(bytes), /XTGETTCAP/, JSON.stringify(bytes).slice(0, 40));
    }
    const tooMany = Array.from({ length: xtgettcapReplyLimit + 1 }, () => refusal).join('') + da;
    assert.throws(() => parseXtgettcapFrame(tooMany), /Too many XTGETTCAP/);
    assert.throws(() => parseXtgettcapReply('\x1bP0+r'), /Unexpected XTGETTCAP reply/);
    assert.throws(() => parseXtgettcapReply('\x1bP0+r\x1b\\x'), /Unexpected XTGETTCAP reply/);
});

test('a retained exchange is revalidated from its raw bytes', () => {
    assert.deepEqual(validateXtgettcapExchange({ barrier: da, replies: [refusal] }), { barrier: da, replies: [refusal] });
    assert.deepEqual(validateXtgettcapExchange({ barrier: da, replies: [] }), { barrier: da, replies: [] });
    for (const value of [null, {}, { barrier: da }, { barrier: 'x', replies: [] }, { barrier: da, replies: 'x' },
        { barrier: da, replies: [refusal, null] }, { barrier: da, replies: [refusal], extra: 1 },
        { barrier: da, replies: Array.from({ length: xtgettcapReplyLimit + 1 }, () => refusal) }]) {
        assert.throws(() => validateXtgettcapExchange(value), /XTGETTCAP/, JSON.stringify(value));
    }
});

test('the fence sends the exact request, then DA, and resolves the replies with their barrier', async () => {
    const input = new Input(); let written;
    const fence = terminalFence(input, { write(value, callback) { written = value; callback(); } });
    const exchange = fence.exchange('\x1bP+q544e\x1b\\', parseXtgettcapFrame);
    assert.equal(written, '\x1bP+q544e\x1b\\\x1b[c');
    input.emit('data', Buffer.from(refusal.slice(0, 3)));
    input.emit('data', Buffer.from(refusal.slice(3) + da));
    assert.deepEqual(await exchange, { barrier: da, replies: [refusal] });
    const list = fence.exchange('\x1bP+q544e;436f\x1b\\', parseXtgettcapFrame);
    input.emit('data', Buffer.from(refusal + coValue + da));
    assert.deepEqual(await list, { barrier: da, replies: [refusal, coValue] });
    const silent = fence.exchange('\x1bP+q\x1b\\', parseXtgettcapFrame);
    input.emit('data', Buffer.from(da));
    assert.deepEqual(await silent, { barrier: da, replies: [] });
    await assert.rejects(fence.exchange('', parseXtgettcapFrame), /Invalid owned exchange request/);
    const broken = fence.exchange('\x1bP+q544e\x1b\\', parseXtgettcapFrame);
    input.emit('data', Buffer.from('\x1b[2;1R'));
    await assert.rejects(broken, /XTGETTCAP/);
    fence.dispose();
});

test('the parent observer accepts a report only on a read ordered by its own barrier', () => {
    assert.deepEqual(validateXtgettcapReadExtras({ kind: 'xgtc-read', barrier: da }), {});
    assert.deepEqual(validateXtgettcapReadExtras({ kind: 'xgtc-read', barrier: da, report: { barrier: da, replies: [refusal] } }),
        { report: { barrier: da, replies: [refusal] } });
    assert.throws(() => validateXtgettcapReadExtras({ kind: 'xgtc-bind', barrier: da, report: { barrier: da, replies: [] } }), /Only reads carry/);
    assert.throws(() => validateXtgettcapReadExtras({ kind: 'xgtc-read', barrier: '\x1b[?1c', report: { barrier: da, replies: [] } }), /not ordered by this read barrier/);
    assert.throws(() => validateXtgettcapReadExtras({ kind: 'xgtc-read', barrier: da, report: { barrier: da, replies: ['x'] } }), /XTGETTCAP/);
});

test('the parent observer journals the replies beside the cell frame and refuses overlapping requests', async () => {
    const observer = new XtgettcapObserver({ appData: '/tmp/none', ownerPid: 2, profileName: 'p', nonce: 'NTILDE_XG_x', expectedPaneId: 'p' });
    let release;
    observer.cells = { restored: false, frames: [], handle: (kind, budget, barrier) => new Promise(resolve => {
        release = () => { observer.cells.frames.push({ index: observer.cells.frames.length, kind, barrier }); resolve({ screen: {} }); };
    }) };
    const pending = observer.handle({ kind: 'xgtc-read', barrier: da, report: { barrier: da, replies: [refusal] } }, () => 1000);
    await assert.rejects(observer.handle({ kind: 'xgtc-read', barrier: da }, () => 1000), /busy/);
    release(); await pending;
    assert.deepEqual(observer.frames[0], { index: 0, kind: 'xgtc-read', barrier: da, report: { barrier: da, replies: [refusal] } });
    const next = observer.handle({ kind: 'xgtc-read', barrier: da }, () => 1000);
    release(); await next;
    assert.equal(observer.frames[1].report, undefined, 'a cells read carries no report');
});
