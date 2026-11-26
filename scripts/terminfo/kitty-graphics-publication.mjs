import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { caseIds, deriveRun } from './kitty-graphics-owned.mjs';
import { replayPinnedGraphics } from './kitty-graphics-pinned.mjs';
import { assertRetainedSourceClosure, capturedKittyGraphicsSourceFiles, capturedKittyGraphicsSourcePolicy } from './owned-sources.mjs';
import { digest, leaseReleasedAt, pinnedBundle, readJson, readManifest, withOwnedArchive, writeUnicodeReport } from './unicode-publication-inputs.mjs';

export const publication = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-26-rich-kitty-graphics/', import.meta.url));
const prior = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-16-static-images/', import.meta.url));
const manifestHash = '26c8fc82653f22d9775db650b9d2ca42a5e7ced3701873b79cbddc7fd4fbb3a4';
const priorManifestHash = 'cd6e18fd9ed4b0c8ce50d91c1fdefbe135d3578009408a107e42d8aad2364aeb';

function verifySources(directory, context) {
    const retained = join(directory, 'sources'), manifest = readJson(directory, 'source-snapshot.json');
    const fields = Object.entries(capturedKittyGraphicsSourceFiles);
    assert.equal(context.sourceSnapshotPolicy, capturedKittyGraphicsSourcePolicy);
    assert.equal(manifest.schemaVersion, 1);
    assert.equal(manifest.policy, context.sourceSnapshotPolicy);
    assert.equal(Object.keys(manifest.hashes).length, fields.length);
    assert.equal(readdirSync(retained).length, fields.length);
    assert.deepEqual(readJson(retained, 'owned-source-inventory.json'), capturedKittyGraphicsSourceFiles);
    for (const [field, file] of fields) {
        assert.equal(manifest.hashes[field], context[field], file + ' manifest binding');
        assert.equal(digest(readFileSync(join(retained, file))), context[field], file + ' retained bytes');
    }
    assertRetainedSourceClosure(retained, capturedKittyGraphicsSourceFiles);
}

function priorLifecycle() {
    const bytes = readFileSync(join(prior, 'manifest.json'));
    assert.equal(digest(bytes), priorManifestHash);
    const manifest = JSON.parse(bytes);
    return withOwnedArchive(prior, manifest, directory => {
        const old = readJson(directory, 'image-observations.json');
        const cases = old.caseResults.filter(row => row.caseId.startsWith('kitty.'));
        assert.deepEqual(cases.map(row => [row.caseId, row.result]), [
            ['kitty.numbered-lifecycle', 'passed'], ['kitty.number-reuse', 'passed'],
        ]);
        return { runId: old.runId, cases };
    });
}

export function withGraphicsArchive(callback) {
    return withOwnedArchive(publication, readManifest(publication, manifestHash, assert.ok), callback);
}

function readRetainedMeasurement(directory, manifest) {
    const { context, frames, report, receipt } = deriveRun(directory);
    assert.deepEqual(report, readJson(directory, 'graphics-report.json'));
    assert.deepEqual(receipt, readJson(directory, 'graphics-receipt.json'));
    verifySources(directory, context);
    const m = manifest.measurement;
    assert.equal(m.runId, context.runId);
    assert.equal(m.sourcePolicy, context.sourceSnapshotPolicy);
    assert.equal(m.receiptSchema, receipt.schemaVersion);
    assert.ok(context.appVersion.endsWith('+' + m.appRevision));
    assert.ok([context.executableSha256, context.assemblySha256].every(hash => /^[a-f\d]{64}$/.test(hash)));
    const { rows, cols } = frames[0].screen;
    const grid = frames[1].pixels[0].renderDetails.grid;
    const stages = frames.map(frame => ({ snapshot: frame.index, stage: frame.stageId ?? frame.kind, pngs: frame.pixels.map(pixel => pixel.file) }));
    return { ...report, stages, measurement: {
        ...m, version: context.appVersion, nodeVersion: context.nodeVersion, startedAt: context.startedAt,
        releasedAt: leaseReleasedAt(directory, context.runId, assert.ok), geometry: { rows, cols }, grid,
        scale: frames[1].pixels[0].scale, executableSha256: context.executableSha256, assemblySha256: context.assemblySha256,
        sourceSnapshotManifestSha256: context.sourceSnapshotManifestSha256, archiveSha256: manifest.archives['owned-run.zip'],
    } };
}

export async function buildPublication() {
    const { observations, stages, measurement, suite } = withGraphicsArchive(readRetainedMeasurement);
    const archive = readFileSync(join(prior, 'terminfo-dev-4.0.0.bundle.mjs.gz'));
    const replay = await replayPinnedGraphics(archive, observations);
    const map = readJson(join(publication, '..', '2026-11-26-rich-tui'), 'evidence-map.json');
    const cases = caseIds.map(id => {
        const stock = map.rows.find(row => row.id === id)?.stock;
        assert.equal(stock, 'supported');
        const captured = observations.filter(row => row.caseId === id);
        return {
            id, stock,
            pinnedReplies: { ...replay[id], stages: captured.map(({ stage, replyEvidence }) => ({ stage, ...replyEvidence })) },
            ownedPixels: captured.map(({ stage, snapshot, pixelEvidence }) => ({ stage, snapshot, ...pixelEvidence })),
        };
    });
    return { schemaVersion: 1, suite, pinnedBundleSha256: pinnedBundle, cases, pixelFrames: stages, retainedLifecycle: priorLifecycle(), measurement };
}

const code = value => '\x60' + value + '\x60';
const wire = hex => code(Buffer.from(hex, 'hex').toString('ascii').slice(3, -2));

export function renderReport(report) {
    const { measurement: m, cases } = report;
    const [transmit, display] = cases, painted = display.ownedPixels.find(row => row.stage === 'place').measurement;
    const transmitUpload = transmit.pinnedReplies.stages[0];
    const [displayUpload, placement] = display.pinnedReplies.stages;
    return [
        '# Rich TUI Kitty graphics transmit and display', '',
        'The two cases already show as supported in the [frozen Rich TUI evidence map](../2026-11-26-rich-tui/evidence-map.json). This batch adds exact-fixture observations from one accepted run in a disposable ordinary local Ntilde tab.', '',
        '## Pinned reply evidence', '',
        '| Pinned terminfo.dev 4.0.0 case | Terminal callback | Termless callback replay |',
        '| --- | --- | --- |',
        ...cases.map(row => '| ' + code(row.id) + ' | ' + row.pinnedReplies.terminal.observation.outcome + ' | ' + row.pinnedReplies.termless.observation.outcome + ' |'),
        '', 'Each path sent its own direct ' + code('a=t,f=24,s=1,v=1,t=d,I=…;/wAA') + ' upload of one red RGB pixel. The child received ' + wire(transmitUpload.replyHex) + ' for transmission and ' + wire(displayUpload.replyHex) + ' for the display path. Display then sent ' + wire(placement.requestHex) + ' and received ' + wire(placement.replyHex) + '. Each path sent its pinned silent deletion. Both unchanged callbacks were replayed against the retained child PTY bytes. The termless callback used the same retained PTY trace.', '',
        '## Owned pixel evidence', '',
        '| Fixture | Offscreen App render observations |',
        '| --- | --- |',
        '| ' + code(transmit.id) + ' | Upload left the captured pane identical to the bound blank-image frame. Deletion left it unchanged. |',
        '| ' + code(display.id) + ' | Upload left the pane unchanged. Placement painted an ' + painted.bounds.width + ' × ' + painted.bounds.height + ' red square (' + painted.changed + ' changed pixels) inside the requested 2 × 1 cell envelope. No pixel outside that envelope changed. Deletion returned the pane to the bound frame. |',
        '', 'Each capture retained two identical PNGs with matching screen and render observations. The placed bitmap was 1 × 1 and the cursor stayed at the anchor. These PNGs show the App’s controlled offscreen renderer. Native desktop appearance is outside this measurement.', '',
        '## Retained lifecycle rederivation', '',
        'Before collecting the exact fixture, the [static-image publication](../2026-11-16-static-images-qualification.md) rederived from its original archive. Both Kitty carrier cases passed. The 16 × 16 RGBA numbered lifecycle covered upload, placement, soft deletion, redisplay, hard deletion and a correlated missing-image reply. Number reuse covered newest-number resolution, deletion and placement by the older allocated ID. Those are separate fixtures and historical observations.', '',
        '## Reproduce', '',
        'The [owned archive](owned-run.zip) contains the original PTY captures, command journal, PNG pairs, profile, source snapshot and cleanup proof. [Machine-readable results](report.json) list pinned replies separately from pixel measurements. The accepted run was ' + code(m.runId) + ' on ' + m.platform + ' with a ' + m.geometry.rows + ' × ' + m.geometry.cols + ' grid. App revision ' + code(m.appRevision) + ', collector revision ' + code(m.scriptsRevision) + '. Cleanup restored the pane and PTY mode, removed the fixture and released the desktop lease.', '',
        'An initial attempt (' + code('cf9c716879092a56b3e2b040a486a285') + ') captured every stage and cleaned up. Its receipt failed because the verifier passed the focused scope’s stage name to the shared PNG verifier. That attempt is retained locally. The accepted archive comes from one run after that correction.', '',
        'Build the App with ' + code('scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar') + '. From the measured collector revision, run ' + code('node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version ' + m.version + ' --scope kitty-graphics') + '.', '',
        'Run ' + code('node scripts/terminfo/kitty-graphics-publication.mjs --check') + ' to rederive the accepted result and replay the digest-pinned callbacks. This publication adds no inventory claims or stock score changes.', '',
    ].join('\n');
}

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
    const report = await buildPublication();
    writeUnicodeReport(publication, report, renderReport(report), process.argv.includes('--check'), assert.ok);
}
