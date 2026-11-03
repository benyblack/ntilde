import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { snapshotHash } from './agent-observer.mjs';
import { ownedReceiptFixture as fixture, runId } from './fixtures/editing-receipt.mjs';
test('v4 receipt binds both independently rederived reports after cleanup', () => fixture(f => {
    const receipt = f.finish(); assert.equal(receipt.schemaVersion, 4); assert.equal(receipt.fixtureRemoved, true);
    assert.deepEqual(receipt.eraseEvidence.counts, { passed: 13 }); assert.deepEqual(receipt.editingEvidence.counts, { passed: 22 });
}));
for (const [name, mutate] of [
    ['missing new binding', f => { delete f.evidence.editingEvidence; }],
    ['missing prior binding', f => { delete f.evidence.pixelEvidence; }],
    ['missing companion suite', f => { delete f.context.eraseSuiteId; f.write('context.json', f.context); }],
    ['both v4 identities removed', f => { delete f.context.eraseSuiteId; delete f.context.editingSuiteId; f.write('context.json', f.context); }],
    ['missing prior suite identity', f => { delete f.context.colorSuiteId; f.write('context.json', f.context); }],
    ['changed journal', f => { writeFileSync(join(f.output, 'erase-snapshots.jsonl'), '[]'); }],
    ['forged receipt counts', f => { f.evidence.editingEvidence.counts = { passed: 23 }; }],
    ['forged report with updated digest', f => {
        const value = JSON.parse(readFileSync(join(f.output, 'erase-observations.json')));
        value.observations[0].result = 'failed'; f.write('erase-observations.json', value); f.evidence.eraseEvidence.sha256 = snapshotHash(value);
    }],
    ['changed cleanup', f => { f.write('ownership-proof.json', { completed: true, cleanedUpAt: '2026-11-03', cleanupError: 'worker still alive' }); }],
]) test('v4 receipt refuses ' + name, () => fixture(f => { mutate(f); assert.throws(f.finish); }));
function unavailable(f, prefix) {
    const binding = f.evidence[prefix + 'Evidence'];
    const value = { runId, suiteId: binding.suiteId, status: 'unavailable', reason: 'screenDetailsUnavailable', fixtureStarted: false, plannedExperiments: prefix === 'erase' ? 13 : 22 };
    binding.file = prefix + '-unavailable.json'; binding.sha256 = snapshotHash(value);
    Object.assign(binding, { status: 'unavailable', reason: value.reason, counts: {} });
    f.write(binding.file, value); writeFileSync(join(f.output, prefix + '-snapshots.jsonl'), ''); return value;
}
test('v4 permits only typed pre-fixture unavailability with empty journals', () => fixture(f => {
    unavailable(f, 'erase'); unavailable(f, 'editing'); assert.equal(f.finish().schemaVersion, 4);
}));
for (const [name, mutate] of [
    ['started fixture', value => { value.fixtureStarted = true; }],
    ['invented reason', value => { value.reason = 'missingBufferState'; }],
    ['wrong planned count', value => { value.plannedExperiments = 0; }],
]) test('v4 refuses unavailable ' + name, () => fixture(f => {
    const value = unavailable(f, 'erase'); mutate(value); f.write('erase-unavailable.json', value);
    f.evidence.eraseEvidence.sha256 = snapshotHash(value); assert.throws(f.finish, /unavailable/);
}));
test('v4 unavailable cannot hide a partial journal', () => fixture(f => {
    unavailable(f, 'erase'); writeFileSync(join(f.output, 'erase-snapshots.jsonl'), '{}'); assert.throws(f.finish, /unavailable/);
}));
