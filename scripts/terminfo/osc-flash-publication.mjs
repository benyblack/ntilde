import assert from 'node:assert/strict';
import { readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { runInNewContext } from 'node:vm';
import { gunzipSync } from 'node:zlib';
import { parseCursorReport } from './cursor-report.mjs';
import { caseIds, contractsVersion, suiteId } from './osc-flash-contract.mjs';
import { deriveFlash } from './osc-flash-owned.mjs';
import { assertRetainedSourceClosure, capturedFlashSourceFiles, capturedFlashSourcePolicy } from './owned-sources.mjs';
import { replayPinned } from './rich-identity-results.mjs';
import { digest, focusedMeasurement, pinnedBundle, readJson, readManifest, withOwnedArchive } from './unicode-publication-inputs.mjs';

const root = fileURLToPath(new URL('../../docs/qa/terminfo/', import.meta.url));
export const publication = join(root, '2026-11-27-osc555-flash');
const manifestSha = '9cd6893b38b316adbe32bbb6e0f3dae271b8e888cbe0fd51f47d50fc9fd3c2df';
const upstream = '2026-11-16-static-images/terminfo-dev-4.0.0.bundle.mjs.gz';

function pinnedRegistration() {
    const bundle = gunzipSync(readFileSync(join(root, upstream)), { maxOutputLength: 1_000_000 });
    assert.equal(digest(bundle), pinnedBundle);
    const source = bundle.toString('utf8');
    const span = (first, next) => {
        const start = source.indexOf(first), end = source.indexOf(next, start + first.length);
        assert.ok(start >= 0 && end > start);
        return source.slice(start, end);
    };
    const helpers = span('function probe(id, termless, term, termObservationEvidence)', 'function readHyperlinkMetadata(')
        + span('function unmeasuredCellResult(', 'function sgrProbe(');
    const registration = span('    probe("extensions.osc555-flash",', ',\n    probe("extensions.osc440-audio",');
    return runInNewContext(helpers + registration, Object.create(null), { timeout: 1000 }); // NOSONAR: verified upstream bundle.
}

function runReport(directory, manifest, label, previews) {
    const { entries, series, context, receipt } = deriveFlash(directory);
    assert.deepEqual(receipt, readJson(directory, 'flash-receipt.json'));
    assert.equal(context.collectionScope.visual, label !== 'disabled');
    const sources = join(directory, 'sources'), snapshot = readJson(directory, 'source-snapshot.json');
    assert.equal(context.sourceSnapshotPolicy, capturedFlashSourcePolicy);
    assert.equal(snapshot.policy, capturedFlashSourcePolicy);
    assert.deepEqual(readJson(sources, 'owned-source-inventory.json'), capturedFlashSourceFiles);
    assert.deepEqual(new Set(readdirSync(sources)), new Set(Object.values(capturedFlashSourceFiles)));
    const hashes = Object.fromEntries(Object.entries(capturedFlashSourceFiles).map(([field, file]) => {
        const sha = digest(readFileSync(join(sources, file)));
        assert.equal(context[field], sha);
        return [field, sha];
    }));
    assert.deepEqual(hashes, snapshot.hashes);
    assertRetainedSourceClosure(sources, capturedFlashSourceFiles);
    const measurement = focusedMeasurement(directory, manifest, context, receipt, entries[0].screen);
    assert.ok(Date.parse(measurement.releasedAt) >= Date.parse(readJson(directory, 'ownership-proof.json').sourceSnapshot.removedAt));
    const action = entries.find(e => e.stage === 'pinned');
    const { writes, query, sample } = action;
    const trace = { writes, query, reply: Buffer.from(sample.hex, 'hex').toString('latin1') };
    const captures = series.map(({ frames, sent, fenced, ...trial }) => {
        const samples = Object.fromEntries(['before', 'during', 'restored'].map(phase => {
            const frame = frames[trial[phase]];
            if (label === 'enabled' && trial.name === 'pinned' && frame) previews[phase] = readFileSync(join(directory, frame.file));
            return [phase, frame ? { ...frame, start: frame.start - sent, end: frame.end - sent } : null];
        }));
        return { ...trial, frameCount: frames.length, fenceAfterMs: fenced - sent, samples };
    });
    return { measurement, receipt, trace, captures };
}

export function effectVerdict(enabled, disabled) {
    if (enabled[0] !== 'flashed' || disabled.some(s => s !== 'steady') || enabled.includes('inconclusive')) return 'inconclusive';
    return enabled.slice(1).every(s => s === 'flashed') ? 'supported' : 'unsupported';
}

export async function buildPublication() {
    const manifest = readManifest(publication, manifestSha, assert.ok), previews = {};
    const runs = {}, probe = pinnedRegistration();
    assert.equal(probe.id, caseIds[0]);
    assert.equal(probe.termless, null);
    for (const label of ['baseline', 'enabled', 'disabled']) {
        const run = withOwnedArchive(join(publication, label), manifest.runs[label], (dir, m) => runReport(dir, m, label, previews));
        const { writes, query, reply } = run.trace;
        run.pinned = await replayPinned(probe, [...writes, query], parseCursorReport(reply)); // NOSONAR: replay each archived exchange.
        runs[label] = run;
    }
    assert.equal(runs.enabled.measurement.assemblySha256, runs.disabled.measurement.assemblySha256);
    assert.equal(runs.enabled.measurement.executableSha256, runs.disabled.measurement.executableSha256);
    assert.equal(runs.enabled.measurement.appRevision, runs.disabled.measurement.appRevision);
    assert.equal(new Set(Object.values(runs).map(run => run.measurement.runId)).size, 3);
    const result = effectVerdict(...['enabled', 'disabled'].map(label => runs[label].captures.map(c => c.state)));
    const stock = readJson(root, '2026-11-09-stock-baseline.json');
    assert.equal(stock.source.pinnedBundleSha256, pinnedBundle);
    const { measurement: m, receipt, pinned } = runs.enabled;
    const context = { adapter: suiteId, appVersion: m.version, appVersionSource: 'operator supplied; not independently verified',
        executableSha256: m.executableSha256, sourceSnapshotPolicy: m.sourcePolicy, sourceSnapshotManifestSha256: m.sourceSnapshotManifestSha256 };
    const summary = { ownedRunId: m.runId, measuredAt: m.startedAt, appRevision: m.appRevision, scriptsRevision: m.scriptsRevision,
        context, receipt, stockCollector: { rerun: false, note: 'Outcome carried forward from 2026-11-09-stock-baseline.json.' } };
    const report = { suiteId, contractsVersion, scope: 'Timed App live-mode TerminalView rasterizations of a disposable direct-local macOS pane.',
        pinnedBundleSha256: pinnedBundle, runs, cases: [{ caseId: caseIds[0], pinned, termless: null, contractResult: { outcome: result },
            stock: { outcome: stock.outcomes[caseIds[0]], runId: stock.source.stockCollectorRunId, rerun: false },
            conclusion: { outcome: pinned.observation.outcome, grading: 'pinned', evidence: 'stored-cells-and-resolved-colors-and-cpr-and-controlled-pixels' },
            ...(result === pinned.observation.outcome ? {} : { divergence: { kind: 'pinned-inconclusive-by-design',
                pinnedOutcome: pinned.observation.outcome, contractOutcome: result } }) }] };
    return { summary, report, previews };
}

export async function publish(check) {
    const { summary, report, previews } = await buildPublication();
    for (const [file, value] of Object.entries({ 'summary.json': summary, 'report.json': report })) {
        const bytes = JSON.stringify(value, null, 2) + '\n', path = join(publication, file);
        if (check) assert.equal(readFileSync(path, 'utf8').replaceAll('\r\n', '\n'), bytes);
        else writeFileSync(path, bytes);
    }
    for (const [phase, bytes] of Object.entries(previews)) {
        const path = join(publication, phase + '.png');
        if (check) assert.deepEqual(readFileSync(path), bytes);
        else writeFileSync(path, bytes);
    }
    return report;
}

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
    assert.ok(process.argv.length === 3 && ['--write', '--check'].includes(process.argv[2]));
    await publish(process.argv[2] === '--check');
}

