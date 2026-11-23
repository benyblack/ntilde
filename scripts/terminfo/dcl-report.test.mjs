import assert from 'node:assert/strict';
import { test } from 'node:test';
import { dclReplyLimit, parseDclFrame, parseDclReply, validateDclExchange } from './dcl-report.mjs';

const da = '\x1b[?62;4;22c';
const three = '\x1b[?3;0$y';

test('one frame retains each mode report byte for byte, in order, before the DA barrier', () => {
    assert.deepEqual(parseDclFrame(three + da), { barrier: da, replies: [three] });
    assert.deepEqual(parseDclFrame(three + '\x1b[?40;0$y' + da), { barrier: da, replies: [three, '\x1b[?40;0$y'] });
    assert.deepEqual(parseDclFrame('\x1b[?7;1$y\x1b[?2;1$y' + da), { barrier: da, replies: ['\x1b[?7;1$y', '\x1b[?2;1$y'] });
    assert.deepEqual(parseDclFrame(da), { barrier: da, replies: [] }, 'a bare DA barrier is the explicitly absent reply list');
});

test('an unfinished reply or barrier is incomplete, never a guess', () => {
    assert.equal(parseDclFrame(three), null, 'incomplete until the barrier arrives');
    for (let end = 0; end < da.length; end++) assert.equal(parseDclFrame(da.slice(0, end)), null, `barrier prefix ${end}`);
    for (let end = 0; end < three.length; end++) assert.equal(parseDclFrame(three.slice(0, end)), null, `reply prefix ${end}`);
    assert.equal(parseDclFrame(three + da.slice(0, 6)), null);
});

test('stray bytes, OSC replies and oversized exchanges are refused', () => {
    assert.throws(() => parseDclFrame('\x1b[31m' + da), /barrier/);
    assert.throws(() => parseDclFrame('\x1b]10;rgb:1/2/3\x07' + da), /barrier/);
    assert.throws(() => parseDclFrame('X' + three + da), /barrier/);
    assert.throws(() => parseDclFrame(three.repeat(dclReplyLimit + 1) + da), /Too many deccolm replies/);
    assert.throws(() => parseDclFrame(three.repeat(200)), /Oversized/);
    assert.throws(() => parseDclFrame(7), /Oversized/);
});

test('a reply is exactly one private mode report with a status from 0 to 4', () => {
    for (const status of [0, 1, 2, 3, 4]) assert.equal(parseDclReply(`\x1b[?3;${status}$y`), `\x1b[?3;${status}$y`);
    for (const bad of ['\x1b[?3;5$y', '\x1b[3;0$y', '\x1b[?3;0$', '\x1b[?3;0$yX', '\x1b[?;0$y', '', '\x1b]3;0\x07', 3]) assert.throws(() => parseDclReply(bad), /Unexpected deccolm reply/, String(bad));
});

test('a retained exchange is validated reply by reply and capped', () => {
    const exchange = { barrier: da, replies: Array.from({ length: dclReplyLimit }, () => three) };
    assert.deepEqual(validateDclExchange(exchange), exchange);
    assert.throws(() => validateDclExchange({ barrier: da, replies: [...exchange.replies, three] }), /Invalid retained deccolm report/);
    assert.throws(() => validateDclExchange({ barrier: '\x1b[?3;0$y', replies: [] }), /Invalid retained deccolm report/);
    assert.throws(() => validateDclExchange({ barrier: da, replies: [three], extra: 1 }), /Invalid retained deccolm report/);
    assert.throws(() => validateDclExchange({ barrier: da, replies: ['\x1b[?3;0$y\x1b[?3;0$y'] }), /Unexpected deccolm reply/, 'a smuggled second report in one reply is refused');
});
