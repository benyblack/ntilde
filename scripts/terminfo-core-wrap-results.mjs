// The frozen Core denominator and all non-wrap conclusions come from the
// independently re-derived tab-stop publication. The owned receipt must be
// validated before calling this function.
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { join } from 'node:path';
import { deriveTabStopsCoreReport, readableTabStopsReport } from './terminfo-core-tab-stops-results.mjs';
import { assertRequirementRows, root } from './terminfo-core-results.mjs';
import { wrapCases } from './terminfo/core-wrap-contract.mjs';
const ids = new Set(['text.wrap', 'modes.auto-wrap']);
const previous = 'docs/qa/terminfo/2026-11-11-core-tab-stops-results/report.json';
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const check = (ok, message) => { if (!ok) throw new Error('Core wrap results: ' + message); };
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
export function deriveWrapCoreReport(directory, frozenMapping, identity, measurement) {
    check(identity?.context?.runId === measurement.runId && identity?.receipt?.coreWrapEvidence?.suiteId === 'ntilde-core-wrap-v1', 'receipt identity or wrap suite missing');
    const oldBytes = readFileSync(join(root, previous));
    check(digest(oldBytes) === '5d9b8331d567222bd977f8db5cd5d20bd2fb8d0f94cbd78d7662ebfc03dbca30', 'preceding report changed');
    const historical = JSON.parse(oldBytes); assertRequirementRows(historical.rows, frozenMapping.rows.map(r => r.id));
    const evidence = JSON.parse(readFileSync(join(directory, 'cw-observations.json')));
    check(evidence.runId === measurement.runId && evidence.suiteId === 'ntilde-core-wrap-v1'
        && same(Object.keys(evidence.effects), wrapCases.map(c => c.role)), 'missing, stale or duplicate effects');
    const raw = JSON.parse(readFileSync(join(directory, 'raw.json')));
    const modeTrace = JSON.parse(raw.rawReplies['modes.auto-wrap']);
    const modeQuery = modeTrace.queries.find(q => q.sequence.startsWith('\x1b[?7$p'));
    check(modeQuery, 'mode recognition query missing');
    const { mapping, report } = deriveTabStopsCoreReport(directory, frozenMapping, identity, measurement);
    assertRequirementRows(report.rows, frozenMapping.rows.map(r => r.id));
    const effects = wrapCases.map((c, i) => {
        const effect = evidence.effects[c.role], before = evidence.snapshots[2 + i * 2], after = evidence.snapshots[3 + i * 2];
        check(effect && ['supported', 'unsupported'].includes(effect.outcome) && before && after
            && effect.observed && same(effect.observed.lines, after.screen.lines.map(x => x.trimEnd()))
            && same(effect.observed.cursor, [after.screen.cursorRow, after.screen.cursorCol]), 'invalid effect ' + c.role);
        return { role: c.role, bytes: c.text, mode: c.enabled, outcome: effect.outcome, expected: effect.expected,
            before: { lines: before.screen.lines, cursor: [before.screen.cursorRow, before.screen.cursorCol], cpr: before.cpr.reply },
            after: { ...effect.observed, cpr: after.cpr.reply } };
    });
    for (const id of ids) {
        const row = report.rows.find(r => r.id === id), prior = historical.rows.find(r => r.id === id);
        const relevant = id === 'text.wrap' ? effects.filter(e => e.mode) : effects;
        row.historicalPinnedProbe = prior.pinnedProbe; row.historicalConclusion = prior.conclusion;
        row.criterionMatch = 'matched'; row.conclusion = relevant.every(e => e.outcome === 'supported') ? 'pass' : 'fail';
        row.wrapEffects = relevant; row.wrapGeometry = { original: evidence.geometry.original, measured: { rows: evidence.geometry.original.rows, cols: 80 }, actions: evidence.geometry.actions,
            originalAutoWrap: evidence.geometry.before.bufferState.autoWrap, restoredAutoWrap: evidence.geometry.after.bufferState.autoWrap };
        row.observationLayers.push('stored cells', 'physical cursor', 'verified App and PTY geometry');
        row.effectVerdicts.push(...relevant.map(e => ({ caseId: e.role, verdict: e.outcome })));
        if (id === 'modes.auto-wrap') row.modeRecognition = { query: modeQuery.sequence, reply: modeQuery.match?.[0] ?? null, reason: modeQuery.reason, raw: modeQuery.raw };
        row.evidence.push(...relevant.map(e => ({ file: 'cw-observations.json', caseId: e.role, kind: 'exact-specimen' })));
        row.explanation = id === 'text.wrap' ? 'An 85-byte ASCII specimen was observed at the proved 80-column grid; independent 80- and 81-byte boundary experiments are retained.'
            : 'Mode-7 recognition remains separate; enabled and disabled writes use the same 85 bytes and compare observed cells and cursor.';
        row.blocker = null;
    }
    for (const row of report.rows) if (!ids.has(row.id)) check(row.conclusion === historical.rows.find(r => r.id === row.id).conclusion, 'unattributed conclusion change: ' + row.id);
    report.totals = Object.fromEntries(['pass', 'fail', 'inconclusive'].map(v => [v, report.rows.filter(r => r.conclusion === v).length]));
    check(Object.values(report.totals).reduce((a, b) => a + b, 0) === 80, 'incorrect totals');
    report.corePassPercentage = report.totals.pass * 100 / 80;
    report.scope = 'Private direct-local Release sidecar; bounded parent-owned 80-column wrapping phase with App and PTY resize proofs and restored original geometry.';
    return { mapping, report };
}
export function readableWrapReport(report, mapping) {
    const base = readableTabStopsReport(report, mapping);
    const lines = base.split('\n');
    lines[0] = '# Ntilde Core wrapping results';
    lines.splice(4, 0, 'The bounded wrap phase used independently verified App and PTY geometry at 80 columns and restored the original dimensions. The 85-character specimen is `0123456789` repeated eight times followed by `ABCDE`; the frozen criterion specifies length but not a payload. Each boundary is an independent setup so ordering fences cannot consume a pending wrap.', '', 'The default-tab-stop conclusions were independently measured in #946 and re-derived in this run. Mode-7 recognition remains separate from the enabled and disabled cell effects.', '', 'After this run, the observer fixture dispatch was extracted without changing its sequence and the owner response-cap prefix check was rewritten for string requests. Focused tests cover dispatch, paired observations, restoration and refusal; these mechanical changes do not alter fixture bytes, measured state, geometry actions or grading.', '');
    for (const id of ids) {
        const row = report.rows.find(r => r.id === id), detail = row.wrapEffects.map(e => `${e.role}: bytes ${JSON.stringify(e.bytes)}, outcome ${e.outcome}, expected ${JSON.stringify(e.expected)}, before ${JSON.stringify(e.before)}, after ${JSON.stringify(e.after)}`).join('; ');
        lines.push('', `## ${id}`, '', `Criterion: ${row.requirement}`, '', `Conclusion: **${row.conclusion}**. Measured identity: ${report.measurement.runId}; geometry ${JSON.stringify(row.wrapGeometry)}.`, '', ...(row.modeRecognition ? [`Mode recognition: ${JSON.stringify(row.modeRecognition)}.` , ''] : []), detail);
    }
    return lines.join('\n').replaceAll('terminfo-core-tab-stops-publication.mjs --check', 'terminfo-core-wrap-publication.mjs --check');
}
