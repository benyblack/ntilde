import assert from 'node:assert/strict';
import { test } from 'node:test';
import { capsReplyLimit, parseCapsFrame, parseCapsReply, validateCapsExchange } from './caps-report.mjs';

const da = '\x1b[?62;4;22c';
const st = '\x1b\\';

test('one frame accepts ST and BEL terminators and retains each reply byte for byte', () => {
    const stReply = '\x1b]1337;Capabilities=T3CwLrMSc7UTs3BFGsGoSyHNoP' + st;
    const belReply = '\x1b]1337;Capabilities=T3CwLrMSc7UTs3BFGsGoSyHNoP\x07';
    assert.deepEqual(parseCapsFrame(stReply + da), { barrier: da, replies: [stReply] });
    assert.deepEqual(parseCapsFrame(belReply + da), { barrier: da, replies: [belReply] });
    assert.deepEqual(parseCapsFrame(da), { barrier: da, replies: [] }, 'a bare DA barrier is the explicitly absent reply list');
    assert.equal(parseCapsFrame(stReply), null, 'incomplete until the barrier arrives');
    assert.equal(parseCapsFrame(stReply.slice(0, -1)), null, 'an unfinished reply is incomplete');
});

test('the framing retains any bounded OSC body and refuses stray bytes', () => {
    // The framing does not interpret the reply body: the pinned valid pattern accepts any characters
    // without BEL or ESC, so nonsense is retained as-is and the reply shape is the contract's business.
    assert.deepEqual(parseCapsFrame('\x1b]1337;Capabilities=anything-goes\x07' + da), { barrier: da, replies: ['\x1b]1337;Capabilities=anything-goes\x07'] });
    assert.deepEqual(parseCapsFrame('\x1b]1337;nonsense\x07' + da), { barrier: da, replies: ['\x1b]1337;nonsense\x07'] });
    assert.throws(() => parseCapsFrame('\x1b[31m' + da), /barrier/);
    assert.throws(() => parseCapsFrame('\x1b]1337;' + '\x1b'.repeat(64) + '\x1b\\' + da), /reply/);
});

test('a retained exchange is validated reply by reply and capped', () => {
    const reply = '\x1b]1337;Capabilities=T3CwLrMSc7UTs3BFGsGoSyHNoP\x07';
    const exchange = { barrier: da, replies: Array.from({ length: capsReplyLimit }, () => reply) };
    assert.doesNotThrow(() => validateCapsExchange(exchange));
    assert.throws(() => validateCapsExchange({ barrier: da, replies: [...exchange.replies, reply] }), /retained feature-reporting/);
    // A reply body carrying an embedded ESC (a smuggled second frame) is refused.
    assert.throws(() => validateCapsExchange({ barrier: da, replies: ['\x1b]1337;x\x1b]1337;y\x07'] }), /Unexpected feature-reporting reply/);
});

test('framing retains unknown OSC replies without stalling and refuses incomplete retained strings', () => {
    assert.deepEqual(parseCapsFrame('\x1b]999;value\x07' + da), { barrier: da, replies: ['\x1b]999;value\x07'] });
    for (const reply of ['\x1b]1337;Capabilities=T3', '\x1b]1337;x\x07tail', '\x1bPunterminated', '\x1b[?1;2#Qjunk']) {
        assert.throws(() => parseCapsReply(reply), /feature-reporting reply/);
    }
    for (let end = 0; end < da.length; end++) assert.equal(parseCapsFrame(da.slice(0, end)), null);
});
