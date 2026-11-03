import assert from 'node:assert/strict';
import { test } from 'node:test';
import { copyBufferState, copyHistory } from './editing-state.mjs';
const valid = () => ({ scrollTop: 0, scrollBottom: 9, originMode: false, autoWrap: true, insertMode: false, pendingWrap: false, alternateScreen: false, scrollbackLines: 0 });
test('editing facts are detached and no missing property receives an invented default', () => {
    const value = valid(), copied = copyBufferState(value, 10); assert.deepEqual(copied, value);
    value.insertMode = true; assert.equal(copied.insertMode, false);
    for (const key of Object.keys(value)) { const changed = valid(); delete changed[key]; assert.throws(() => copyBufferState(changed, 10)); }
});
for (const [key, value] of [['scrollTop', -1], ['scrollBottom', 10], ['originMode', 0], ['autoWrap', null], ['scrollbackLines', null], ['scrollbackLines', 65], ['scrollbackLines', 1.5]]) {
    test(`reject ${key}=${value}`, () => assert.throws(() => copyBufferState({ ...valid(), [key]: value }, 10)));
}
test('windowless unknown count is explicit and cannot enter local owned comparisons', () => {
    const value = { ...valid(), scrollbackLines: null }; assert.equal(copyBufferState(value, 10, false).scrollbackLines, null);
    assert.throws(() => copyBufferState(value, 10)); assert.throws(() => copyBufferState(valid(), 10, false));
});
test('history proves complete row coverage with a bounded exact total', () => {
    const value = { startLine: 0, totalLines: 2, lines: ['HISTORY0', 'HISTORY1'] };
    const copied = copyHistory(value, 2, 48); value.lines[0] = 'changed'; assert.equal(copied.lines[0], 'HISTORY0');
    for (const changed of [{ ...value, startLine: 1 }, { ...value, totalLines: 3 }, { ...value, lines: ['partial'] }, { ...value, lines: ['x'.repeat(49), 'ok'] }, { ...value, lines: ['\x1b[H', 'ok'] }]) assert.throws(() => copyHistory(changed, 2, 48));
});
