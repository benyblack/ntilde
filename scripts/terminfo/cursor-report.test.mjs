import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { test } from 'node:test';
import { cursorReportQuery, parseCursorReport, parseCursorReportFrame, validateCursorReport } from './cursor-report.mjs';
import { terminalFence } from './terminal-fence.mjs';

class Input extends EventEmitter {
    isRaw = false; paused = true;
    isPaused() { return this.paused; }
    setRawMode(value) { this.isRaw = value; }
    pause() { this.paused = true; }
    resume() { this.paused = false; }
}

test('parses a CPR reply and refuses anything that is not a 1-based report', () => {
    assert.deepEqual(parseCursorReport('\x1b[11;1R'), { raw: '\x1b[11;1R', row: 11, col: 1 });
    for (const bad of ['\x1b[0;1R', '\x1b[1;0R', '\x1b[1R', '\x1b[?1;1R', '11;1R', '\x1b[1;1;1R', 5, null, '\x1b[99999;1R']) {
        assert.throws(() => parseCursorReport(bad), /cursor position report|1-based/i, String(bad));
    }
});

test('a frame is incomplete until the DA barrier, and a DA alone is an explicit absence', () => {
    assert.equal(parseCursorReportFrame(''), null);
    assert.equal(parseCursorReportFrame('\x1b'), null);
    assert.equal(parseCursorReportFrame('\x1b[11;'), null);
    assert.equal(parseCursorReportFrame('\x1b[11;1R'), null);
    assert.equal(parseCursorReportFrame('\x1b[11;1R\x1b[?62;'), null);
    assert.deepEqual(parseCursorReportFrame('\x1b[11;1R\x1b[?62;4c'), { barrier: '\x1b[?62;4c', reply: { raw: '\x1b[11;1R', row: 11, col: 1 } });
    assert.deepEqual(parseCursorReportFrame('\x1b[?62;4c'), { barrier: '\x1b[?62;4c', reply: null });
    assert.throws(() => parseCursorReportFrame('\x1b[11;1Rjunk'), /barrier bytes/);
    assert.throws(() => parseCursorReportFrame('x'.repeat(161)), /Oversized/);
    assert.throws(() => parseCursorReportFrame('\x1b[1x'), /Unexpected cursor report bytes/);
});

test('validates a retained exchange against its raw bytes', () => {
    const good = { barrier: '\x1b[?62c', reply: { raw: '\x1b[2;3R', row: 2, col: 3 } };
    assert.deepEqual(validateCursorReport(good), good);
    assert.deepEqual(validateCursorReport({ barrier: '\x1b[?62c', reply: null }), { barrier: '\x1b[?62c', reply: null });
    assert.throws(() => validateCursorReport({ ...good, reply: { raw: '\x1b[2;3R', row: 3, col: 3 } }), /disagrees/);
    assert.throws(() => validateCursorReport({ ...good, barrier: 'x' }), /Invalid retained/);
    assert.throws(() => validateCursorReport(null), /Invalid retained/);
});

test('the fence orders a CPR with a DA and retains the raw reply across fragments', async () => {
    const input = new Input(); let written;
    const fence = terminalFence(input, { write(value, callback) { written = value; callback(); } });
    const pending = fence.cursorReport('\x1b[2;1H');
    assert.equal(written, '\x1b[2;1H' + cursorReportQuery + '\x1b[c');
    await assert.rejects(fence.write(''), /Concurrent/);
    for (const byte of Buffer.from('\x1b[2;1R\x1b[?62;4;22c')) input.emit('data', Buffer.from([byte]));
    assert.deepEqual(await pending, { barrier: '\x1b[?62;4;22c', reply: { raw: '\x1b[2;1R', row: 2, col: 1 } });
    fence.dispose();
});

test('a missing CPR is reported as an explicit absence and a malformed one fails the fence', async () => {
    const input = new Input(), fence = terminalFence(input, { write(_, callback) { callback(); } });
    const absent = fence.cursorReport();
    input.emit('data', Buffer.from('\x1b[?62c'));
    assert.deepEqual(await absent, { barrier: '\x1b[?62c', reply: null });
    const bad = fence.cursorReport();
    input.emit('data', Buffer.from('\x1b[0;1R\x1b[?62c'));
    await assert.rejects(bad, /1-based/);
    await assert.rejects(fence.cursorReport(), /1-based/);
    fence.dispose();
});
