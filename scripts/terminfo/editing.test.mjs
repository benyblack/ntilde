import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { buildEditingReport } from './editing.mjs';
import { syntheticEditingJournal } from './fixtures/editing-journal.mjs';
import { snapshotHash } from './agent-observer.mjs';
const runId = 'a'.repeat(32);
function coherent(frame, mutate) {
    mutate(frame.screen); frame.sha256 = snapshotHash(frame.screen);
    frame.afterScreen = structuredClone(frame.screen); frame.afterSha256 = frame.sha256;
}
for (const prefix of ['erase', 'editing']) test(`logical ${prefix} report retains exact independent inputs and all observed facts`, () => {
    const frames = syntheticEditingJournal(prefix, runId), report = buildEditingReport(frames, runId, prefix);
    assert.equal(report.observations.length, prefix === 'erase' ? 13 : 22);
    assert.ok(report.observations.every(value => value.result === 'passed'));
    assert.ok(report.inputs.every(value => value.before.history.lines.length === 2));
    assert.ok(report.inputs.every(value => !value.setup.includes('\x1b[2J') && !value.setup.includes('\x1b[3J')));
    assert.equal(report.snapshots[0].sha256, report.snapshots.at(-1).sha256);
    assert.ok(report.inputs.every(value => value.before.expectedAttributeRuns.length < 8));
});
for (const [name, mutate] of [
    ['missing snapshot', frames => frames.pop()], ['another pane', frames => { frames[2].paneId = '22222222-2222-2222-2222-222222222222'; }],
    ['changed case order', frames => { frames[2].caseId = 'invented'; }], ['changed stage', frames => { frames[2].stageId = 'after'; }],
    ['missing fence', frames => { frames[2].barrier = null; }], ['missing modes', frames => { delete frames[2].screen.bufferState; }],
    ['unknown history count', frames => { frames[2].screen.bufferState.scrollbackLines = null; }],
    ['truncated history', frames => { frames[2].screen.history.lines.pop(); }],
    ['changed after cursor', frames => { frames[2].afterScreen.cursorCol++; }],
    ['changed after hash', frames => { frames[2].afterSha256 = 'b'.repeat(64); }],
    ['changed nonce screen mode', frames => coherent(frames[1], screen => { screen.bufferState.alternateScreen = false; })],
    ['unrestored history', frames => coherent(frames.at(-1), screen => { screen.bufferState.scrollbackLines = 1; screen.history = { startLine: 0, totalLines: 1, lines: ['LEFT'] }; })],
]) test('editing report refuses ' + name, () => {
    const frames = syntheticEditingJournal('erase', runId); mutate(frames); assert.throws(() => buildEditingReport(frames, runId, 'erase'));
});
test('valid but wrong before controls remain inconclusive rather than unsupported', () => {
    const frames = syntheticEditingJournal('erase', runId);
    coherent(frames[2], screen => { screen.history.lines[0] = 'UNKNOWN'; });
    const report = buildEditingReport(frames, runId, 'erase'); assert.equal(report.observations[0].result, 'inconclusive');
});
test('valid wrong post-state with qualified controls is a failed local comparison', () => {
    const frames = syntheticEditingJournal('editing', runId);
    coherent(frames[3], screen => { screen.lines[2] = 'wrong'; });
    const report = buildEditingReport(frames, runId, 'editing'); assert.equal(report.observations[0].result, 'failed');
    assert.equal(report.observations[0].controls[0].passed, true);
});

test('published before/after runs rederive exact reports and preserve the three original negatives', () => {
    const data = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-03-editing.json', import.meta.url)));
    for (const phase of ['before', 'after']) {
        const run = data[phase];
        assert.equal(run.receipt.schemaVersion, 4); assert.equal(run.receipt.fixtureRemoved, true);
        assert.equal(run.appBuildSource, run.adapterSource);
        for (const prefix of ['erase', 'editing']) {
            const report = run[prefix + 'Report'], derived = buildEditingReport(report.snapshots, run.context.runId, prefix);
            assert.deepEqual(derived, report);
            assert.equal(run.receipt[prefix + 'Evidence'].sha256, createHash('sha256').update(JSON.stringify(derived, null, 2) + '\n').digest('hex'));
            assert.deepEqual(report.snapshots[0].screen, report.snapshots.at(-1).screen);
            assert.ok(report.observations.every(value => value.controls.every(control => control.passed)));
            assert.ok(report.inputs.every(value => value.before.history.totalLines === 2));
        }
        assert.deepEqual(run.stockCollector.counts, { inconclusive: 177, supported: 78 });
        assert.equal(run.stockCollector.suiteComplete, false); assert.ok(run.stockCollector.ungradedDiagnostics['cursor.reverse-wrap']);
    }
    const observations = run => [...run.eraseReport.observations, ...run.editingReport.observations];
    assert.deepEqual(observations(data.before).filter(value => value.result !== 'passed').map(value => value.featureId),
        ['erase.characters.0', 'editing.insert-lines.0', 'editing.delete-lines.0']);
    assert.equal(observations(data.after).length, 35); assert.ok(observations(data.after).every(value => value.result === 'passed'));
    assert.deepEqual(data.before.eraseReport.inputs, data.after.eraseReport.inputs);
    assert.deepEqual(data.before.editingReport.inputs, data.after.editingReport.inputs);
});
