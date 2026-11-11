// A distinct publication over the same frozen 80 requirements as #937. It closes only `modes.reverse-video` and
// `text.wide.cjk`; every other conclusion is re-derived from the same run and must equal the concurrently merged
// publication it builds on (never overwritten or silently carried).
// The caller must validate the complete owned receipt before deriving any row.
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { join } from 'node:path';
import { describeLayers } from './terminfo-core-text-cursor-results.mjs';
import { deriveSgrRasterCoreReport, rasterSection } from './terminfo-core-sgr-raster-results.mjs';
import { assertRequirementRows, root } from './terminfo-core-results.mjs';
import { reverseWideIds, reverseWideSuiteId, reverseWideSpecimens } from './terminfo/core-reverse-wide.mjs';

const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const check = (ok, message) => { if (!ok) throw new Error('Core reverse-video and wide-cell results: ' + message); };
const digest = bytes => createHash('sha256').update(bytes).digest('hex');

/** The publication this one builds on: its report bytes are pinned, its rows are re-derived from the same run and compared. */
export const previousPublicationReport = 'docs/qa/terminfo/2026-11-11-core-sgr-raster-results/report.json';
export const previousPublicationReportSha256 = 'fc9eafd7f6bd95e114b95622c7187c7fe8a7cdfbbc7fb535aa7d783a8dd7638c';
export const defaultPrevious = Object.freeze({ derive: deriveSgrRasterCoreReport, report: previousPublicationReport, sha256: previousPublicationReportSha256 });

const explanation = {
    'modes.reverse-video': 'DECSET 5 (`ESC [ ? 5 h`) turned DECSCNM on and DECRST 5 turned it off again, each observed through two layers: the raw DECRPM reply for mode 5 (Ps 1 after set, Ps 2 after reset) and the App\'s stored mode readback agree, beside a measured mode-off control and a verified restoration of the original state. The toggle left stored text, per-cell rendition, cursor, margins and unrelated modes unchanged. A mode readback only: no rendered color inversion is claimed.',
    'text.wide.cjk': 'U+4E2D (UTF-8 e4 b8 ad) written from a measured clean home is stored as a wide cell at row 0, column 0 (Wide storage flag, decoded text U+4E2D) followed by a continuation cell, with the physical cursor at column 2 and every other cell unchanged. Narrow controls show that a cursor at column 2 alone is not width evidence. Stored-cell evidence for this one character only.',
};
const blockers = {
    'observation-controls-invalid': 'A required control (as-found state, known mode-off, per-cell inverse, unrelated modes, clean home, narrow or stale-cell controls, or the restoration) did not behave as required, so the observation path cannot support a verdict.',
    'transition-observation-incoherent': 'The DECRPM replies and the stored mode readback did not form a coherent activation and restoration, so no verdict is made.',
};
const failures = {
    'decset-ignored': 'DECSET 5 was sent, but both the DECRPM reply and the stored readback stayed reset while every control held: the mode was not activated.',
};

/** A pass needs the whole measured transition and restoration (recognition of the mode alone can never satisfy it). */
function transitionSupports(rv) {
    const reversed = rv.off.decrpm === 2 && rv.off.stored === false && rv.set.decrpm === 1 && rv.set.stored === true && rv.reset.decrpm === 2 && rv.reset.stored === false;
    return reversed && rv.restored.decrpm === rv.initial.decrpm && rv.restored.stored === rv.initial.stored && rv.toggleLeavesStoredCellsUntouched === true;
}
/** A pass needs the stored wide cell, its continuation and the cursor; the character and cursor advance alone never satisfy it. */
function wideSupports(o, specimen) {
    return o.char === specimen.fixture && o.kind === 'wide' && o.continuation === 'continuation' && same(o.cursor, [0, 2]) && o.after?.cpr?.raw === '\x1b[1;3R';
}
/** Refuse a specimen that is not the exact frozen fixture, or a verdict its own evidence does not support. */
function checkSpecimen(id, effect, specimen) {
    check(effect?.id === id && ['supported', 'unsupported', 'inconclusive'].includes(effect.outcome)
        && effect.fixture === specimen.fixture && same(effect.specimen, specimen)
        && effect.observed?.before && effect.observed.after?.cpr, 'invalid specimen ' + id);
    if (id === 'modes.reverse-video') {
        const rv = effect.transition;
        check(rv?.set && rv.reset && rv.initial && rv.restored && effect.observed.after.modes?.length === 3, 'incomplete transition ' + id);
        check(effect.outcome !== 'supported' || transitionSupports(rv), 'verdict without a measured transition: ' + id);
    } else {
        check(effect.outcome !== 'supported' || wideSupports(effect.observed, specimen), 'verdict without stored wide-cell evidence: ' + id);
    }
}
/** The explanation or blocker of one row from its outcome. */
function explainRow(id, effect) {
    if (effect.outcome === 'inconclusive') return { text: blockers[effect.reason], blocker: blockers[effect.reason] };
    if (effect.outcome === 'unsupported') return { text: failures[effect.reason] ?? `The exact specimen did not match its stored-cell contract (${effect.reason}).`, blocker: null };
    return { text: explanation[id], blocker: null };
}
/** Replace one row with its exact-specimen conclusion, keeping the earlier publication's verdicts beside it. */
function closeRow(row, earlierRow, effect, evidence) {
    const reverse = row.id === 'modes.reverse-video', explained = explainRow(row.id, effect);
    row.historicalPinnedProbe = earlierRow.pinnedProbe;
    row.historicalConclusion = earlierRow.conclusion;
    row.criterionMatch = 'matched';
    row.conclusion = { supported: 'pass', unsupported: 'fail', inconclusive: 'inconclusive' }[effect.outcome];
    row.observationLayers = reverse ? ['query', 'owned-mode-readback'] : ['owned-stored-cells'];
    row.exactFixtureEffects = [{ role: reverse ? 'decscnm' : 'cjk', ...structuredClone(effect) }];
    row.controls = structuredClone(evidence.controls);
    row.evidence.push({ file: 'rw-observations.json', caseId: row.id, kind: 'exact-specimen' });
    row.explanation = explained.text;
    row.blocker = explained.blocker;
}

export function deriveReverseWideCoreReport(directory, frozenMapping, identity, measurement, previous = defaultPrevious, base = root) {
    check(identity?.context?.runId === measurement.runId && identity?.receipt?.reverseWideEvidence?.suiteId === reverseWideSuiteId,
        'receipt identity or reverse-video and wide-cell suite missing');
    const previousBytes = readFileSync(join(base, previous.report));
    check(digest(previousBytes) === previous.sha256, 'preceding publication changed');
    const earlier = JSON.parse(previousBytes);
    assertRequirementRows(earlier.rows, frozenMapping.rows.map(row => row.id));
    const evidence = JSON.parse(readFileSync(join(directory, 'rw-observations.json')));
    check(evidence.suiteId === reverseWideSuiteId && evidence.runId === measurement.runId, 'stale reverse-video and wide-cell evidence');
    check(same(Object.keys(evidence.effects), reverseWideIds), 'missing, duplicate or reordered specimens');
    const { mapping, report } = previous.derive(directory, frozenMapping, identity, measurement);
    assertRequirementRows(report.rows, frozenMapping.rows.map(row => row.id));
    for (const id of reverseWideIds) {
        checkSpecimen(id, evidence.effects[id], reverseWideSpecimens[id]);
        closeRow(report.rows.find(r => r.id === id), earlier.rows.find(r => r.id === id), evidence.effects[id], evidence);
    }
    // Every other conclusion must equal the publication this one builds on (this includes concurrently merged closures).
    for (const row of report.rows) if (!reverseWideIds.includes(row.id)) {
        check(row.conclusion === earlier.rows.find(r => r.id === row.id).conclusion, 'unattributed conclusion change or lost merged conclusion: ' + row.id);
    }
    report.totals = Object.fromEntries(['pass', 'fail', 'inconclusive'].map(v => [v, report.rows.filter(row => row.conclusion === v).length]));
    check(Object.values(report.totals).reduce((a, b) => a + b, 0) === 80, 'incorrect totals');
    report.corePassPercentage = report.totals.pass * 100 / 80;
    report.scope = 'Private direct-local Release sidecar; the exact DECSCNM activation and restoration (raw DECRPM replies beside the App\'s stored mode readback) and the exact U+4E2D stored wide cell with its continuation and cursor, at measured geometry. No rendered color inversion, glyph appearance, font coverage or general Unicode width claim. Historical pinned fixtures and verdicts remain separate.';
    return { mapping, report };
}

const cell = value => String(value ?? '').replaceAll('|', String.raw`\|`).replaceAll('\n', ' ').replaceAll('\r', '');
const literal = value => JSON.stringify(value).replaceAll('`', String.raw`\u0060`);
/** A decoded-cell projection is null when a stored cell carries an attribute the contract never sets; negative results still render. */
const unavailable = 'unavailable (a stored cell carries an attribute the contract never sets)';
const row0Cells = view => (view.ink === null ? unavailable : literal(view.ink.filter(p => p.row === 0).map(p => [p.col, p.char, p.kind])));
const inverseChars = view => (view.ink === null ? unavailable : literal(view.ink.filter(p => p.inverse).map(p => p.char).join('')));
const modeList = view => view.modes.map(m => `${m.raw === null ? 'no reply' : literal(m.raw)}`).join(' ');
function describeReverse(row) {
    const [e] = row.exactFixtureEffects, t = e.transition, c = row.controls;
    const state = (name, s) => `${name}: DECRPM ${s.decrpm}, stored ${s.stored}`;
    return `${e.outcome}${e.reason ? ' (' + e.reason + ')' : ''}; bytes ${literal(e.specimen.sequence)}; requests ${literal(e.specimen.requests)}; ${state('as found', t.initial)}; ${state('mode-off control', t.off)}; ${state('after DECSET 5', t.set)} (default colors swapped ${t.set.defaultsSwapped}); ${state('after DECRST 5', t.reset)}; ${state('restored', t.restored)}; raw replies before ${modeList(e.observed.before)}, after set ${modeList(e.observed.after)}, restored ${modeList(c.restored)}; stored cells, per-cell inverse, cursor ${literal(e.observed.before.cursor)}, margins and modes 7 and 25 unchanged by the toggle: ${t.toggleLeavesStoredCellsUntouched}; per-cell inverse cells present with the mode off (seed): ${inverseChars(c['rv:seed'])}; CPR after set ${literal(e.observed.after.cpr.raw)}`;
}
function describeWide(row) {
    const [e] = row.exactFixtureEffects, o = e.observed;
    const c = row.controls;
    return `${e.outcome}${e.reason ? ' (' + e.reason + ')' : ''}; ${e.specimen.codePoint} bytes ${literal(e.fixture)} (UTF-8 ${e.specimen.utf8Hex}); before (clean home) row 0 cells ${row0Cells(o.before)}, cursor ${literal(o.before.cursor)}; after row 0 cells [column, text, kind] ${row0Cells(o.after)}, cursor ${literal(o.after.cursor)}, raw CPR ${literal(o.after.cpr.raw)}; other rows ${literal(o.after.lines.slice(1))}; controls: U+4E16 first seeded as a wide cell then cleared ${row0Cells(c['wide:stale-seed'])}, ASCII A ${row0Cells(c['ctl:ascii'])} cursor ${literal(c['ctl:ascii'].cursor)}, AB ${row0Cells(c['ctl:narrow2'])} cursor ${literal(c['ctl:narrow2'].cursor)} (same cursor column as the target, stored narrow)`;
}
/** The observation text of one row: its explanation plus the exact-fixture detail of whichever publication closed it. */
function observationOf(row) {
    if (row.exactFixtureEffects && row.id === 'modes.reverse-video') return `${row.explanation} ${describeReverse(row)}`;
    if (row.exactFixtureEffects && row.id === 'text.wide.cjk') return `${row.explanation} ${describeWide(row)}`;
    if (row.exactFixtureEffects) return `${row.explanation} ${row.exactFixtureEffects.map(describeEarlierEffect).join('; ')}`;
    if (row.rasterExperiment) return `${row.explanation} Route: ${row.rasterExperiment.decidingRoute}. Details (closed by #949, retained unchanged) are in the SGR reset raster section below.`;
    if (row.wrapEffects) return row.explanation + ' Exact wrapping effects (closed by #948, retained unchanged) are listed in the section for this row below.';
    if (row.observed) return `Bytes ${literal(row.fixture)}; observed ${literal(row.observed)}.`;
    return row.explanation;
}
/** A one-line description of an exact-fixture effect closed by an earlier publication. */
function describeEarlierEffect(e) {
    if (!e.observed?.before?.lines) return `${e.role}: ${e.outcome}`;
    return `${e.role}: ${e.outcome}; bytes ${literal(e.fixture)}; before ${literal(e.observed.before.lines.slice(0, 3))}, cursor ${literal(e.observed.before.cursor)}; after ${literal(e.observed.after.lines.slice(0, 3))}, cursor ${literal(e.observed.after.cursor)}`;
}
/** One table row of the readable report. */
function tableRow(row, mapping) {
    const entry = mapping.rows.find(r => r.id === row.id);
    const refs = row.evidence.map(e => `${e.file}#${e.caseId}`).join('; ');
    const effects = row.effectVerdicts.map(e => `${e.caseId}:${e.verdict}`).join(', ') || (reverseWideIds.includes(row.id) ? `exact specimen ${row.exactFixtureEffects[0].outcome}` : 'query trace');
    const pinned = row.pinnedProbe.qualified.map(q => q.caseId + ':' + q.verdict).join(', ') || (reverseWideIds.includes(row.id) ? 'none run' : '');
    const previousStock = row.historicalPinnedProbe ? 'previous stock ' + cell(row.historicalPinnedProbe.stockVerdict) + '; ' : '';
    return `| [\`${row.id}\`](${entry.url}) — ${cell(row.requirement)} | **${row.conclusion}** | ${cell(describeLayers(row.observationLayers).join(', '))}; ${cell(refs)}; ${previousStock}current stock ${cell(row.pinnedProbe.stockVerdict)}; pinned code ${cell(pinned)}; effect ${cell(effects)} | ${cell(observationOf(row))} |`;
}
/** The exact-effect section of a wrapping row closed by the concurrently merged #948. */
function wrapSection(row) {
    const detail = row.wrapEffects.map(e => `${e.role}: bytes ${JSON.stringify(e.bytes)}, outcome ${e.outcome}, expected ${JSON.stringify(e.expected)}, before ${JSON.stringify(e.before)}, after ${JSON.stringify(e.after)}`).join('; ');
    return ['', `## ${row.id} (#948, retained)`, '', `Criterion: ${row.requirement}`, '', `Conclusion: **${row.conclusion}**. Geometry ${JSON.stringify(row.wrapGeometry)}.`, '',
        ...(row.modeRecognition ? [`Mode recognition: ${JSON.stringify(row.modeRecognition)}.`, ''] : []), detail];
}
export function readableReverseWideReport(report, mapping) {
    const m = report.measurement, t = report.totals;
    const changes = report.rows.filter(r => r.historicalConclusion && r.historicalConclusion !== r.conclusion)
        .map(r => `${r.id}: ${r.historicalConclusion} to ${r.conclusion}`).join('; ') || 'none';
    const lines = ['# Ntilde Core reverse-video and CJK wide-cell results', '',
        `**${t.pass} pass / ${t.fail} fail / ${t.inconclusive} inconclusive — ${report.corePassPercentage}% of 80 requirements.**`, '',
        'This version uses the frozen #937 denominator. The earlier [#932](../2026-11-11-core-results/report.md), [#937](../2026-11-11-core-text-cursor-results/report.md), [erase](../2026-11-11-core-erase-results/report.md), [tab-stop](../2026-11-11-core-tab-stops-results/report.md), [wrapping](../2026-11-11-core-wrap-results/report.md) and [SGR reset raster](../2026-11-11-core-sgr-raster-results/report.md) publications remain unchanged. The historical pinned verdict, the pinned-code result (none was run for these two ids) and the exact-fixture effects are distinct.', '',
        `Owned run \`${m.runId}\`; App source \`${m.appRevision}\`; scripts \`${m.scriptsRevision}\`; source policy \`${m.sourcePolicy}\`; receipt schema ${m.receiptSchema}; geometry ${m.geometry.rows} × ${m.geometry.cols}. Physical positions are zero-based (row, column); CPR is one-based.`, '',
        `Changes from the #937 baseline: ${changes}. Evidence for the erase, tab-stop, wrapping (text.wrap and modes.auto-wrap, closed by #948) and SGR reset raster (sgr.selective-reset.bold, sgr.reset and reset.sgr, closed by #949) rows comes from the separately merged publications and is re-derived unchanged from this run's receipt.`, '',
        'Reverse video: the criterion needs a measured activation, not recognition of the mode. The as-found DECSCNM state is read first (raw DECRPM reply and the App\'s stored readback), a known mode-off control is established, then DECSET 5, DECRST 5 and a restoring write are each observed through both layers. A DA fence reply is never accepted as a DECRPM reply (each reply is parsed for its own mode and bound to its ordering barrier). Per-cell inverse (SGR 7) cells are written with the mode off and must be exactly as written after the mode toggles (none appears, none disappears). DECSCNM is graded here; per-cell inverse rendering belongs to the separate SGR work. The App readback reports theme-resolved default colors (they swap under DECSCNM); that is a stored-state layer, not a raster, so no rendered color inversion is claimed.', '',
        'Wide cell: the exact character U+4E2D is written as UTF-8 from a measured clean home (cells that previously held a different wide character, U+4E16, were cleared and re-measured) and the stored physical cells are read. The wide flag, decoded text and continuation cell come from the App\'s stored cell details; the cursor and its raw CPR are separate observations. Width is never inferred from cursor advance, string length, UTF-8 length, a neighboring blank or a glyph: two narrow characters reach the same cursor column and are stored narrow. This qualifies one stored-cell specimen, not general Unicode width correctness, grapheme handling, font coverage or glyph appearance.', '',
        'Evidence: [owned run](owned-run.zip), [requirements](../2026-11-11-core-results/requirements-v1.json), [machine-readable report](report.json), [source manifest](app-source-manifest.json).', '',
        '| ID and frozen criterion | Conclusion | Evidence and pinned / effect verdicts | Observations or blocker |', '|---|---|---|---|'];
    for (const row of report.rows) lines.push(tableRow(row, mapping));
    for (const row of report.rows.filter(r => r.wrapEffects)) lines.push(...wrapSection(row));
    if (report.rows.some(r => r.rasterExperiment)) lines.push('', ...rasterSection(report));
    lines.push('', '## Attempts', '', ...(report.attempts ?? []).map(a => `- \`${a.runId}\` — ${a.status}: ${a.reason}${a.archive ? ' ([evidence](' + a.archive + '))' : ''}`), '', '## Reproduction', '', '```sh', 'node scripts/terminfo-core-reverse-wide-publication.mjs --check', '```', '', mapping.sourceAttribution, '');
    return lines.join('\n');
}
