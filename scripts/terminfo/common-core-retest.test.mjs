import test from 'node:test';
import assert from 'node:assert/strict';
import { focusedRetestCases, invalidatedWindowCases } from './common-core-retest.mjs';
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

test('focused Ghostty selection keeps both unrelated timeouts and non-timeout outcomes out', () => {
    const ids = ['modes.alt-screen.exit', 'sgr.bold', 'sgr.faint', 'sgr.italic', 'sgr.underline.single'];
    const timeout = (featureId) => ({ featureId, status: 'probe-todo', reason: 'Probe exceeded 30000ms' });
    const input = { ...raw([...ids.map(timeout), timeout('sgr.hidden')]), subject: { id: 'ghostty' } };
    assert.deepEqual(focusedRetestCases(input, [...ids, 'sgr.hidden'], 'probe-timeout'), ids);
    input.results[0].reason = 'no screenshot adapter';
    assert.throws(() => focusedRetestCases(input, ids, 'probe-timeout'), /five Ghostty timeout rows/);
});

test('focused iTerm2 selection requires the complete seven-case stability failure set', () => {
    const ids = ['alt-screen', 'decstbm', 'decstbm-reset', 'reverse-index', 'scroll-down', 'scroll-up', 'set-region']
        .map((name) => 'scrollback.' + name);
    const failure = (featureId) => ({
        featureId,
        status: 'probe-invalid',
        reason: 'two captures of one static frame were not byte-stable',
    });
    const input = { ...raw([...ids.map(failure), failure('cursor.hide')]), subject: { id: 'iterm2' } };
    assert.deepEqual(focusedRetestCases(input, [...ids, 'cursor.hide'], 'capture-stability'), ids);
    input.results[1].status = 'supported';
    assert.throws(() => focusedRetestCases(input, ids, 'capture-stability'), /seven iTerm2 static-frame invalidations/);
});
