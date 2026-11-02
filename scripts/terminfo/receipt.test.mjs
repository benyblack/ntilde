import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { completeOwnedReceipt } from './receipt.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
function fixture(action) {
    const output = mkdtempSync(join(tmpdir(), 'nt-receipt-shape-'));
    const scratch = join(output, 'removed-fixture'), runId = 'a'.repeat(32);
    const write = (file, value) => writeFileSync(join(output, file), JSON.stringify(value) + '\n');
    const proof = { completed: true, cleanedUpAt: '2026-11-02T00:00:00Z', cleanupError: null };
    write('ownership-proof.json', proof); write('context.json', { runId }); write('raw.json', { observations: [] });
    const entry = (prefix, unavailable = false) => {
        const file = `${prefix}-${unavailable ? 'unavailable' : 'observations'}.json`;
        write(file, { runId, fixtureStarted: !unavailable });
        return { runId, suiteId: `independent-${prefix}-fixture`, file, sha256: hash(readFileSync(join(output, file))), counts: {} };
    };
    const evidence = { ...entry('cell'), sgrEvidence: entry('sgr'), rgbEvidence: entry('rgb'),
        renditionEvidence: entry('rendition'), cursorEvidence: entry('cursor') };
    try { action({ output, scratch, evidence, proof, write, entry }); }
    finally { rmSync(output, { recursive: true, force: true }); }
}

test('portable production finalizer binds all five suites and original inputs after cleanup', () => fixture(({ output, scratch, evidence }) => {
    const receipt = completeOwnedReceipt(output, scratch, evidence);
    for (const field of ['sgrEvidence', 'rgbEvidence', 'renditionEvidence', 'cursorEvidence']) assert.deepEqual(receipt[field], evidence[field]);
    assert.equal(receipt.sha256, evidence.sha256);
    for (const [file, field] of [['ownership-proof.json', 'ownershipProofSha256'], ['context.json', 'contextSha256'], ['raw.json', 'rawSha256']])
        assert.equal(receipt[field], hash(readFileSync(join(output, file))));
    assert.equal(receipt.fixtureRemoved, true);
    assert.deepEqual(JSON.parse(readFileSync(join(output, 'cell-receipt.json'))), receipt);
    assert.throws(() => completeOwnedReceipt(output, scratch, evidence), /EEXIST/);
}));

test('portable finalizer retains unavailable markers without inventing outcomes', () => fixture(({ output, scratch, evidence, entry }) => {
    for (const prefix of ['rgb', 'rendition', 'cursor']) evidence[prefix + 'Evidence'] = { ...entry(prefix, true), status: 'unavailable', reason: 'screenDetailsUnavailable' };
    const receipt = completeOwnedReceipt(output, scratch, evidence);
    assert.equal(receipt.cursorEvidence.status, 'unavailable');
    assert.deepEqual(receipt.cursorEvidence.counts, {});
}));

for (const field of ['sgrEvidence', 'rgbEvidence', 'renditionEvidence', 'cursorEvidence']) {
    test('portable finalizer refuses missing ' + field, () => fixture(({ output, scratch, evidence }) => {
        delete evidence[field]; assert.throws(() => completeOwnedReceipt(output, scratch, evidence), /binding/);
        assert.equal(existsSync(join(output, 'cell-receipt.json')), false);
    }));
}

for (const [name, mutate] of [
    ['incomplete cleanup', f => { f.proof.completed = false; f.write('ownership-proof.json', f.proof); }],
    ['missing cleanup time', f => { delete f.proof.cleanedUpAt; f.write('ownership-proof.json', f.proof); }],
    ['cleanup error', f => { f.proof.cleanupError = 'worker did not exit'; f.write('ownership-proof.json', f.proof); }],
    ['remaining fixture', f => mkdirSync(f.scratch)],
    ['changed report', f => f.write('cursor-observations.json', { changed: true })],
    ['wrong run', f => { f.evidence.cursorEvidence.runId = 'b'.repeat(32); }],
    ['path escape', f => { f.evidence.cursorEvidence.file = '../other.json'; }],
]) test('portable finalizer refuses ' + name, () => fixture(f => {
    mutate(f); assert.throws(() => completeOwnedReceipt(f.output, f.scratch, f.evidence));
    assert.equal(existsSync(join(f.output, 'cell-receipt.json')), false);
}));

test('a dangling fixture symlink is still an unremoved entry', { skip: process.platform === 'win32' }, () => fixture(f => {
    symlinkSync(join(f.output, 'missing-target'), f.scratch);
    assert.throws(() => completeOwnedReceipt(f.output, f.scratch, f.evidence), /cleanup/);
}));
