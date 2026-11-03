import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { snapshotHash } from './agent-observer.mjs';
import { ownedReceiptFixture, runId } from './fixtures/editing-receipt.mjs';
const fixture = action => ownedReceiptFixture(action, { history: true });
test('v5 receipt binds the rederived complete history plan and parent-observed output mode', () => fixture(f => {
    const receipt = f.finish(); assert.equal(receipt.schemaVersion, 5); assert.equal(receipt.fixtureRemoved, true);
    assert.deepEqual(receipt.historyEvidence.counts, { passed: 30 });
}));
for (const [name, mutate] of [
    ['missing binding', f => { delete f.evidence.historyEvidence; }],
    ['missing v5 identity', f => { delete f.context.historySuiteId; f.write('context.json', f.context); }],
    ['history ID and binding stripped with output policy retained', f => { delete f.context.historySuiteId; delete f.evidence.historyEvidence; f.write('context.json', f.context); }],
    ['history ID binding and policy stripped with v5 source retained', f => { delete f.context.historySuiteId; delete f.context.terminalOutput; delete f.evidence.historyEvidence; f.context.historySuiteSourceSha256 = 'a'.repeat(64); f.write('context.json', f.context); }],
    ['context markers and history binding stripped with measured output proof retained', f => { delete f.context.historySuiteId; delete f.context.terminalOutput; delete f.evidence.historyEvidence; f.write('context.json', f.context); }],
    ['missing output policy', f => { delete f.context.terminalOutput; f.write('context.json', f.context); }],
    ['both v4 identities removed', f => { delete f.context.eraseSuiteId; delete f.context.editingSuiteId; f.write('context.json', f.context); }],
    ['earlier suite missing', f => { delete f.evidence.editingEvidence; }],
    ['earlier suite identity missing', f => { delete f.context.pixelSuiteId; f.write('context.json', f.context); }],
    ['changed journal', f => { writeFileSync(join(f.output, 'history-snapshots.jsonl'), '[]'); }],
    ['forged counts', f => { f.evidence.historyEvidence.counts = { passed: 29 }; }],
    ['forged report with updated digest', f => {
        const value = JSON.parse(readFileSync(join(f.output, 'history-observations.json'))); value.observations[0].result = 'failed';
        f.write('history-observations.json', value); f.evidence.historyEvidence.sha256 = snapshotHash(value);
    }],
]) test('v5 refuses ' + name, () => fixture(f => { mutate(f); assert.throws(f.finish); }));
for (const [name, mutate] of [
    ['absent', proof => { delete proof.proofs; }],
    ['output processing enabled', proof => { proof.proofs[0].terminalOutput.outputProcessing = true; }],
    ['wrong tool', proof => { proof.proofs[0].terminalOutput.tool = 'stty'; }],
    ['missing measured flag', proof => { proof.proofs[0].terminalOutput.description = 'oflags: opost onlcr'; }],
    ['missing timestamp', proof => { delete proof.proofs[0].terminalOutput.verifiedAt; }],
    ['mismatched kernel geometry', proof => { proof.proofs[0].terminalOutput.description = 'speed 9600 baud; 11 rows; 48 columns;\noflags: -opost onlcr'; }],
]) test('v5 refuses parent output proof ' + name, () => fixture(f => {
    mutate(f.proof); f.write('ownership-proof.json', f.proof); assert.throws(f.finish);
}));
function unavailable(f) {
    const binding = f.evidence.historyEvidence;
    const value = { runId, suiteId: binding.suiteId, status: 'unavailable', reason: 'screenDetailsUnavailable', fixtureStarted: false, plannedExperiments: 30 };
    Object.assign(binding, { file: 'history-unavailable.json', sha256: snapshotHash(value), status: 'unavailable', reason: value.reason, counts: {} });
    f.write(binding.file, value); writeFileSync(join(f.output, 'history-snapshots.jsonl'), ''); return value;
}
test('v5 records only initial typed history unavailability with an empty journal', () => fixture(f => {
    unavailable(f); assert.equal(f.finish().schemaVersion, 5);
}));
for (const [name, mutate] of [
    ['partial journal', f => { writeFileSync(join(f.output, 'history-snapshots.jsonl'), '{}'); }],
    ['started fixture', (f, value) => { value.fixtureStarted = true; }],
    ['changed planned count', (f, value) => { value.plannedExperiments = 0; }],
]) test('v5 refuses unavailable ' + name, () => fixture(f => {
    const value = unavailable(f); mutate(f, value); f.write('history-unavailable.json', value);
    f.evidence.historyEvidence.sha256 = snapshotHash(value); assert.throws(f.finish);
}));
