import test from 'node:test';
import assert from 'node:assert/strict';
import { invalidatedWindowCases } from './common-core-retest.mjs';
const moved = (id) => ({
    featureId: id,
    status: 'probe-invalid',
    reason: 'Darwin owned window 123 identity or bounds changed during capture: expected x, observed y',
});
const raw = (results) => ({ schemaVersion: 4, source: { kind: 'live' }, results });
test('selects every movement-invalidated case, excludes failures, missing controls and unrelated IDs', () => {
    const input = raw([
        moved('core.b'),
        moved('core.a'),
        moved('outside'),
        { ...moved('core.c'), status: 'unsupported' },
        { featureId: 'cursor.hide', status: 'probe-invalid', reason: 'no visible-cursor control' },
    ]);
    assert.deepEqual(invalidatedWindowCases(input, ['core.a', 'core.b', 'core.c', 'cursor.hide']), [
        'core.a',
        'core.b',
    ]);
});
test('refuses missing, historical and duplicate movement evidence', () => {
    assert.throws(() => invalidatedWindowCases(raw([]), ['core.a']));
    assert.throws(() => invalidatedWindowCases({ ...raw([moved('core.a')]), schemaVersion: 2 }, ['core.a']));
    assert.throws(() => invalidatedWindowCases(raw([moved('core.a'), moved('core.a')]), ['core.a']));
});
