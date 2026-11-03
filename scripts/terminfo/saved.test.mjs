import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { buildSavedReport } from './saved.mjs';
import { syntheticSavedJournal } from './fixtures/saved-journal.mjs';
import { snapshotHash } from './agent-observer.mjs';
const runId = 'a'.repeat(32);
function coherent(frame, mutate) {
    mutate(frame.screen); frame.afterScreen = structuredClone(frame.screen);
    frame.sha256 = snapshotHash(frame.screen); frame.afterSha256 = frame.sha256;
}
test('nine source-known staged cases retain all 57 paired frames and qualify complete active facts', () => {
    const report = buildSavedReport(syntheticSavedJournal(), runId);
    assert.equal(report.inputs.length, 9); assert.equal(report.snapshots.length, 57);
    assert.ok(report.observations.every(item => item.result === 'passed'));
    assert.equal(report.observations[3].assertions.length, 4);
    assert.equal(report.snapshots.find(frame => frame.caseId === 'saved.cursor.main-1048' && frame.stageId === 'restored').screen.bufferState.pendingWrap, true);
});
for (const [name, mutate] of [
    ['missing stage', values => values.pop()],
    ['reordered stage', values => { values[4].stageId = 'invented'; }],
    ['another pane', values => { values[4].paneId = '22222222-2222-2222-2222-222222222222'; }],
    ['unpaired state', values => { values[4].afterScreen.cursorCol++; }],
    ['forged hash', values => { values[4].sha256 = '0'.repeat(64); }],
    ['missing barrier', values => { values[4].barrier = null; }],
    ['error-bearing frame', values => { values[4].error = 'failed observation'; }],
    ['wrong nonce', values => coherent(values[1], screen => { screen.lines[0] = 'NTILDE_SV_' + 'b'.repeat(32); })],
    ['failed fixture restoration', values => coherent(values.at(-1), screen => { screen.cursorCol = 1; })],
]) test('saved report rejects ' + name, () => {
    const frames = syntheticSavedJournal(); mutate(frames); assert.throws(() => buildSavedReport(frames, runId));
});
test('a coherent target mismatch stays a negative measurement, with its passing control retained', () => {
    const frames = syntheticSavedJournal(); coherent(frames[3], screen => { screen.lines[2] = 'wrong'; });
    const report = buildSavedReport(frames, runId);
    assert.equal(report.observations[0].result, 'failed'); assert.equal(report.observations[0].control.passed, true);
});
test('a coherent failed control remains inconclusive', () => {
    const frames = syntheticSavedJournal(); coherent(frames[2], screen => { screen.cursorCol++; });
    const report = buildSavedReport(frames, runId); assert.equal(report.observations[0].result, 'inconclusive');
});
test('cursor presentation is independently compared rather than inferred from its position', () => {
    const frames = syntheticSavedJournal(); coherent(frames[3], screen => { screen.cursorMode.blink = false; });
    assert.equal(buildSavedReport(frames, runId).observations[0].result, 'failed');
});

test('published committed-source saved evidence rederives eight passes and the controlled RIS negative', () => {
    const data = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-03-saved-state.json', import.meta.url)));
    const value = data.savedReport;
    assert.deepEqual(buildSavedReport(value.snapshots, data.context.runId), value);
    assert.equal(value.snapshots.length, 57);
    assert.equal(value.inputs.length, 9);
    assert.ok(value.observations.every(item => item.control.passed));
    assert.deepEqual(value.observations.map(item => item.result), [...Array(8).fill('passed'), 'failed']);
    assert.equal(value.observations.at(-1).featureId, 'saved.reset.full-both-banks');
    assert.deepEqual(value.observations.at(-1).assertions.map(item => item.passed), [false, true, true, true, true]);
    assert.deepEqual(value.snapshots[51].screen.lines, ['M', ...Array(39).fill('')]);
    assert.deepEqual(value.snapshots[0].screen, value.snapshots.at(-1).screen);
    assert.ok(value.snapshots.every(frame => frame.paneId === data.savedOwnership.paneId &&
        frame.screen.rows === data.savedOwnership.geometry.rows && frame.screen.cols === data.savedOwnership.geometry.cols));
    assert.deepEqual(data.savedOwnership.geometry, { rows: 40, cols: 140 });
    assert.equal(data.receipt.schemaVersion, 7);
    assert.equal(data.receipt.fixtureRemoved, true);
    assert.deepEqual(data.receipt.savedEvidence.counts, { passed: 8, failed: 1 });
    const bytes = JSON.stringify(value, null, 2) + '\n';
    assert.equal(Buffer.byteLength(bytes), 417479);
    assert.equal(data.receipt.savedEvidence.sha256, createHash('sha256').update(bytes).digest('hex'));
    assert.equal(data.appBuildSource, data.adapterSource);
    assert.equal(data.appBuildSource, '4878d7abc63c1dc9f7efaeda79b075b0e3f72d01');
    assert.equal(data.context.appVersionSource, 'operator supplied; not independently verified');
    assert.deepEqual(data.stockCollector.counts, { supported: 78, inconclusive: 177 });
    assert.equal(data.stockCollector.suiteComplete, false);
    assert.ok(data.stockCollector.ungradedDiagnostics['cursor.reverse-wrap']);
});
