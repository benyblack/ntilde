// Distinct Core publication: re-use the frozen 80 criteria and the established
// independently rederived same-run Core evidence. This module never imports an
// archived script or treats the historical pinned verdict as the new effect.
import { readFileSync, writeFileSync, existsSync, mkdtempSync, rmSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';
import { deriveReport, assertRequirementRows, extractOwnedArchive, loadMapping, verifyOwnedRun, reportText, root } from './terminfo-core-results.mjs';
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

const tableCell = value => String(value ?? '').replaceAll('|', '\\|').replaceAll('\n', ' ').replaceAll('\r', '');
const literal = value => JSON.stringify(value).replaceAll('`', '\\u0060');
/** Render the rederived 80 rows; raw CPR is one-based, physical cells/cursors zero-based. */
export function readableTextCursorReport(report, mapping) {
    const { totals, measurement } = report;
    const lines = ['# Ntilde Core text and cursor results', '',
        `**${totals.pass} pass / ${totals.fail} fail / ${totals.inconclusive} inconclusive — ${report.corePassPercentage}% of 80 requirements.**`, '',
        'The earlier [#932 publication](../2026-11-11-core-results/report.md) remains unchanged. This publication re-derives the same 80 frozen criteria from the retained, independently checked current run. Pinned stock results and independent effects are separate.', '',
        `Owned run: \`${measurement.runId}\`; App source: \`${measurement.appRevision}\`; scripts: \`${measurement.scriptsRevision}\`; source policy: \`${measurement.sourcePolicy}\`; receipt schema: ${measurement.receiptSchema}. Geometry: ${measurement.geometry.rows} rows by ${measurement.geometry.cols} columns. Physical coordinates are zero-based (row, column); CPR is one-based.`, '',
        'The DECSC control transmits `ESC [ 5 ; 5 H`, a one-based CUP destination, and observes physical row 4, column 4 before restoring. The original live-page line-feed fixture remains distinct from the catalogue LF-only App alternative.', '',
        'Evidence: [owned run](owned-run.zip), [versioned requirements](../2026-11-11-core-results/requirements-v1.json), [production-source manifest](app-source-manifest.json), [machine-readable report](report.json).', '',
        '## All 80 Core requirements', '',
        '| ID and criterion | Conclusion | Evidence and pinned / effect verdicts | Observation or remaining blocker |', '|---|---|---|---|'];
    for (const row of report.rows) {
        const mappingRow = mapping.rows.find(item => item.id === row.id);
        const layers = describeLayers(row.observationLayers).join(', ');
        const refs = row.evidence.map(e => `${e.file}#${e.caseId}`).join('; ');
        const effects = row.effectVerdicts.map(e => `${e.caseId}:${e.verdict}`).join(', ') || 'query trace';
        const observation = row.observed
            ? `Bytes ${literal(row.fixture)}; observed rows ${literal(row.observed.lines)}, cursor ${literal(row.observed.cursor)}, guard ${literal(row.observed.guard)}, CPR ${literal(row.observed.cpr)}.`
            : row.explanation;
        lines.push(`| [\`${row.id}\`](${mappingRow.url}) — ${tableCell(row.requirement)} | **${row.conclusion}** | ${tableCell(layers)}; ${tableCell(refs)}; stock ${tableCell(row.pinnedProbe.stockVerdict)}, effect ${tableCell(effects)} | ${tableCell(observation)} |`);
    }
    lines.push('', '## Reproduction', '', '```sh', 'node scripts/terminfo-core-text-cursor-results.mjs --check', '```', '',
        'The check validates the immutable source and archive digests, reissues the cleanup receipt using the trusted local graders, and derives all rows from the retained run. A missing setup, stale frame, failed restoration or incomplete observation cannot establish a result.', '');
    return lines.join('\n');
}

// Digests pin this versioned publication; a replacement run requires a new
// version and an explicit code review of its manifest, not an editable label.
export const textCursorPublication = 'docs/qa/terminfo/2026-11-11-core-text-cursor-results';
export const measurementSha256 = '1ace19850ed07089b92b10b64c9b3384b0ba18a4f258eec2a42ca64a0ae6c515';

const digest = bytes => createHash('sha256').update(bytes).digest('hex');
export function loadTextCursorMeasurement(base = root) {
    const dir = join(base, textCursorPublication), bytes = readFileSync(join(dir, 'measurement.json'));
    check(digest(bytes) === measurementSha256, 'measurement manifest changed');
    const manifest = JSON.parse(bytes);
    check(manifest.schemaVersion === 1 && /^[a-f\d]{64}$/.test(manifest.ownedArchiveSha256)
        && /^[a-f\d]{64}$/.test(manifest.appSourceManifestSha256), 'invalid manifest');
    check(digest(readFileSync(join(dir, 'owned-run.zip'))) === manifest.ownedArchiveSha256, 'archive changed');
    check(digest(readFileSync(join(dir, 'app-source-manifest.json'))) === manifest.appSourceManifestSha256, 'production source manifest changed');
    return manifest;
}

export function assertTextCursorCurrentSource(base = root) {
    const manifest = loadTextCursorMeasurement(base);
    const sources = JSON.parse(readFileSync(join(base, textCursorPublication, 'app-source-manifest.json')));
    const git = spawnSync('/usr/bin/git', ['ls-files', '--cached', '--others', '--exclude-standard', '--', 'src', 'assets',
        'Directory.Build.props', 'Directory.Build.targets', 'global.json'], { cwd: base, encoding: 'utf8' });
    check(git.status === 0 && same(git.stdout.trim().split('\n').sort(), Object.keys(sources).sort()), 'production source set changed');
    for (const [file, hash] of Object.entries(sources))
        check(digest(Buffer.from(readFileSync(join(base, file)).toString('latin1').replaceAll('\r\n', '\n'), 'latin1')) === hash, 'production source changed: ' + file);
    return manifest.measurement.appRevision;
}

export function buildTextCursorPublication(base = root) {
    const metadata = loadTextCursorMeasurement(base), mapping = loadMapping(base);
    const temp = mkdtempSync(join(tmpdir(), 'ntilde-core-text-results-'));
    try {
        const archive = join(base, textCursorPublication, 'owned-run.zip');
        extractOwnedArchive(base, { archives: { 'owned-run.zip': metadata.ownedArchiveSha256 } }, temp, archive);
        const identity = verifyOwnedRun(temp, { ...mapping, measurement: metadata.measurement });
        const derived = deriveTextCursorCoreReport(temp, mapping, identity, metadata.measurement);
        derived.report.measurement.ownedArchiveSha256 = metadata.ownedArchiveSha256;
        derived.report.measurement.appSourceManifestSha256 = metadata.appSourceManifestSha256;
        derived.report.attempts = metadata.attempts;
        return derived;
    } finally { rmSync(temp, { recursive: true, force: true }); }
}

async function main() {
    const mode = process.argv[2];
    check(process.argv.length === 3 && ['--check', '--write', '--assert-current-source'].includes(mode), 'use --check, --write or --assert-current-source');
    if (mode === '--assert-current-source') { console.log('Measured production sources match: ' + assertTextCursorCurrentSource()); return; }
    const { report, mapping } = buildTextCursorPublication();
    for (const [file, content] of Object.entries({ 'report.json': reportText(report), 'report.md': readableTextCursorReport(report, mapping) })) {
        const path = join(root, textCursorPublication, file);
        if (mode === '--write') writeFileSync(path, content);
        else check(existsSync(path) && readFileSync(path, 'utf8').replaceAll('\r\n', '\n') === content, 'stale report: ' + file);
    }
    console.log(`Core results: ${report.totals.pass} pass / ${report.totals.fail} fail / ${report.totals.inconclusive} inconclusive; 80 exact rows.`);
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    try { await main(); } catch (error) { console.error(error.stack); process.exitCode = 1; }
}
