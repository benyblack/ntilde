import assert from 'node:assert/strict';
import { test } from 'node:test';
import { xssRecordingContext, xssExpectedCalls } from './xss-collector.mjs';
import { xssContracts } from './xss-contracts.mjs';

const da = '\x1b[?62;4;22c';
const byId = Object.fromEntries(xssContracts.map(item => [item.caseId, item]));
const push = byId['modes.xtpushsgr'], pop = byId['modes.xtpopsgr'];
const exchange = { barrier: da, replies: [] };

test('the push termless context serves its capture, the DA1 barrier and accepts only the pinned calls in order', () => {
    const context = xssRecordingContext(push, 'termless', exchange, [0, 0]);
    assert.equal(context.feedCapture('\x1b[#{'), '');
    assert.equal(context.feedCapture('\x1b[c'), da);
    context.feed('\x1b[#}');
    assert.equal(context.callCount, 3);
    assert.throws(() => context.feed('\x1b[#}'), /differs from the contract/, 'a fourth call is refused');
});

test('the pop termless context feeds the push first, captures the pop and then the DA1 barrier', () => {
    const context = xssRecordingContext(pop, 'termless', { barrier: da, replies: ['\x1b[?0;0#Q'] }, [0, 0]);
    context.feed('\x1b[#{');
    assert.equal(context.feedCapture('\x1b[#}'), '\x1b[?0;0#Q', 'output of the pop is served byte for byte');
    assert.equal(context.feedCapture('\x1b[c'), da);
    assert.equal(context.callCount, 3);
});

test('contexts refuse a different call kind, text or order and any read', async () => {
    assert.throws(() => xssRecordingContext(push, 'termless', exchange, [0, 0]).feed('\x1b[#{'), /differs from the contract/, 'the push is captured, not plain-fed');
    assert.throws(() => xssRecordingContext(push, 'termless', exchange, [0, 0]).feedCapture('\x1b[#}'), /differs from the contract/);
    assert.throws(() => xssRecordingContext(pop, 'termless', exchange, [0, 0]).feedCapture('\x1b[#}'), /differs from the contract/, 'the pop captures only after the push');
    const context = xssRecordingContext(push, 'terminal', exchange, [0, 0]);
    for (const [name, what] of [['getCell', /cells/], ['getCursor', /the cursor/], ['getText', /text/], ['getTitle', /the title/]]) assert.throws(() => context[name](), what);
    await assert.rejects(context.queryCursorPosition(), /differs from the contract/, 'the cursor query comes after the push write');
});

test('the terminal contexts serve the read cursor as a one-based position, in the pinned order', async () => {
    const pushed = xssRecordingContext(push, 'terminal', exchange, [2, 5]);
    pushed.write('\x1b[#{');
    assert.deepEqual(await pushed.queryCursorPosition(), { row: 3, col: 6 });
    pushed.write('\x1b[#}');
    assert.equal(pushed.callCount, 3);
    const popped = xssRecordingContext(pop, 'terminal', exchange, [0, 0]);
    popped.write('\x1b[#{');
    popped.write('\x1b[#}');
    assert.deepEqual(await popped.queryCursorPosition(), { row: 1, col: 1 });
    assert.equal(await xssRecordingContext(pop, 'terminal', exchange, null).queryCursorPosition === undefined, false);
    const absent = xssRecordingContext(pop, 'terminal', exchange, null);
    absent.write('\x1b[#{'); absent.write('\x1b[#}');
    assert.equal(await absent.queryCursorPosition(), null, 'no cursor means no position');
});

test('the expected call count is the full script, or the early invalid-reply return of the termless path', () => {
    const noisy = { barrier: da, replies: ['\x1b[?0;0#Q'] };
    assert.equal(xssExpectedCalls(push, 'termless', exchange), 3);
    assert.equal(xssExpectedCalls(push, 'termless', noisy), 1, 'the push probe stops at its first capture');
    assert.equal(xssExpectedCalls(pop, 'termless', noisy), 2, 'the pop probe stops at its capture after the push feed');
    for (const contract of [push, pop]) assert.equal(xssExpectedCalls(contract, 'terminal', noisy), 3, 'the terminal path makes every call whatever came back');
    assert.equal(xssExpectedCalls(pop, 'termless', null), 3);
    const context = xssRecordingContext(push, 'termless', noisy, [0, 0]);
    assert.equal(context.feedCapture('\x1b[#{'), '\x1b[?0;0#Q');
    assert.equal(context.callCount, xssExpectedCalls(push, 'termless', noisy));
});
