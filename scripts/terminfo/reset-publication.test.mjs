// Frozen real-App observations. Synthetic tests do not replace this measurement.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { buildSavedReport } from './saved.mjs';
import { capturedV8SourceFiles, capturedV8SourcePolicy } from './owned-sources.mjs';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const path = new URL('../../docs/qa/terminfo/2026-11-04-reset-observations.json', import.meta.url);
const publication = () => JSON.parse(readFileSync(path));

test('fresh reset publication retains all nine controls, complete paired witnesses and the historical negative', () => {
    const bytes = readFileSync(path), data = JSON.parse(bytes), report = data.savedReport;
    assert.equal(hash(bytes), '30699be1b2f132bd5a572e2f8a0a235ed4c3021c003482ca90386a2a6af32e23');
    assert.equal(data.appBuildSource, 'dc4e2c297d9bcbf89ab0d3d34b3dfacc2fdec4d6');
    assert.equal(data.adapterSource, data.appBuildSource);
    assert.deepEqual(buildSavedReport(report.snapshots, data.context.runId), report);
    assert.equal(report.snapshots.length, 57); assert.equal(report.inputs.length, 9);
    assert.ok(report.observations.every(item => item.control.passed && item.result === 'passed'));
    const reset = report.observations.at(-1);
    assert.equal(reset.featureId, 'saved.reset.full-both-banks');
    assert.equal(reset.control.snapshot, 50);
    assert.deepEqual(reset.assertions.map(item => item.snapshot), [51, 52, 53, 54, 55]);
    assert.ok(reset.assertions.every(item => item.passed));
    assert.deepEqual(data.receipt.savedEvidence.counts, { passed: 9 });
    const serialized = JSON.stringify(report, null, 2) + '\n';
    assert.equal(Buffer.byteLength(serialized), 417476);
    assert.equal(hash(serialized), 'b8c061904a2aadfa02415d69b232e28f5a0a9798289963619d7ccd28646b903f');
    assert.equal(data.receipt.savedEvidence.sha256, hash(serialized));
    const historical = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-03-saved-state.json', import.meta.url)));
    assert.deepEqual(historical.receipt.savedEvidence.counts, { passed: 8, failed: 1 });
    assert.equal(historical.savedReport.observations.at(-1).result, 'failed');
});

test('reset archive preserves the fixed captured scope and every original receipt binding', () => {
    const data = publication(), archive = readFileSync(new URL('../../docs/qa/terminfo/' + data.archive.file, import.meta.url));
    assert.equal(archive.length, 602443);
    assert.equal(hash(archive), 'd3d85d29f5d90c17ef8dfb62739d94b29ce46018b258534c0c038e9fa3a7992d');
    assert.equal(data.archive.sha256, hash(archive)); assert.equal(data.archive.bytes, archive.length);
    assert.equal(data.archive.files, 165); assert.equal(Object.keys(data.archive.fileSha256).length, 165);
    assert.equal(data.sourceSnapshot.policy, capturedV8SourcePolicy);
    assert.deepEqual(Object.keys(data.sourceSnapshot.hashes).sort(), Object.keys(capturedV8SourceFiles).sort());
    for (const [key, file] of Object.entries(capturedV8SourceFiles)) {
        assert.equal(data.sourceSnapshot.hashes[key], data.context[key]);
        assert.equal(data.archive.fileSha256['sources/' + file], data.context[key]);
    }
    assert.equal(data.archive.fileSha256['source-snapshot.json'], data.context.sourceSnapshotManifestSha256);
    assert.equal(data.sourceSnapshotCleanup.policy, capturedV8SourcePolicy);
    assert.equal(data.sourceSnapshotCleanup.root, data.context.sourceSnapshotRoot);
    assert.ok(Date.parse(data.sourceSnapshotCleanup.removedAt) <= Date.parse(data.receipt.completedAt));
    assert.equal(data.receipt.schemaVersion, 8); assert.equal(data.receipt.fixtureRemoved, true);
    for (const [key, file] of Object.entries({ contextSha256: 'context.json', ownershipProofSha256: 'ownership-proof.json', rawSha256: 'raw.json' })) {
        assert.equal(data.receipt[key], data.archive.fileSha256[file]);
    }
    const suites = Object.values(data.receipt).filter(value => value?.suiteId && value.file);
    // The outer receipt is the ASCII binding; twelve supplemental bindings follow.
    assert.deepEqual(suites.map(value => value.suiteId).sort(), [
        'ntilde-owned-cell-editing-v1', 'ntilde-owned-cursor-modes-v1', 'ntilde-owned-erasure-v1',
        'ntilde-owned-history-scrolling-v1', 'ntilde-owned-pane-metadata-v1', 'ntilde-owned-render-pixels-v1',
        'ntilde-owned-rendition-charsets-v3', 'ntilde-owned-resize-reflow-v1', 'ntilde-owned-rgb-underline-v1',
        'ntilde-owned-runtime-colors-v1', 'ntilde-owned-saved-alternate-v1', 'ntilde-owned-sgr-indices-v1',
    ]);
    assert.equal(new Set(suites.map(value => value.file)).size, 12);
    assert.equal(data.receipt.sha256, data.archive.fileSha256[data.receipt.file]);
    for (const binding of suites) assert.equal(binding.sha256, data.archive.fileSha256[binding.file]);
});

test('new reply-based grades and active-window notification limits do not rewrite older observations', () => {
    const data = publication();
    assert.deepEqual(data.stockCollector.counts, { inconclusive: 173, supported: 82 });
    assert.equal(data.stockCollector.suiteComplete, false);
    assert.equal(data.stockCollector.ungradedDiagnostics['cursor.reverse-wrap'].name, 'UndeclaredTerminalGeometry');
    const old = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-04-metadata-snapshot.json', import.meta.url)));
    assert.deepEqual(old.stockCollector.counts, { supported: 78, inconclusive: 177 });
    assert.equal(data.stockCollector.probeHash, '16a690f34c0e');
    const expected = {
        'device.xtwinops-18': ['\x1b[18t\x1b[c', '\x1b[8;40;140t'],
        'device.tertiary-da': ['\x1b[=c\x1b[c', '\x1bP!|00000000\x1b\\'],
    };
    for (const [id, [sequence, reply]] of Object.entries(expected)) {
        const witness = data.stockReplyWitnesses[id], query = witness.trace.queries[0];
        assert.equal(witness.observation.outcome, 'supported');
        assert.equal(query.sequence, sequence); assert.deepEqual(query.match, [reply]);
        assert.equal(query.raw, reply + '\x1b[?62;4;22c');
        assert.equal(Buffer.from(query.rawBase64, 'base64').toString(), query.raw);
    }
    assert.equal(data.stockReplyWitnesses['extensions.kitty-graphics.transmit'].trace.queries.length, 1);
    assert.equal(data.stockReplyWitnesses['extensions.kitty-graphics.display'].trace.queries.length, 2);
    const transmit = data.stockReplyWitnesses['extensions.kitty-graphics.transmit'];
    const display = data.stockReplyWitnesses['extensions.kitty-graphics.display'];
    assert.equal(transmit.observation.outcome, 'supported');
    assert.equal(transmit.trace.queries[0].match[0], '\x1b_Gi=1,I=322121290;OK\x1b\\');
    assert.equal(display.observation.outcome, 'supported');
    assert.deepEqual(display.trace.queries.map(query => query.match[0]), [
        '\x1b_Gi=2,I=1652542871;OK\x1b\\', '\x1b_Gi=2;OK\x1b\\',
    ]);
    assert.deepEqual(data.metadataSummary.counts, { passed: 9, inconclusive: 3, failed: 1 });
    const notification = data.metadataSummary.observations.find(item => item.featureId === 'metadata.notification.osc9');
    assert.equal(notification.positiveDeliveryPreconditions, false); assert.equal(notification.result, 'inconclusive');
    assert.equal(data.metadataSummary.observations.find(item => item.featureId === 'metadata.title.empty').result, 'failed');
    assert.deepEqual(data.receipt.pixelEvidence.counts, { failed: 1, passed: 24 });
});
