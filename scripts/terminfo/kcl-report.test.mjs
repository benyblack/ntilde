import assert from 'node:assert/strict';
import { test } from 'node:test';
import { kclReplyLimit, parseKclFrame, parseKclReply, validateKclExchange } from './kcl-report.mjs';
import { KclObserver, validateKclReadExtras } from './kcl-capture.mjs';

const da = '\x1b[?62;4;22c';
const reply = '\x1b]21;foreground=rgb:cc/cc/cc\x1b\\';
const legacy = '\x1b]10;rgb:cccc/cccc/cccc\x1b\\';

test('an exchange is any bounded OSC replies followed by the DA barrier, or the barrier alone', () => {
    assert.deepEqual(parseKclFrame(reply + da), { barrier: da, replies: [reply] });
    assert.deepEqual(parseKclFrame(reply + legacy + reply + da), { barrier: da, replies: [reply, legacy, reply] });
    assert.deepEqual(parseKclFrame(da), { barrier: da, replies: [] }, 'an explicitly absent reply list');
    assert.equal(parseKclFrame(reply), null, 'incomplete until the barrier arrives');
    assert.equal(parseKclFrame(reply.slice(0, -1)), null, 'an unfinished reply is incomplete');
    assert.equal(kclReplyLimit, 8);
});

test('anything that is not a complete bounded reply or the barrier is refused', () => {
    assert.throws(() => parseKclFrame('plain text' + da), /Unexpected device-query/);
    assert.throws(() => parseKclFrame(`\x1b]21;${'x'.repeat(200)}\x1b\\${da}`), /Unexpected device-query/, 'a reply body is bounded');
    assert.throws(() => parseKclFrame(reply.repeat(9) + da), /Too many device-query replies/);
    assert.throws(() => parseKclFrame('x'.repeat(2000)), /Oversized/);
    assert.throws(() => parseKclReply('\x1b]21;a=b\x07'), /Unexpected device-query reply/, 'a retained reply ends with ST');
    assert.deepEqual(parseKclReply(reply), { raw: reply });
});

test('a retained exchange validates and refuses a changed shape', () => {
    assert.deepEqual(validateKclExchange({ barrier: da, replies: [reply] }), { barrier: da, replies: [reply] });
    for (const bad of [null, {}, { barrier: 'x', replies: [] }, { barrier: da, replies: 'x' }, { barrier: da, replies: [], extra: 1 }, { barrier: da, replies: [reply.slice(0, -1)] }]) {
        assert.throws(() => validateKclExchange(bad), /Invalid retained|Unexpected device-query/);
    }
});

test('only a read may carry a report, and it must be ordered by that read\'s own barrier', () => {
    assert.deepEqual(validateKclReadExtras({ kind: 'kcl-read', barrier: da }), {});
    assert.deepEqual(validateKclReadExtras({ kind: 'kcl-read', barrier: da, report: { barrier: da, replies: [reply] } }), { report: { barrier: da, replies: [reply] } });
    assert.throws(() => validateKclReadExtras({ kind: 'kcl-bind', barrier: da, report: { barrier: da, replies: [] } }), /Only reads carry/);
    assert.throws(() => validateKclReadExtras({ kind: 'kcl-read', barrier: '\x1b[?1;1c', report: { barrier: da, replies: [] } }), /not ordered by this read barrier/);
    assert.throws(() => validateKclReadExtras({ kind: 'kcl-read', barrier: da, report: { barrier: da, replies: ['x'] } }), /Unexpected device-query reply/);
});

test('the observer journals a report beside its own frame and refuses reuse while busy or after restoration', async () => {
    const observer = new KclObserver({ paneId: 'p', requests: [] });
    assert.equal(typeof observer.handle, 'function');
    assert.deepEqual(observer.frames, []);
    assert.equal(observer.restored, observer.cells.restored);
    const frames = [];
    observer.cells = { frames, restored: false, handle: async (kind, budget, barrier) => { frames.push({ kind, barrier }); return { kind }; } };
    assert.deepEqual(await observer.handle({ kind: 'kcl-read', barrier: da, report: { barrier: da, replies: [reply] } }), { kind: 'kcl-read' });
    assert.deepEqual(observer.frames[0], { kind: 'kcl-read', barrier: da, report: { barrier: da, replies: [reply] } });
    await observer.handle({ kind: 'kcl-read', barrier: da });
    assert.equal('report' in observer.frames[1], false, 'a cells read carries no report');
    observer.cells.restored = true;
    await assert.rejects(observer.handle({ kind: 'kcl-finish', barrier: da }), /busy or complete/);
});
