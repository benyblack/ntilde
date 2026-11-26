import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { extractOwnedArchive, verifyOwnedRun } from '../terminfo-core-results.mjs';
import { pinnedBundleArchive, staticImagesQualification } from './static-images-qualification.mjs';

export const sixelNativeSuiteId = 'ntilde-owned-sixel-native-fidelity-v1';
export const sixelNativeVersion = 1;
export const sixelNativeIds = Object.freeze(['extensions.sixel']);

const qa = fileURLToPath(new URL('../../docs/qa/terminfo/', import.meta.url));
const publication = join(qa, '2026-11-26-sixel-native-fidelity');
const archive = join(publication, 'owned-run.zip');
const read = file => JSON.parse(readFileSync(file, 'utf8'));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');

export async function deriveSixelNativePublication() {
    const manifest = read(join(publication, 'manifest.json'));
    for (const file of ['before-fixed-metrics.zip', 'inconclusive-owned-run.zip', 'prior-attempts.json']) {
        assert.equal(hash(readFileSync(join(publication, file))), manifest.archives[file]);
    }
    const root = mkdtempSync(join(tmpdir(), 'nt-native-sixel-'));
    try {
        extractOwnedArchive(publication, manifest, root, archive);
        const { context, receipt } = verifyOwnedRun(root, manifest);
        const { appRevision, scriptsRevision, executableSha256, assemblySha256, sourceManifestSha256 } = manifest.measurement;
        assert.match(appRevision, /^[a-f0-9]{40}$/);
        assert.match(scriptsRevision, /^[a-f0-9]{40}$/);
        assert.ok(context.appVersion.endsWith('+' + appRevision));
        assert.equal(context.executableSha256, executableSha256);
        assert.equal(context.assemblySha256, assemblySha256);
        assert.equal(context.sourceSnapshotManifestSha256, sourceManifestSha256);
        assert.equal(context.staticImageCaptureScale, 1.1);

        const bundle = readFileSync(join(qa, '2026-11-16-static-images', pinnedBundleArchive));
        const result = await staticImagesQualification(root, { appRevision, scriptsRevision }, bundle);
        const sixel = result.report.cases.find(item => item.caseId === sixelNativeIds[0]);
        assert.equal(sixel.contractResult.outcome, 'supported');
        assert.equal(sixel.stock.outcome, 'inconclusive');
        assert.equal(sixel.pinned.termless.observation.outcome, 'inconclusive');
        assert.equal(sixel.pinned.terminal.observation.outcome, 'inconclusive');
        const stages = sixel.observations[0].stages;
        assert.deepEqual(stages.map(stage => stage.stage), ['sixel.native-raster.blank', 'sixel.native-raster.show', 'sixel.native-raster.clear']);
        assert.ok(stages.every(stage => stage.result === 'passed' && stage.assertions.every(check => check.passed === true)));
        const show = stages[1].measurement;
        assert.equal(show.nativeRaster.mismatches, 0);
        assert.ok(show.nativeRaster.safeColorPixels.every(count => count > 0) && show.nativeRaster.safeBackgroundPixels > 0);
        assert.ok(show.compatibility.mismatches > 0);
        return {
            summary: { ...result.summary, receipt },
            report: {
                ...result.report,
                suiteId: sixelNativeSuiteId,
                contractsVersion: sixelNativeVersion,
                scope: 'Current App, one fixed square-pixel SIXEL raster with stable font and capture metrics in a disposable direct-local macOS pane.',
                cases: [sixel],
            },
        };
    } finally {
        rmSync(root, { recursive: true, force: true });
    }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    assert.ok(process.argv.length === 3 && ['--write', '--check'].includes(process.argv[2]));
    const result = await deriveSixelNativePublication();
    for (const [name, value] of Object.entries(result)) {
        const file = join(publication, name + '.json');
        if (process.argv[2] === '--write') writeFileSync(file, JSON.stringify(value, null, 2) + '\n');
        else assert.deepEqual(read(file), value);
    }
}
