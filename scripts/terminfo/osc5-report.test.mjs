import assert from 'node:assert/strict';
import { test } from 'node:test';
import { osc5ReplyLimit, parseOsc5Frame, parseOsc5Reply, validateOsc5Exchange } from './osc5-report.mjs';

const da = '\x1b[?62;4;22c';
const st = '\x1b\\';

test('one frame accepts ST and BEL terminators and retains each reply byte for byte', () => {
    const stReply = '\x1b]5;0;rgb:1212/3434/5656' + st;
    const belReply = '\x1b]5;0;rgb:cccc/cccc/cccc\x07';
    assert.deepEqual(parseOsc5Frame(stReply + da), { barrier: da, replies: [stReply] });
    assert.deepEqual(parseOsc5Frame(belReply + da), { barrier: da, replies: [belReply] });
    assert.deepEqual(parseOsc5Frame(da), { barrier: da, replies: [] }, 'a bare DA barrier is the explicitly absent reply list');
    assert.equal(parseOsc5Frame(stReply), null, 'incomplete until the barrier arrives');
    assert.equal(parseOsc5Frame(stReply.slice(0, -1)), null, 'an unfinished reply is incomplete');
});

test('the framing retains any bounded OSC body and refuses stray bytes', () => {
    // The framing does not interpret the reply body: rgb:1/2 and nonsense are retained as-is and the
    // reply shape is the contract's business. Bytes that are neither a bounded OSC reply nor the DA
    // barrier are refused.
    assert.deepEqual(parseOsc5Frame('\x1b]5;0;rgb:1/2\x07' + da), { barrier: da, replies: ['\x1b]5;0;rgb:1/2\x07'] });
    assert.deepEqual(parseOsc5Frame('\x1b]5;0;nonsense\x07' + da), { barrier: da, replies: ['\x1b]5;0;nonsense\x07'] });
    assert.throws(() => parseOsc5Frame('\x1b[31m' + da), /barrier/);
    assert.throws(() => parseOsc5Frame('\x1b]5;0;' + '\x1b'.repeat(64) + '\x1b\\' + da), /reply/);
});

test('a retained exchange is validated reply by reply and capped', () => {
    const reply = '\x1b]5;0;rgb:1010/1010/1010\x07';
    const exchange = { barrier: da, replies: Array.from({ length: osc5ReplyLimit }, () => reply) };
    assert.doesNotThrow(() => validateOsc5Exchange(exchange));
    assert.throws(() => validateOsc5Exchange({ barrier: da, replies: [...exchange.replies, reply] }), /retained special-color/);
    // A reply body carrying an embedded ESC (a smuggled second frame) is refused.
    assert.throws(() => validateOsc5Exchange({ barrier: da, replies: ['\x1b]5;0;x\x1b]5;0;y\x07'] }), /Unexpected special-color reply/);
});
