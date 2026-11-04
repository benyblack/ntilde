// Retained real-App data, including the first failed attempt. Unit fixtures are separate.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync, mkdtempSync, mkdirSync, rmSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { tmpdir } from 'node:os';
import { createHash } from 'node:crypto';
import { historicalArchive } from './fixtures/captured-sources.mjs';
import { capturedV9SourceFiles } from './owned-sources.mjs';
import { buildInputReport } from './input-state.mjs';
import { completeOwnedReceipt } from './receipt.mjs';
const name = '2026-11-04-input-observations';
const directory = new URL('../../docs/qa/terminfo/', import.meta.url);
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const publication = () => JSON.parse(readFileSync(new URL(name + '.json', directory)));

test('real input publication keeps exact bytes and the fixed60-source v9 scope', () => {
    const bytes = readFileSync(new URL(name + '.json', directory)), data = JSON.parse(bytes);
    assert.equal(bytes.length, 1640969);
    assert.equal(hash(bytes), 'daccfb73ae4f153a602cdae4e2fe00cdf8b0865b0819262c334a476017c8faf5');
    assert.equal(data.appBuildSource, '9648f5171b2d383f4954e2bcf6170c2b92e3f98e');
    assert.equal(data.adapterSource, data.appBuildSource);
    assert.equal(data.sourceSnapshot.policy, 'readonly-owned-source-graph-v2');
    assert.deepEqual(Object.keys(data.sourceSnapshot.hashes).sort(), Object.keys(capturedV9SourceFiles).sort());
    assert.equal(Object.keys(data.sourceSnapshot.hashes).length, 60);
    for (const [key, file] of Object.entries(capturedV9SourceFiles)) {
        assert.equal(data.sourceSnapshot.hashes[key], data.context[key]);
        assert.equal(data.archive.fileSha256['sources/' + file], data.context[key]);
    }
    assert.equal(data.receipt.schemaVersion, 9);
    assert.equal(data.receipt.fixtureRemoved, true);
    assert.ok(Date.parse(data.sourceSnapshotCleanup.removedAt) < Date.parse(data.receipt.completedAt));
});

test('all39 live input cases rederive from81 paired frames,39 actions and the healthy raw seal', () => {
    const data = publication(), report = data.inputReport;
    assert.deepEqual(buildInputReport(report.snapshots, report.actions, report.seal, data.context.runId), report);
    assert.equal(report.snapshots.length, 81); assert.equal(report.actions.length, 39);
    assert.deepEqual(data.receipt.inputEvidence.counts, { passed: 39 });
    assert.equal(hash(JSON.stringify(report, null, 2) + '\n'), data.receipt.inputEvidence.sha256);
    assert.deepEqual(report.actions, data.inputOwnership.actions);
    assert.deepEqual(report.seal, data.inputOwnership.seal);
    assert.equal(report.seal.attempts, 80); assert.equal(report.seal.disposed, true); assert.equal(report.seal.failed, false);
    const left = report.observations.find(item => item.featureId === 'input.mouse.x10.left.raw');
    assert.equal(left.actualHex, '1b5b4d208023');
    assert.equal(report.observations.find(item => item.featureId === 'input.keypad.normal.text').actualHex, '37');
    assert.equal(report.observations.find(item => item.featureId === 'input.mouse.any.hover.coalesced').actualHex, '');
});

test('both attempt archives are pinned; complete scope and historical negative stay distinct', () => {
    const data = publication(), files = historicalArchive(name);
    const archive = readFileSync(new URL(data.archive.file, directory));
    assert.equal(archive.length, 642621);
    assert.equal(hash(archive), 'd967c6553d985d5722ed1274dc6e66b4cdf5c8a59c54fddbc786356c2d650541');
    assert.equal(files.size, 176);
    assert.deepEqual([...files.keys()].sort(), Object.keys(data.archive.fileSha256).sort());
    const context = JSON.parse(files.get('context.json')); delete context.executable;
    assert.deepEqual(data.context, context);
    assert.deepEqual(data.pixelReport, JSON.parse(files.get('pixel-observations.json')));
    assert.deepEqual(data.receipt.pixelEvidence.counts, { passed: 25 });
    assert.equal(data.pixelReport.observations.find(item => item.featureId === 'rendition.bold').result, 'passed');
    const old = JSON.parse(readFileSync(new URL('2026-11-03-pixels.json', directory)));
    assert.deepEqual(old.receipt.pixelEvidence.counts, { failed: 1, passed: 24 });
    assert.deepEqual(data.metadataSummary.counts, { passed: 9, inconclusive: 3, failed: 1 });
    assert.deepEqual(data.stockCollector.counts, { inconclusive: 173, supported: 82 });
    assert.equal(data.stockCollector.suiteComplete, false);
    assert.equal(data.stockCollector.ungradedDiagnostics['cursor.reverse-wrap'].name, 'UndeclaredTerminalGeometry');
    const failure = data.firstAttempt, failedBytes = readFileSync(new URL(failure.archive.file, directory));
    assert.equal(failedBytes.length, 500953);
    assert.equal(hash(failedBytes), '425c747ba17b4fca7df7c2d677663bbf9afc8764a3ea4dbdc55c11cf08e45cb8');
    assert.equal(failure.completed, false); assert.equal(failure.archive.files, 163);
    assert.equal(Object.hasOwn(failure.archive.fileSha256, 'cell-receipt.json'), false);
    assert.equal(failure.diagnosis.actualRejected, true); assert.equal(failure.diagnosis.reorderedAccepted, true);
});

test('complete real14-suite receipt reproduces in a private copy without changing archived data', () => {
    const output = mkdtempSync(join(tmpdir(), 'nt-real-input-v9-'));
    try {
        const files = historicalArchive(name);
        for (const [file, bytes] of files) {
            const target = join(output, file); mkdirSync(dirname(target), { recursive: true }); writeFileSync(target, bytes);
        }
        const old = JSON.parse(files.get('cell-receipt.json'));
        const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = old;
        rmSync(join(output, 'cell-receipt.json'));
        const actual = completeOwnedReceipt(output, join(output, 'already-removed-disposable-fixture'), evidence);
        const { completedAt: newCompletedAt, ...actualFields } = actual;
        const { completedAt: oldCompletedAt, ...oldFields } = old;
        assert.deepEqual(actualFields, oldFields);
        assert.equal(actual.schemaVersion, 9);
        assert.deepEqual(actual.inputEvidence.counts, { passed: 39 });
        assert.ok(Date.parse(newCompletedAt) >= Date.parse(oldCompletedAt));
    } finally { rmSync(output, { recursive: true, force: true }); }
});
