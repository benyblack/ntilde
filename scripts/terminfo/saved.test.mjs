import test from 'node:test';
import assert from 'node:assert/strict';
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
