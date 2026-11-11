import { createHash } from 'node:crypto';

export const comparisonStatusLabels = Object.freeze({
    supported: 'Supported',
    unsupported: 'Unsupported',
    untestable: 'Untestable',
    'probe-todo': 'Not concluded',
    'probe-invalid': 'Invalid measurement',
    'not-scheduled': 'Not tested',
});
/** Hash exact retained input bytes. */
const hash = (bytes) => createHash('sha256').update(bytes).digest('hex');
/** Stop projection before an unbound result can be published. */
const fail = (message) => {
    throw new Error(message);
};
/** Decode a named evidence record with a provenance-specific error. */
const parse = (bytes, name) => {
    try {
        return JSON.parse(bytes.toString());
    } catch {
        return fail(`${name} is not valid JSON.`);
    }
};

/** Project exact upstream results; this module never reinterprets a failed or inconclusive probe. */
export function buildCommonCoreComparison({ requirementsBytes, subjects }) {
    const mapping = parse(requirementsBytes, 'Requirement mapping');
    if (!Array.isArray(mapping.rows) || mapping.rows.length !== 80) fail('Expected the frozen 80-row Core target.');
    const ids = mapping.rows.map((row) => row.id);
    if (new Set(ids).size !== ids.length || ids.some((id) => typeof id !== 'string' || !id))
        fail('Core target IDs must be unique.');
    if (
        !Array.isArray(subjects) ||
        subjects.length !== 3 ||
        new Set(subjects.map((s) => s.id)).size !== 3 ||
        subjects.some((s) => !['ntilde', 'iterm2', 'ghostty'].includes(s.id))
    )
        fail('Expected exactly Ntilde, iTerm2 and Ghostty.');
    const readings = subjects.map((subject) => readCommonCoreSubject(subject, ids, hash(requirementsBytes)));
    for (const key of ['probeHash', 'sourceRevision', 'workerSha256', 'workerBundleSha256', 'os']) {
        if (new Set(readings.map((s) => s.method[key])).size !== 1) fail(`Comparison subjects have different ${key}.`);
    }
    if (new Set(readings.map((s) => JSON.stringify(s.geometry))).size !== 1)
        fail('Comparison subjects have different observed PTY geometry.');
    return {
        schemaVersion: 1,
        kind: 'common-upstream-terminal-probes',
        requirementsSha256: hash(requirementsBytes),
        requirementCount: ids.length,
        method: { ...readings[0].method, geometry: readings[0].geometry },
        subjects: readings.map(({ results, method, geometry, ...subject }) => subject),
        rows: mapping.rows.map((row) => ({
            id: row.id,
            name: row.name,
            requirement: row.siteRequirement,
            url: row.url,
            results: Object.fromEntries(readings.map((s) => [s.id, s.results[row.id]])),
        })),
    };
}

/** Validate one full or explicitly scoped run without regrading its observations. */
export function readCommonCoreSubject(subject, ids, requirementsSha256, selection = {}) {
    const identity = parse(subject.identityBytes, `${subject.id} upstream identity check`);
    const raw = parse(subject.rawBytes, `${subject.id} raw run`);
    const config = parse(subject.configBytes, `${subject.id} configuration`);
    const source = parse(subject.sourceBytes, `${subject.id} source identity`);
    const cleanup = parse(subject.cleanupBytes, `${subject.id} cleanup`);
    const ready = parse(subject.readyBytes, `${subject.id} worker identity`);
    const completion = parse(subject.completionBytes, `${subject.id} completion`);
    const measurement = parse(subject.geometryBytes, `${subject.id} measurement geometry`);
    const context = { identity, raw, config, source, cleanup, ready, completion, measurement, subject };
    assertMeasuredIdentity(context);
    assertCompletedRun(context, selection);
    assertCoreFixture(context, ids, requirementsSha256, selection);
    const geometry = measurement.geometry;
    const { results, counts } = projectCommonCoreResults(context, ids);
    const calibration = readDiagnostic(raw, 'collector.captureCalibration');
    const ownership = readDiagnostic(raw, 'collector.disposableOwnership');
    return {
        id: subject.id,
        label: subject.label,
        version: source.version,
        versionBasis: source.versionBasis,
        measuredAt: raw.measuredAt,
        runId: raw.runId,
        rawSha256: hash(subject.rawBytes),
        executableSha256: source.executableSha256,
        launchDriverSha256: source.driverSha256,
        buildPayloadSha256: source.buildPayloadSha256 ?? null,
        measurementLimits: {
            captureOutcome: calibration?.outcome ?? null,
            captureRefusal: calibration?.refusal ?? null,
            disposableOwnership: ownership?.kind ?? null,
            withheldProbeCount: ids.filter((id) => results[id].reason?.startsWith('Required grant missing:')).length,
            invalidWindowBoundsCount: ids.filter(
                (id) =>
                    results[id].status === 'probe-invalid' &&
                    results[id].reason?.includes('bounds changed during capture'),
            ).length,
        },
        counts,
        supportedShare: (counts.supported / ids.length) * 100,
        geometry,
        method: {
            probeHash: raw.probeHash,
            sourceRevision: raw.sourceRevision,
            workerSha256: source.workerSha256,
            workerBundleSha256: source.workerBundleSha256 ?? null,
            os: source.os,
        },
        results,
    };
}

/** Check the application, source digests and measurement chronology as one bound identity. */
function assertMeasuredIdentity({ identity, raw, source, completion, cleanup, subject }) {
    if (
        identity.runId !== raw.runId ||
        identity.subject?.id !== subject.id ||
        identity.subject?.version !== source.version ||
        identity.rawSha256 !== hash(subject.rawBytes) ||
        identity.upstreamRevision !== source.upstreamRevision ||
        identity.check?.ok !== true ||
        identity.check?.checked !== true
    )
        fail(`${subject.id} lacks a matching upstream identity guard result.`);
    for (const [key, size] of [
        ['upstreamRevision', 40],
        ['workerSha256', 64],
        ['driverSha256', 64],
        ['executableSha256', 64],
    ]) {
        if (typeof source[key] !== 'string' || !new RegExp(`^[a-f0-9]{${size}}$`).test(source[key]))
            fail(`${subject.id} lacks a source or executable digest: ${key}.`);
    }
    if (!/^[a-f0-9]{12}$/.test(raw.probeHash ?? '')) fail(`${subject.id} lacks a suite fingerprint.`);
    const times = [raw.measuredAt, completion.completedAt, cleanup.completedAt].map((value) => Date.parse(value));
    if (times.some((time) => !Number.isFinite(time)) || times[1] < times[0] || times[2] < times[1])
        fail(`${subject.id} has invalid measurement/cleanup chronology.`);
    if (raw.subject?.osVersion !== source.os) fail(`${subject.id} operating-system records disagree.`);
}

/** Require a completed worker and successful cleanup for this exact run. */
function assertCompletedRun({ raw, config, source, ready, completion, cleanup, subject }, selection) {
    if (config.purpose !== (selection.purpose ?? 'comparison'))
        fail(`${subject.id} is a preflight, not a complete comparison run.`);
    if (
        raw.schemaVersion !== 4 ||
        raw.source?.kind !== 'live' ||
        raw.subject?.kind !== 'term' ||
        raw.subject.id !== subject.id ||
        raw.subject.version !== source.version ||
        raw.subject.os !== 'macos'
    )
        fail(`${subject.id} has mismatched measured subject identity.`);
    if (
        !Array.isArray(raw.results) ||
        !raw.runId ||
        raw.runId !== config.runId ||
        raw.runId !== ready.runId ||
        raw.runId !== completion.runId ||
        raw.runId !== cleanup.runId ||
        completion.pid !== ready.pid
    )
        fail(`${subject.id} run and worker identities do not agree.`);
    if (
        !Array.isArray(cleanup.failures) ||
        cleanup.failures.length ||
        !cleanup.completedAt ||
        !completion.completedAt ||
        cleanup.ownedWindowAbsent !== true ||
        cleanup.workerExited !== true
    )
        fail(`${subject.id} has no completed successful cleanup.`);
}

/** Require the frozen target, permitted selection and paired observed PTY geometry. */
function assertCoreFixture({ raw, config, source, ready, measurement, subject }, ids, requirementsSha256, selection) {
    if (
        source.requirementsSha256 !== requirementsSha256 ||
        config.coreIds?.length !== ids.length ||
        new Set(config.coreIds).size !== ids.length ||
        ids.some((id) => !config.coreIds.includes(id))
    )
        fail(`${subject.id} measured a different Core target.`);
    if (
        raw.probeHash !== config.probeHash ||
        raw.probeHash !== source.suite?.probeHash ||
        raw.sourceRevision !== source.upstreamRevision
    )
        fail(`${subject.id} collector identity changed.`);
    const scheduled = config.scheduledIds;
    const expectedScheduled = selection.scheduledIds ?? ids.filter((id) => source.suite.probes.term.includes(id));
    if (
        !Array.isArray(scheduled) ||
        new Set(scheduled).size !== scheduled.length ||
        scheduled.some((id) => !ids.includes(id) || !source.suite.probes.term.includes(id)) ||
        ids.some((id) => expectedScheduled.includes(id) !== scheduled.includes(id))
    )
        fail(`${subject.id} has an unexplained probe selection.`);
    if (measurement.runId !== raw.runId || measurement.pid !== ready.pid || measurement.tty !== ready.tty)
        fail(`${subject.id} geometry belongs to another worker.`);
    const geometry = measurement.geometry;
    if (
        !Number.isSafeInteger(geometry?.rows) ||
        !Number.isSafeInteger(geometry?.cols) ||
        geometry.rows < 1 ||
        geometry.cols < 1
    )
        fail(`${subject.id} lacks measured PTY geometry.`);
}

/** Preserve upstream verdicts while labeling an unattempted position as not tested. */
function displayStatus(result, scheduled) {
    const status = result?.status ?? 'not-scheduled';
    if (!scheduled && status === 'probe-todo' && result?.attempt?.scope === 'none') {
        return 'not-scheduled';
    }
    return status;
}

/** Do not admit a conclusive result without a selected observation and assertion. */
function assertConclusiveObservation({ raw, subject, result, id, scheduled, status }) {
    const assertions = raw.assertions?.filter((assertion) => assertion.featureId === id) ?? [];
    if (
        ['supported', 'unsupported'].includes(status) &&
        (!scheduled.includes(id) ||
            !assertions.length ||
            !result.rawReplyRef ||
            !Object.hasOwn(raw.rawReplies ?? {}, result.rawReplyRef) ||
            result.attempt?.scope !== 'probe')
    ) {
        fail(`${subject.id} ${id} claims a conclusive result without a measured observation and assertion.`);
    }
}

/** Project exact results and their original raw statuses without changing the raw run. */
function projectCommonCoreResults({ raw, config, subject }, ids) {
    const scheduled = config.scheduledIds;
    const rawLines = subject.rawBytes.toString().split('\n');
    const results = {},
        counts = Object.fromEntries(Object.keys(comparisonStatusLabels).map((status) => [status, 0]));
    for (const id of ids) {
        const matches = raw.results.filter((result) => result.featureId === id);
        if (matches.length > 1) fail(`${subject.id} repeats result ${id}.`);
        const result = matches[0];
        if (!result && scheduled.includes(id)) fail(`${subject.id} lost scheduled result ${id}.`);
        const rawStatus = result?.status ?? 'not-scheduled';
        const status = displayStatus(result, scheduled.includes(id));
        if (!Object.hasOwn(comparisonStatusLabels, status)) fail(`${subject.id} has an unknown upstream status.`);
        assertConclusiveObservation({ raw, subject, result, id, scheduled, status });
        counts[status]++;
        const evidenceLine = rawLines.findIndex((line) => line.trim() === `"featureId": ${JSON.stringify(id)},`) + 1;
        results[id] = {
            rawStatus,
            evidenceLine: evidenceLine || null,
            status,
            scheduled: scheduled.includes(id),
            probeId: result?.probeId ?? null,
            method: result?.assertionType ?? null,
            reason: result?.reason ?? null,
            note: result?.note ?? null,
            tests: result?.tests ?? [],
        };
    }
    return { results, counts };
}

/** Read optional collector diagnostics without turning a parse failure into a feature grade. */
function readDiagnostic(raw, key) {
    const text = raw.rawReplies?.[key];
    if (!text) {
        return null;
    }
    try {
        return JSON.parse(text);
    } catch {
        return { raw: text };
    }
}
