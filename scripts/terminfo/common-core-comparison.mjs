import { createHash } from 'node:crypto';

export const comparisonStatusLabels = Object.freeze({
    supported: 'Supported', unsupported: 'Unsupported', untestable: 'Untestable',
    'probe-todo': 'Not concluded', 'probe-invalid': 'Invalid measurement', 'not-scheduled': 'Not tested',
});
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const fail = message => { throw new Error(message); };
const parse = (bytes, name) => {
    try { return JSON.parse(bytes.toString()); }
    catch { return fail(`${name} is not valid JSON.`); }
};

/** Project exact upstream results; this module never reinterprets a failed or inconclusive probe. */
export function buildCommonCoreComparison({ requirementsBytes, subjects }) {
    const mapping = parse(requirementsBytes, 'Requirement mapping');
    if (!Array.isArray(mapping.rows) || mapping.rows.length !== 80) fail('Expected the frozen 80-row Core target.');
    const ids = mapping.rows.map(row => row.id);
    if (new Set(ids).size !== ids.length || ids.some(id => typeof id !== 'string' || !id)) fail('Core target IDs must be unique.');
    if (!Array.isArray(subjects) || subjects.length !== 3 || new Set(subjects.map(s => s.id)).size !== 3 ||
        subjects.some(s => !['ntilde', 'iterm2', 'ghostty'].includes(s.id))) fail('Expected exactly Ntilde, iTerm2 and Ghostty.');
    const readings = subjects.map(subject => readSubject(subject, ids, hash(requirementsBytes)));
    for (const key of ['probeHash', 'sourceRevision', 'workerSha256', 'os']) {
        if (new Set(readings.map(s => s.method[key])).size !== 1) fail(`Comparison subjects have different ${key}.`);
    }
    if (new Set(readings.map(s => JSON.stringify(s.geometry))).size !== 1) fail('Comparison subjects have different observed PTY geometry.');
    return {
        schemaVersion: 1,
        kind: 'common-upstream-terminal-probes',
        requirementsSha256: hash(requirementsBytes),
        requirementCount: ids.length,
        method: { ...readings[0].method, geometry: readings[0].geometry },
        subjects: readings.map(({ results, method, geometry, ...subject }) => subject),
        rows: mapping.rows.map(row => ({ id: row.id, name: row.name, requirement: row.siteRequirement,
            url: row.url, results: Object.fromEntries(readings.map(s => [s.id, s.results[row.id]])) })),
    };
}

function readSubject(subject, ids, requirementsSha256) {
    const raw = parse(subject.rawBytes, `${subject.id} raw run`);
    const config = parse(subject.configBytes, `${subject.id} configuration`);
    const source = parse(subject.sourceBytes, `${subject.id} source identity`);
    const cleanup = parse(subject.cleanupBytes, `${subject.id} cleanup`);
    const ready = parse(subject.readyBytes, `${subject.id} worker identity`);
    const completion = parse(subject.completionBytes, `${subject.id} completion`);
    const measurement = parse(subject.geometryBytes, `${subject.id} measurement geometry`);
    if (config.purpose !== 'comparison') fail(`${subject.id} is a preflight, not a complete comparison run.`);
    if (raw.schemaVersion !== 4 || raw.source?.kind !== 'live' || raw.subject?.kind !== 'term' ||
        raw.subject.id !== subject.id || raw.subject.version !== source.version || raw.subject.os !== 'macos') fail(`${subject.id} has mismatched measured subject identity.`);
    if (!Array.isArray(raw.results) || !raw.runId || raw.runId !== config.runId || raw.runId !== ready.runId ||
        raw.runId !== completion.runId || raw.runId !== cleanup.runId || completion.pid !== ready.pid) fail(`${subject.id} run and worker identities do not agree.`);
    if (!Array.isArray(cleanup.failures) || cleanup.failures.length || !cleanup.completedAt || !completion.completedAt ||
        cleanup.ownedWindowAbsent !== true || cleanup.workerExited !== true) fail(`${subject.id} has no completed successful cleanup.`);
    if (source.requirementsSha256 !== requirementsSha256 || config.coreIds?.length !== ids.length ||
        new Set(config.coreIds).size !== ids.length || ids.some(id => !config.coreIds.includes(id))) fail(`${subject.id} measured a different Core target.`);
    if (raw.probeHash !== config.probeHash || raw.probeHash !== source.suite?.probeHash ||
        raw.sourceRevision !== source.upstreamRevision) fail(`${subject.id} collector identity changed.`);
    const scheduled = config.scheduledIds;
    if (!Array.isArray(scheduled) || new Set(scheduled).size !== scheduled.length ||
        scheduled.some(id => !ids.includes(id)) || ids.some(id => source.suite.probes.term.includes(id) !== scheduled.includes(id))) fail(`${subject.id} has an unexplained probe selection.`);
    if (measurement.runId !== raw.runId || measurement.pid !== ready.pid || measurement.tty !== ready.tty) fail(`${subject.id} geometry belongs to another worker.`);
    const geometry = measurement.geometry;
    if (!Number.isSafeInteger(geometry?.rows) || !Number.isSafeInteger(geometry?.cols) || geometry.rows < 1 || geometry.cols < 1) fail(`${subject.id} lacks measured PTY geometry.`);
    const results = {}, counts = Object.fromEntries(Object.keys(comparisonStatusLabels).map(status => [status, 0]));
    for (const id of ids) {
        const matches = raw.results.filter(result => result.featureId === id);
        if (matches.length > 1) fail(`${subject.id} repeats result ${id}.`);
        const result = matches[0];
        if (!result && scheduled.includes(id)) fail(`${subject.id} lost scheduled result ${id}.`);
        const status = result?.status ?? 'not-scheduled';
        if (!Object.hasOwn(comparisonStatusLabels, status)) fail(`${subject.id} has an unknown upstream status.`);
        const assertions = raw.assertions?.filter(assertion => assertion.featureId === id) ?? [];
        if (['supported', 'unsupported'].includes(status) && (!assertions.length || !result.rawReplyRef ||
            !Object.hasOwn(raw.rawReplies ?? {}, result.rawReplyRef) || result.attempt?.scope !== 'probe')) {
            fail(`${subject.id} ${id} claims a conclusive result without a measured observation and assertion.`);
        }
        counts[status]++;
        results[id] = { status, scheduled: scheduled.includes(id), probeId: result?.probeId ?? null,
            method: result?.assertionType ?? null, reason: result?.reason ?? null,
            note: result?.note ?? null, tests: result?.tests ?? [] };
    }
    const diagnostic = key => { const text = raw.rawReplies?.[key]; if (!text) return null; try { return JSON.parse(text); } catch { return { raw: text }; } };
    const calibration = diagnostic('collector.captureCalibration');
    const ownership = diagnostic('collector.disposableOwnership');
    return { id: subject.id, label: subject.label, version: source.version, versionBasis: source.versionBasis, measuredAt: raw.measuredAt,
        runId: raw.runId, rawSha256: hash(subject.rawBytes), executableSha256: source.executableSha256, launchDriverSha256: source.driverSha256, buildPayloadSha256: source.buildPayloadSha256 ?? null,
        measurementLimits: { captureOutcome: calibration?.outcome ?? null, captureRefusal: calibration?.refusal ?? null, disposableOwnership: ownership?.kind ?? null },
        counts, supportedShare: counts.supported / ids.length * 100,
        geometry, method: { probeHash: raw.probeHash, sourceRevision: raw.sourceRevision,
            workerSha256: source.workerSha256, os: source.os }, results };
}
