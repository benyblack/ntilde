import assert from 'node:assert/strict';
import { test } from 'node:test';
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
