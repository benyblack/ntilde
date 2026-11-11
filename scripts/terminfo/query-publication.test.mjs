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
import { capturedV11SourceFiles, assertRetainedSourceClosure } from './owned-sources.mjs';
import { completeOwnedReceipt } from './receipt.mjs';
import { decodeCapturePng } from './pixels-png.mjs';
const name = '2026-11-05-query-observations', directory = new URL('../../docs/qa/terminfo/', import.meta.url);
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const read = (files, file) => JSON.parse(files.get(file));
const journal = (files, file) => files.get(file).toString().trim().split('\n').filter(Boolean).map(line => JSON.parse(line));
const publication = () => {
    const bytes = readFileSync(new URL(name + '.json', directory));
    assert.equal(bytes.length, 1036228);
    assert.equal(hash(bytes), '37c023b1b554b23aa7f18b0b1bcf1b0126c3863cfa4b75224f11de01d7cc0d7d');
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
    ['input', 'input-state.mjs', 'buildInputReport'], ['image', 'static-images.mjs', 'buildStaticImageReport'], ['query', 'queries.mjs', 'buildQueryReport'],
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
    publication(); const files = historicalArchive(name), output = mkdtempSync(join(tmpdir(), 'nt-real-query-v11-'));
    try { writeFiles(files, output); return await action(output, files); }
    finally { rmSync(output, { recursive: true, force: true }); }
}
async function deriveReport(entry, output, data, files) {
    const [name, file, method] = entry, report = read(files, name + '-observations.json');
    const frames = journal(files, name + '-snapshots.jsonl'); assert.deepEqual(frames, name === 'query' ? report.frames : report.snapshots);
    const module = await import(new URL('./' + file, import.meta.url));
    if (name === 'query') return module[method]({ binding: report.binding, priorChallenges: report.priorChallenges, initial: read(files, 'query-initial.json'), actions: journal(files, 'query-actions.jsonl'), frames, seal: data.ownershipProof.proofs[0].queries.seal });
    if (name === 'resize') return module[method](frames, journal(files, 'resize-actions.jsonl'), data.ownedRunId);
    if (name === 'input') return module[method](frames, journal(files, 'input-actions.jsonl'), data.ownershipProof.proofs[0].input.seal, data.ownedRunId);
    if (name === 'image') return module[method](frames, journal(files, 'image-actions.jsonl'), data.ownershipProof.proofs[0].staticImages.seal, data.ownedRunId, output, report.priorChallenges);
    return module[method](frames, data.ownedRunId, ...(['pixel', 'color'].includes(name) ? [output] : []));
}

test('v11 publication binds the complete original context, proof and all 16 suite summaries', () => {
    const data = publication(), files = historicalArchive(name), original = read(files, 'context.json');
    const { executable, ...context } = original;
    assert.equal(typeof executable, 'string'); assert.deepEqual(data.context, context);
    assert.equal(data.appBuildSource, '630fe12606f24a0cf482b8756da32cf02c46ae72'); assert.equal(data.adapterSource, data.appBuildSource);
    assert.equal(data.context.appVersionSource, 'operator supplied; not independently verified');
    assert.equal(data.ownedRunId, '34412a050e77b76da29514b3f483246f'); assert.equal(data.stockRunId, 'f11ba90e3cad6138fa0ee2ee6bcf9c7f');
    assert.notEqual(data.stockRunId, data.ownedRunId);
    for (const [key, file] of [['queryReport', 'query-observations.json'], ['receipt', 'cell-receipt.json'], ['ownershipProof', 'ownership-proof.json'], ['sourceSnapshot', 'source-snapshot.json']]) assert.deepEqual(data[key], read(files, file));
    assert.deepEqual(data.sourceInventory, capturedV11SourceFiles); assert.deepEqual(data.sourceInventory, read(files, 'sources/owned-source-inventory.json'));
    assert.equal(Object.keys(data.sourceInventory).length, 74); assert.equal(data.sourceSnapshot.policy, 'readonly-owned-source-graph-v4');
    assert.equal(hash(files.get('source-snapshot.json')), data.context.sourceSnapshotManifestSha256);
    for (const [key, file] of Object.entries(capturedV11SourceFiles)) {
        assert.equal(hash(files.get('sources/' + file)), data.context[key]); assert.equal(data.sourceSnapshot.hashes[key], data.context[key]);
    }
    assert.equal(data.context.sourceImportsSourceSha256, '27737137fc22aa080b671153573a1f0741ba136fc7e35c0db39b6a3849fda6fd');
    assert.equal(data.receipt.schemaVersion, 11); assert.equal(data.receipt.fixtureRemoved, true); assert.equal(data.ownershipProof.completed, true);
    assert.deepEqual(data.sourceSnapshotCleanup, data.ownershipProof.sourceSnapshot);
    assert.ok(Date.parse(data.ownershipProof.cleanedUpAt) < Date.parse(data.sourceSnapshotCleanup.removedAt));
    assert.ok(Date.parse(data.sourceSnapshotCleanup.removedAt) < Date.parse(data.receipt.completedAt));
    assert.equal(Object.keys(data.suiteSummaries).length, 16);
    for (const [name] of reportEntries) {
        const report = read(files, name + '-observations.json'), summary = data.suiteSummaries[name];
        assert.deepEqual(summary.binding, bindingProjection(bindingFor(data.receipt, name)));
        assert.equal(summary.pairedFrames, (name === 'query' ? report.frames : report.snapshots).length); assert.equal(summary.observations, report.observations.length);
    }
});

test('the exact 266-member archive preserves all original bytes and decodable PNG inputs', () => {
    const data = publication(); assert.equal(data.archive.bytes, 1055814); assert.equal(data.archive.files, 266);
    assert.equal(data.archive.sha256, '38b81ecf9592e5eed3df554f4a5a750eb4711e2a994ea443d9a04e90849ffc5b');
    const files = archiveMembers(data.archive); assert.deepEqual(files, historicalArchive(name));
    let pngs = 0, sources = 0;
    for (const [file, bytes] of files) if (file.endsWith('.png')) {
        const decoded = decodeCapturePng(bytes); pngs++;
        if (file.startsWith('image-inputs/')) { sources++; assert.equal(decoded.width, 16); assert.equal(decoded.height, 16); assert.deepEqual(decoded.pixels, files.get(file.replace('.png', '.rgba'))); }
    }
    assert.equal(pngs, 140); assert.equal(sources, 3);
});

test('exact query packets, 110 prior and 44 new paired fences retain mismatch semantics', async () => {
    const data = publication(), files = historicalArchive(name), report = data.queryReport;
    const { queryWitnessPlan } = await import('./query-witness-cases.mjs');
    const contracts = queryWitnessPlan({ rows: 40, cols: 140, top: 1, bottom: 40 });
    assert.equal(contracts.length, 40); assert.equal(report.actions.length, 44); assert.equal(report.frames.length, 44);
    assert.deepEqual(report.readerCounts, [48, 42]); assert.deepEqual(report.unobservedState, ['reverseVideo']);
    const prior = ['input', 'image'].flatMap(prefix => read(files, prefix + '-observations.json').snapshots.filter(frame => frame.capture).map(frame => frame.capture.nonces.join(',')));
    assert.equal(prior.length, 110); assert.deepEqual(report.priorChallenges, prior);
    const pairs = [...prior]; const queryActions = report.actions.filter(action => action.kind === 'query');
    assert.equal(queryActions.length, contracts.length);
    for (const [index, contract] of contracts.entries()) {
        assert.equal(queryActions[index].id, contract.id); assert.equal(queryActions[index].commandHex, Buffer.from(contract.request).toString('hex'));
    }
    for (const frame of report.frames) {
        const capture = frame.capture; assert.equal(capture.byteLength, Buffer.from(capture.hex, 'hex').length);
        assert.equal(capture.barrier, capture.nonces.map(n => '\x1b[?' + n + ';0$y').join(''));
        assert.equal(capture.nonces.length, 2); assert.notEqual(...capture.nonces);
        for (const nonce of capture.nonces) assert.ok(Number.isInteger(nonce) && nonce >= 32768 && nonce <= 65535);
        pairs.push(capture.nonces.join(','));
    }
    assert.equal(pairs.length, 154); assert.equal(new Set(pairs).size, 154);
    assert.equal(report.seal.status.attempts, 44); assert.equal(report.seal.status.failed, false); assert.equal(report.seal.status.disposed, true);
    const last = report.frames.at(-1).capture;
    assert.equal(report.seal.status.lastAttempt.hex, last.hex + Buffer.from(last.barrier).toString('hex'));
    assert.deepEqual(report.seal.status.lastAttempt.nonces, last.nonces);
    assert.deepEqual(data.receipt.queryEvidence.counts, { matched: 38, mismatched: 2 });
    const mismatches = report.observations.filter(value => !value.contractMatched);
    assert.deepEqual(mismatches.map(value => value.id), ['q12.decrqss.sgr', 'q12.decrqss.cursor-style']);
    assert.deepEqual(mismatches.map(value => value.receivedHex), ['1b50312472306d1b5c', '1b503124723020711b5c']);
    assert.ok(mismatches.every(value => value.expectedEvidence === 'negative-reply' && value.observedEvidence === 'unexpected-bytes'));
    assert.deepEqual(data.querySummary.mismatches, mismatches);
    assert.equal(report.observations.filter(value => value.contractMatched && value.expectedEvidence === 'negative-reply').length, 19);
    assert.equal(report.observations.filter(value => value.contractMatched && value.expectedEvidence === 'silence').length, 16);
});

test('all 16 reports and complete schema 11 finalizer rederive in a private copy', () => privateEvidence(async (output, files) => {
    const data = publication(); assertRetainedSourceClosure(join(output, 'sources'), capturedV11SourceFiles);
    for (const entry of reportEntries) assert.deepEqual(await deriveReport(entry, output, data, files), read(files, entry[0] + '-observations.json'), entry[0]);
    const old = read(files, 'cell-receipt.json'); rmSync(join(output, 'cell-receipt.json'));
    compareReceipt(completeOwnedReceipt(output, join(output, 'already-removed-fixture'), evidenceFrom(old)), old);
}));

test('existing cursor/pixel negatives, calibrated image negative and stock delta remain unchanged', () => {
    const data = publication(), files = historicalArchive(name);
    for (const witness of data.prefixNegativeWitnesses) {
        const report = read(files, witness.suite + '-observations.json');
        assert.deepEqual(witness.negativeObservations, report.observations.filter(value => value.result !== 'passed'));
        assert.deepEqual(witness.counts, witness.suite === 'cursor' ? { passed: 8, failed: 1 } : { passed: 24, failed: 1 });
    }
    assert.deepEqual(data.receipt.staticImageEvidence.counts, { passed: 21, failed: 1, inconclusive: 1 });
    assert.deepEqual(data.receipt.inputEvidence.counts, { passed: 39 }); assert.deepEqual(data.receipt.metadataEvidence.counts, { passed: 10, inconclusive: 2, failed: 1 });
    assert.deepEqual(data.metadataSummary.observations, read(files, 'metadata-observations.json').observations);
    assert.deepEqual(data.stockCollector.counts, { inconclusive: 170, supported: 85 }); assert.equal(data.stockCollector.suiteComplete, false);
    assert.deepEqual(data.stockCollector.ungradedDiagnostics['cursor.reverse-wrap'].name, 'UndeclaredTerminalGeometry');
    const raw = read(files, 'raw.json'), previous = read(historicalArchive('2026-11-04-image-observations'), 'raw.json');
    const changed = raw.observations.filter(value => previous.observations.find(old => old.featureId === value.featureId).outcome !== value.outcome);
    assert.deepEqual(changed.map(value => value.featureId), ['device.decrqss']);
    assert.deepEqual(data.stockDeltaFromPublishedV10.changedGrades.map(value => value.observation), changed);
    assert.equal(data.stockDeltaFromPublishedV10.changedGrades[0].previous, 'inconclusive');
    assert.equal(data.stockDeltaFromPublishedV10.changedGrades[0].current, 'supported');
});

for (const variant of ['promote', 'strip-prior', 'unavailable', 'downgrade'])
    test('real v11 receipt rejects coherent query ' + variant, () => privateEvidence((output, files) => {
        const report = read(files, 'query-observations.json'), original = read(files, 'cell-receipt.json'), evidence = evidenceFrom(original);
        if (variant === 'promote') {
            report.observations.forEach(value => { value.contractMatched = true; value.observedEvidence = value.expectedEvidence; });
            evidence.queryEvidence.counts = { matched: 40 };
        } else if (variant === 'strip-prior') report.priorChallenges.pop();
        else if (variant === 'unavailable') {
            writeFileSync(join(output, 'query-unavailable.json'), JSON.stringify({ reason: 'queryPrerequisiteUnavailable' }));
            rmSync(join(output, 'query-initial.json'));
        } else {
            delete evidence.queryEvidence;
            const context = read(files, 'context.json'); delete context.ownedQueryPolicy;
            writeFileSync(join(output, 'context.json'), JSON.stringify(context));
        }
        const bytes = JSON.stringify(report, null, 2) + '\n'; writeFileSync(join(output, 'query-observations.json'), bytes);
        if (evidence.queryEvidence) evidence.queryEvidence.sha256 = hash(bytes);
        const summary = read(files, 'summary.json'); summary.queryEvidence = evidence.queryEvidence;
        writeFileSync(join(output, 'summary.json'), JSON.stringify(summary)); rmSync(join(output, 'cell-receipt.json'));
        assert.throws(() => completeOwnedReceipt(output, join(output, 'removed-fixture'), evidence), /Invalid|comparison|ENOENT/);
    }));

for (const [label, grow, policy] of [['inventory', true, false], ['policy', false, true], ['inventory and policy', true, true]])
    test('future active ' + label + ' preserves the actual frozen 74-file v11 receipt', async () => {
        const root = mkdtempSync(join(tmpdir(), 'nt-v11-future-'));
        try {
            const modules = join(root, 'modules'); cpSync(new URL('./', import.meta.url), modules, { recursive: true });
            if (grow) {
                const path = join(modules, 'owned-source-inventory.json'), inventory = JSON.parse(readFileSync(path));
                inventory.futureSourceSha256 = 'future.mjs'; writeFileSync(path, JSON.stringify(inventory));
            }
            if (policy) {
                const path = join(modules, 'owned-sources.mjs'); writeFileSync(path, readFileSync(path, 'utf8').replace(/export const sourceSnapshotPolicy = [^;]+;/,
                    "export const sourceSnapshotPolicy = 'readonly-owned-source-graph-v46';"));
            }
            const sources = await import(pathToFileURL(join(modules, 'owned-sources.mjs')).href);
            assert.equal(sources.sourceSnapshotPolicy, policy ? 'readonly-owned-source-graph-v46' : 'readonly-owned-source-graph-v45');
            assert.equal(Object.keys(sources.ownedSourceFiles).length, grow ? 301 : 300); assert.deepEqual(sources.capturedV11SourceFiles, capturedV11SourceFiles);
            const next = await import(pathToFileURL(join(modules, 'receipt.mjs')).href), output = join(root, 'evidence'); mkdirSync(output);
            const files = historicalArchive(name); writeFiles(files, output); const old = read(files, 'cell-receipt.json'); rmSync(join(output, 'cell-receipt.json'));
            compareReceipt(next.completeOwnedReceipt(output, join(output, 'removed-fixture'), evidenceFrom(old)), read(files, 'cell-receipt.json'));
        } finally { rmSync(root, { recursive: true, force: true }); }
    });

test('a coherently rehashed expanded graph cannot acquire the frozen v11 policy', () => privateEvidence((output, files) => {
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
