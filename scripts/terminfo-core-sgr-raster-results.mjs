// A distinct publication over the same frozen 80 requirements as #937. It closes only the SGR reset raster criteria of
// `sgr.selective-reset.bold`, `sgr.reset` and `reset.sgr`; every other conclusion is re-derived from the same run and must equal the
// publication it builds on (the Core tab-stop publication, which itself carries the erase and text/cursor closures), never overwritten.
// The caller must validate the complete owned receipt before deriving any row.
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { join } from 'node:path';
import { describeLayers } from './terminfo-core-text-cursor-results.mjs';
import { deriveWrapCoreReport } from './terminfo-core-wrap-results.mjs';
import { assertRequirementRows, root } from './terminfo-core-results.mjs';
import { sgrRasterIds, sgrRasterSuiteId, sgrRasterExperiments, experimentSteps, subsetsOf, decrqssRequest } from './terminfo/core-sgr-raster.mjs';

const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const check = (ok, message) => { if (!ok) throw new Error('Core SGR raster results: ' + message); };
const digest = bytes => createHash('sha256').update(bytes).digest('hex');

/** The publication this one builds on: its report bytes are pinned, its rows are re-derived from the same run and compared. */
export const previousPublicationReport = 'docs/qa/terminfo/2026-11-11-core-wrap-results/report.json';
export const previousPublicationReportSha256 = '38068596162783f5f92533f73f32bc74e3efb3d6d457cb0450501734300845c7';
export const defaultPrevious = Object.freeze({ derive: deriveWrapCoreReport, report: previousPublicationReport, sha256: previousPublicationReportSha256 });

const blockers = {
    'observation-controls-invalid': 'A structural control of the observation path (coherent stored state, an unchanged control region, ink in the neutral crop or a repeatable all-set crop) failed, so neither the raster experiment nor the DECRQSS fallback is admitted.',
    'probe-invalid-sgr99-changed-crop': 'The made-up SGR 99 changed the crop under qualified raster controls: the retained criterion names this PROBE INVALID, not a terminal failure.',
    'probe-invalid-final-crop-matches-no-admitted-crop': 'Under qualified raster controls the final crop equals neither the expected crop nor the neutral or another subset crop: the retained criterion names this PROBE INVALID.',
    'decrqss-silent': 'The raster controls did not qualify and the DECRQSS fallback was silent: the row keeps its named refusal.',
    'decrqss-refused': 'The raster controls did not qualify and the DECRQSS fallback was refused with a status-0 reply.',
    'decrqss-malformed-reply': 'The raster controls did not qualify and the DECRQSS fallback reply was malformed.',
    'decrqss-wrong-setting-reply': 'The raster controls did not qualify and the DECRQSS fallback reply reported another setting.',
    'decrqss-unrecognized-parameter': 'The raster controls did not qualify and the DECRQSS fallback reply carried parameters this experiment never sets.',
    'decrqss-controls-failed': 'The raster controls did not qualify and the DECRQSS fallback controls (neutral, all-set and SGR 99 queries) did not report the specified pens.',
    'decrqss-missing': 'The raster controls did not qualify and no DECRQSS reply was retained.',
};
const outcomeToConclusion = { supported: 'pass', unsupported: 'fail', inconclusive: 'inconclusive' };

/** The final Core conclusion: the pinned/stored-state criterion and the App raster criterion must both be supported for a pass. */
export function combineConclusion(stored, raster) {
    if (stored === 'unsupported' || raster === 'unsupported') return 'fail';
    return stored === 'supported' && raster === 'supported' ? 'pass' : 'inconclusive';
}

const routeName = { raster: 'controlled raster experiment', 'decrqss-fallback': 'DECRQSS fallback (query-based conclusion; the raster controls did not qualify)', none: 'no admitted route' };

function describe(effect, stages) {
    const experiment = sgrRasterExperiments.find(e => e.id === effect.id);
    return { experiment: { attributes: experiment.attributes, resetUnderTest: experiment.resetName, expectedFinalCrop: experiment.expected,
        subsets: subsetsOf(experiment).map(s => s.join('+')) }, steps: experimentSteps(experiment).map(step => ({ role: step.role, feed: step.feed, ...(step.query ? { query: decrqssRequest } : {}) })),
    stages: Object.fromEntries(Object.entries(stages).filter(([role]) => role.startsWith(effect.id + '/'))) };
}

const requiredControls = ['stored-state-coherent-with-input', 'unchanged-control-region-stable', 'neutral-crop-has-ink', 'all-set-crop-repeats-its-subset-crop',
    'stored-attributes-match-the-input-at-every-control-stage', 'every-subset-changes-the-neutral-crop', 'every-DECRC-restore-reproduces-the-neutral-crop'];
const planned = { specimenRegion: { row: 3, col: 11, rows: 3, cols: 4 }, controlRegion: { row: 9, col: 12, rows: 1, cols: 7 } };

const byName = names => names.toSorted((a, b) => a.localeCompare(b));

/** A verdict is accepted only when its route, controls, crop coordinates and render context are the ones this suite specifies. */
function checkEffect(id, effect, evidence) {
    const experiment = sgrRasterExperiments.find(e => e.id === id);
    const wanted = [...requiredControls, ...(experiment.expected === 'italic-only' ? ['italic-only-crop-differs-from-neutral-and-every-other-subset'] : []), 'sgr99-leaves-the-crop-unchanged'];
    check(effect?.id === id && ['supported', 'unsupported', 'inconclusive'].includes(effect.outcome) && ['raster', 'decrqss-fallback', 'none'].includes(effect.route)
        && effect.crossExperimentStable !== undefined, 'invalid experiment ' + id);
    check(same(evidence.specimenRegion, planned.specimenRegion) && same(evidence.controlRegion, planned.controlRegion), 'wrong crop coordinates: ' + id);
    const { geometry } = evidence;
    check(geometry?.presentationTimeMs === 0 && /^[a-f\d]{64}$/.test(geometry.font?.primarySha256 ?? '') && geometry.grid?.cellWidthPx > 0 && geometry.grid.cellHeightPx > 0, 'render context incomplete: ' + id);
    check(same(byName(Object.keys(effect.rasterControls ?? {})), byName(wanted)), 'raster controls missing or substituted: ' + id);
    const qualified = requiredControls.concat(experiment.expected === 'italic-only' ? ['italic-only-crop-differs-from-neutral-and-every-other-subset'] : []).every(name => effect.rasterControls[name].passed);
    if (effect.route === 'raster') {
        check(qualified, 'raster route without qualified controls: ' + id);
        const sgr99 = effect.rasterControls['sgr99-leaves-the-crop-unchanged'].passed;
        check(effect.outcome === (sgr99 ? effect.raster.outcome : 'inconclusive'), 'final-crop classification does not match the verdict: ' + id);
        check(effect.outcome !== 'supported' || effect.raster.matched === experiment.expected, 'supported without the expected crop: ' + id);
    } else if (effect.route === 'decrqss-fallback') {
        check(!qualified && effect.decidedBy === 'query' && /query-based/.test(effect.claim ?? '') && effect.outcome === effect.fallback.outcome, 'invalid fallback conclusion: ' + id);
        check(effect.outcome === 'inconclusive' || (effect.fallback.reply?.kind === 'sgr' && Object.values(effect.fallback.controls).every(Boolean)), 'fallback verdict without a qualified valid reply: ' + id);
    } else check(effect.outcome === 'inconclusive' && effect.reason === 'observation-controls-invalid', 'verdict without an admitted route: ' + id);
}

/** Why a row stays inconclusive: the experiment's own refusal, or an unsupported stored-state qualification. */
function blockerFor(conclusion, effect) {
    if (conclusion !== 'inconclusive') return null;
    if (effect.outcome === 'inconclusive') return blockers[effect.reason];
    return 'The stored-state qualification is not supported, so the combined conclusion cannot be a pass.';
}

/** Add the raster/query layer to one row beside, never instead of, its stored-state qualification. */
function applyRow(row, effect, earlierRow, evidence) {
    // The stored-state qualification is this run's: the row the preceding publication's derivation re-derived from the same receipt. Only the historical fields come from the earlier publication.
    const id = row.id, stored = row.effectVerdicts.map(item => item.verdict);
    check(stored.length === 1 && ['supported', 'unsupported', 'inconclusive'].includes(stored[0]), 'stored-state qualification missing: ' + id);
    row.historicalPinnedProbe = earlierRow.pinnedProbe;
    row.historicalConclusion = earlierRow.conclusion;
    row.storedStateResult = { verdict: stored[0], effectVerdicts: structuredClone(row.effectVerdicts), pinnedProbe: structuredClone(row.pinnedProbe),
        note: 'The stored-state qualification measured earlier in this run; it is a separate experiment from the raster matrix.' };
    row.rasterExperiment = { suiteId: sgrRasterSuiteId, ...describe(effect, evidence.stages), outcome: effect.outcome, ...(effect.reason ? { reason: effect.reason } : {}),
        decidingRoute: routeName[effect.route], decidedBy: effect.decidedBy, ...(effect.claim ? { claim: effect.claim, fallbackReason: effect.fallbackReason } : {}),
        controls: structuredClone(effect.rasterControls), rasterClassification: structuredClone(effect.raster), decrqssFallback: structuredClone(effect.fallback),
        supportingFacts: structuredClone(effect.supporting), crossExperimentStable: effect.crossExperimentStable, geometry: structuredClone(evidence.geometry),
        specimenRegion: evidence.specimenRegion, controlRegion: evidence.controlRegion };
    row.criterionMatch = effect.outcome === 'inconclusive' ? 'partial' : 'matched';
    row.conclusion = combineConclusion(stored[0], effect.outcome);
    row.evidence.push({ file: 'sr-observations.json', caseId: id, kind: 'raster-experiment' });
    const earlierLayers = Array.isArray(row.observationLayers) ? row.observationLayers : [row.observationLayers];
    row.observationLayers = [...earlierLayers, 'owned-render-pixels', ...(effect.route === 'decrqss-fallback' || effect.supporting.decrqssFinal ? ['decrqss-query'] : [])];
    row.explanation = effect.outcome === 'inconclusive' ? blockers[effect.reason] : explanationFor(id, effect);
    row.blocker = blockerFor(row.conclusion, effect);
}

export function deriveSgrRasterCoreReport(directory, frozenMapping, identity, measurement, previous = defaultPrevious, base = root) {
    check(identity?.context?.runId === measurement.runId && identity?.receipt?.sgrRasterEvidence?.suiteId === sgrRasterSuiteId, 'receipt identity or SGR raster suite missing');
    const previousBytes = readFileSync(join(base, previous.report));
    check(digest(previousBytes) === previous.sha256, 'preceding publication changed');
    const earlier = JSON.parse(previousBytes);
    assertRequirementRows(earlier.rows, frozenMapping.rows.map(row => row.id));
    const evidence = JSON.parse(readFileSync(join(directory, 'sr-observations.json')));
    check(evidence.suiteId === sgrRasterSuiteId && evidence.runId === measurement.runId, 'stale SGR raster evidence');
    check(same(Object.keys(evidence.effects), sgrRasterIds), 'missing, duplicate or reordered experiments');
    const { mapping, report } = previous.derive(directory, frozenMapping, identity, measurement);
    assertRequirementRows(report.rows, frozenMapping.rows.map(row => row.id));
    check(evidence.geometry.rows === report.measurement.geometry.rows && evidence.geometry.cols === report.measurement.geometry.cols, 'raster geometry differs from the measured geometry');
    for (const id of sgrRasterIds) {
        checkEffect(id, evidence.effects[id], evidence);
        applyRow(report.rows.find(r => r.id === id), evidence.effects[id], earlier.rows.find(r => r.id === id), evidence);
    }
    // Every other conclusion must equal the publication this one builds on (this includes concurrently merged closures).
    for (const row of report.rows) if (!sgrRasterIds.includes(row.id)) {
        const earlierRow = earlier.rows.find(r => r.id === row.id);
        check(row.conclusion === earlierRow.conclusion, 'unattributed conclusion change or lost merged conclusion: ' + row.id);
    }
    report.totals = Object.fromEntries(['pass', 'fail', 'inconclusive'].map(v => [v, report.rows.filter(row => row.conclusion === v).length]));
    check(Object.values(report.totals).reduce((a, b) => a + b, 0) === 80, 'incorrect totals');
    report.corePassPercentage = report.totals.pass * 100 / 80;
    report.scope = 'Private direct-local Release sidecar; controlled offscreen renders of the production draw path (exact decoded-pixel crops at the recorded font, palette, geometry and presentation phase), DECSC/DECRC restoration, SGR 99 negative controls and exact DECRQSS SGR reports, beside the earlier stored-state qualification. Not desktop appearance, other fonts or platforms, or animation cadence. Historical pinned fixtures and verdicts remain separate.';
    return { mapping, report };
}

function explanationFor(id, effect) {
    const experiment = sgrRasterExperiments.find(e => e.id === id);
    if (effect.route === 'decrqss-fallback') {
        return `The raster controls did not qualify (${effect.fallbackReason.replace('raster controls did not qualify: ', '')}); the exact DECRQSS SGR report of the state after ${experiment.resetName} decided. This is a query-based conclusion: the raster experiment is not claimed.`;
    }
    const matched = effect.raster.matched;
    return effect.outcome === 'supported'
        ? `Under qualified raster controls (seven subsets, DECRC restoration, SGR 99) the crop after ${experiment.resetName} equals the ${matched} crop.`
        : `Under qualified raster controls the crop after ${experiment.resetName} equals the ${matched} crop, which is not the expected ${experiment.expected} crop.`;
}

const cell = value => String(value ?? '').replaceAll('|', String.raw`\|`).replaceAll('\n', ' ').replaceAll('\r', '');
const literal = value => JSON.stringify(value).replaceAll('`', String.raw`\u0060`);
const yesNo = value => (value ? 'yes' : 'no');
const controlSummary = controls => Object.entries(controls).map(([name, value]) => name + ': ' + yesNo(value.passed)).join('; ');
const matchedText = classification => (classification.matched ? ` (matched ${classification.matched})` : '');

/** Whether the DECRQSS fallback decided, or was only a supporting fact beside qualified raster controls. */
function fallbackSentence(r) {
    const reason = r.decrqssFallback.reason ? ` (${r.decrqssFallback.reason})` : '';
    if (r.decidingRoute.startsWith('DECRQSS')) return `The DECRQSS fallback decided: ${r.decrqssFallback.outcome}${reason}; the raster experiment is not claimed.`;
    return `The fallback was not used because the raster controls qualified; its report is a supporting fact only (it would have been ${r.decrqssFallback.outcome}).`;
}

export function rasterSection(report) {
    const out = ['## SGR reset raster experiments', '',
        'Each experiment draws a neutral two-cell specimen (YY) before any attribute is set and saves the pen and position with DECSC. For each of the seven nonempty attribute subsets it draws YY with the subset, restores with DECRC and redraws; it then sets every attribute, sends the made-up SGR 99, and sends the reset under test, redrawing YY each time. Crops are exact decoded RGBA rectangles (the two cells plus one blank cell on every side, three rows) at coordinates derived from the draw’s own grid metadata; an unchanged control text row is compared across every capture. The cursor is hidden for the whole experiment. DECRQSS (`ESC P $ q m ESC \\`) is asked in the neutral, all-set, SGR 99 and final states and retained raw; it decides only when a subset, restoration or separation control does not qualify.', ''];
    for (const id of sgrRasterIds) {
        const row = report.rows.find(r => r.id === id), r = row.rasterExperiment, c = r.controls;
        out.push(`### \`${id}\``, '',
            `- Conclusion: **${row.conclusion}** (stored-state ${row.storedStateResult.verdict}; raster/query ${r.outcome}${r.reason ? ' — ' + r.reason : ''}). Deciding route: ${r.decidingRoute}.`,
            `- Reset under test: ${r.experiment.resetUnderTest}; attributes ${literal(r.experiment.attributes)}; expected final crop: ${r.experiment.expectedFinalCrop}.`,
            `- Controls: ${controlSummary(c)}.`,
            `- Final-crop classification: ${r.rasterClassification.outcome}${matchedText(r.rasterClassification)}; SGR 99 left the crop unchanged: ${yesNo(r.rasterClassification.sgr99Unchanged)}.`,
            `- DECRQSS report of the state after the reset: ${literal(r.supportingFacts.decrqssFinal)}; stored attributes of the redrawn cells: ${literal(r.supportingFacts.storedFinal)}. ${fallbackSentence(r)}`,
            `- Geometry ${r.geometry.rows} × ${r.geometry.cols}, ${r.geometry.widthPx} × ${r.geometry.heightPx} px, cell ${r.geometry.grid.cellWidthPx} × ${r.geometry.grid.cellHeightPx} px, origin (${r.geometry.grid.originXPx}, ${r.geometry.grid.originYPx}); font ${r.geometry.font.primaryFamily} (SHA-256 ${r.geometry.font.primarySha256}), ${r.geometry.font.sizeDip} DIP; presentation time ${r.geometry.presentationTimeMs} ms.`, '');
    }
    return out;
}

export function readableSgrRasterReport(report, mapping) {
    const m = report.measurement, t = report.totals;
    const changes = report.rows.filter(r => r.historicalConclusion && r.historicalConclusion !== r.conclusion)
        .map(r => `${r.id}: ${r.historicalConclusion} to ${r.conclusion}`).join('; ') || 'none';
    const lines = ['# Ntilde Core SGR reset raster results', '',
        `**${t.pass} pass / ${t.fail} fail / ${t.inconclusive} inconclusive — ${report.corePassPercentage}% of 80 requirements.**`, '',
        'This version uses the frozen #937 denominator. The earlier [#932](../2026-11-11-core-results/report.md), [#937](../2026-11-11-core-text-cursor-results/report.md), [erase](../2026-11-11-core-erase-results/report.md) and [tab-stop](../2026-11-11-core-tab-stops-results/report.md) and [wrap](../2026-11-11-core-wrap-results/report.md) publications remain unchanged. The historical pinned verdict, the stored-state qualification and the raster/query experiment are distinct.', '',
        `Owned run \`${m.runId}\`; App source \`${m.appRevision}\`; scripts \`${m.scriptsRevision}\`; source policy \`${m.sourcePolicy}\`; receipt schema ${m.receiptSchema}; geometry ${m.geometry.rows} × ${m.geometry.cols}.`, '',
        `Changes from the Core wrap publication baseline: ${changes}. Every other conclusion is re-derived from this run's receipt and equals the publication this one builds on.`, '',
        'Evidence: [owned run](owned-run.zip), [requirements](../2026-11-11-core-results/requirements-v1.json), [machine-readable report](report.json), [source manifest](app-source-manifest.json).', '',
        '| ID and frozen criterion | Conclusion | Evidence and pinned / effect verdicts | Observations or blocker |', '|---|---|---|---|'];
    for (const row of report.rows) {
        const entry = mapping.rows.find(r => r.id === row.id);
        const refs = row.evidence.map(e => `${e.file}#${e.caseId}`).join('; ');
        const effects = row.effectVerdicts.map(e => `${e.caseId}:${e.verdict}`).join(', ') || 'query trace';
        let observation = row.explanation;
        if (row.rasterExperiment) observation += ` Route: ${row.rasterExperiment.decidingRoute}.`;
        else if (row.exactFixtureEffects) observation += ' ' + row.exactFixtureEffects.map(e => `${e.role}: ${e.outcome}; bytes ${literal(e.fixture)}; before ${literal(e.observed.before.lines.slice(0, 3))}, cursor ${literal(e.observed.before.cursor)}; after ${literal(e.observed.after.lines.slice(0, 3))}, cursor ${literal(e.observed.after.cursor)}`).join('; ');
        else if (row.observed) observation = `Bytes ${literal(row.fixture)}; observed ${literal(row.observed)}.`;
        lines.push(`| [\`${row.id}\`](${entry.url}) — ${cell(row.requirement)} | **${row.conclusion}** | ${cell(describeLayers(row.observationLayers).join(', '))}; ${cell(refs)}; ${row.historicalPinnedProbe ? 'previous stock ' + cell(row.historicalPinnedProbe.stockVerdict) + '; ' : ''}current stock ${cell(row.pinnedProbe.stockVerdict)}; pinned code ${cell(row.pinnedProbe.qualified.map(q => q.caseId + ':' + q.verdict).join(', '))}; effect ${cell(effects)} | ${cell(observation)} |`);
    }
    lines.push('', ...rasterSection(report), '## Attempts', '', ...(report.attempts ?? []).map(a => `- \`${a.runId}\` — ${a.status}: ${a.reason}${a.archive ? ' ([evidence](' + a.archive + '))' : ''}`), '',
        '## Reproduction', '', '```sh', 'node scripts/terminfo-core-sgr-raster-publication.mjs --check', '```', '', mapping.sourceAttribution, '');
    return lines.join('\n');
}
