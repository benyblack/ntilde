import assert from 'node:assert/strict';
import { test } from 'node:test';
import { parseXcsFrame, parseXcsReply, validateXcsExchange, xcsReplyLimit } from './xcs-report.mjs';

const da = '\x1b[?62;4;22c';
const st = '\x1b\\';
const reply = '\x1b]11;rgb:1111/2222/3333' + st;

test('a barrier alone is an explicitly absent reply list (the silence of a push or pop)', () => {
    assert.deepEqual(parseXcsFrame(da), { barrier: da, replies: [] });
});

test('replies are retained byte for byte in order before the barrier', () => {
    const second = '\x1b]4;5;rgb:4444/5555/6666' + st;
    assert.deepEqual(parseXcsFrame(reply + second + da), { barrier: da, replies: [reply, second] });
});

test('incomplete frames wait and malformed bytes are refused', () => {
    for (const partial of ['', '\x1b', '\x1b]11;rgb:11', '\x1b]11;rgb:1111/2222/3333\x1b', reply, reply + '\x1b[?62;4', '\x1b[']) assert.equal(parseXcsFrame(partial), null, JSON.stringify(partial));
    for (const bad of ['x', reply + 'x', '\x1b[?0;0#Q' + da, '\x1bP1+R41' + st + da, '\x1b]' + 'a'.repeat(200) + st + da]) assert.throws(() => parseXcsFrame(bad), /xterm-color-stack/, JSON.stringify(bad.slice(0, 20)));
    assert.throws(() => parseXcsFrame('a'.repeat(1025)), /Oversized/);
    assert.throws(() => parseXcsFrame(reply.repeat(xcsReplyLimit + 1) + da), /Too many/);
});

test('one retained reply must be one complete bounded OSC string', () => {
    assert.deepEqual(parseXcsReply(reply), { raw: reply });
    for (const bad of [null, '', 'x', '\x1b]11;x', '\x1b]11;a\x1bb' + st, '\x1b[?0;0#Q']) assert.throws(() => parseXcsReply(bad), /Unexpected xterm-color-stack reply/);
});

test('a retained exchange has exactly a barrier and a bounded reply list', () => {
    assert.deepEqual(validateXcsExchange({ barrier: da, replies: [reply] }), { barrier: da, replies: [reply] });
    for (const bad of [null, {}, { barrier: da }, { barrier: 'x', replies: [] }, { barrier: da, replies: [], extra: 1 }, { barrier: da, replies: ['x'] }]) {
        assert.throws(() => validateXcsExchange(bad), /xterm-color-stack/);
    }
});
