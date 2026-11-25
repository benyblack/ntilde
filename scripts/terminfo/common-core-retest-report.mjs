import { createHash } from 'node:crypto';
import { focusedRetestCases, retestAdapterPaths, retestReasons } from './common-core-retest.mjs';
import { readCommonCoreSubject, comparisonStatusLabels } from './common-core-comparison.mjs';
/** Bind a retest to the exact original raw bytes. */
const sha = (bytes) => createHash('sha256').update(bytes).digest('hex');
/** Decode an already retained JSON input. */
const parse = (bytes) => JSON.parse(bytes.toString());

/** Record all focused outcomes and compute a traceable latest-evidence count, preserving the full run. */
export function buildCommonCoreRetest({ comparison, baseline, retest, captureAdapter }) {
    const original = parse(baseline.rawBytes),
        config = parse(retest.configBytes);
    const baseSource = parse(baseline.sourceBytes),
        newSource = parse(retest.sourceBytes);
    const ids = comparison.rows.map((row) => row.id);
    const kind = config.retest?.kind ?? 'window-movement';
    const selected = focusedRetestCases(original, ids, kind);
    if (kind !== 'window-movement' && config.retest.reason !== retestReasons[kind])
        throw new Error('Retest reason differs from its selected coverage.');
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
    const collector = retestAdapterPaths(kind) ? captureAdapter : comparison.method;
    assertRetestCollector(measured, comparison.method, collector, kind);
    if (JSON.stringify(measured.geometry) !== JSON.stringify(comparison.method.geometry))
        throw new Error('Retest changed PTY geometry.');
    if (Date.parse(measured.measuredAt) < Date.parse(original.measuredAt))
        throw new Error('Retest predates its baseline.');
    const raw = parse(retest.rawBytes);
    if (kind === 'capture-custody')
        assertITermCaptureCustody(raw, selected, parse(retest.windowBytes), parse(retest.readyBytes));
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
        ...(kind !== 'window-movement' && {
            kind,
            probeHash: measured.method.probeHash,
            sourceRevision: measured.method.sourceRevision,
        }),
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

/** Keep the collection and its screenshot observer bound to their verified identities. */
function assertRetestCollector(measured, baselineMethod, collector, kind) {
    if (!collector || measured.method.probeHash !== collector.probeHash ||
        measured.method.sourceRevision !== collector.sourceRevision) {
        throw new Error('Retest collector identity differs from its verified source.');
    }
    for (const key of ['workerSha256', 'workerBundleSha256', 'os']) {
        if (measured.method[key] !== baselineMethod[key])
            throw new Error(`Retest changed its shared collector: ${key}`);
    }
    if (kind !== 'window-movement' && measured.measurementLimits.captureOutcome !== 'installed')
        throw new Error('Focused screenshot retest requires installed native capture.');
}

/** Admit native calibration and feature frames only with paired custody of the launched worker. */
export function assertITermCaptureCustody(raw, ids, window, worker) {
    if (!Number.isSafeInteger(window?.windowId) || window.windowId < 1 ||
        !Number.isSafeInteger(window.appPid) || window.appPid < 1 ||
        !/^\/dev\/tty[a-zA-Z0-9._-]+$/.test(window.tty) || window.tty !== worker?.tty)
        throw new Error('iTerm2 retest window and worker TTY disagree.');
    const calibration = readCaptures(raw, 'collector.captureCalibration.trace');
    if (!calibration.length) throw new Error('iTerm2 retest lacks custody-checked calibration frames.');
    validateCaptureCustody(calibration, window);
    for (const row of raw.results.filter(({ featureId }) => ids.includes(featureId))) {
        const captures = readCaptures(raw, row.rawReplyRef ?? row.featureId);
        validateCaptureCustody(captures, window);
        const witnessed = new Set(captures.flatMap(({ frame }) => [frame.ref, frame.sourceRef]));
        const frames = (row.frames ?? []).filter(({ kind }) => kind === 'pixels');
        const references = [row.screenshotRef, ...frames.flatMap(({ ref, sourceRef }) => [ref, sourceRef])];
        if (references.some((ref) => ref && !witnessed.has(ref)))
            throw new Error(`iTerm2 result cites a frame without session custody: ${row.featureId}.`);
    }
}

function readCaptures(raw, ref) {
    return JSON.parse(raw.rawReplies[ref] ?? '{}').captures ?? [];
}

function validateCaptureCustody(captures, window) {
    for (const { frame, trace } of captures) {
        const custody = trace?.itermSessionCustody;
        if (frame?.kind !== 'pixels' || trace?.platform !== 'darwin' ||
            trace.window?.id !== window.windowId || trace.window.ownerPid !== window.appPid ||
            custody?.windowId !== window.windowId || custody.tty !== window.tty ||
            custody.beforeCapture !== true || custody.afterCapture !== true)
            throw new Error('iTerm2 frame lacks paired session custody for the owned window.');
    }
}
