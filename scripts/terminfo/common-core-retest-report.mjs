import { createHash } from 'node:crypto';
import { invalidatedWindowCases } from './common-core-retest.mjs';
import { readCommonCoreSubject, comparisonStatusLabels } from './common-core-comparison.mjs';
/** Bind a retest to the exact original raw bytes. */
const sha = (bytes) => createHash('sha256').update(bytes).digest('hex');
/** Decode an already retained JSON input. */
const parse = (bytes) => JSON.parse(bytes.toString());

/** Record all focused outcomes and compute a traceable latest-evidence count, preserving the full run. */
export function buildWindowMovementRetest({ comparison, baseline, retest }) {
    const original = parse(baseline.rawBytes),
        config = parse(retest.configBytes);
    const baseSource = parse(baseline.sourceBytes),
        newSource = parse(retest.sourceBytes);
    const ids = comparison.rows.map((row) => row.id),
        selected = invalidatedWindowCases(original, ids);
    if (
        retest.id !== baseline.id ||
        config.retest?.baselineRunId !== original.runId ||
        config.retest?.baselineRawSha256 !== sha(baseline.rawBytes)
    )
        throw new Error('Retest does not bind to its retained baseline.');
    for (const key of ['version', 'executableSha256', 'buildPayloadSha256']) {
        if (baseSource[key] !== newSource[key]) throw new Error(`Retest changed application identity: ${key}`);
    }
    const measured = readCommonCoreSubject(retest, ids, comparison.requirementsSha256, {
        purpose: 'focused-retest',
        scheduledIds: selected,
    });
    for (const key of ['probeHash', 'sourceRevision', 'workerSha256', 'workerBundleSha256', 'os']) {
        if (measured.method[key] !== comparison.method[key])
            throw new Error(`Retest changed its shared collector: ${key}`);
    }
    if (JSON.stringify(measured.geometry) !== JSON.stringify(comparison.method.geometry))
        throw new Error('Retest changed PTY geometry.');
    if (Date.parse(measured.measuredAt) < Date.parse(original.measuredAt))
        throw new Error('Retest predates its baseline.');
    const raw = parse(retest.rawBytes);
    if (raw.results.some((result) => !selected.includes(result.featureId) && result.attempt?.scope === 'probe'))
        throw new Error('Retest executed a case outside the invalidated selection.');
    const counts = Object.fromEntries(Object.keys(comparisonStatusLabels).map((status) => [status, 0]));
    const currentCounts = { ...comparison.subjects.find((subject) => subject.id === baseline.id).counts };
    const results = Object.fromEntries(
        selected.map((id) => {
            const result = measured.results[id],
                old = comparison.rows.find((row) => row.id === id).results[baseline.id];
            counts[result.status]++;
            currentCounts[old.status]--;
            currentCounts[result.status]++;
            return [id, { ...result, runId: measured.runId, previousStatus: old.status }];
        }),
    );
    return {
        subjectId: baseline.id,
        runId: measured.runId,
        measuredAt: measured.measuredAt,
        baselineRunId: original.runId,
        baselineRawSha256: sha(baseline.rawBytes),
        rawSha256: sha(retest.rawBytes),
        launchDriverSha256: newSource.driverSha256,
        caseIds: selected,
        counts,
        currentCounts,
        results,
    };
}
