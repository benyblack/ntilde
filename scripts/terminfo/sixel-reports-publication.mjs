import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { caseIds, limit } from './sixel-reports-contracts.mjs';
import { deriveRun } from './sixel-reports-owned.mjs';
import { readPinnedBundle, rederiveSixelHistory, replaySixelReport } from './sixel-reports-pinned.mjs';
import { assertRetainedSourceClosure, capturedSixelReportsSourceFiles, capturedSixelReportsSourcePolicy } from './owned-sources.mjs';
import { digest, focusedMeasurement, pinnedBundle, readJson, readManifest, withOwnedArchive, writeUnicodeReport } from './unicode-publication-inputs.mjs';

const qa = fileURLToPath(new URL('../../docs/qa/terminfo/', import.meta.url));
export const publication = join(qa, '2026-11-26-rich-sixel-reports');
const manifestHash = '3555a67f922c40603b9fd3bbfbce9be0291907e24475d2a5f84d365e2586a02d';

function verifySources(directory, context) {
    const retained = join(directory, 'sources'), manifest = readJson(directory, 'source-snapshot.json');
    const files = Object.entries(capturedSixelReportsSourceFiles);
    assert.equal(context.sourceSnapshotPolicy, capturedSixelReportsSourcePolicy);
    assert.equal(manifest.policy, context.sourceSnapshotPolicy);
    assert.equal(manifest.schemaVersion, 1);
    assert.equal(readdirSync(retained).length, files.length);
    assert.deepEqual(readJson(retained, 'owned-source-inventory.json'), capturedSixelReportsSourceFiles);
    const hashes = Object.fromEntries(files.map(([field, file]) => [field, digest(readFileSync(join(retained, file)))]));
    const contextHashes = Object.fromEntries(files.map(([field]) => [field, context[field]]));
    assert.deepEqual(hashes, manifest.hashes);
    assert.deepEqual(hashes, contextHashes);
    assertRetainedSourceClosure(retained, capturedSixelReportsSourceFiles);
}

export function withSixelArchive(callback) {
    const manifest = readManifest(publication, manifestHash, assert.ok);
    return withOwnedArchive(publication, manifest, callback);
}

function retainedMeasurement(directory, manifest) {
    const { context, frames, report, receipt } = deriveRun(directory);
    assert.deepEqual(report, readJson(directory, 'sixel-report.json'));
    assert.deepEqual(receipt, readJson(directory, 'sixel-receipt.json'));
    verifySources(directory, context);
    return { report, measurement: focusedMeasurement(directory, manifest, context, receipt, frames[0].screen) };
}

async function replayStage(stage, probes) {
    return { stage: stage.stage, cases: await Promise.all(Array.from(probes, async (probe, i) => ({
        id: probe.id, exchange: stage.pinnedExchanges[i], terminal: await replaySixelReport(probe, stage.pinnedExchanges[i]),
    }))) };
}

export async function buildPublication() {
    const probes = readPinnedBundle(qa);
    const history = await rederiveSixelHistory(qa, probes);
    const { report, measurement } = withSixelArchive(retainedMeasurement);
    const map = readJson(join(qa, '2026-11-26-rich-tui'), 'evidence-map.json');
    const stock = Object.fromEntries(caseIds.map(id => [id, map.rows.find(row => row.id === id)?.stock]));
    assert.ok(Object.values(stock).every(outcome => outcome === 'supported'));
    const pinnedReplies = await Promise.all(report.samples.map(stage => replayStage(stage, probes)));
    const geometry = report.samples.map(({ stage, geometry }) => ({ stage, ...geometry }));
    return { schemaVersion: 1, suite: report.suite, pinnedBundleSha256: pinnedBundle, history, stock,
        pinnedReplies, ownedGeometry: { pixelCap: limit, geometry, actions: report.actions, restored: report.restored }, measurement };
}

const code = value => '\x60' + value + '\x60';
const csi = raw => raw ? code(raw.replaceAll('\x1b[', 'CSI ')) : 'Absent';
const size = value => value ? value.width + ' × ' + value.height : 'Absent';

export function renderReport(report) {
    const { history, pinnedReplies, ownedGeometry, measurement: m } = report;
    const [older, later] = history;
    return [
        '# Rich TUI Sixel DA1 and geometry reports', '',
        'One owned local Ntilde tab on current main measured ' + code(caseIds[0]) + ' and ' + code(caseIds[1]) + '. Both already show as supported in the [frozen Rich TUI evidence map](../2026-11-26-rich-tui/evidence-map.json).', '',
        '## Historical replies', '',
        'The unchanged pinned callbacks replayed the exact archived writes and child replies from the [November 4 reset archive](../' + older.file + ') and the [November 16 static-image archive](../' + later.file + '). DA1 advertised attribute ' + code('4') + ' in both. The November 4 geometry exchange returned only its DA1 fence and replayed as ' + code(older.cases[1].outcome) + '. The November 16 exchange returned '
            + csi(later.cases[1].exchange.match[0]) + ' before its fence and replayed as ' + code(later.cases[1].outcome) + '.', '',
        '## Pinned child PTY replies', '',
        'The exact requests were ' + code('CSI c') + ' and ' + code('CSI ? 2;1;0 S') + '. The geometry query included the pinned DA1 sentinel. Each exchange also retained a separate pair of raw ordering replies. The table shows the case reply and its pinned terminfo.dev 4.0.0 verdict.', '',
        '| Stage | DA1 reply | DA1 verdict | Geometry reply | Geometry verdict |',
        '| --- | --- | --- | --- | --- |',
        ...pinnedReplies.map(({ stage, cases: [da1, geometry] }) => '| ' + stage + ' | ' + csi(da1.exchange.match?.[0]) + ' | ' + da1.terminal.observation.outcome
            + ' | ' + csi(geometry.exchange.match?.[0]) + ' | ' + geometry.terminal.observation.outcome + ' |'),
        '', '## Owned resize comparison', '',
        '| Stage | Grid (rows × columns) | Text area (width × height, px) | Sixel report (width × height, px) | Check |',
        '| --- | --- | --- | --- | --- |',
        ...ownedGeometry.geometry.map(row => '| ' + row.stage + ' | ' + row.grid.rows + ' × ' + row.grid.cols + ' | ' + size(row.reference)
            + ' | ' + size(row.observed) + ' | ' + row.outcome + ' |'),
        '', 'The parent resized only the nonce-bound pane and independently checked its kernel grid. App cell metrics and device scaling give the text-area dimensions, corroborated by ' + code('CSI 14 t') + '. The Sixel report matched those dimensions with a '
            + ownedGeometry.pixelCap + '-pixel cap per axis. The initial width crossed that cap, and the resized width fell below it. Returning to the original grid reproduced the original report.', '',
        '## Raster evidence', '',
        'These cases measure device reports. The [separate native Sixel raster qualification](../2026-11-16-static-images-qualification.md) records an unsupported raster-fidelity effect. Its pixel evidence and outcome are unchanged.', '',
        '## Reproduce', '',
        'The [owned archive](owned-run.zip) retains the ordered raw PTY replies, fences, App cell metrics, resize journal, settings, captured sources and cleanup proof. The [machine-readable report](report.json) retains pinned outcomes separately from the geometry comparison. Run ' + code(m.runId) + ' used ' + m.platform
            + '. App revision ' + code(m.appRevision) + ', collector revision ' + code(m.scriptsRevision) + '. Cleanup restored the original pane and kernel PTY mode, removed the fixture and released the desktop lease.', '',
        'Build the App revision with ' + code('scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar')
            + '. From the measured collector revision run ' + code('node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version ' + m.version + ' --scope sixel-reports') + '.', '',
        'Run ' + code('node scripts/terminfo/sixel-reports-publication.mjs --check') + ' to replay the retained and new pinned replies, independently verify the geometry comparison, and rederive the receipt. Stock and inventory totals are unchanged.', '',
    ].join('\n');
}

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
    const report = await buildPublication();
    writeUnicodeReport(publication, report, renderReport(report), process.argv.includes('--check'), assert.ok);
}
