import assert from 'node:assert/strict';
import { test } from 'node:test';
import { pclReplyLimit, parsePclFrame, parsePclReply, validatePclExchange } from './pcl-report.mjs';

const da = '\x1b[?62;4;22c';
const st = '\x1b\\';

test('one frame accepts ST and BEL terminators and retains each reply byte for byte', () => {
    const stReply = '\x1b]10;rgb:1010/2020/3030' + st;
    const belReply = '\x1b]10;rgb:1010/2020/3030\x07';
    assert.deepEqual(parsePclFrame(stReply + da), { barrier: da, replies: [stReply] });
    assert.deepEqual(parsePclFrame(belReply + da), { barrier: da, replies: [belReply] });
    assert.deepEqual(parsePclFrame(da), { barrier: da, replies: [] }, 'a bare DA barrier is the explicitly absent reply list');
    assert.equal(parsePclFrame(stReply), null, 'incomplete until the barrier arrives');
    assert.equal(parsePclFrame(stReply.slice(0, -1)), null, 'an unfinished reply is incomplete');
});

test('the framing retains any bounded OSC body and refuses stray bytes', () => {
    // The framing does not interpret the reply body: the pinned valid pattern accepts any characters
    // without BEL or ESC, so nonsense is retained as-is and the reply shape is the contract's business.
    assert.deepEqual(parsePclFrame('\x1b]13;anything-goes\x07' + da), { barrier: da, replies: ['\x1b]13;anything-goes\x07'] });
    assert.deepEqual(parsePclFrame('\x1b]13;nonsense\x07' + da), { barrier: da, replies: ['\x1b]13;nonsense\x07'] });
    assert.throws(() => parsePclFrame('\x1b[31m' + da), /barrier/);
    assert.throws(() => parsePclFrame('\x1b]13;' + '\x1b'.repeat(64) + '\x1b\\' + da), /reply/);
});

test('a retained exchange is validated reply by reply and capped', () => {
    const reply = '\x1b]10;rgb:1010/2020/3030\x07';
    const exchange = { barrier: da, replies: Array.from({ length: pclReplyLimit }, () => reply) };
    assert.doesNotThrow(() => validatePclExchange(exchange));
    assert.throws(() => validatePclExchange({ barrier: da, replies: [...exchange.replies, reply] }), /retained pointer-color/);
    // A reply body carrying an embedded ESC (a smuggled second frame) is refused.
    assert.throws(() => validatePclExchange({ barrier: da, replies: ['\x1b]13;x\x1b]13;y\x07'] }), /Unexpected pointer-color reply/);
});

test('framing retains unknown OSC replies without stalling and refuses incomplete retained strings', () => {
    assert.deepEqual(parsePclFrame('\x1b]999;value\x07' + da), { barrier: da, replies: ['\x1b]999;value\x07'] });
    for (const reply of ['\x1b]10;rgb:1010/20', '\x1b]13;x\x07tail', '\x1bPunterminated', '\x1b[?1;2#Qjunk']) {
        assert.throws(() => parsePclReply(reply), /pointer-color reply/);
    }
    for (let end = 0; end < da.length; end++) assert.equal(parsePclFrame(da.slice(0, end)), null);
});
