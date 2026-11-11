// A distinct publication over the same frozen 80 requirements as #937.
// The caller must validate the complete owned receipt before deriving any row.
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { join } from 'node:path';
import { deriveTextCursorCoreReport, describeLayers } from './terminfo-core-text-cursor-results.mjs';
import { assertRequirementRows, root } from './terminfo-core-results.mjs';
import { eraseIds, eraseSuiteId, specimens } from './terminfo/core-erase.mjs';
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const check = (ok, message) => { if (!ok) throw new Error('Core erase results: ' + message); };
const groups = Object.freeze({ 'erase.line.right': ['el-home', 'el-interior'],
    'erase.screen.above': ['ed1-site', 'ed1-catalogue'], 'erase.screen.below': ['ed0-home', 'ed0-interior'] });

export function deriveEraseCoreReport(directory, frozenMapping, identity, measurement) {
    check(identity?.context?.runId === measurement.runId && identity?.receipt?.coreEraseEvidence?.suiteId === eraseSuiteId,
        'receipt identity or erase suite missing');
    const historicalBytes = readFileSync(join(root, 'docs/qa/terminfo/2026-11-11-core-text-cursor-results/report.json'));
    check(createHash('sha256').update(historicalBytes).digest('hex') === '75791ae1ce7b1a5a0f156d8563c9171957ec9bd014c4ca97147071a970ba5a11', 'historical report changed');
    const historical = JSON.parse(historicalBytes);
    assertRequirementRows(historical.rows, frozenMapping.rows.map(row => row.id));
    const evidence = JSON.parse(readFileSync(join(directory, 'ce-observations.json')));
    check(evidence.suiteId === eraseSuiteId && evidence.runId === measurement.runId, 'stale erase evidence');
    check(same(Object.keys(evidence.effects), specimens.map(s => s.role)), 'missing, duplicate or reordered specimens');
    const { mapping, report } = deriveTextCursorCoreReport(directory, frozenMapping, identity, measurement);
    assertRequirementRows(report.rows, frozenMapping.rows.map(row => row.id));
    for (const id of eraseIds) {
        const row = report.rows.find(r => r.id === id), roles = groups[id];
        const results = roles.map(role => {
            const e = evidence.effects[role], specimen = specimens.find(s => s.role === role);
            check(e && e.id === id && ['supported', 'unsupported'].includes(e.outcome)
                && same(e.fixture, { seed: specimen.seed, position: specimen.position, operation: specimen.operation })
                && same(e.expected, { before: specimen.before, after: specimen.after, cursor: specimen.cursor })
                && e.observed?.before?.lines?.length && e.observed?.after?.lines?.length, 'invalid specimen ' + role);
            return { role, ...e };
        });
        row.historicalPinnedProbe = historical.rows.find(r => r.id === id).pinnedProbe;
        row.historicalConclusion = historical.rows.find(r => r.id === id).conclusion;
        row.criterionMatch = 'matched';
        row.conclusion = results.every(e => e.outcome === 'supported') ? 'pass' : 'fail';
        row.exactFixtureEffects = results;
        row.evidence.push(...roles.map(role => ({ file: 'ce-observations.json', caseId: role, kind: 'exact-specimen' })));
        row.explanation = id === 'erase.screen.above'
            ? 'Site CUP 3;2 and catalogue CUP 2;3 are separate measured specimens; the historical middle-row pinned result remains distinct.'
            : 'The exact omitted-parameter home specimen and separate interior preservation control are independently measured.';
        row.blocker = null;
    }
    for (const row of report.rows) if (!eraseIds.includes(row.id))
        check(row.conclusion === historical.rows.find(r => r.id === row.id).conclusion, 'unattributed conclusion change: ' + row.id);
    report.totals = Object.fromEntries(['pass', 'fail', 'inconclusive'].map(v => [v, report.rows.filter(row => row.conclusion === v).length]));
    check(Object.values(report.totals).reduce((a, b) => a + b, 0) === 80, 'incorrect totals');
    report.corePassPercentage = report.totals.pass * 100 / 80;
    report.scope = 'Private direct-local Release sidecar; exact erase specimens, stored physical cells and cursor at measured geometry. Historical pinned fixtures and verdicts remain separate.';
    return { mapping, report };
}
const cell = value => String(value ?? '').replaceAll('|', String.raw`\|`).replaceAll('\n', ' ').replaceAll('\r', '');
const literal = value => JSON.stringify(value).replaceAll('`', String.raw`\u0060`);
export function readableEraseReport(report, mapping) {
    const m = report.measurement, t = report.totals;
    const changes = report.rows.filter(r => r.historicalConclusion && r.historicalConclusion !== r.conclusion)
        .map(r => `${r.id}: ${r.historicalConclusion} to ${r.conclusion}`).join('; ') || 'none';
    const lines = ['# Ntilde Core erase fixture results', '',
        `**${t.pass} pass / ${t.fail} fail / ${t.inconclusive} inconclusive — ${report.corePassPercentage}% of 80 requirements.**`, '',
        'This version uses the frozen #937 denominator. The earlier [#932](../2026-11-11-core-results/report.md) and [#937](../2026-11-11-core-text-cursor-results/report.md) publications remain unchanged. The historical pinned verdict, this run’s pinned-code result and the exact-fixture effects are distinct.', '',
        `Owned run \`${m.runId}\`; App source \`${m.appRevision}\`; scripts \`${m.scriptsRevision}\`; source policy \`${m.sourcePolicy}\`; receipt schema ${m.receiptSchema}; geometry ${m.geometry.rows} × ${m.geometry.cols}. CUP/CHA parameters are one-based and physical positions are zero-based.`, '',
        `Changes from #937: ${changes}. Other Core conclusions were rederived unchanged; the concurrent OSC 133 qualification added no Core conclusion changes.`, '',
        'The frozen site and catalogue describe three lines without prescribing text; this batch uses AAAAA, BBBBB and CCCCC in consecutive rows. Both ED 1 addresses are measured independently. Erased-cell background storage is not part of this claim.', '',
        'Evidence: [owned run](owned-run.zip), [requirements](../2026-11-11-core-results/requirements-v1.json), [machine-readable report](report.json), [source manifest](app-source-manifest.json).', '',
        '| ID and frozen criterion | Conclusion | Evidence and pinned / effect verdicts | Observations or blocker |', '|---|---|---|---|'];
    for (const row of report.rows) {
        const entry = mapping.rows.find(r => r.id === row.id);
        const refs = row.evidence.map(e => `${e.file}#${e.caseId}`).join('; ');
        const effects = row.effectVerdicts.map(e => `${e.caseId}:${e.verdict}`).join(', ') || 'query trace';
        let observation = row.explanation;
        if (row.exactFixtureEffects) observation += ' ' + row.exactFixtureEffects.map(e => `${e.role}: ${e.outcome}; bytes ${literal(e.fixture)}; before ${literal(e.observed.before.lines.slice(0, 3))}, cursor ${literal(e.observed.before.cursor)}; after ${literal(e.observed.after.lines.slice(0, 3))}, cursor ${literal(e.observed.after.cursor)}`).join('; ');
        else if (row.observed) observation = `Bytes ${literal(row.fixture)}; observed ${literal(row.observed)}.`;
        lines.push(`| [\`${row.id}\`](${entry.url}) — ${cell(row.requirement)} | **${row.conclusion}** | ${cell(describeLayers(row.observationLayers).join(', '))}; ${cell(refs)}; ${row.historicalPinnedProbe ? 'previous stock ' + cell(row.historicalPinnedProbe.stockVerdict) + '; ' : ''}current stock ${cell(row.pinnedProbe.stockVerdict)}; pinned code ${cell(row.pinnedProbe.qualified.map(q => q.caseId + ':' + q.verdict).join(', '))}; effect ${cell(effects)} | ${cell(observation)} |`);
    }
    lines.push('', '## Attempts', '', ...(report.attempts ?? []).map(a => `- \`${a.runId}\` — ${a.status}: ${a.reason}${a.archive ? ' ([evidence](' + a.archive + '))' : ''}`), '', '## Reproduction', '', '```sh', 'node scripts/terminfo-core-erase-publication.mjs --check', '```', '', mapping.sourceAttribution, '');
    return lines.join('\n');
}
