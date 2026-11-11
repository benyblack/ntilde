import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { buildCommonCoreComparison } from './common-core-comparison.mjs';

import { bytes, ids, fixture } from './common-core-fixture.mjs';
/** Mutate one synthetic record while retaining its separate test-only identity binding. */
function change(input, key, mutate) {
    const value = JSON.parse(input.subjects[0][key]);
    mutate(value);
    input.subjects[0][key] = bytes(value);
    if (key === 'rawBytes') {
        const identity = JSON.parse(input.subjects[0].identityBytes);
        identity.rawSha256 = createHash('sha256').update(input.subjects[0].rawBytes).digest('hex');
        input.subjects[0].identityBytes = bytes(identity);
    }
}
test('projects each upstream status without promoting TODO, invalid or unsupported', () => {
    const input = fixture();
    change(input, 'rawBytes', (raw) => {
        raw.results[0].status = 'unsupported';
        raw.results[1].status = 'probe-todo';
        raw.results[2].status = 'probe-invalid';
        raw.results[3].status = 'untestable';
    });
    const result = buildCommonCoreComparison(input);
    assert.equal(result.rows.length, 80);
    assert.deepEqual(result.subjects[0].counts, {
        supported: 76,
        unsupported: 1,
        untestable: 1,
        'probe-todo': 1,
        'probe-invalid': 1,
        'not-scheduled': 0,
    });
    assert.equal(result.subjects[0].supportedShare, 95);
    assert.equal(result.rows[1].results.ntilde.status, 'probe-todo');
});
test('a manual catalogue position stays untested, with no invented execution', () => {
    const input = fixture();
    for (const subject of input.subjects) {
        const raw = JSON.parse(subject.rawBytes);
        raw.results.pop();
        subject.rawBytes = bytes(raw);
        const identity = JSON.parse(subject.identityBytes);
        identity.rawSha256 = createHash('sha256').update(subject.rawBytes).digest('hex');
        subject.identityBytes = bytes(identity);
        const source = JSON.parse(subject.sourceBytes);
        source.suite.probes.term = ids.slice(0, -1);
        subject.sourceBytes = bytes(source);
        const config = JSON.parse(subject.configBytes);
        config.scheduledIds = ids.slice(0, -1);
        subject.configBytes = bytes(config);
    }
    const result = buildCommonCoreComparison(input);
    assert.equal(result.rows[79].results.ntilde.status, 'not-scheduled');
    assert.equal(result.rows[79].results.ntilde.scheduled, false);
});
for (const [name, key, mutate] of [
    [
        'preflight-only run',
        'configBytes',
        (r) => {
            r.purpose = 'preflight-only';
        },
    ],
    [
        'failed identity check',
        'identityBytes',
        (r) => {
            r.check.ok = false;
        },
    ],
    [
        'uncorroborated identity',
        'identityBytes',
        (r) => {
            r.check.checked = false;
        },
    ],
    [
        'identity for different bytes',
        'identityBytes',
        (r) => {
            r.rawSha256 = 'other';
        },
    ],
    [
        'identity for different run',
        'identityBytes',
        (r) => {
            r.runId = 'other';
        },
    ],
    [
        'missing executable digest',
        'sourceBytes',
        (r) => {
            delete r.executableSha256;
        },
    ],
    [
        'invalid timestamp',
        'cleanupBytes',
        (r) => {
            r.completedAt = 'not-a-date';
        },
    ],
    [
        'cleanup before completion',
        'cleanupBytes',
        (r) => {
            r.completedAt = '2026-11-11T11:59:00Z';
        },
    ],
    [
        'wrong subject',
        'rawBytes',
        (r) => {
            r.subject.id = 'ghostty';
        },
    ],
    [
        'wrong version',
        'sourceBytes',
        (r) => {
            r.version = '2.0';
        },
    ],
    [
        'different run',
        'completionBytes',
        (r) => {
            r.runId = 'other';
        },
    ],
    [
        'different worker',
        'completionBytes',
        (r) => {
            r.pid++;
        },
    ],
    [
        'cleanup failure',
        'cleanupBytes',
        (r) => {
            r.failures.push('window survived');
        },
    ],
    [
        'missing owned-window absence proof',
        'cleanupBytes',
        (r) => {
            delete r.ownedWindowAbsent;
        },
    ],
    [
        'surviving worker',
        'cleanupBytes',
        (r) => {
            r.workerExited = false;
        },
    ],
    [
        'different suite',
        'rawBytes',
        (r) => {
            r.probeHash = 'other';
        },
    ],
    [
        'different source',
        'rawBytes',
        (r) => {
            r.sourceRevision = 'other';
        },
    ],
    [
        'different mapping',
        'sourceBytes',
        (r) => {
            r.requirementsSha256 = 'other';
        },
    ],
    [
        'different worker script',
        'sourceBytes',
        (r) => {
            r.workerSha256 = 'e'.repeat(64);
        },
    ],
    [
        'different OS',
        'sourceBytes',
        (r) => {
            r.os = 'other';
        },
    ],
    [
        'different geometry',
        'geometryBytes',
        (r) => {
            r.geometry.cols = 80;
        },
    ],
    [
        'foreign geometry read',
        'geometryBytes',
        (r) => {
            r.pid++;
        },
    ],
    [
        'lost scheduled result',
        'rawBytes',
        (r) => {
            r.results.pop();
        },
    ],
    [
        'duplicate result',
        'rawBytes',
        (r) => {
            r.results.push(r.results[0]);
        },
    ],
    [
        'unexplained selection',
        'configBytes',
        (r) => {
            r.scheduledIds.pop();
        },
    ],
    [
        'legacy boolean-only support',
        'rawBytes',
        (r) => {
            r.assertions = [];
        },
    ],
    [
        'missing observation bytes',
        'rawBytes',
        (r) => {
            delete r.rawReplies[ids[0]];
        },
    ],
    [
        'invented execution',
        'rawBytes',
        (r) => {
            r.results[0].attempt.scope = 'none';
        },
    ],
    [
        'unsupported without observations',
        'rawBytes',
        (r) => {
            r.results[0].status = 'unsupported';
            r.assertions = [];
        },
    ],
    [
        'unknown status',
        'rawBytes',
        (r) => {
            r.results[0].status = 'pass';
        },
    ],
]) {
    test(`refuses ${name}`, () => {
        const input = fixture();
        change(input, key, mutate);
        assert.throws(() => buildCommonCoreComparison(input));
    });
}
test('refuses duplicate target rows and duplicate comparison subjects', () => {
    const input = fixture();
    const mapping = JSON.parse(input.requirementsBytes);
    mapping.rows[1] = mapping.rows[0];
    assert.throws(() => buildCommonCoreComparison({ ...input, requirementsBytes: bytes(mapping) }));
    const other = fixture();
    other.subjects[1] = other.subjects[0];
    assert.throws(() => buildCommonCoreComparison(other));
});

test('per-application launch adapter identities remain explicit without changing the shared probe engine', () => {
    const input = fixture();
    change(input, 'sourceBytes', (source) => {
        source.driverSha256 = 'e'.repeat(64);
    });
    const result = buildCommonCoreComparison(input);
    assert.equal(result.subjects[0].launchDriverSha256, 'e'.repeat(64));
    assert.equal(result.subjects[1].launchDriverSha256, 'c'.repeat(64));
});

test('an unattempted unscheduled TODO is displayed as not tested, preserving its raw status', () => {
    const input = fixture();
    for (const subject of input.subjects) {
        const raw = JSON.parse(subject.rawBytes);
        raw.results.at(-1).status = 'probe-todo';
        raw.results.at(-1).attempt.scope = 'none';
        subject.rawBytes = bytes(raw);
        const identity = JSON.parse(subject.identityBytes);
        identity.rawSha256 = createHash('sha256').update(subject.rawBytes).digest('hex');
        subject.identityBytes = bytes(identity);
        const config = JSON.parse(subject.configBytes);
        config.scheduledIds.pop();
        subject.configBytes = bytes(config);
        const source = JSON.parse(subject.sourceBytes);
        source.suite.probes.term = source.suite.probes.term.slice(0, -1);
        subject.sourceBytes = bytes(source);
    }
    const report = buildCommonCoreComparison(input);
    assert.equal(report.rows.at(-1).results.ntilde.status, 'not-scheduled');
    assert.equal(report.rows.at(-1).results.ntilde.rawStatus, 'probe-todo');
    assert.equal(report.subjects[0].counts['not-scheduled'], 1);
});
