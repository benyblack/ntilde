import assert from 'node:assert/strict';
import { test } from 'node:test';
import { xcsRecordingContext } from './xcs-collector.mjs';
import { xcsContracts } from './xcs-contracts.mjs';

const da = '\x1b[?62;4;22c';
const byId = Object.fromEntries(xcsContracts.map(item => [item.caseId, item]));
const push = byId['modes.xtpushcolors'], pop = byId['modes.xtpopcolors'];
const exchange = { barrier: da, replies: [] };

test('the push termless context serves its capture, the DA1 barrier and accepts only the pinned calls in order', () => {
    const context = xcsRecordingContext(push, 'termless', exchange, [0, 0]);
    assert.equal(context.feedCapture('\x1b[#P'), '');
    assert.equal(context.feedCapture('\x1b[c'), da);
    context.feed('\x1b[#Q');
    assert.equal(context.callCount, 3);
    assert.throws(() => context.feed('\x1b[#Q'), /differs from the contract/, 'a fourth call is refused');
});

test('the pop termless context feeds the push first, captures the pop and then the DA1 barrier', () => {
    const context = xcsRecordingContext(pop, 'termless', { barrier: da, replies: ['\x1b[?0;0#Q'] }, [0, 0]);
    context.feed('\x1b[#P');
    assert.equal(context.feedCapture('\x1b[#Q'), '\x1b[?0;0#Q', 'output of the pop is served byte for byte');
    assert.equal(context.feedCapture('\x1b[c'), da);
    assert.equal(context.callCount, 3);
});

test('contexts refuse a different call kind, text or order and any read', async () => {
    assert.throws(() => xcsRecordingContext(push, 'termless', exchange, [0, 0]).feed('\x1b[#P'), /differs from the contract/, 'the push is captured, not plain-fed');
    assert.throws(() => xcsRecordingContext(push, 'termless', exchange, [0, 0]).feedCapture('\x1b[#Q'), /differs from the contract/);
    assert.throws(() => xcsRecordingContext(pop, 'termless', exchange, [0, 0]).feedCapture('\x1b[#Q'), /differs from the contract/, 'the pop captures only after the push');
    const context = xcsRecordingContext(push, 'terminal', exchange, [0, 0]);
    for (const [name, what] of [['getCell', /cells/], ['getCursor', /the cursor/], ['getText', /text/], ['getTitle', /the title/]]) assert.throws(() => context[name](), what);
    await assert.rejects(context.queryCursorPosition(), /differs from the contract/, 'the cursor query comes after the push write');
});

test('the terminal contexts serve the read cursor as a one-based position, in the pinned order', async () => {
    const pushed = xcsRecordingContext(push, 'terminal', exchange, [2, 5]);
    pushed.write('\x1b[#P');
    assert.deepEqual(await pushed.queryCursorPosition(), { row: 3, col: 6 });
    pushed.write('\x1b[#Q');
    assert.equal(pushed.callCount, 3);
    const popped = xcsRecordingContext(pop, 'terminal', exchange, [0, 0]);
    popped.write('\x1b[#P');
    popped.write('\x1b[#Q');
    assert.deepEqual(await popped.queryCursorPosition(), { row: 1, col: 1 });
    assert.equal(await xcsRecordingContext(pop, 'terminal', exchange, null).queryCursorPosition === undefined, false);
    const absent = xcsRecordingContext(pop, 'terminal', exchange, null);
    absent.write('\x1b[#P'); absent.write('\x1b[#Q');
    assert.equal(await absent.queryCursorPosition(), null, 'no cursor means no position');
});
