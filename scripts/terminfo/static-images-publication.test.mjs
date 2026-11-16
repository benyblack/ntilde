import test, { after, before } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { cpSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { completeOwnedReceipt } from './receipt.mjs';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { caseOutcome, gradeEffects, measuredAppRevision, measuredScriptsRevision, pinnedBundleArchive, pinnedSpanDigests, replayPinnedImageProbes, staticImagesCarrierPolicy, staticImagesCarrierSuiteId, staticImagesQualification,
    staticImagesQualificationIds, staticImagesQualificationSuiteId, validityControls } from './static-images-qualification.mjs';

const dir = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-16-static-images/', import.meta.url));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const read = name => readFileSync(join(dir, name));
const clone = value => JSON.parse(JSON.stringify(value));
let root, original, evidenceOf;

before(() => {
    const manifest = JSON.parse(read('manifest.json'));
    assert.deepEqual(Object.keys(manifest.archives).sort(), ['owned-run.zip', pinnedBundleArchive]);
    assert.equal(hash(read('owned-run.zip')), manifest.archives['owned-run.zip']);
    root = mkdtempSync(join(tmpdir(), 'nt-static-images-'));
    extractOwnedArchive(dir, manifest, root, join(dir, 'owned-run.zip'));
    original = JSON.parse(readFileSync(join(root, 'cell-receipt.json')));
    evidenceOf = receipt => { const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = receipt; return evidence; };
});
after(() => { if (root) rmSync(root, { recursive: true, force: true }); });

const retained = () => JSON.parse(readFileSync(join(root, 'image-observations.json')));

test('the owned run independently rederives the complete receipt and the publication projection', async () => {
    const copy = mkdtempSync(join(tmpdir(), 'nt-static-images-rederive-'));
    try {
        cpSync(root, copy, { recursive: true });
        rmSync(join(copy, 'cell-receipt.json'));
        const derived = completeOwnedReceipt(copy, join(copy, 'removed-fixture'), evidenceOf(original));
        assert.deepEqual({ ...derived, completedAt: original.completedAt }, original);
        assert.equal(original.schemaVersion, 69);
        assert.equal(original.fixtureRemoved, true);
        assert.deepEqual(original.collectionScope.omittedSuites, ['osc9', 'notify']);
        assert.equal(original.collectionScope.predecessor.runId, 'b45702ad11ea1f0ea46eeb4908206155');
        const archive = read(pinnedBundleArchive);
        const projected = await staticImagesQualification(root, { appRevision: measuredAppRevision, scriptsRevision: measuredScriptsRevision }, archive);
        projected.summary.receipt = original;
        assert.deepEqual(projected.summary, JSON.parse(read('summary.json')));
        assert.deepEqual(projected.report, JSON.parse(read('report.json')));
        assert.equal(projected.report.suiteId, staticImagesQualificationSuiteId);
        assert.deepEqual(projected.report.cases.map(item => item.caseId), [...staticImagesQualificationIds]);
        assert.equal(projected.summary.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v62');
        assert.equal(projected.summary.context.ownedStaticImagePolicy, staticImagesCarrierPolicy);
        assert.equal(projected.summary.context.staticImageSuiteId, staticImagesCarrierSuiteId);
    } finally { rmSync(copy, { recursive: true, force: true }); }
});

test('both pinned callbacks are replayed from the retained pinned bundle and are inconclusive on every declaration', async () => {
    const archive = read(pinnedBundleArchive);
    assert.equal(hash(archive), JSON.parse(read('manifest.json')).archives[pinnedBundleArchive]);
    const replay = await replayPinnedImageProbes(archive);
    assert.deepEqual(Object.keys(replay), [...staticImagesQualificationIds]);
    for (const id of staticImagesQualificationIds) {
        assert.equal(replay[id].termless.observation.outcome, 'inconclusive');
        assert.equal(replay[id].termless.observation.evidence, 'legacy');
        assert.equal(replay[id].termless.pass, false);
        assert.equal(replay[id].terminal.observation.outcome, 'inconclusive');
        assert.equal(replay[id].terminal.observation.evidence, 'none');
        assert.notEqual(replay[id].termless.response, replay[id].termless.declaredFalseResponse);
    }
    assert.equal(Object.keys(pinnedSpanDigests).length, 3);
    const tampered = Buffer.from(archive);
    tampered[tampered.length - 12] ^= 1;
    await assert.rejects(() => replayPinnedImageProbes(tampered));
});

test('the retained effect results, divergences and unchanged stock grades stay separate', () => {
    const report = JSON.parse(read('report.json')), summary = JSON.parse(read('summary.json'));
    const byId = Object.fromEntries(report.cases.map(item => [item.caseId, item]));
    assert.equal(byId['extensions.sixel'].contractResult.outcome, 'unsupported');
    assert.equal(byId['extensions.iterm2-images'].contractResult.outcome, 'supported');
    for (const item of report.cases) {
        assert.equal(item.pinned.observation.outcome, 'inconclusive');
        assert.equal(item.stock.outcome, 'inconclusive');
        assert.equal(item.divergence.kind, 'pinned-inconclusive-by-design');
        assert.ok(item.effectContract.validity.length >= 8 && item.effectContract.validity.every(value => value.held));
    }
    // A SIXEL failure is the retained native-size defect: its independent controls held and the oracle was not weakened.
    const finding = byId['extensions.sixel'].finding;
    assert.equal(finding.nativeRasterPassed, false);
    assert.ok(Object.values(finding.independentControlsHeld).every(value => value === true));
    assert.equal(finding.wholeCellCompatibilityMismatches, 0);
    assert.ok(finding.nativeMismatches > 0);
    assert.equal(byId['extensions.iterm2-images'].finding, undefined);
    assert.deepEqual(summary.carrier.caseResults.map(item => item.caseId).slice(0, 3), ['sixel.native-raster', 'iterm.explicit-stretch', 'iterm.default-aspect']);
});

test('only the two requested cases are qualified; the carrier\'s Kitty cases are retained but not claimed', () => {
    const report = JSON.parse(read('report.json')), summary = JSON.parse(read('summary.json'));
    assert.ok(report.cases.every(item => !JSON.stringify(item.effectContract).includes('kitty')));
    assert.equal(summary.carrier.caseResults.length, 5);
    assert.ok(report.scope.includes('Kitty cases of the same carrier are not part of this qualification'));
});

test('a promoted or stripped carrier result is refused', () => {
    const report = retained();
    // Stage-level promotion without the carrier's own case result is refused.
    const promoted = clone(report);
    for (const item of promoted.observations) if (item.featureId.startsWith('sixel.native-raster.')) item.result = 'passed';
    assert.throws(() => gradeEffects(promoted), /carrier result failed differs/);
    // A coherent rewrite is refused by the receipt's independent rederivation (below); it is never graded as supported here.
    const stale = clone(report);
    stale.caseResults.find(item => item.caseId === 'sixel.native-raster').result = 'passed';
    assert.throws(() => gradeEffects(stale), /carrier result passed differs/);
    const missing = clone(report);
    missing.observations = missing.observations.filter(item => item.featureId !== 'iterm.default-aspect.show');
    assert.throws(() => gradeEffects(missing), /stage list differs/);
    assert.equal(caseOutcome(['passed', 'failed', 'inconclusive']), 'failed');
    assert.equal(caseOutcome(['passed', 'inconclusive']), 'inconclusive');
    assert.equal(caseOutcome(['passed', 'passed']), 'passed');
});

test('a coherent native-result promotion in the image report cannot pass the receipt rederivation', () => {
    const copy = mkdtempSync(join(tmpdir(), 'nt-static-images-forged-'));
    try {
        cpSync(root, copy, { recursive: true });
        const forged = retained();
        const show = forged.observations.find(item => item.featureId === 'sixel.native-raster.show');
        show.result = 'passed';
        for (const assertion of show.assertions) assertion.passed = true;
        show.measurement.nativeRaster.passed = true;
        const clear = forged.observations.find(item => item.featureId === 'sixel.native-raster.clear');
        clear.result = 'passed'; clear.inconclusiveReason = null;
        forged.caseResults.find(item => item.caseId === 'sixel.native-raster').result = 'passed';
        const bytes = Buffer.from(JSON.stringify(forged, null, 2) + '\n');
        writeFileSync(join(copy, 'image-observations.json'), bytes);
        const evidence = evidenceOf(clone(original));
        evidence.staticImageEvidence.sha256 = hash(bytes);
        rmSync(join(copy, 'cell-receipt.json'));
        assert.throws(() => completeOwnedReceipt(copy, join(copy, 'removed-fixture'), evidence), /Invalid or incomplete owned static-image evidence/);
    } finally { rmSync(copy, { recursive: true, force: true }); }
});

test('stale, reused or unhealthy captures fail the validity controls before any verdict', () => {
    const context = JSON.parse(readFileSync(join(root, 'context.json'))), bytes = readFileSync(join(root, 'image-observations.json'));
    const good = validityControls(retained(), original, context, bytes);
    assert.ok(good.every(item => item.held), JSON.stringify(good.filter(item => !item.held)));
    const failing = mutate => { const report = retained(); mutate(report); return validityControls(report, original, context, bytes).filter(item => !item.held).map(item => item.name); };
    const frames = report => report.snapshots.filter(frame => frame.capture);
    assert.deepEqual(failing(report => { frames(report)[3].capture.nonces[0] = frames(report)[2].capture.nonces[0]; }), ['fresh-paired-fences']);
    // An earlier input-phase fence replayed in the carrier's actual numeric-pair representation.
    assert.deepEqual(failing(report => { frames(report)[1].capture.nonces = report.priorChallenges[0].split(',').map(Number); }), ['fresh-paired-fences']);
    assert.ok(retained().priorChallenges.every(pair => /^\d+,\d+$/.test(pair)));
    assert.deepEqual(failing(report => { report.seal.failed = true; }), ['raw-reader-healthy']);
    assert.deepEqual(failing(report => { report.backgroundControls[4].argb = 0xff000000; }), ['controlled-background']);
    assert.deepEqual(failing(report => { report.observations.find(item => item.featureId === 'iterm.default-aspect.blank').result = 'failed'; }), ['blank-before-controls']);
    assert.deepEqual(failing(report => { report.observations[2].reply.passed = false; }), ['acknowledgement-separate-from-pixels']);
    assert.ok(validityControls(retained(), { ...original, fixtureRemoved: false }, context, bytes).some(item => !item.held && item.name === 'receipt-binds-image-report'));
});

test('the qualification is bound to the same App, adapter and carrier as the retained stock run', () => {
    const summary = JSON.parse(read('summary.json'));
    assert.equal(summary.appRevision, measuredAppRevision);
    assert.equal(summary.scriptsRevision, measuredScriptsRevision);
    assert.equal(summary.context.upstreamBundleSha256, 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6');
    assert.equal(summary.stockCollector.probeHash, '16a690f34c0e');
    assert.equal(summary.stockCollector.suiteComplete, false);
    assert.equal(summary.carrier.sha256, original.staticImageEvidence.sha256);
    assert.deepEqual(summary.carrier.counts, original.staticImageEvidence.counts);
});
