import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { completeOwnedReceipt } from './receipt.mjs';
import { fontSizePublication } from './font-size-publication.mjs';
import { buildFontSizeReport } from './font-size-grading.mjs';
import { fontSizeIds } from './font-size-contracts.mjs';
import { validateOutputGeometry } from './terminal-output.mjs';

const dir = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-18-osc-font-queries/', import.meta.url));
const read = (root, name) => readFileSync(join(root, name));
const json = (root, name) => JSON.parse(read(root, name));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const pinnedManifestSha256 = '432005d07ac039da7a57f36dadf39abb2d928d8b3c424a1396087a51708ebc39';

function manifest() {
    assert.equal(hash(read(dir, 'manifest.json')), pinnedManifestSha256);
    const pins = json(dir, 'manifest.json');
    for (const [name, digest] of Object.entries({ ...pins.archives, ...pins.files })) assert.equal(hash(read(dir, name)), digest, name);
    return pins;
}

function withArchive(pins, name, verify) {
    const root = mkdtempSync(join(tmpdir(), 'nt-font-size-publication-'));
    try {
        extractOwnedArchive(dir, { archives: { 'owned-run.zip': pins.archives[name] } }, root, join(dir, name));
        verify(root);
    } finally { rmSync(root, { recursive: true, force: true }); }
}

test('the retained font run rederives its receipt, both exact probe paths and the complete publication', () => {
    const pins = manifest();
    withArchive(pins, 'owned-run.zip', root => {
        const receipt = json(root, 'cell-receipt.json'), proof = json(root, 'ownership-proof.json');
        const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, collectionScope, ...evidence } = receipt;
        rmSync(join(root, 'cell-receipt.json'));
        const derived = completeOwnedReceipt(root, join(root, 'removed-fixture'), evidence);
        assert.deepEqual({ ...derived, completedAt }, receipt);
        assert.ok(Date.parse(completedAt) >= Date.parse(proof.cleanedUpAt));
        assert.equal(fixtureRemoved, true);
        assert.equal(schemaVersion, 76);
        assert.equal(json(root, 'context.json').sourceSnapshotPolicy, pins.measurement.sourcePolicy);
        assert.equal(receipt.runId, pins.measurement.runId);
        assert.deepEqual(receipt.fontSizeEvidence.counts, { supported: 2 });
        assert.equal(Object.keys(json(root, 'source-snapshot.json').hashes).length, 461);
        assert.deepEqual(collectionScope.omittedSuites, ['osc9', 'notify']);
        const setup = proof.proofs[0].fontFixtureGeometry;
        assert.deepEqual(setup.target, { rows: 40, cols: 140 });
        validateOutputGeometry(proof.proofs[0].terminalOutput.description, 40, 140);
        const frames = read(root, 'font-size-snapshots.jsonl').toString('utf8').trim().split('\n').map(line => JSON.parse(line));
        assert.equal(frames.length, 27);
        assert.equal(frames[0].paneId, setup.before.paneId);
        assert.deepEqual(frames.at(-1).screen, frames[0].screen);
        assert.deepEqual(frames.at(-1).geometry, frames[0].geometry);
        const measured = json(root, 'font-size-observations.json');
        assert.deepEqual(buildFontSizeReport(frames, json(root, 'font-size-run.json'), receipt.runId, measured.adapter), measured);
        const projected = fontSizePublication(root, pins.measurement);
        projected.summary.receipt = receipt;
        assert.deepEqual(projected.summary, json(dir, 'summary.json'));
        assert.deepEqual(projected.report, json(dir, 'report.json'));
        assert.deepEqual(projected.report.cases.map(item => item.caseId), fontSizeIds);
        for (const [index, item] of projected.report.cases.entries()) {
            const code = index === 0 ? 7770 : 7777;
            const bel = `\x1b]${code};10\x07`, st = `\x1b]${code};10\x1b\\`;
            assert.equal(item.pinned.termless.observation.outcome, 'supported');
            assert.equal(item.pinned.terminal.observation.outcome, 'supported');
            assert.equal(item.effectContract.outcome, 'supported');
            assert.equal(item.stock.outcome, 'supported');
            assert.ok(item.effectContract.required.every(check => check.held));
            assert.ok(item.effectContract.validity.every(check => check.held));
            assert.deepEqual(item.observed.exchanges.find(exchange => exchange.id === 'documented-bel').report.replies, [bel]);
            assert.deepEqual(item.observed.exchanges.find(exchange => exchange.id === 'documented-st').report.replies, [st]);
            assert.equal(item.observed.geometry.fontSizeDip, 40 / 3);
        }
    });
});

test('both unsuccessful desktop attempts retain their independent cleanup and have no admitted receipt', () => {
    const pins = manifest(), attempts = json(dir, 'attempts.json');
    assert.equal(attempts.length, 3);
    for (const attempt of attempts.slice(0, 2)) {
        withArchive(pins, attempt.archive, root => {
            const proof = json(root, 'ownership-proof.json');
            assert.equal(json(root, 'context.json').runId, attempt.runId);
            assert.equal(json(root, 'failure.json').error, attempt.error);
            assert.equal(proof.completed, false);
            assert.ok(proof.cleanedUpAt);
            assert.equal(proof.cleanupError, undefined);
            assert.equal(existsSync(join(root, 'cell-receipt.json')), false);
        });
    }
    for (const attempt of attempts) assert.ok(attempt.runnerLog.includes(`[terminfo-owned] desktop lease ${attempt.runId} released after verified cleanup.`));
});
