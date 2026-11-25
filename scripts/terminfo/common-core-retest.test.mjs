import test from 'node:test';
import assert from 'node:assert/strict';
import { focusedRetestCases, invalidatedWindowCases, retestAdapterPaths, retestEvidenceDirectory } from './common-core-retest.mjs';
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
    assert.deepEqual(focusedRetestCases(input, [...ids, 'sgr.hidden'], 'capture-throughput'), ids);
    input.results[0].reason = 'no screenshot adapter';
    assert.throws(() => focusedRetestCases(input, ids, 'probe-timeout'), /five Ghostty timeout rows/);
    assert.throws(() => focusedRetestCases(input, ids, 'capture-throughput'), /five Ghostty timeout rows/);
});

test('each capture retest has a distinct allowed adapter path and timeout evidence URL', () => {
    assert.deepEqual(retestAdapterPaths('capture-stability'), ['packages/terminfo.dev/src/serve.ts']);
    assert.deepEqual(retestAdapterPaths('capture-throughput'), ['packages/terminfo.dev/src/darwin-capture.ts']);
    assert.equal(retestAdapterPaths('probe-timeout'), undefined);
    assert.equal(retestEvidenceDirectory('probe-timeout'), 'focused-retest');
    assert.equal(retestEvidenceDirectory('capture-throughput'), 'timeout-retest');
});

test('iTerm2 custody selection follows retained native frames across verdicts and criterion types', () => {
    const cases = ['invalid-visual', 'query-with-frame', 'pure-query', 'non-native', 'unscheduled'];
    const input = {
        ...raw(cases.map((featureId, index) => ({
            featureId, status: index === 0 ? 'probe-invalid' : 'supported',
            attempt: { scope: index === 4 ? 'none' : 'probe' },
        }))),
        subject: { id: 'iterm2' },
        rawReplies: Object.fromEntries(cases.filter((id) => id !== 'pure-query').map((id) => [
            id, JSON.stringify({ captures: [{ frame: { kind: 'pixels' },
                trace: { platform: id === 'non-native' ? 'linux' : 'darwin' } }] }),
        ])),
    };
    assert.deepEqual(focusedRetestCases(input, cases, 'capture-custody'), cases.slice(0, 2));
    assert.throws(() => focusedRetestCases(input, ['pure-query'], 'capture-custody'), /native captures/);
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
