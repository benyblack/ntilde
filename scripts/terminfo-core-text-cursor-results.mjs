// Distinct Core publication: re-use the frozen 80 criteria and the established
// independently rederived same-run Core evidence. This module never imports an
// archived script or treats the historical pinned verdict as the new effect.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { deriveReport, assertRequirementRows } from './terminfo-core-results.mjs';
import { textCursorIds, textCursorSuiteId } from './terminfo/core-text-cursor.mjs';

const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const check = (value, message) => { if (!value) throw new Error('Core text/cursor report: ' + message); };

/** The caller must first reissue and verify the owned receipt. */
export function deriveTextCursorCoreReport(directory, frozenMapping, identity, measurement) {
    const report = JSON.parse(readFileSync(join(directory, 'tc-observations.json')));
    check(report.suiteId === textCursorSuiteId && report.runId === measurement.runId
        && identity.context.runId === measurement.runId, 'stale suite identity');
    check(Array.isArray(report.cases) && same(report.cases.map(c => c.caseId), textCursorIds), 'missing, duplicate or reordered cases');
    const mapping = structuredClone(frozenMapping);
    mapping.measurement = measurement;
    for (const row of mapping.rows) {
        if (!textCursorIds.includes(row.id)) continue;
        const effect = report.cases.find(c => c.caseId === row.id)?.effectContract;
        check(effect?.id === 'ntilde-core-text-cursor-effect-v1'
            && ['supported', 'unsupported'].includes(effect.outcome), 'untrusted effect: ' + row.id);
        row.criterionMatch = 'matched';
        row.evidence = [{ file: 'tc-observations.json', caseId: row.id, kind: 'case' }];
        row.rationale = 'The exact joined bytes and independent physical-cell, cursor, CPR and control observations are retained in the same measured run.';
    }
    const result = deriveReport(directory, mapping, identity);
    assertRequirementRows(result.rows, frozenMapping.rows.map(row => row.id));
    for (const row of result.rows) if (textCursorIds.includes(row.id)) {
        const effect = report.cases.find(c => c.caseId === row.id).effectContract;
        row.fixture = effect.fixture;
        row.observed = effect.observed;
        row.blocker = null;
    }
    result.scope = 'Private direct-local Release sidecar; exact joined fixtures, physical stored cells, cursor and raw CPR at measured geometry. Other platforms and profiles are outside this measurement.';
    return { mapping, report: result };
}

/** Format nested layer descriptions without coercing objects to [object Object]. */
export function describeLayers(value) {
    if (Array.isArray(value)) return value.flatMap(item => describeLayers(item)).filter(Boolean);
    if (value && typeof value === 'object') return Object.entries(value).map(([key, item]) => `${key}: ${item}`);
    return value == null ? [] : [String(value)];
}
