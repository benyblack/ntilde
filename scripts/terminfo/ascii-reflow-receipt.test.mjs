import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { copyFileSync, mkdirSync, mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { syntheticReflowJournal } from './fixtures/reflow-journal.mjs';
import { adaptReflowCollector, asciiReflowAdapterId, asciiReflowSeams } from './ascii-reflow-grading-collector.mjs';
import { buildReflowReport, reflowCounts } from './ascii-reflow-grading.mjs';
import { asciiReflowSuiteId } from './ascii-reflow-contracts.mjs';
import { asciiReflowSources, asciiReflowPrefixFiles, includesReflowEvidence, checkReflowEvidence } from './ascii-reflow-receipt.mjs';

const hash = value => createHash('sha256').update(value).digest('hex');
const policy = 'readonly-owned-source-graph-v52', ownedPolicy = 'parent-selected App resize in proved private pane; independent kernel geometry and -opost; two ASCII reflow experiments on one disposable main screen';
const suiteRoot = fileURLToPath(new URL('.', import.meta.url));

function fixture(action) {
    const dir = mkdtempSync(join(tmpdir(), 'nt-reflow-receipt-'));
    try {
        const old = Buffer.from(asciiReflowSeams.join('\n/* gap */\n') + '\n');
        const adapter = Buffer.from(adaptReflowCollector(old));
        const e = syntheticReflowJournal();
        e.adapter.bundleSha256 = hash(adapter);
        const run = Buffer.from(JSON.stringify(e.run) + '\n');
        const journal = Buffer.from(e.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
        const actions = Buffer.from(e.actions.map(action => JSON.stringify(action)).join('\n') + '\n');
        const report = Buffer.from(JSON.stringify(buildReflowReport(e.frames, e.actions, e.run, e.runId, e.adapter)) + '\n');
        for (const [name, bytes] of [['collector-ost.mjs', old], ['collector-rfl.mjs', adapter], ['rf-run.json', run],
            ['reflow-snapshots.jsonl', journal], ['reflow-actions.jsonl', actions], ['reflow-observations.json', report]]) writeFileSync(join(dir, name), bytes);
        mkdirSync(join(dir, 'sources'));
        const settingsBytes = Buffer.from(JSON.stringify(settings()) + '\n');
        const context = { sourceSnapshotPolicy: policy, initialSettingsSha256: hash(settingsBytes),
            asciiReflowAdapterId, asciiReflowSuiteId, asciiReflowContractsVersion: 1, ownedAsciiReflowPolicy: ownedPolicy,
            oscTitleBundleSha256: hash(old), asciiReflowBundleSha256: hash(adapter), upstreamBundleSha256: 'a'.repeat(64) };
        for (const [key, file] of Object.entries(asciiReflowSources)) {
            copyFileSync(join(suiteRoot, file), join(dir, 'sources', file));
            context[key] = hash(readFileSync(join(dir, 'sources', file)));
        }
        writeFileSync(join(dir, 'initial-settings.json'), settingsBytes);
        const proof = { proofs: [{ terminalOutput: { tool: '/bin/stty', outputProcessing: false, description: '40 rows; 140 columns;\n-opost' },
            asciiReflow: { policy: ownedPolicy, actions: actions.toString().trim().split('\n').map(line => JSON.parse(line)), paneId: e.frames[0].paneId,
                originalGeometry: { rows: 40, cols: 140 } } }] };
        const evidence = { reflowEvidence: { runId: e.runId, suiteId: asciiReflowSuiteId, contractsVersion: 1,
            file: 'reflow-observations.json', runFile: 'rf-run.json', provisionalUntil: 'cell-receipt.json',
            adapterBundleSha256: hash(adapter), sha256: hash(report), runSha256: hash(run),
            counts: reflowCounts(JSON.parse(report.toString())) } };
        const f = { dir, e, context, evidence, proof };
        action(f);
    } finally { rmSync(dir, { recursive: true, force: true }); }
}
function settings() {
    return { AgentAccessObserveEnabled: true, AgentAccessActEnabled: true, SessionPersistence: 'Off', Profiles: [{ Type: 0 }] };
}

test('receipt rederives journals, actions, adapter derivation and refuses substitution and tampering', () => fixture(f => {
    const check = () => checkReflowEvidence(f.dir, f.evidence.reflowEvidence, f.e.runId, f.context, f.proof);
    assert.equal(includesReflowEvidence(f.dir, f.evidence, f.context), true);
    assert.doesNotThrow(check);
    const path = join(f.dir, 'reflow-actions.jsonl'), original = readFileSync(path);
    writeFileSync(path, original.toString().replace('"stageId":"after"', '"stageId":"restored"'));
    assert.throws(check, /action/);
    writeFileSync(path, original);
    f.context.asciiReflowBundleSha256 = 'a'.repeat(64);
    assert.throws(check, /derivation|Changed|hash/);
}));

test('receipt refuses a symlinked artifact', { skip: process.platform === 'win32' }, () => fixture(f => {
    const run = join(f.dir, 'rf-run.json'); rmSync(run); symlinkSync(join(f.dir, 'reflow-snapshots.jsonl'), run);
    assert.throws(() => checkReflowEvidence(f.dir, f.evidence.reflowEvidence, f.e.runId, f.context, f.proof), /regular bounded file/);
}));

test('typed initial unavailability is retained, while partial artifacts cannot be hidden', () => fixture(f => {
    const marker = { schemaVersion: 1, runId: f.e.runId, suiteId: asciiReflowSuiteId, status: 'unavailable', reason: 'asciiReflowGeometryUnavailable', fixtureStarted: false, plannedExperiments: 2 };
    const bytes = Buffer.from(JSON.stringify(marker) + '\n');
    for (const name of ['rf-run.json', 'reflow-observations.json']) rmSync(join(f.dir, name), { force: true });
    for (const name of ['reflow-snapshots.jsonl', 'reflow-actions.jsonl']) writeFileSync(join(f.dir, name), '');
    writeFileSync(join(f.dir, 'reflow-unavailable.json'), bytes);
    Object.assign(f.evidence.reflowEvidence, { file: 'reflow-unavailable.json', status: 'unavailable', reason: marker.reason,
        counts: {}, sha256: hash(bytes) });
    delete f.evidence.reflowEvidence.runFile; delete f.evidence.reflowEvidence.runSha256;
    f.proof.proofs[0].asciiReflow = { policy: ownedPolicy, actions: [] };
    assert.doesNotThrow(() => checkReflowEvidence(f.dir, f.evidence.reflowEvidence, f.e.runId, f.context, f.proof));
    // A retained completion artifact under an unavailable binding must refuse.
    writeFileSync(join(f.dir, 'rf-run.json'), '{"schemaVersion":1}');
    assert.throws(() => checkReflowEvidence(f.dir, f.evidence.reflowEvidence, f.e.runId, f.context, f.proof), /completion artifacts/);
    rmSync(join(f.dir, 'rf-run.json'));
    // A completion-only binding field on an unavailable run must refuse.
    f.evidence.reflowEvidence.runFile = 'rf-run.json';
    assert.throws(() => checkReflowEvidence(f.dir, f.evidence.reflowEvidence, f.e.runId, f.context, f.proof), /regular bounded file|Changed|Invalid|JSON/);
    delete f.evidence.reflowEvidence.runFile;
    // Partial journal bytes cannot be hidden either.
    writeFileSync(join(f.dir, 'reflow-snapshots.jsonl'), 'partial');
    assert.throws(() => checkReflowEvidence(f.dir, f.evidence.reflowEvidence, f.e.runId, f.context, f.proof));
}));

test('each partial declaration requires the suffix even under an older policy', () => {
    for (const key of ['asciiReflowAdapterId', 'asciiReflowSuiteId', 'asciiReflowBundleSha256', 'asciiReflowContractsVersion', 'ownedAsciiReflowPolicy', ...Object.keys(asciiReflowSources)]) {
        assert.equal(includesReflowEvidence('/nonexistent', {}, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v51', [key]: 'present' }), true, key);
    }
    assert.equal(includesReflowEvidence('/nonexistent', { reflowEvidence: {} }, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v51' }), true);
    assert.equal(includesReflowEvidence('/nonexistent', {}, { sourceSnapshotPolicy: policy }), true);
    assert.equal(includesReflowEvidence('/nonexistent', {}, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v51' }), false);
});

test('the runner creates every journal the receipt reads before any reflow request, so a typed refusal can still finalize', () => {
    const runner = readFileSync(fileURLToPath(new URL('../terminfo-owned-runner.mjs', import.meta.url)), 'utf8');
    for (const name of ['reflow-snapshots.jsonl', 'reflow-actions.jsonl'])
        assert.ok(runner.includes(`writeFileSync(join(output, '${name}'), '', { flag: 'wx', mode: 0o600 });`), name);
});
