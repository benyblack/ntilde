// Frozen real-App data. Archive code is never executed; trusted current/future validators read private copies.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync, mkdtempSync, mkdirSync, rmSync, cpSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { tmpdir } from 'node:os';
import { pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';
import { inflateRawSync, crc32 } from 'node:zlib';
import { historicalArchive } from './fixtures/captured-sources.mjs';
import { capturedV10SourceFiles, assertRetainedSourceClosure } from './owned-sources.mjs';
import { completeOwnedReceipt } from './receipt.mjs';
import { decodeCapturePng } from './pixels-png.mjs';
const name = '2026-11-04-image-observations', directory = new URL('../../docs/qa/terminfo/', import.meta.url);
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const read = (files, file) => JSON.parse(files.get(file));
const journal = (files, file) => files.get(file).toString().trim().split('\n').filter(Boolean).map(line => JSON.parse(line));
const publication = () => {
    const bytes = readFileSync(new URL(name + '.json', directory));
    assert.equal(bytes.length, 1467269);
    assert.equal(hash(bytes), 'd2e1a7dcd67145a4da18c031f31e56af6757366895ca8dcf9a4ffa5de3923cfa');
    return JSON.parse(bytes);
};
function archiveMembers(descriptor) {
    assert.match(descriptor.file, /^[a-z\d.-]+\.zip$/);
    const archive = readFileSync(new URL(descriptor.file, directory));
    assert.equal(archive.length, descriptor.bytes); assert.equal(hash(archive), descriptor.sha256);
    const files = new Map(); let offset = 0;
    while (archive.readUInt32LE(offset) === 0x04034b50) {
        const flags = archive.readUInt16LE(offset + 6), method = archive.readUInt16LE(offset + 8);
        const compressed = archive.readUInt32LE(offset + 18), size = archive.readUInt32LE(offset + 22);
        const length = archive.readUInt16LE(offset + 26), extra = archive.readUInt16LE(offset + 28), start = offset + 30;
        assert.equal(flags & 9, 0); assert.ok(method === 0 || method === 8); assert.ok(size <= 16 * 1024 * 1024);
        const file = archive.subarray(start, start + length).toString();
        assert.ok(!file.startsWith('/') && !file.split('/').includes('..') && !files.has(file));
        const bytes = archive.subarray(start + length + extra, start + length + extra + compressed);
        const value = method === 8 ? inflateRawSync(bytes, { maxOutputLength: 16 * 1024 * 1024 }) : Buffer.from(bytes);
        assert.equal(value.length, size); assert.equal(crc32(value), archive.readUInt32LE(offset + 14));
        assert.equal(hash(value), descriptor.fileSha256[file]); files.set(file, value);
        offset = start + length + extra + compressed;
    }
    assert.equal(archive.readUInt32LE(offset), 0x02014b50);
    assert.equal(files.size, descriptor.files);
    assert.deepEqual([...files.keys()].sort(), Object.keys(descriptor.fileSha256).sort());
    return files;
}
const reportEntries = [
    ['cell', 'cells.mjs', 'buildCellReport'], ['sgr', 'sgr.mjs', 'buildSgrReport'], ['rgb', 'rgb.mjs', 'buildRgbReport'],
    ['rendition', 'rendition.mjs', 'buildRenditionReport'], ['cursor', 'cursor.mjs', 'buildCursorReport'],
    ['pixel', 'pixels.mjs', 'buildPixelReport'], ['color', 'colors.mjs', 'buildColorReport'],
    ['erase', 'editing.mjs', 'buildEraseReport'], ['editing', 'editing.mjs', 'buildCellEditingReport'],
    ['history', 'history.mjs', 'buildHistoryReport'], ['resize', 'resize.mjs', 'buildResizeReport'],
    ['saved', 'saved.mjs', 'buildSavedReport'], ['metadata', 'metadata.mjs', 'buildMetadataReport'],
    ['input', 'input-state.mjs', 'buildInputReport'], ['image', 'static-images.mjs', 'buildStaticImageReport'],
];
function bindingFor(receipt, name) {
    if (name === 'cell') return receipt;
    const key = name === 'image' ? 'staticImageEvidence' : name + 'Evidence';
    return receipt[key];
}
function bindingProjection(binding) {
    const keys = ['runId', 'suiteId', 'file', 'sha256', 'counts', 'provisionalUntil', 'status', 'reason'];
    return Object.fromEntries(keys.filter(key => Object.hasOwn(binding, key)).map(key => [key, binding[key]]));
}
function evidenceFrom(receipt) {
    const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = receipt;
    return evidence;
}
function compareReceipt(actual, expected) {
    const { completedAt: actualTime, ...actualFields } = actual, { completedAt: oldTime, ...expectedFields } = expected;
    assert.deepEqual(actualFields, expectedFields); assert.ok(Date.parse(actualTime) >= Date.parse(oldTime));
}
function writeFiles(files, output) {
    for (const [file, bytes] of files) {
        assert.ok(!file.startsWith('/') && !file.split('/').includes('..'));
        const target = join(output, file); mkdirSync(dirname(target), { recursive: true }); writeFileSync(target, bytes);
    }
}
async function privateEvidence(action) {
    publication(); const files = historicalArchive(name), output = mkdtempSync(join(tmpdir(), 'nt-real-image-v10-'));
    try { writeFiles(files, output); return await action(output, files); }
    finally { rmSync(output, { recursive: true, force: true }); }
}
async function deriveReport(entry, output, data, files) {
    const [name, file, method] = entry, report = read(files, name + '-observations.json');
    const frames = journal(files, name + '-snapshots.jsonl'); assert.deepEqual(frames, report.snapshots);
    const module = await import(new URL('./' + file, import.meta.url));
    if (name === 'resize') return module[method](frames, journal(files, 'resize-actions.jsonl'), data.ownedRunId);
    if (name === 'input') return module[method](frames, journal(files, 'input-actions.jsonl'), data.ownershipProof.proofs[0].input.seal, data.ownedRunId);
    if (name === 'image') return module[method](frames, journal(files, 'image-actions.jsonl'), data.ownershipProof.proofs[0].staticImages.seal, data.ownedRunId, output, report.priorChallenges);
    return module[method](frames, data.ownedRunId, ...(['pixel', 'color'].includes(name) ? [output] : []));
}

test('calibrated v10 publication pins source scope, original context and all 15 binding summaries', () => {
    const data = publication(), files = historicalArchive(name), originalContext = read(files, 'context.json');
    assert.equal(data.appBuildSource, 'ff169411547425d706bcf9a26e7614b40c39bb64'); assert.equal(data.adapterSource, data.appBuildSource);
    const { executable, ...context } = originalContext; assert.equal(typeof executable, 'string'); assert.deepEqual(data.context, context);
    assert.equal(data.context.appVersionSource, 'operator supplied; not independently verified');
    assert.equal(data.ownedRunId, '7aa3740a43eabef4cec7b1ccc0ee69be'); assert.equal(data.stockRunId, '2d2433d5082b544ef96f6d01fcadd4ae');
    assert.notEqual(data.stockRunId, data.ownedRunId);
    assert.deepEqual(data.imageReport, read(files, 'image-observations.json')); assert.deepEqual(data.receipt, read(files, 'cell-receipt.json'));
    assert.deepEqual(data.ownershipProof, read(files, 'ownership-proof.json')); assert.deepEqual(data.sourceSnapshot, read(files, 'source-snapshot.json'));
    assert.deepEqual(data.sourceInventory, capturedV10SourceFiles); assert.deepEqual(data.sourceInventory, read(files, 'sources/owned-source-inventory.json'));
    assert.equal(hash(files.get('source-snapshot.json')), data.context.sourceSnapshotManifestSha256);
    assert.equal(data.sourceSnapshot.policy, 'readonly-owned-source-graph-v3'); assert.equal(Object.keys(data.sourceSnapshot.hashes).length, 67);
    for (const [key, file] of Object.entries(capturedV10SourceFiles)) {
        assert.equal(hash(files.get('sources/' + file)), data.context[key]); assert.equal(data.sourceSnapshot.hashes[key], data.context[key]);
    }
    assert.equal(data.receipt.schemaVersion, 10); assert.equal(data.receipt.fixtureRemoved, true);
    assert.deepEqual(data.sourceSnapshotCleanup, data.ownershipProof.sourceSnapshot);
    assert.ok(Date.parse(data.ownershipProof.cleanedUpAt) < Date.parse(data.sourceSnapshotCleanup.removedAt));
    assert.ok(Date.parse(data.sourceSnapshotCleanup.removedAt) < Date.parse(data.receipt.completedAt));
    assert.equal(Object.keys(data.suiteSummaries).length, 15);
    for (const [name] of reportEntries) {
        const report = read(files, name + '-observations.json'), summary = data.suiteSummaries[name];
        assert.deepEqual(summary.binding, bindingProjection(bindingFor(data.receipt, name)));
        assert.equal(summary.pairedFrames, report.snapshots.length); assert.equal(summary.observations, report.observations.length);
    }
});

test('all three exact archives preserve every member and keep old preparation diagnostics unqualified', () => {
    const data = publication();
    assert.equal(data.archive.bytes, 996321); assert.equal(data.archive.sha256, '9416be18cb059e379d49e287a4b8e85c2d7ded6a5325754fb096bd8307add451');
    assert.equal(data.archive.files, 255); assert.deepEqual(archiveMembers(data.archive), historicalArchive(name));
    const [failed, diagnostic] = data.preparationAttempts;
    assert.equal(failed.archive.bytes, 827901); assert.equal(failed.archive.sha256, '0a4ea679d91d2c29de92140e857fb7c155c550f761cf3e3dda34195e7768358c');
    assert.equal(diagnostic.archive.bytes, 1004416); assert.equal(diagnostic.archive.sha256, '8342a25beca8297675d9b0e32412f9fcbb8a19ef4ddebc8bf67f59087fd8e5c5');
    const failures = archiveMembers(failed.archive), diagnostics = archiveMembers(diagnostic.archive);
    assert.equal(failures.size, 241); assert.equal(failed.completed, false); assert.equal(failed.receiptPresent, false); assert.equal(failures.has('cell-receipt.json'), false);
    const partial = read(failures, 'image-failed-observation.json'), first = journal(failures, 'image-snapshots.jsonl')[0];
    assert.equal(partial.kind, 'images-finish'); assert.equal(first.screen.cursorVisible, true); assert.equal(partial.pair.screen.cursorVisible, false);
    assert.equal(read(failures, 'image-raw-failure.json').failed, false); assert.equal(failed.geometryQualified, false);
    assert.equal(diagnostics.size, 255); assert.equal(diagnostic.completed, true); assert.equal(diagnostic.geometryQualified, false);
    assert.equal(diagnostic.imagePolicy, 'owned-static-images-paired-raw-render-v1');
    assert.deepEqual(diagnostic.originalModelImageCounts, read(diagnostics, 'cell-receipt.json').staticImageEvidence.counts);
    assert.deepEqual(diagnostic.originalStockCounts, { inconclusive: 172, supported: 83 });
    assert.equal(read(diagnostics, 'image-observations.json').snapshots[3].pixels[0].imageDetails, undefined);
});

test('calibrated native negative, exact metadata witness and incomplete stock deltas remain separate', () => {
    const data = publication(), image = data.imageReport, files = historicalArchive(name);
    assert.equal(image.policy, 'owned-static-images-paired-raw-render-v2'); assert.deepEqual(data.receipt.staticImageEvidence.counts, { passed: 21, failed: 1, inconclusive: 1 });
    assert.equal(image.snapshots.length, 31); assert.equal(image.actions.length, 30); assert.deepEqual(image.actions, data.ownershipProof.proofs[0].staticImages.actions);
    assert.equal(image.seal.attempts, 30); assert.equal(image.seal.disposed, true); assert.equal(image.seal.failed, false);
    const show = image.observations.find(o => o.featureId === 'sixel.native-raster.show'), clear = image.observations.find(o => o.featureId === 'sixel.native-raster.clear');
    assert.equal(show.result, 'failed'); assert.equal(show.assertions[0].passed, false); assert.equal(show.pixelQualification, null);
    assert.equal(show.measurement.nativeRaster.mismatches, 80); assert.equal(show.measurement.compatibility.mismatches, 0);
    assert.equal(clear.result, 'inconclusive'); assert.ok(clear.assertions.every(a => a.passed)); assert.match(clear.inconclusiveReason, /requires passed prior witness/);
    assert.ok(image.observations.every(o => o.assertions.at(-1).passed)); assert.equal(image.caseResults[0].result, 'failed');
    assert.deepEqual(data.calibrationSummary.placement, image.snapshots[show.snapshot].pixels[0].imageDetails.placements[0]);
    assert.deepEqual(data.metadataSummary.counts, { passed: 10, inconclusive: 2, failed: 1 });
    const metadata = read(files, 'metadata-observations.json'); assert.deepEqual(data.metadataSummary.observations, metadata.observations);
    assert.deepEqual(data.metadataWitnesses.emptyTitle.before, metadata.snapshots[39].before.metadata);
    assert.equal(data.metadataWitnesses.emptyTitle.before.oscTitle, ''); assert.notEqual(data.metadataWitnesses.emptyTitle.before.displayTitle, '');
    for (const value of data.metadataWitnesses.notification) assert.deepEqual(value.before, metadata.snapshots[value.snapshot].before.metadata.presentation);
    assert.equal(data.metadataWitnesses.notification[1].before.windowActive, false); assert.equal(data.metadataWitnesses.notification[1].before.toastVisible, true);
    assert.equal(data.metadataWitnesses.notification[2].before.toastVisible, false); assert.deepEqual(data.receipt.inputEvidence.counts, { passed: 39 });
    assert.deepEqual(data.receipt.pixelEvidence.counts, { passed: 25 }); assert.deepEqual(data.stockCollector.counts, { inconclusive: 171, supported: 84 });
    assert.equal(data.stockCollector.suiteComplete, false); assert.equal(data.stockCollector.ungradedDiagnostics['cursor.reverse-wrap'].name, 'UndeclaredTerminalGeometry');
    assert.deepEqual(data.stockDeltaFromPublishedV9.changedGrades.map(o => o.featureId), ['modes.reverse-video', 'modes.alt-scroll-1007']);
    const raw = read(files, 'raw.json');
    for (const changed of data.stockDeltaFromPublishedV9.changedGrades) {
        assert.deepEqual(changed.observation, raw.observations.find(o => o.featureId === changed.featureId)); assert.deepEqual(changed.rawReply, raw.rawReplies[changed.rawReplyRef]);
        assert.equal(changed.previous, 'inconclusive'); assert.equal(changed.current, 'supported');
    }
});

test('every PNG/source input and all 15 reports rederive before private full-receipt reproduction', () => privateEvidence(async (output, files) => {
    const data = publication(); let pngs = 0, sourcePngs = 0;
    for (const [file, bytes] of files) if (file.endsWith('.png')) {
        const decoded = decodeCapturePng(bytes); pngs++;
        if (file.startsWith('image-inputs/')) { sourcePngs++; assert.equal(decoded.width, 16); assert.equal(decoded.height, 16); assert.deepEqual(decoded.pixels, files.get(file.replace('.png', '.rgba'))); }
    }
    assert.equal(pngs, 140); assert.equal(sourcePngs, 3); assertRetainedSourceClosure(join(output, 'sources'), capturedV10SourceFiles);
    for (const entry of reportEntries) assert.deepEqual(await deriveReport(entry, output, data, files), read(files, entry[0] + '-observations.json'), entry[0]);
    const old = read(files, 'cell-receipt.json'); rmSync(join(output, 'cell-receipt.json'));
    compareReceipt(completeOwnedReceipt(output, join(output, 'already-removed-fixture'), evidenceFrom(old)), read(files, 'cell-receipt.json'));
}));

test('real calibrated data rejects coherent native-result promotion and requested-metadata stripping', async () => {
    for (const variant of ['promote', 'strip']) await privateEvidence((output, files) => {
        const report = read(files, 'image-observations.json'), old = read(files, 'cell-receipt.json'), evidence = evidenceFrom(old);
        if (variant === 'promote') {
            for (const observation of report.observations) { observation.result = 'passed'; observation.inconclusiveReason = null; observation.assertions.forEach(a => a.passed = true); }
            report.caseResults.forEach(value => value.result = 'passed'); evidence.staticImageEvidence.counts = { passed: 23 };
            for (const observation of report.observations) for (const witness of observation.causality) { witness.result = 'passed'; witness.passed = true; }
            const native = report.observations.find(value => value.featureId === 'sixel.native-raster.show').measurement.nativeRaster;
            native.passed = true; native.mismatches = 0;
        } else {
            for (const frame of report.snapshots) for (const pixel of frame.pixels) delete pixel.imageDetails;
            writeFileSync(join(output, 'image-snapshots.jsonl'), report.snapshots.map(frame => JSON.stringify(frame)).join('\n') + '\n');
        }
        const bytes = JSON.stringify(report, null, 2) + '\n'; writeFileSync(join(output, 'image-observations.json'), bytes); evidence.staticImageEvidence.sha256 = hash(bytes);
        const summary = read(files, 'summary.json'); summary.staticImageEvidence = evidence.staticImageEvidence;
        writeFileSync(join(output, 'summary.json'), JSON.stringify(summary, null, 2) + '\n');
        rmSync(join(output, 'cell-receipt.json'));
        assert.throws(() => completeOwnedReceipt(output, join(output, 'removed-fixture'), evidence), /Invalid|comparison/);
    });
});

for (const [label, grow, policy] of [['inventory', true, false], ['policy', false, true], ['inventory and policy', true, true]])
    test('future active ' + label + ' preserves the actual frozen 67-file v10 receipt', async () => {
        const root = mkdtempSync(join(tmpdir(), 'nt-v10-future-'));
        try {
            const modules = join(root, 'modules'); cpSync(new URL('./', import.meta.url), modules, { recursive: true });
            if (grow) {
                const path = join(modules, 'owned-source-inventory.json'), inventory = JSON.parse(readFileSync(path));
                inventory.futureSourceSha256 = 'future.mjs'; writeFileSync(path, JSON.stringify(inventory));
            }
            if (policy) {
                const path = join(modules, 'owned-sources.mjs'); writeFileSync(path, readFileSync(path, 'utf8').replace(/export const sourceSnapshotPolicy = [^;]+;/,
                    "export const sourceSnapshotPolicy = 'readonly-owned-source-graph-v30';"));
            }
            const sources = await import(pathToFileURL(join(modules, 'owned-sources.mjs')).href);
            assert.equal(sources.sourceSnapshotPolicy, policy ? 'readonly-owned-source-graph-v30' : 'readonly-owned-source-graph-v29');
            assert.equal(Object.keys(sources.ownedSourceFiles).length, grow ? 227 : 226); assert.deepEqual(sources.capturedV10SourceFiles, capturedV10SourceFiles);
            const next = await import(pathToFileURL(join(modules, 'receipt.mjs')).href), output = join(root, 'evidence'); mkdirSync(output);
            const files = historicalArchive(name); writeFiles(files, output); const old = read(files, 'cell-receipt.json'); rmSync(join(output, 'cell-receipt.json'));
            compareReceipt(next.completeOwnedReceipt(output, join(output, 'removed-fixture'), evidenceFrom(old)), read(files, 'cell-receipt.json'));
        } finally { rmSync(root, { recursive: true, force: true }); }
    });

test('a coherently rehashed expanded graph cannot acquire the frozen v10 policy', () => privateEvidence((output, files) => {
    const context = read(files, 'context.json'), manifest = read(files, 'source-snapshot.json');
    const inventory = read(files, 'sources/owned-source-inventory.json'), original = read(files, 'cell-receipt.json');
    const future = Buffer.from('// Synthetic future measured module; never executed.\n');
    writeFileSync(join(output, 'sources', 'future.mjs'), future); context.futureSourceSha256 = hash(future); inventory.futureSourceSha256 = 'future.mjs';
    const inventoryBytes = JSON.stringify(inventory, null, 2) + '\n'; writeFileSync(join(output, 'sources', 'owned-source-inventory.json'), inventoryBytes);
    context.sourceInventorySha256 = hash(inventoryBytes);
    Object.assign(manifest.hashes, { sourceInventorySha256: context.sourceInventorySha256, futureSourceSha256: context.futureSourceSha256 });
    const manifestBytes = JSON.stringify(manifest, null, 2) + '\n'; writeFileSync(join(output, 'source-snapshot.json'), manifestBytes);
    context.sourceSnapshotManifestSha256 = hash(manifestBytes); writeFileSync(join(output, 'context.json'), JSON.stringify(context, null, 2) + '\n');
    rmSync(join(output, 'cell-receipt.json'));
    assert.throws(() => completeOwnedReceipt(output, join(output, 'removed-fixture'), evidenceFrom(original)), /Invalid/);
}));
