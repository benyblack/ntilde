import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { syntheticOscTitleEvidence } from './fixtures/osc-title-evidence.mjs';
import { adaptOscTitleCollector, oscTitleAdapterId, oscTitleSeams } from './osc-title-grading-collector.mjs';
import { buildOscTitleReport, oscTitleCounts } from './osc-title-grading.mjs';
import { oscTitleSuiteId } from './osc-title-contracts.mjs';
import { checkOscTitleEvidence, includesOscTitleEvidence } from './osc-title-receipt.mjs';

const hash = value => createHash('sha256').update(value).digest('hex');
function fixture(action) {
    const dir = mkdtempSync(join(tmpdir(), 'nt-osc-title-receipt-'));
    try {
        const old = Buffer.from(oscTitleSeams.join('\n') + '\nexport const ntildeTitleStackAdapter = Object.freeze({});\n');
        const adapter = Buffer.from(adaptOscTitleCollector(old)), e = syntheticOscTitleEvidence();
        e.adapter.bundleSha256 = hash(adapter);
        const run = Buffer.from(JSON.stringify(e.run) + '\n'), journal = Buffer.from(e.frames.map(JSON.stringify).join('\n') + '\n');
        const report = Buffer.from(JSON.stringify(buildOscTitleReport(e.frames, e.run, e.runId, e.adapter)) + '\n');
        for (const [name, bytes] of [['collector-tst.mjs', old], ['collector-ost.mjs', adapter], ['ost-run.json', run],
            ['ost-snapshots.jsonl', journal], ['ost-observations.json', report]]) writeFileSync(join(dir, name), bytes);
        const context = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v47', oscTitleAdapterId, oscTitleSuiteId, oscTitleContractsVersion: 1, upstreamBundleSha256: e.adapter.upstreamBundleSha256,
            titleStackBundleSha256: hash(old), oscTitleBundleSha256: hash(adapter) };
        const evidence = { titleStackEvidence: {}, oscTitleEvidence: { runId: e.runId, suiteId: oscTitleSuiteId, contractsVersion: 1,
            file: 'ost-observations.json', runFile: 'ost-run.json', counts: oscTitleCounts(JSON.parse(report)),
            provisionalUntil: 'cell-receipt.json', adapterBundleSha256: hash(adapter), sha256: hash(report), runSha256: hash(run) } };
        action({ dir, e, context, evidence });
    } finally { rmSync(dir, { recursive: true, force: true }); }
}

test('receipt rederives journal and refuses substitution, tampering and missing restoration', () => fixture(f => {
    const check = () => checkOscTitleEvidence(f.dir, f.evidence, f.e.runId, f.context);
    assert.equal(includesOscTitleEvidence(f.dir, f.evidence, f.context), true);
    assert.doesNotThrow(check);
    const path = join(f.dir, 'ost-snapshots.jsonl'), original = readFileSync(path);
    writeFileSync(path, original.toString().replace('ost-finish', 'ost-read'));
    assert.throws(check);
    writeFileSync(path, original);
    f.context.titleStackBundleSha256 = 'a'.repeat(64); assert.throws(check, /derivation/);
    f.context.titleStackBundleSha256 = hash(readFileSync(join(f.dir, 'collector-tst.mjs')));
    const run = join(f.dir, 'ost-run.json'); rmSync(run); symlinkSync(path, run);
    assert.throws(check, /regular file/);
}));

test('typed initial unavailability is retained, while partial artifacts cannot be hidden', () => fixture(f => {
    const marker = { schemaVersion: 1, runId: f.e.runId, suiteId: oscTitleSuiteId, status: 'unavailable', reason: 'paneMetadataUnavailable', plannedCases: 3 };
    const bytes = Buffer.from(JSON.stringify(marker) + '\n');
    for (const name of ['ost-run.json', 'ost-observations.json', 'ost-snapshots.jsonl']) rmSync(join(f.dir, name));
    writeFileSync(join(f.dir, 'ost-unavailable.json'), bytes);
    Object.assign(f.evidence.oscTitleEvidence, { file: 'ost-unavailable.json', status: 'unavailable', reason: marker.reason, counts: {}, sha256: hash(bytes) });
    assert.doesNotThrow(() => checkOscTitleEvidence(f.dir, f.evidence, f.e.runId, f.context));
    writeFileSync(join(f.dir, 'ost-snapshots.jsonl'), 'partial');
    assert.throws(() => checkOscTitleEvidence(f.dir, f.evidence, f.e.runId, f.context), /partial evidence/);
}));

test('each partial declaration requires the suffix even under an older policy', () => {
    for (const key of ['oscTitleAdapterId', 'oscTitleSuiteId', 'oscTitleBundleSha256', 'oscTitleContractsVersion']) {
        assert.equal(includesOscTitleEvidence('/nonexistent', {}, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v46', [key]: 'present' }), true, key);
    }
});
