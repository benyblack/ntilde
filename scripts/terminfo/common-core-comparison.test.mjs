import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { buildCommonCoreComparison } from './common-core-comparison.mjs';

const bytes = value => Buffer.from(JSON.stringify(value));
const ids = Array.from({ length: 80 }, (_, index) => `test.case-${index}`);
const requirementsBytes = bytes({ rows: ids.map(id => ({ id, name: id, siteRequirement: 'Test fixture only', url: 'https://example.invalid/' })) });
const requirementsSha256 = createHash('sha256').update(requirementsBytes).digest('hex');
function fixture() {
    return { requirementsBytes, subjects: ['ntilde', 'iterm2', 'ghostty'].map(id => {
        const runId = id + '-synthetic-test-only';
        const raw = { schemaVersion: 4, source: { kind: 'live' }, subject: { kind: 'term', id, version: '1.0', os: 'macos' }, runId,
            probeHash: 'suite-test-only', sourceRevision: 'revision-test-only', measuredAt: '2026-11-11T12:00:00Z',
            results: ids.map(featureId => ({ featureId, status: 'supported', attempt: { scope: 'probe' }, rawReplyRef: featureId, tests: [] })),
            assertions: ids.map(featureId => ({ featureId, assertionType: 'query_reply', expected: 'test', observed: 'test' })),
            rawReplies: Object.fromEntries(ids.map(id => [id, '{"synthetic":true}'])) };
        return { id, label: id, rawBytes: bytes(raw),
            sourceBytes: bytes({ version: '1.0', suite: { probeHash: raw.probeHash, probes: { term: ids } }, upstreamRevision: raw.sourceRevision,
                requirementsSha256, workerSha256: 'worker', driverSha256: 'driver', os: 'test-os', executableSha256: 'executable' }),
            configBytes: bytes({ purpose: 'comparison', runId, probeHash: raw.probeHash, coreIds: ids, scheduledIds: ids }),
            readyBytes: bytes({ runId, pid: 123, tty: '/dev/ttys123' }),
            geometryBytes: bytes({ runId, pid: 123, tty: '/dev/ttys123', geometry: { rows: 40, cols: 140 } }),
            completionBytes: bytes({ runId, pid: 123, completedAt: '2026-11-11T12:01:00Z' }),
            cleanupBytes: bytes({ runId, completedAt: '2026-11-11T12:01:01Z', failures: [], ownedWindowAbsent: true, workerExited: true }) };
    }) };
}
function change(input, key, mutate) {
    const value = JSON.parse(input.subjects[0][key]);
    mutate(value);
    input.subjects[0][key] = bytes(value);
}
test('projects each upstream status without promoting TODO, invalid or unsupported', () => {
    const input = fixture();
    change(input, 'rawBytes', raw => {
        raw.results[0].status = 'unsupported'; raw.results[1].status = 'probe-todo';
        raw.results[2].status = 'probe-invalid'; raw.results[3].status = 'untestable';
    });
    const result = buildCommonCoreComparison(input);
    assert.equal(result.rows.length, 80);
    assert.deepEqual(result.subjects[0].counts, { supported: 76, unsupported: 1, untestable: 1, 'probe-todo': 1, 'probe-invalid': 1, 'not-scheduled': 0 });
    assert.equal(result.subjects[0].supportedShare, 95);
    assert.equal(result.rows[1].results.ntilde.status, 'probe-todo');
});
test('a manual catalogue position stays untested, with no invented execution', () => {
    const input = fixture();
    for (const subject of input.subjects) {
        const raw = JSON.parse(subject.rawBytes); raw.results.pop(); subject.rawBytes = bytes(raw);
        const source = JSON.parse(subject.sourceBytes); source.suite.probes.term = ids.slice(0, -1); subject.sourceBytes = bytes(source);
        const config = JSON.parse(subject.configBytes); config.scheduledIds = ids.slice(0, -1); subject.configBytes = bytes(config);
    }
    const result = buildCommonCoreComparison(input);
    assert.equal(result.rows[79].results.ntilde.status, 'not-scheduled');
    assert.equal(result.rows[79].results.ntilde.scheduled, false);
});
for (const [name, key, mutate] of [
    ['preflight-only run', 'configBytes', r => { r.purpose = 'preflight-only'; }],
    ['wrong subject', 'rawBytes', r => { r.subject.id = 'ghostty'; }],
    ['wrong version', 'sourceBytes', r => { r.version = '2.0'; }],
    ['different run', 'completionBytes', r => { r.runId = 'other'; }],
    ['different worker', 'completionBytes', r => { r.pid++; }],
    ['cleanup failure', 'cleanupBytes', r => { r.failures.push('window survived'); }],
    ['missing owned-window absence proof', 'cleanupBytes', r => { delete r.ownedWindowAbsent; }],
    ['surviving worker', 'cleanupBytes', r => { r.workerExited = false; }],
    ['different suite', 'rawBytes', r => { r.probeHash = 'other'; }],
    ['different source', 'rawBytes', r => { r.sourceRevision = 'other'; }],
    ['different mapping', 'sourceBytes', r => { r.requirementsSha256 = 'other'; }],
    ['different worker script', 'sourceBytes', r => { r.workerSha256 = 'other'; }],
    ['different OS', 'sourceBytes', r => { r.os = 'other'; }],
    ['different geometry', 'geometryBytes', r => { r.geometry.cols = 80; }],
    ['foreign geometry read', 'geometryBytes', r => { r.pid++; }],
    ['lost scheduled result', 'rawBytes', r => { r.results.pop(); }],
    ['duplicate result', 'rawBytes', r => { r.results.push(r.results[0]); }],
    ['unexplained selection', 'configBytes', r => { r.scheduledIds.pop(); }],
    ['legacy boolean-only support', 'rawBytes', r => { r.assertions = []; }],
    ['missing observation bytes', 'rawBytes', r => { delete r.rawReplies[ids[0]]; }],
    ['invented execution', 'rawBytes', r => { r.results[0].attempt.scope = 'none'; }],
    ['unsupported without observations', 'rawBytes', r => { r.results[0].status = 'unsupported'; r.assertions = []; }],
    ['unknown status', 'rawBytes', r => { r.results[0].status = 'pass'; }],
]) {
    test(`refuses ${name}`, () => { const input = fixture(); change(input, key, mutate); assert.throws(() => buildCommonCoreComparison(input)); });
}
test('refuses duplicate target rows and duplicate comparison subjects', () => {
    const input = fixture(); const mapping = JSON.parse(input.requirementsBytes); mapping.rows[1] = mapping.rows[0];
    assert.throws(() => buildCommonCoreComparison({ ...input, requirementsBytes: bytes(mapping) }));
    const other = fixture(); other.subjects[1] = other.subjects[0];
    assert.throws(() => buildCommonCoreComparison(other));
});

test('per-application launch adapter identities remain explicit without changing the shared probe engine', () => {
    const input = fixture();
    change(input, 'sourceBytes', source => { source.driverSha256 = 'another-recorded-launcher'; });
    const result = buildCommonCoreComparison(input);
    assert.equal(result.subjects[0].launchDriverSha256, 'another-recorded-launcher');
    assert.equal(result.subjects[1].launchDriverSha256, 'driver');
});
