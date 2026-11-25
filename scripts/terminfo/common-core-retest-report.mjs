import { createHash } from 'node:crypto';
import { focusedRetestCases, retestAdapterPaths, retestReasons } from './common-core-retest.mjs';
import { readCommonCoreSubject, comparisonStatusLabels } from './common-core-comparison.mjs';
/** Bind a retest to the exact original raw bytes. */
const sha = (bytes) => createHash('sha256').update(bytes).digest('hex');
/** Decode an already retained JSON input. */
const parse = (bytes) => JSON.parse(bytes.toString());

/** Record all focused outcomes and compute a traceable latest-evidence count, preserving the full run. */
export function buildCommonCoreRetest({ comparison, baseline, retest, captureAdapter, previous }) {
    const original = parse(baseline.rawBytes),
        config = parse(retest.configBytes);
    const baseSource = parse(baseline.sourceBytes),
        newSource = parse(retest.sourceBytes);
    const ids = comparison.rows.map((row) => row.id);
    const kind = config.retest?.kind ?? 'window-movement';
    assertPreviousRetest(kind, previous, baseline, original);
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
    if (previous && measured.runId === previous.runId)
        throw new Error('A follow-up retest requires a distinct run identity.');
    assertRetestObservations(retest, selected, kind);
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
        ...summarizeOutcomes(comparison, baseline.id, measured, selected, previous),
    };
}

/** Replace selected results while retaining the previous stage for a follow-up. */
function summarizeOutcomes(comparison, subjectId, measured, selected, previous) {
    const counts = Object.fromEntries(Object.keys(comparisonStatusLabels).map((status) => [status, 0]));
    const subject = comparison.subjects.find((entry) => entry.id === subjectId);
    const currentCounts = { ...(previous?.currentCounts ?? subject.counts) };
    const results = Object.fromEntries(selected.map((id) => {
        const result = measured.results[id],
            old = previous ? previous.results[id] : comparison.rows.find((row) => row.id === id).results[subjectId];
        if (!old) throw new Error(`Follow-up case lacks a preceding outcome: ${id}.`);
        counts[result.status]++;
        currentCounts[old.status]--;
        currentCounts[result.status]++;
        return [id, { ...result, runId: measured.runId, previousStatus: old.status, ...(previous && { priorRetest: old }) }];
    }));
    return { counts, currentCounts, results };
}

function assertRetestObservations(retest, selected, kind) {
    const raw = parse(retest.rawBytes);
    if (['capture-custody', 'iterm-sgr-timeout'].includes(kind))
        assertITermCaptureCustody(raw, selected, parse(retest.windowBytes), parse(retest.readyBytes),
            kind === 'iterm-sgr-timeout');
    if (kind === 'ntilde-capture')
        assertNtildeCursorForeground(raw, parse(retest.windowBytes), parse(retest.readyBytes));
    if (raw.results.some((result) => !selected.includes(result.featureId) && result.attempt?.scope === 'probe'))
        throw new Error('Retest executed a case outside the invalidated selection.');
}

/** Validate the cursor-specific foreground witness against the parent-owned application. */
export function assertNtildeCursorForeground(raw, owner, worker) {
    if (!Number.isSafeInteger(owner?.ownerPid) || owner.ownerPid < 1 ||
        !/^\/dev\/tty[a-zA-Z0-9._-]+$/.test(owner.tty) || owner.tty !== worker?.tty)
        throw new Error('Ntilde retest owner and worker TTY disagree.');
    const row = raw.results.find(({ featureId }) => featureId === 'cursor.hide');
    const captures = readCaptures(raw, row.rawReplyRef ?? row.featureId);
    for (const { frame, trace } of captures) {
        const attention = trace?.cursorForeground;
        if (frame?.kind !== 'pixels' || trace?.platform !== 'darwin' ||
            trace.window?.ownerPid !== owner.ownerPid || attention?.appPid !== owner.ownerPid ||
            attention.beforeCapture !== true || attention.afterCapture !== true || trace.paintSettleMs !== 250)
            throw new Error('Ntilde cursor frame lacks paired foreground checks for the owned window.');
    }
    assertWitnessedFrames(row, captures);
}

/** The nine-case follow-up can only refine the already validated custody retest. */
function assertPreviousRetest(kind, previous, baseline, original) {
    if (kind === 'iterm-sgr-timeout') {
        if (previous?.kind !== 'capture-custody' || previous.subjectId !== baseline.id ||
            previous.runId !== original.runId || previous.rawSha256 !== sha(baseline.rawBytes))
            throw new Error('iTerm2 timeouts require the validated custody retest as baseline.');
    } else if (previous) {
        throw new Error('Unexpected preceding retest.');
    }
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
export function assertITermCaptureCustody(raw, ids, window, worker, witnessedSgr = false) {
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
        if (witnessedSgr) validateSgrPaintWait(captures, row.featureId);
        assertWitnessedFrames(row, captures);
    }
}

function assertWitnessedFrames(row, captures) {
    const witnessed = new Set(captures.flatMap(({ frame }) => [frame.ref, frame.sourceRef]));
    const frames = (row.frames ?? []).filter(({ kind }) => kind === 'pixels');
    const references = [row.screenshotRef, ...frames.flatMap(({ ref, sourceRef }) => [ref, sourceRef])];
    if (references.some((ref) => ref && !witnessed.has(ref)))
        throw new Error(`Result cites a frame without capture custody: ${row.featureId}.`);
}

/** The feature captures keep the same static-pair wait and record the active paint-witness path. */
function validateSgrPaintWait(captures, id) {
    for (const { frame, trace } of captures) {
        const staticControl = frame.label === `${id}: capture stability 1` ||
            frame.label === `${id}: capture stability 2`;
        if (trace.paintSettleMs !== (staticControl ? 250 : 0))
            throw new Error(`iTerm2 SGR frame used the wrong adapter settle policy: ${id}.`);
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
