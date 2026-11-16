import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { syntheticOsc633Evidence } from './fixtures/osc633-evidence.mjs';
import { adaptOsc633Collector, osc633Seams } from './osc633-grading-collector.mjs';
import { buildOsc633GradingReport, osc633Counts } from './osc633-grading.mjs';
import { checkOsc633Evidence, includesOsc633Evidence } from './osc633-receipt.mjs';
import { privateClipboardReceiptFixture } from './fixtures/private-clipboard-receipt.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const json = value => Buffer.from(JSON.stringify(value) + '\n');
function setup() {
    const output = mkdtempSync(join(tmpdir(), 'osc633-test-'));
    const predecessor = Buffer.from(osc633Seams.join('\n/* gap */\n'));
    const adapterBytes = Buffer.from(adaptOsc633Collector(predecessor));
    const synthetic = syntheticOsc633Evidence({ bundleSha256: hash(adapterBytes) });
    const derived = buildOsc633GradingReport(synthetic.frames, synthetic.run, synthetic.runId, synthetic.adapter);
    const reportBytes = json(derived), runBytes = json(synthetic.run);
    const write = (name, data) => writeFileSync(join(output, name), data);
    write('collector-shi.mjs', predecessor); write('collector-vs633.mjs', adapterBytes);
    write('vs633-observations.json', reportBytes); write('vs633-run.json', runBytes);
    write('vs633-snapshots.jsonl', synthetic.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
    const context = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v61', osc133BundleSha256: hash(predecessor),
        osc633AdapterId: derived.adapter.id, osc633SuiteId: derived.suiteId, osc633ContractsVersion: 1,
        osc633BundleSha256: hash(adapterBytes), upstreamBundleSha256: derived.adapter.upstreamBundleSha256 };
    const evidence = { osc133Evidence: {}, osc633Evidence: { runId: synthetic.runId, suiteId: derived.suiteId, contractsVersion: 1,
        provisionalUntil: 'cell-receipt.json', file: 'vs633-observations.json', sha256: hash(reportBytes), runFile: 'vs633-run.json',
        runSha256: hash(runBytes), adapterBundleSha256: hash(adapterBytes), counts: osc633Counts(derived) } };
    return { output, context, evidence, runId: synthetic.runId, write };
}

test('the receipt rederives complete synthetic evidence and rejects tampering', () => {
    const value = setup();
    try {
        assert.equal(includesOsc633Evidence(value.output, value.evidence, value.context), true);
        checkOsc633Evidence(value.output, value.evidence, value.runId, value.context);
        value.write('vs633-snapshots.jsonl', '{}\n');
        assert.throws(() => checkOsc633Evidence(value.output, value.evidence, value.runId, value.context));
    } finally { rmSync(value.output, { recursive: true, force: true }); }
});

test('adapter, predecessor and receipt identity are mandatory', () => {
    for (const mutate of [v => v.write('collector-vs633.mjs', 'modified'), v => v.write('collector-shi.mjs', 'modified'),
        v => { delete v.evidence.osc133Evidence; }, v => { v.evidence.osc633Evidence.runId = 'other'; },
        v => { v.evidence.osc633Evidence.counts = {}; }]) {
        const value = setup();
        try { mutate(value); assert.throws(() => checkOsc633Evidence(value.output, value.evidence, value.runId, value.context)); }
        finally { rmSync(value.output, { recursive: true, force: true }); }
    }
});

test('initial typed unavailability is admitted only without retained partial observations', () => {
    const value = setup();
    try {
        for (const name of ['vs633-observations.json', 'vs633-run.json', 'vs633-snapshots.jsonl']) rmSync(join(value.output, name));
        const marker = { schemaVersion: 1, runId: value.runId, suiteId: value.context.osc633SuiteId, status: 'unavailable', reason: 'paneMetadataUnavailable', plannedCases: 4 };
        const bytes = json(marker); value.write('vs633-unavailable.json', bytes);
        Object.assign(value.evidence.osc633Evidence, { status: marker.status, reason: marker.reason, file: 'vs633-unavailable.json', sha256: hash(bytes), counts: {} });
        checkOsc633Evidence(value.output, value.evidence, value.runId, value.context);
        value.write('vs633-snapshots.jsonl', '{}\n');
        assert.throws(() => checkOsc633Evidence(value.output, value.evidence, value.runId, value.context), /partial evidence/);
    } finally { rmSync(value.output, { recursive: true, force: true }); }
});

test('the shared receipt admits the synthetic suffix only with its predecessor and verified cleanup', async () => {
    await privateClipboardReceiptFixture(f => {
        const bytes = Buffer.from(adaptOsc633Collector(readFileSync(join(f.output, 'collector-shi.mjs')).toString()));
        const synthetic = syntheticOsc633Evidence({ runId: f.context.runId, bundleSha256: hash(bytes) });
        synthetic.adapter.upstreamBundleSha256 = f.context.upstreamBundleSha256;
        const report = buildOsc633GradingReport(synthetic.frames, synthetic.run, f.context.runId, synthetic.adapter);
        f.write('vs633-observations.json', report); f.write('vs633-run.json', synthetic.run);
        writeFileSync(join(f.output, 'vs633-snapshots.jsonl'), synthetic.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
        writeFileSync(join(f.output, 'collector-vs633.mjs'), bytes);
        Object.assign(f.context, { osc633AdapterId: report.adapter.id, osc633SuiteId: report.suiteId, osc633ContractsVersion: 1, osc633BundleSha256: hash(bytes) });
        f.evidence.osc633Evidence = { runId: f.context.runId, suiteId: report.suiteId, contractsVersion: 1, provisionalUntil: 'cell-receipt.json',
            file: 'vs633-observations.json', sha256: hash(readFileSync(join(f.output, 'vs633-observations.json'))),
            runFile: 'vs633-run.json', runSha256: hash(readFileSync(join(f.output, 'vs633-run.json'))), adapterBundleSha256: hash(bytes), counts: osc633Counts(report) };
        f.write('context.json', f.context);
        const receipt = f.finish();
        assert.deepEqual(receipt.osc633Evidence.counts, { 'inconclusive/unsupported': 4 });
        assert.equal(receipt.fixtureRemoved, true);
    }, { routed: true });
});
