import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { fixture, bytes, ids } from './common-core-fixture.mjs';
import { buildCommonCoreComparison } from './common-core-comparison.mjs';
import { buildWindowMovementRetest } from './common-core-retest-report.mjs';
const sha = (value) => createHash('sha256').update(value).digest('hex');
/** Mutate synthetic evidence while keeping unrelated bindings valid. */
function update(subject, key, mutate) {
    const data = JSON.parse(subject[key]);
    mutate(data);
    subject[key] = bytes(data);
    if (key === 'rawBytes')
        update(subject, 'identityBytes', (identity) => {
            identity.rawSha256 = sha(subject.rawBytes);
        });
}
/** Build a synthetic full run and its six-case movement retest. */
function scenario() {
    const input = fixture(),
        baseline = input.subjects[0];
    update(baseline, 'rawBytes', (raw) => {
        for (let i = 0; i < 6; i++)
            Object.assign(raw.results[i], {
                status: 'probe-invalid',
                reason: 'Darwin owned window 1 identity or bounds changed during capture',
            });
        Object.assign(raw.results[6], { status: 'probe-invalid', reason: 'no visible-cursor control' });
    });
    const comparison = buildCommonCoreComparison(input),
        retest = { ...baseline },
        runId = 'focused-synthetic-test-only';
    update(retest, 'configBytes', (config) => {
        config.purpose = 'focused-retest';
        config.runId = runId;
        config.scheduledIds = ids.slice(0, 6);
        config.retest = { baselineRunId: comparison.subjects[0].runId, baselineRawSha256: sha(baseline.rawBytes) };
    });
    update(retest, 'rawBytes', (raw) => {
        raw.runId = runId;
        raw.measuredAt = '2026-11-11T12:02:00Z';
        raw.results.forEach((result, index) => {
            result.status = index < 6 ? 'supported' : 'probe-todo';
            result.attempt.scope = index < 6 ? 'probe' : 'none';
        });
    });
    update(retest, 'identityBytes', (identity) => {
        identity.runId = runId;
    });
    for (const key of ['readyBytes', 'completionBytes', 'geometryBytes', 'cleanupBytes'])
        update(retest, key, (record) => {
            record.runId = runId;
            if (record.pid) record.pid = 456;
            if (record.completedAt)
                record.completedAt = key === 'cleanupBytes' ? '2026-11-11T12:03:01Z' : '2026-11-11T12:03:00Z';
        });
    return { comparison, baseline, retest };
}
test('preserves the original full run while all six movement cases gain separately identified evidence', () => {
    const input = scenario(),
        result = buildWindowMovementRetest(input);
    assert.equal(result.counts.supported, 6);
    assert.equal(result.currentCounts.supported, 79);
    assert.equal(result.currentCounts['probe-invalid'], 1);
    assert.equal(input.comparison.subjects[0].counts.supported, 73);
    assert.equal(input.comparison.rows[0].results.ntilde.status, 'probe-invalid');
    assert.equal(result.results[ids[0]].previousStatus, 'probe-invalid');
    assert.deepEqual(result.caseIds, ids.slice(0, 6));
});
test('a valid unsupported retest is recorded instead of selecting only favorable outcomes', () => {
    const input = scenario();
    update(input.retest, 'rawBytes', (raw) => {
        raw.results[0].status = 'unsupported';
    });
    const result = buildWindowMovementRetest(input);
    assert.equal(result.currentCounts.unsupported, 1);
    assert.equal(result.counts.supported, 5);
});
for (const [name, key, change] of [
    ['missing case', 'configBytes', (config) => config.scheduledIds.pop()],
    ['unrelated case', 'configBytes', (config) => config.scheduledIds.push(ids[7])],
    [
        'wrong baseline',
        'configBytes',
        (config) => {
            config.retest.baselineRawSha256 = 'other';
        },
    ],
    [
        'different payload',
        'sourceBytes',
        (source) => {
            source.buildPayloadSha256 = 'other';
        },
    ],
    [
        'different executable',
        'sourceBytes',
        (source) => {
            source.executableSha256 = 'e'.repeat(64);
        },
    ],
    [
        'different shared worker',
        'sourceBytes',
        (source) => {
            source.workerSha256 = 'e'.repeat(64);
        },
    ],
    [
        'different worker helpers',
        'sourceBytes',
        (source) => { source.workerBundleSha256 = 'e'.repeat(64); },
    ],
    [
        'different grid',
        'geometryBytes',
        (geometry) => {
            geometry.geometry.cols = 80;
        },
    ],
    [
        'extra executed case',
        'rawBytes',
        (raw) => {
            raw.results[7].attempt.scope = 'probe';
        },
    ],
    [
        'cleanup failure',
        'cleanupBytes',
        (cleanup) => {
            cleanup.failures = ['window remains'];
        },
    ],
])
    test('refuses retest ' + name, () => {
        const input = scenario();
        update(input.retest, key, change);
        assert.throws(() => buildWindowMovementRetest(input));
    });
