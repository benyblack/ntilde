import test from 'node:test';
import assert from 'node:assert/strict';
import { writeFileSync, unlinkSync } from 'node:fs';
import { join } from 'node:path';
import { ownedResizeReceiptFixture, runId } from './fixtures/resize-receipt.mjs';
import { snapshotHash } from './agent-observer.mjs';
import { resizeSources } from './resize-receipt.mjs';
const fixture = ownedResizeReceiptFixture;
test('v6 binds all preceding suites, exact rederived resize actions and removed fixture', () => fixture(f => {
    const receipt = f.finish(); assert.equal(receipt.schemaVersion, 6); assert.equal(receipt.fixtureRemoved, true);
    assert.deepEqual(receipt.resizeEvidence.counts, { passed: 3 }); assert.deepEqual(receipt.historyEvidence.counts, { passed: 30 });
}));
test('v6 rejects a coherently errored action even when its observed facts, journals, proof and digest agree', () => fixture(f => {
    f.actions[0].error = 'Failed post-request proof.';
    f.write('resize-observations.json', f.report); f.write('ownership-proof.json', f.proof);
    writeFileSync(join(f.output, 'resize-actions.jsonl'), f.actions.map(value => JSON.stringify(value)).join('\n'));
    f.evidence.resizeEvidence.sha256 = snapshotHash(f.report);
    assert.throws(f.finish);
}));
for (const [name, mutate] of [
    ['missing resize binding', f => { delete f.evidence.resizeEvidence; }],
    ['missing suite identity', f => { delete f.context.resizeSuiteId; f.write('context.json', f.context); }],
    ['missing output policy', f => { delete f.context.ownedResizePolicy; f.write('context.json', f.context); }],
    ['missing earlier history', f => { delete f.evidence.historyEvidence; }],
    ['missing earlier editing', f => { delete f.evidence.editingEvidence; }],
    ['missing earlier pixel', f => { delete f.evidence.pixelEvidence; }],
    ['missing parent declaration', f => { delete f.proof.proofs[0].resize; f.write('ownership-proof.json', f.proof); }],
    ['changed parent actions', f => { f.proof.proofs[0].resize.actions[0].observed.app.exact = false; f.write('ownership-proof.json', f.proof); }],
    ['changed action stream', f => { writeFileSync(join(f.output, 'resize-actions.jsonl'), '[]'); }],
    ['changed snapshot stream', f => { writeFileSync(join(f.output, 'resize-snapshots.jsonl'), '[]'); }],
    ['forged counts', f => { f.evidence.resizeEvidence.counts = { passed: 2 }; }],
    ['changed source', f => { writeFileSync(join(f.output, 'sources', 'resize-cases.mjs'), 'altered'); }],
    ['missing source', f => { unlinkSync(join(f.output, 'sources', 'resize-action.mjs')); }],
    ['wrong original geometry', f => { f.proof.proofs[0].resize.originalGeometry.cols++; f.write('ownership-proof.json', f.proof); }],
    ['different common cell pane', f => { const cell = { snapshots: [{ paneId: '22222222-2222-2222-2222-222222222222', screen: { rows: 12, cols: 60 } }] }; f.write('cell-observations.json', cell); f.evidence.sha256 = snapshotHash(cell); }],
    ['act disabled with updated settings digest', f => {
        const settings = { AgentAccessObserveEnabled: true, AgentAccessActEnabled: false, SessionPersistence: 'Off', Profiles: [{ Type: 0 }] };
        f.write('initial-settings.json', settings); f.context.initialSettingsSha256 = snapshotHash(settings); f.write('context.json', f.context);
    }],
    ['forged report with updated digest', f => { f.report.observations[0].result = 'failed'; f.write('resize-observations.json', f.report); f.evidence.resizeEvidence.sha256 = snapshotHash(f.report); }],
]) test('v6 refuses ' + name, () => fixture(f => { mutate(f); assert.throws(f.finish); }));
function strip(f) {
    delete f.evidence.resizeEvidence; delete f.context.resizeSuiteId; delete f.context.ownedResizePolicy; delete f.context.initialSettingsSha256;
    for (const key of Object.keys(resizeSources)) delete f.context[key]; f.write('context.json', f.context);
}
test('parent resize declaration prevents downgrade after context and binding stripping', () => fixture(f => { strip(f); assert.throws(f.finish); }));
test('retained action streams prevent downgrade after all context and parent markers are stripped', () => fixture(f => {
    strip(f); delete f.proof.proofs[0].resize; f.write('ownership-proof.json', f.proof); assert.throws(f.finish);
}));
function unavailable(f, reason) {
    const value = { schemaVersion: 1, runId, suiteId: f.context.resizeSuiteId, status: 'unavailable', reason, fixtureStarted: false, plannedExperiments: 3 };
    f.write('resize-unavailable.json', value); writeFileSync(join(f.output, 'resize-snapshots.jsonl'), ''); writeFileSync(join(f.output, 'resize-actions.jsonl'), '');
    f.proof.proofs[0].resize.actions = [];
    delete f.proof.proofs[0].resize.paneId; delete f.proof.proofs[0].resize.originalGeometry;
    f.write('ownership-proof.json', f.proof);
    Object.assign(f.evidence.resizeEvidence, { file: 'resize-unavailable.json', sha256: snapshotHash(value), status: 'unavailable', reason, counts: {} });
    return value;
}
for (const reason of ['screenDetailsUnavailable', 'resizeGeometryUnavailable']) test('v6 retains initial ' + reason + ' with empty streams', () => fixture(f => {
    unavailable(f, reason); assert.equal(f.finish().schemaVersion, 6);
}));
for (const [key, value] of [['paneId', '11111111-1111-1111-1111-111111111111'], ['originalGeometry', { rows: 12, cols: 60 }]]) {
    test('v6 refuses unavailable evidence with retained parent ' + key, () => fixture(f => {
        unavailable(f, 'resizeGeometryUnavailable'); f.proof.proofs[0].resize[key] = value;
        f.write('ownership-proof.json', f.proof); assert.throws(f.finish);
    }));
}
for (const [name, mutate] of [
    ['started fixture', value => { value.fixtureStarted = true; }],
    ['different count', value => { value.plannedExperiments = 0; }],
    ['unrecognized refusal', value => { value.reason = 'timeout'; }],
]) test('v6 refuses unavailable ' + name, () => fixture(f => {
    const value = unavailable(f, 'resizeGeometryUnavailable'); mutate(value); f.write('resize-unavailable.json', value);
    f.evidence.resizeEvidence.sha256 = snapshotHash(value); assert.throws(f.finish);
}));
