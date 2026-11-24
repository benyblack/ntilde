import assert from 'node:assert/strict';
import { test } from 'node:test';
import { dslReplyLimit, parseDslFrame, parseDslReply, validateDslExchange } from './dsl-report.mjs';

const da = '\x1b[?62;4;22c';
const three = '\x1b[?3;0$y';

test('one frame retains each mode report byte for byte, in order, before the DA barrier', () => {
    assert.deepEqual(parseDslFrame(three + da), { barrier: da, replies: [three] });
    assert.deepEqual(parseDslFrame(three + '\x1b[?40;0$y' + da), { barrier: da, replies: [three, '\x1b[?40;0$y'] });
    assert.deepEqual(parseDslFrame('\x1b[?7;1$y\x1b[?2;1$y' + da), { barrier: da, replies: ['\x1b[?7;1$y', '\x1b[?2;1$y'] });
    assert.deepEqual(parseDslFrame(da), { barrier: da, replies: [] }, 'a bare DA barrier is the explicitly absent reply list');
});

test('an unfinished reply or barrier is incomplete, never a guess', () => {
    assert.equal(parseDslFrame(three), null, 'incomplete until the barrier arrives');
    for (let end = 0; end < da.length; end++) assert.equal(parseDslFrame(da.slice(0, end)), null, `barrier prefix ${end}`);
    for (let end = 0; end < three.length; end++) assert.equal(parseDslFrame(three.slice(0, end)), null, `reply prefix ${end}`);
    assert.equal(parseDslFrame(three + da.slice(0, 6)), null);
});

test('stray bytes, OSC replies and oversized exchanges are refused', () => {
    assert.throws(() => parseDslFrame('\x1b[31m' + da), /barrier/);
    assert.throws(() => parseDslFrame('\x1b]10;rgb:1/2/3\x07' + da), /barrier/);
    assert.throws(() => parseDslFrame('X' + three + da), /barrier/);
    assert.throws(() => parseDslFrame(three.repeat(dslReplyLimit + 1) + da), /Too many decsclm replies/);
    assert.throws(() => parseDslFrame(three.repeat(200)), /Oversized/);
    assert.throws(() => parseDslFrame(7), /Oversized/);
});

test('a reply is exactly one private mode report with a status from 0 to 4', () => {
    for (const status of [0, 1, 2, 3, 4]) assert.equal(parseDslReply(`\x1b[?3;${status}$y`), `\x1b[?3;${status}$y`);
    for (const bad of ['\x1b[?3;5$y', '\x1b[3;0$y', '\x1b[?3;0$', '\x1b[?3;0$yX', '\x1b[?;0$y', '', '\x1b]3;0\x07', 3]) assert.throws(() => parseDslReply(bad), /Unexpected decsclm reply/, String(bad));
});

test('a retained exchange is validated reply by reply and capped', () => {
    const exchange = { barrier: da, replies: Array.from({ length: dslReplyLimit }, () => three) };
    assert.deepEqual(validateDslExchange(exchange), exchange);
    assert.throws(() => validateDslExchange({ barrier: da, replies: [...exchange.replies, three] }), /Invalid retained decsclm report/);
    assert.throws(() => validateDslExchange({ barrier: '\x1b[?3;0$y', replies: [] }), /Invalid retained decsclm report/);
    assert.throws(() => validateDslExchange({ barrier: da, replies: [three], extra: 1 }), /Invalid retained decsclm report/);
    assert.throws(() => validateDslExchange({ barrier: da, replies: ['\x1b[?3;0$y\x1b[?3;0$y'] }), /Unexpected decsclm reply/, 'a smuggled second report in one reply is refused');
});
