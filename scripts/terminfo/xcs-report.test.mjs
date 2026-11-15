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
    for (const partial of ['', '\x1b', '\x1b]11;rgb:11', '\x1b]11;rgb:1111/2222/3333\x1b', reply, reply + '\x1b[?62;4', '\x1b[', '\x1b[?0;0#Q', '\x1b[?0;0#Q\x1b[?62']) assert.equal(parseXcsFrame(partial), null, JSON.stringify(partial));
    for (const bad of [da + 'x', '\x1b]' + 'a'.repeat(200) + st + da, 'a'.repeat(400)]) assert.throws(() => parseXcsFrame(bad), /xterm-color-stack/, JSON.stringify(bad.slice(0, 20)));
    assert.throws(() => parseXcsFrame('a'.repeat(1025)), /Oversized/);
    assert.throws(() => parseXcsFrame(reply.repeat(xcsReplyLimit + 1) + da), /Too many/);
});

test('unexpected output of a stack operation is retained byte for byte before the barrier, never refused', () => {
    assert.deepEqual(parseXcsFrame('\x1b[?0;0#Q' + da), { barrier: da, replies: ['\x1b[?0;0#Q'] }, 'a CSI report');
    assert.deepEqual(parseXcsFrame('\x1bP1+R41' + st + da), { barrier: da, replies: ['\x1bP1+R41' + st] }, 'a DCS reply');
    assert.deepEqual(parseXcsFrame(reply + 'x' + da), { barrier: da, replies: [reply, 'x'] }, 'an OSC reply followed by stray text');
    assert.deepEqual(parseXcsFrame('x' + reply + da), { barrier: da, replies: ['x' + reply] }, 'stray text keeps everything up to the barrier');
});

test('one retained reply must be one complete bounded OSC string', () => {
    assert.deepEqual(parseXcsReply(reply), { raw: reply });
    for (const bad of [null, '', 'a'.repeat(161), 'x' + da, 7]) assert.throws(() => parseXcsReply(bad), /Unexpected xterm-color-stack reply/);
});

test('a retained exchange has exactly a barrier and a bounded reply list', () => {
    assert.deepEqual(validateXcsExchange({ barrier: da, replies: [reply] }), { barrier: da, replies: [reply] });
    for (const bad of [null, {}, { barrier: da }, { barrier: 'x', replies: [] }, { barrier: da, replies: [], extra: 1 }, { barrier: da, replies: [''] }]) {
        assert.throws(() => validateXcsExchange(bad), /xterm-color-stack/);
    }
});
