import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';
import { readFileSync } from 'node:fs';

const root = fileURLToPath(new URL('../../', import.meta.url));
test('the iTerm2 follow-up updates only the nine prior deadline results', () => {
    const publication = resolve(root, 'docs/qa/terminfo/2026-11-25-common-core-comparison');
    const read = (file) => JSON.parse(readFileSync(resolve(publication, file), 'utf8'));
    const earlier = read('iterm2/custody-retest/cases.json');
    const latest = read('iterm2/sgr-timeout-retest/cases.json');
    const report = read('report.json');
    const timedOut = earlier.records.filter(({ reason }) => /^Probe exceeded \d+ms$/.test(reason));
    assert.equal(timedOut.length, 9);
    assert.deepEqual(latest.records.map(({ featureId }) => featureId), timedOut.map(({ featureId }) => featureId));
    const followup = new Map(latest.records.map((record) => [record.featureId, record]));
    for (const old of earlier.records) {
        const retest = report.rows.find(({ id }) => id === old.featureId).results.iterm2.retest;
        const replacement = followup.get(old.featureId);
        assert.equal(retest.runId, replacement ? latest.runId : earlier.runId);
        assert.equal(retest.status, replacement?.status ?? old.status);
        if (replacement) {
            assert.equal(retest.priorRetest.runId, earlier.runId);
            assert.equal(retest.priorRetest.status, old.status);
            assert.match(retest.priorRetest.evidencePath, /iterm2\/custody-retest\/cases\.json$/);
            assert.ok(retest.priorRetest.evidenceLine > 0);
        }
    }
});

test('the Ntilde retest replaces ten capture results and preserves the six other gaps', () => {
    const publication = resolve(root, 'docs/qa/terminfo/2026-11-25-common-core-comparison');
    const report = JSON.parse(readFileSync(resolve(publication, 'report.json'), 'utf8'));
    const results = report.rows.map(({ id, results }) => ({ id, ...results.ntilde }));
    const changed = results.filter(({ retest }) => retest);
    assert.equal(changed.length, 10);
    assert.equal(results.filter(({ status }) => status === 'supported').length, 64);
    assert.ok(changed.every(({ status, retest }) => status !== 'supported' && retest.status === 'supported'));
    assert.deepEqual(results.filter(({ status, retest }) => status !== 'supported' && !retest)
        .map(({ id }) => id), [
        'erase.screen.scrollback', 'scrollback.accumulate', 'scrollback.total-lines',
        'reset.method', 'device.env-term', 'extensions.osc1-icon',
    ]);
});

for (const [label, args] of [
    ['the refreshed comparison and site projection', ['--check']],
    ['the November 11 comparison and focused retest', ['--check', '--historical']],
]) {
    test(label + ' rederives from retained upstream runs and native captures', () => {
        const output = execFileSync(
            process.execPath,
            [resolve(root, 'scripts/terminfo-common-core-publication.mjs'), ...args],
            { cwd: root, encoding: 'utf8', timeout: 120000 },
        );
        assert.match(output, /verified: 80 requirements, 3 applications/);
    });
}
