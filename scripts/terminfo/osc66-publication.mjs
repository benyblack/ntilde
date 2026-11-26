import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { deriveRun } from './osc66-owned.mjs';
import { featureId, pinnedOsc66, replayOsc66, rederiveHistoricalOsc66 } from './osc66-pinned.mjs';
import { assertRetainedSourceClosure, capturedOsc66SourceFiles, capturedOsc66SourcePolicy } from './owned-sources.mjs';
import { digest, leaseReleasedAt, pinnedBundle, readJson, readManifest, withOwnedArchive, writeUnicodeReport } from './unicode-publication-inputs.mjs';

export const publication = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-26-rich-osc66/', import.meta.url));
const manifestHash = '579083ed116c6e3c8c34fa244776357aacf7addc79a1c6e0fe02cedbb3d28700';
const comparison = '2026-11-01-text-sizing-comparison.json';
export const upstreamBundle = new URL('../../docs/qa/terminfo/2026-11-16-static-images/terminfo-dev-4.0.0.bundle.mjs.gz', import.meta.url);

function verifySources(directory, context) {
    const retained = join(directory, 'sources'), manifest = readJson(directory, 'source-snapshot.json');
    const fields = Object.entries(capturedOsc66SourceFiles);
    assert.equal(context.sourceSnapshotPolicy, capturedOsc66SourcePolicy);
    assert.equal(manifest.schemaVersion, 1);
    assert.equal(manifest.policy, context.sourceSnapshotPolicy);
    assert.equal(Object.keys(manifest.hashes).length, fields.length);
    assert.equal(readdirSync(retained).length, fields.length);
    assert.deepEqual(readJson(retained, 'owned-source-inventory.json'), capturedOsc66SourceFiles);
    for (const [field, file] of fields) {
        assert.equal(manifest.hashes[field], context[field], file + ' manifest binding');
        assert.equal(digest(readFileSync(join(retained, file))), context[field], file + ' retained bytes');
    }
    assertRetainedSourceClosure(retained, capturedOsc66SourceFiles);
}

export function withOsc66Archive(callback) {
    const manifest = readManifest(publication, manifestHash, assert.ok);
    return withOwnedArchive(publication, manifest, callback);
}

function readRetainedMeasurement(directory, manifest) {
    const { context, frames, report, receipt } = deriveRun(directory);
    assert.deepEqual(report, readJson(directory, 'osc66-report.json'));
    assert.deepEqual(receipt, readJson(directory, 'osc66-receipt.json'));
    verifySources(directory, context);
    const m = manifest.measurement;
    assert.equal(m.runId, context.runId);
    assert.equal(m.sourcePolicy, context.sourceSnapshotPolicy);
    assert.equal(m.receiptSchema, receipt.schemaVersion);
    assert.ok(context.appVersion.endsWith('+' + m.appRevision));
    assert.ok([context.executableSha256, context.assemblySha256].every(hash => /^[a-f\d]{64}$/.test(hash)));
    const { rows, cols } = frames[0].screen;
    return { report, measurement: {
        ...m, version: context.appVersion, nodeVersion: context.nodeVersion, startedAt: context.startedAt,
        releasedAt: leaseReleasedAt(directory, context.runId, assert.ok), geometry: { rows, cols },
        executableSha256: context.executableSha256, assemblySha256: context.assemblySha256,
        sourceSnapshotManifestSha256: context.sourceSnapshotManifestSha256, archiveSha256: manifest.archives['owned-run.zip'],
    } };
}

export async function buildPublication() {
    const upstream = pinnedOsc66(readFileSync(upstreamBundle));
    const historical = await rederiveHistoricalOsc66(upstream, readFileSync(join(publication, '..', comparison)));
    const { report, measurement } = withOsc66Archive(readRetainedMeasurement);
    const { rows, cols } = measurement.geometry;
    const terminal = await replayOsc66(upstream, rows, cols, report.exchanges);
    const stockMap = readJson(join(publication, '..', '2026-11-26-rich-tui'), 'evidence-map.json');
    const stock = stockMap.rows.find(row => row.id === featureId)?.stock;
    assert.equal(stock, 'supported');
    return {
        schemaVersion: 1, featureId, suite: report.suite, stock, pinnedBundleSha256: pinnedBundle, historical,
        pinnedReplies: { terminal, exchanges: report.exchanges }, ownedPixels: report.ownedPixels, measurement,
    };
}

const code = value => '\x60' + value + '\x60';
const dimensions = bounds => bounds ? bounds.width + ' × ' + bounds.height + ' px' : 'None';

export function renderReport(report) {
    const { historical, pinnedReplies, ownedPixels, measurement: m } = report;
    const { width, scale } = ownedPixels;
    return [
        '# Rich TUI OSC 66 text sizing', '',
        'One disposable local Ntilde tab measured ' + code(report.featureId) + ' on current main. The [frozen Rich TUI map](../2026-11-26-rich-tui/evidence-map.json) already grades the stock case supported.', '',
        '## Historical replay', '',
        'The original terminfo.dev 4.0.0 callback rederived the before and after grades from the [committed November 1 trace comparison](../' + comparison + '). The before run stayed at ' + code(historical.before.replies[0].slice(2, -1)) + ' after both writes. The after run returned ' + historical.after.replies.map(raw => code(raw.slice(2, -1))).join(' → ') + '. The comparison preserves the ordered writes and all three raw replies for each run. The original ignored run directories were unavailable.', '',
        '## Pinned cursor replies', '',
        '| Exact fixture stage | Child CPR | Physical App cursor (1-based) |',
        '| --- | --- | --- |',
        ...pinnedReplies.exchanges.map(({ stage, raw, physical }) => '| ' + stage + ' | ' + (raw === null ? 'Absent' : code('CSI ' + raw.slice(2))) + ' | '
            + physical.row + ', ' + physical.col + ' |'),
        '', 'The pinned callback clears the first line and writes one space with ' + code('w=2') + ', then one space with ' + code('s=2') + '. Its terminal verdict rederived as ' + code(pinnedReplies.terminal.observation.outcome) + ' from the retained replies. The callback grades two columns of advance per write on the same row.', '',
        '## Owned visible-glyph pixels', '',
        '| Separate visible fixture | Ordinary ink bounds | OSC 66 ink bounds | Owned outcome |',
        '| --- | --- | --- | --- |',
        '| ' + code('w=2;MMMM') + ' | ' + dimensions(width.ordinary.bounds) + ' | ' + dimensions(width.measurement.bounds) + ' | ' + width.outcome + ' |',
        '| ' + code('s=2;M') + ' | ' + dimensions(scale.ordinary.bounds) + ' | ' + dimensions(scale.measurement.bounds) + ' | ' + scale.outcome + ' |',
        '', 'Explicit width kept the visible ink inside two cells. Scale enlarged a single glyph across both rows of the two-by-two-cell region. No pixels outside either target changed. The ordinary controls used the same anchor and font. Erasing the fixture restored the guarded blank frame, and leaving the alternate screen restored the original frame.', '',
        'The 16 PNGs are paired, fixed-phase offscreen App renders, bracketed by the same screen state. The measurements cover ink bounds and confinement for this font and fixture. Native desktop appearance and exact font parity are outside the measured scope.', '',
        '## Reproduce', '',
        'The [owned archive](owned-run.zip) contains the child PTY exchanges, screen journal, paired PNGs, profile, source snapshot and cleanup proof. The [machine-readable report](report.json) retains cursor replies and pixel evidence independently. Run ' + code(m.runId) + ' used ' + m.platform + ' and a ' + m.geometry.rows + ' × ' + m.geometry.cols
            + ' grid. App revision ' + code(m.appRevision) + ', collector revision ' + code(m.scriptsRevision) + '. The pane and kernel PTY state were restored, the fixture was removed, and the desktop lease was released.', '',
        'Build the App revision with ' + code('scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar') + '. From the recorded collector revision run '
            + code('node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version ' + m.version + ' --scope osc66') + '.', '',
        'Run ' + code('node scripts/terminfo/osc66-publication.mjs --check') + ' to rederive the historical cursor grades and verify the new receipt, reply grade and PNG comparisons. This batch leaves stock and inventory totals unchanged.', '',
    ].join('\n');
}

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
    const report = await buildPublication();
    writeUnicodeReport(publication, report, renderReport(report), process.argv.includes('--check'), assert.ok);
}
