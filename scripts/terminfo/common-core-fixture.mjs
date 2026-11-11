// Synthetic test inputs only; never measurement evidence.
import { createHash } from 'node:crypto';
/** Serialize synthetic test input; never measured evidence. */
export const bytes = (value) => Buffer.from(JSON.stringify(value));
export const ids = Array.from({ length: 80 }, (_, index) => `test.case-${index}`);
const requirementsBytes = bytes({
    rows: ids.map((id) => ({ id, name: id, siteRequirement: 'Test fixture only', url: 'https://example.invalid/' })),
});
const requirementsSha256 = createHash('sha256').update(requirementsBytes).digest('hex');
/** Build three matching synthetic subjects for refusal tests. */
export function fixture() {
    return {
        requirementsBytes,
        subjects: ['ntilde', 'iterm2', 'ghostty'].map((id) => {
            const runId = id + '-synthetic-test-only';
            const raw = {
                schemaVersion: 4,
                source: { kind: 'live' },
                subject: { kind: 'term', id, version: '1.0', os: 'macos', osVersion: 'test-os' },
                runId,
                probeHash: 'a'.repeat(12),
                sourceRevision: 'a'.repeat(40),
                measuredAt: '2026-11-11T12:00:00Z',
                results: ids.map((featureId) => ({
                    featureId,
                    status: 'supported',
                    attempt: { scope: 'probe' },
                    rawReplyRef: featureId,
                    tests: [],
                })),
                assertions: ids.map((featureId) => ({
                    featureId,
                    assertionType: 'query_reply',
                    expected: 'test',
                    observed: 'test',
                })),
                rawReplies: Object.fromEntries(ids.map((id) => [id, '{"synthetic":true}'])),
            };
            return {
                id,
                label: id,
                rawBytes: bytes(raw),
                identityBytes: bytes({
                    runId,
                    subject: raw.subject,
                    rawSha256: createHash('sha256').update(bytes(raw)).digest('hex'),
                    upstreamRevision: raw.sourceRevision,
                    check: { ok: true, checked: true },
                }),
                sourceBytes: bytes({
                    version: '1.0',
                    suite: { probeHash: raw.probeHash, probes: { term: ids } },
                    upstreamRevision: raw.sourceRevision,
                    requirementsSha256,
                    workerSha256: 'b'.repeat(64),
                    driverSha256: 'c'.repeat(64),
                    os: 'test-os',
                    executableSha256: 'd'.repeat(64),
                }),
                configBytes: bytes({
                    purpose: 'comparison',
                    runId,
                    probeHash: raw.probeHash,
                    coreIds: ids,
                    scheduledIds: ids,
                }),
                readyBytes: bytes({ runId, pid: 123, tty: '/dev/ttys123' }),
                geometryBytes: bytes({ runId, pid: 123, tty: '/dev/ttys123', geometry: { rows: 40, cols: 140 } }),
                completionBytes: bytes({ runId, pid: 123, completedAt: '2026-11-11T12:01:00Z' }),
                cleanupBytes: bytes({
                    runId,
                    completedAt: '2026-11-11T12:01:01Z',
                    failures: [],
                    ownedWindowAbsent: true,
                    workerExited: true,
                }),
            };
        }),
    };
}
