// A distinct publication over the same frozen 80 requirements as #937. It closes only `text.tab` and
// `unicode.tab-stops`; every other conclusion is re-derived from the same run and must equal the
// concurrently merged publication it builds on (never overwritten or silently carried).
// The caller must validate the complete owned receipt before deriving any row.
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { join } from 'node:path';
import { describeLayers } from './terminfo-core-text-cursor-results.mjs';
import { deriveEraseCoreReport } from './terminfo-core-erase-results.mjs';
import { assertRequirementRows, root } from './terminfo-core-results.mjs';
import { tabStopsIds, tabStopsSuiteId, tabStopsSpecimens } from './terminfo/core-tab-stops.mjs';

const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const check = (ok, message) => { if (!ok) throw new Error('Core tab-stops results: ' + message); };
const digest = bytes => createHash('sha256').update(bytes).digest('hex');

/** The publication this one builds on: its report bytes are pinned, its rows are re-derived from the same run and compared. */
export const erasePublicationReport = 'docs/qa/terminfo/2026-11-11-core-erase-results/report.json';
export const erasePublicationReportSha256 = '3f45ff2f12a2117917a9e32597000a9104cc0854d43a921b195335ff364e801e';
export const defaultPrevious = Object.freeze({ derive: deriveEraseCoreReport, report: erasePublicationReport, sha256: erasePublicationReportSha256 });

const explanation = {
    'text.tab': 'TAB then X were written from a default tab-stop state restored by a documented full reset (RIS) after a measured custom-stop state; X is stored at column 8 with blank cells before it and the cursor rests at column 9. Cell content and cursor are separate observations.',
    'unicode.tab-stops': 'One TAB byte from home reaches physical column 8 from the same reset-restored default state; the raw CPR is retained beside the physical cursor and no cell changed.',
};
const blockers = {
    'default-state-not-established': 'The custom-stop state, the reset restoration or the post-control default walk was not measured as required, so no verdict is made.',
    'observation-controls-invalid': 'A false-positive control did not behave as required, so the observation path cannot support a verdict.',
};

export function deriveTabStopsCoreReport(directory, frozenMapping, identity, measurement, previous = defaultPrevious, base = root) {
    check(identity?.context?.runId === measurement.runId && identity?.receipt?.tabStopsEvidence?.suiteId === tabStopsSuiteId,
        'receipt identity or tab-stops suite missing');
    const previousBytes = readFileSync(join(base, previous.report));
    check(digest(previousBytes) === previous.sha256, 'preceding publication changed');
    const earlier = JSON.parse(previousBytes);
    assertRequirementRows(earlier.rows, frozenMapping.rows.map(row => row.id));
    const evidence = JSON.parse(readFileSync(join(directory, 'ts-observations.json')));
    check(evidence.suiteId === tabStopsSuiteId && evidence.runId === measurement.runId, 'stale tab-stops evidence');
    check(same(Object.keys(evidence.effects), tabStopsIds), 'missing, duplicate or reordered specimens');
    check(evidence.provenance?.strategy === 'documented-reset-restoration' && evidence.provenance.reset?.bytes === '\x1bc', 'default-state provenance missing');
    const { mapping, report } = previous.derive(directory, frozenMapping, identity, measurement);
    assertRequirementRows(report.rows, frozenMapping.rows.map(row => row.id));
    for (const id of tabStopsIds) {
        const row = report.rows.find(r => r.id === id), effect = evidence.effects[id], specimen = tabStopsSpecimens[id];
        check(effect?.id === id && ['supported', 'unsupported', 'inconclusive'].includes(effect.outcome)
            && effect.fixture === specimen.fixture && same(effect.expected, specimen.expected)
            && effect.observed?.before?.cpr && effect.observed.after?.cpr, 'invalid specimen ' + id);
        const defaultProven = evidence.provenance.customStatePrecededReset === true && evidence.provenance.defaultStopsRestoredAfterReset === true
            && evidence.provenance.defaultStopsIntactAfterControls === true;
        check(effect.outcome === 'inconclusive' || defaultProven, 'verdict without default-state proof: ' + id);
        const earlierRow = earlier.rows.find(r => r.id === id);
        row.historicalPinnedProbe = earlierRow.pinnedProbe;
        row.historicalConclusion = earlierRow.conclusion;
        row.criterionMatch = 'matched';
        row.conclusion = { supported: 'pass', unsupported: 'fail', inconclusive: 'inconclusive' }[effect.outcome];
        row.defaultStateProvenance = structuredClone(evidence.provenance);
        row.exactFixtureEffects = [{ role: id === 'text.tab' ? 'tab' : 'ht', ...structuredClone(effect) }];
        row.controls = structuredClone(evidence.controls);
        row.evidence.push({ file: 'ts-observations.json', caseId: id, kind: 'exact-specimen' });
        row.explanation = effect.outcome === 'inconclusive' ? blockers[effect.reason] : explanation[id];
        row.blocker = effect.outcome === 'inconclusive' ? blockers[effect.reason] : null;
    }
    // Every other conclusion must equal the publication this one builds on (this includes concurrently merged closures).
    for (const row of report.rows) if (!tabStopsIds.includes(row.id)) {
        const earlierRow = earlier.rows.find(r => r.id === row.id);
        check(row.conclusion === earlierRow.conclusion, 'unattributed conclusion change or lost merged conclusion: ' + row.id);
    }
    report.totals = Object.fromEntries(['pass', 'fail', 'inconclusive'].map(v => [v, report.rows.filter(row => row.conclusion === v).length]));
    check(Object.values(report.totals).reduce((a, b) => a + b, 0) === 80, 'incorrect totals');
    report.corePassPercentage = report.totals.pass * 100 / 80;
    report.scope = 'Private direct-local Release sidecar; exact default-tab-stop specimens after a documented full reset, stored physical cells, cursor and raw CPR at measured geometry. Historical pinned fixtures and verdicts remain separate.';
    return { mapping, report };
}

const cell = value => String(value ?? '').replaceAll('|', String.raw`\|`).replaceAll('\n', ' ').replaceAll('\r', '');
const literal = value => JSON.stringify(value).replaceAll('`', String.raw`\u0060`);
function describeEffect(row) {
    const [e] = row.exactFixtureEffects, p = row.defaultStateProvenance;
    const c = row.controls;
    const taint = `custom stops (TBC 3, HTS at columns 4 and 20): TAB reached ${literal(['taint:ht1', 'taint:ht2', 'taint:ht3'].map(r => c[r].cursor))}`;
    const walk = `default walk after RIS: row ${literal(c['reset:walk'].lines[0])}, cursor ${literal(c['reset:walk'].cursor)}; after all controls ${literal(c['final:walk'].cursor)}`;
    const guards = row.id === 'text.tab'
        ? `; X without TAB: ${literal(c['ctl:x'].lines)} cursor ${literal(c['ctl:x'].cursor)}; seeded row with TAB then X ${literal(c['ctl:tab-preserves'].lines)}, with eight spaces then X ${literal(c['ctl:spaces-overwrite'].lines)}`
        : '';
    return `${e.outcome}${e.reason ? ' (' + e.reason + ')' : ''}; strategy ${p.strategy} (${p.reset.sequence}); bytes ${literal(e.fixture)}; before ${literal(e.observed.before.lines)}, cursor ${literal(e.observed.before.cursor)}; after ${literal(e.observed.after.lines)}, cursor ${literal(e.observed.after.cursor)}, raw CPR ${literal(e.observed.after.cpr.raw)}; ${taint}; ${walk}; as-found walk default: ${p.asFoundWalkMatchesDefault}${guards}`;
}
export function readableTabStopsReport(report, mapping) {
    const m = report.measurement, t = report.totals;
    const changes = report.rows.filter(r => r.historicalConclusion && r.historicalConclusion !== r.conclusion)
        .map(r => `${r.id}: ${r.historicalConclusion} to ${r.conclusion}`).join('; ') || 'none';
    const lines = ['# Ntilde Core default tab-stop results', '',
        `**${t.pass} pass / ${t.fail} fail / ${t.inconclusive} inconclusive — ${report.corePassPercentage}% of 80 requirements.**`, '',
        'This version uses the frozen #937 denominator. The earlier [#932](../2026-11-11-core-results/report.md), [#937](../2026-11-11-core-text-cursor-results/report.md) and [erase](../2026-11-11-core-erase-results/report.md) publications remain unchanged. The historical pinned verdict, this run’s pinned-code result and the exact-fixture effects are distinct.', '',
        `Owned run \`${m.runId}\`; App source \`${m.appRevision}\`; scripts \`${m.scriptsRevision}\`; source policy \`${m.sourcePolicy}\`; receipt schema ${m.receiptSchema}; geometry ${m.geometry.rows} × ${m.geometry.cols}. Physical positions are zero-based (row, column); CPR is one-based.`, '',
        `Changes from the #937 baseline: ${changes}. Evidence for the three erase rows comes from the separately merged erase publication and is re-derived unchanged from this run's receipt.`, '',
        'Default-state provenance: the specimens are written only after a documented full reset (RIS, `ESC c`) that follows a measured custom-stop state (TBC 3 plus HTS at columns 4 and 20). A full walk of every default stop (a marker at each stop, then a clamping TAB) proves the restoration and is repeated after all controls. A custom stop installed at column 8 is never used. The as-found walk at the start of the suite is retained as corroboration of the initial state and does not decide any conclusion. No claim is made that erase, home, alternate-screen switching or DECSTR restores tab stops.', '',
        'Evidence: [owned run](owned-run.zip), [requirements](../2026-11-11-core-results/requirements-v1.json), [machine-readable report](report.json), [source manifest](app-source-manifest.json).', '',
        '| ID and frozen criterion | Conclusion | Evidence and pinned / effect verdicts | Observations or blocker |', '|---|---|---|---|'];
    for (const row of report.rows) {
        const entry = mapping.rows.find(r => r.id === row.id);
        const refs = row.evidence.map(e => `${e.file}#${e.caseId}`).join('; ');
        const effects = row.effectVerdicts.map(e => `${e.caseId}:${e.verdict}`).join(', ') || 'query trace';
        let observation = row.explanation;
        if (row.exactFixtureEffects && tabStopsIds.includes(row.id)) observation += ' ' + describeEffect(row);
        else if (row.exactFixtureEffects) observation += ' ' + row.exactFixtureEffects.map(e => `${e.role}: ${e.outcome}; bytes ${literal(e.fixture)}; before ${literal(e.observed.before.lines.slice(0, 3))}, cursor ${literal(e.observed.before.cursor)}; after ${literal(e.observed.after.lines.slice(0, 3))}, cursor ${literal(e.observed.after.cursor)}`).join('; ');
        else if (row.observed) observation = `Bytes ${literal(row.fixture)}; observed ${literal(row.observed)}.`;
        lines.push(`| [\`${row.id}\`](${entry.url}) — ${cell(row.requirement)} | **${row.conclusion}** | ${cell(describeLayers(row.observationLayers).join(', '))}; ${cell(refs)}; ${row.historicalPinnedProbe ? 'previous stock ' + cell(row.historicalPinnedProbe.stockVerdict) + '; ' : ''}current stock ${cell(row.pinnedProbe.stockVerdict)}; pinned code ${cell(row.pinnedProbe.qualified.map(q => q.caseId + ':' + q.verdict).join(', '))}; effect ${cell(effects)} | ${cell(observation)} |`);
    }
    lines.push('', '## Attempts', '', ...(report.attempts ?? []).map(a => `- \`${a.runId}\` — ${a.status}: ${a.reason}${a.archive ? ' ([evidence](' + a.archive + '))' : ''}`), '', '## Reproduction', '', '```sh', 'node scripts/terminfo-core-tab-stops-publication.mjs --check', '```', '', mapping.sourceAttribution, '');
    return lines.join('\n');
}
