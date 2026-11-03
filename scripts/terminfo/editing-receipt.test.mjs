import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { snapshotHash } from './agent-observer.mjs';
import { syntheticEditingJournal } from './fixtures/editing-journal.mjs';
import { buildEditingReport } from './editing.mjs';
import { completeOwnedReceipt } from './receipt.mjs';
import { pixelSuiteId } from './pixels-cases.mjs';
import { colorSuiteId } from './color-cases.mjs';
import { eraseSuiteId, editingSuiteId } from './editing-cases.mjs';
const runId = 'a'.repeat(32);
function fixture(action) {
    const output = mkdtempSync(join(tmpdir(), 'nt-edit-receipt-'));
    const write = (file, value) => writeFileSync(join(output, file), JSON.stringify(value));
    const context = { runId, pixelSuiteId, colorSuiteId, eraseSuiteId, editingSuiteId }, evidence = {};
    try {
        write('context.json', context); write('raw.json', {});
        write('ownership-proof.json', { completed: true, cleanedUpAt: '2026-11-03', cleanupError: null });
        for (const prefix of ['cell', 'sgr', 'rgb', 'rendition', 'cursor', 'pixel', 'color']) {
            let value = {}, suiteId = prefix;
            const optional = prefix === 'pixel' || prefix === 'color';
            if (optional) {
                suiteId = prefix === 'pixel' ? pixelSuiteId : colorSuiteId;
                value = { runId, suiteId, status: 'unavailable', reason: 'screenDetailsUnavailable', fixtureStarted: false };
            }
            const file = prefix + (optional ? '-unavailable.json' : '-observations.json');
            write(file, value);
            const binding = { runId, suiteId, file, sha256: snapshotHash(value) };
            if (prefix === 'cell') Object.assign(evidence, binding); else evidence[prefix + 'Evidence'] = binding;
        }
        for (const prefix of ['erase', 'editing']) {
            const report = buildEditingReport(syntheticEditingJournal(prefix, runId), runId, prefix);
            write(prefix + '-observations.json', report);
            writeFileSync(join(output, prefix + '-snapshots.jsonl'), report.snapshots.map(value => JSON.stringify(value)).join('\n'));
            evidence[prefix + 'Evidence'] = { runId, suiteId: report.suiteId, file: prefix + '-observations.json', sha256: snapshotHash(report), counts: { passed: report.observations.length } };
        }
        action({ output, context, evidence, write, finish: () => completeOwnedReceipt(output, join(output, 'removed'), evidence) });
    } finally { rmSync(output, { recursive: true, force: true }); }
}
test('v4 receipt binds both independently rederived reports after cleanup', () => fixture(f => {
    const receipt = f.finish(); assert.equal(receipt.schemaVersion, 4); assert.equal(receipt.fixtureRemoved, true);
    assert.deepEqual(receipt.eraseEvidence.counts, { passed: 13 }); assert.deepEqual(receipt.editingEvidence.counts, { passed: 22 });
}));
for (const [name, mutate] of [
    ['missing new binding', f => { delete f.evidence.editingEvidence; }],
    ['missing prior binding', f => { delete f.evidence.pixelEvidence; }],
    ['missing companion suite', f => { delete f.context.eraseSuiteId; f.write('context.json', f.context); }],
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
