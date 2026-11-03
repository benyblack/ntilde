import assert from 'node:assert/strict';
import { test } from 'node:test';
import { makeEditingCases } from './editing-cases.mjs';
import { compressExpected, matchesExpected } from './editing-expectations.mjs';
const cases = makeEditingCases(10, 48);
test('independent full-cell expectations compress without losing row/column or mode/history controls', () => {
    for (const item of cases) for (const stage of [item.before, item.after]) {
        const expected = compressExpected(stage);
        const attributes = stage.grid.flat().map(({ text, ...value }) => value);
        const full = attributes.map(value => ({ flags: 0, ...value }));
        assert.equal(matchesExpected(expected, expected, full), true, item.id);
        assert.ok(expected.expectedAttributeRuns.length <= 7, item.id);
        const wrongCursor = { ...expected, cursorCol: expected.cursorCol + 1 };
        assert.equal(matchesExpected(wrongCursor, expected, full), false);
        const wrongState = { ...expected, bufferState: { ...expected.bufferState, scrollTop: 1 } };
        if (expected.bufferState.scrollTop !== 1) assert.equal(matchesExpected(wrongState, expected, full), false);
        const wrong = structuredClone(full); wrong[0].backgroundArgb = 123;
        assert.equal(matchesExpected(expected, expected, wrong), false);
    }
});
test('BCE qualifies the background only while all recorded complete attributes stay available', () => {
    const expected = compressExpected(cases.find(value => value.id === 'erase.characters.background-and-margins').after);
    const attrs = expected.expectedAttributeRuns.flatMap(run => Array.from({ length: run.count }, () => ({ flags: 0, ...run.values })));
    attrs[4].foregroundArgb = 123; // No foreground semantics are invented for an erased blank.
    assert.equal(matchesExpected(expected, expected, attrs), true);
    attrs[4].backgroundArgb = 123;
    assert.equal(matchesExpected(expected, expected, attrs), false);
});
