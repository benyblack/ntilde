import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { buildResizeReport } from './resize.mjs';
import { syntheticResizeJournal } from './fixtures/resize-journal.mjs';
import { snapshotHash } from './agent-observer.mjs';
const runId = 'a'.repeat(32);
const report = value => buildResizeReport(value.frames, value.actions, runId);
function change(frame, mutate) {
    mutate(frame.screen); frame.afterScreen = structuredClone(frame.screen);
    frame.sha256 = snapshotHash(frame.screen); frame.afterSha256 = frame.sha256;
}
test('complete synthetic evidence derives all three plans and compares full smaller/restored states', () => {
    const result = report(syntheticResizeJournal());
    assert.equal(result.complete, true); assert.equal(result.inputs.length, 3);
    assert.equal(result.snapshots.length, 12); assert.equal(result.actions.length, 6);
    assert.deepEqual(result.observations.map(value => value.result), ['passed', 'passed', 'passed']);
    assert.equal(result.inputs[2].after.history.totalLines, 16);
    assert.deepEqual(result.observations[2].snapshots, [8, 9, 10]);
    assert.deepEqual(result.observations[2].actions, [4, 5]);
});
for (const error of ['Failed post-request proof.', null, false]) test('an error field cannot qualify an otherwise valid successful action: ' + JSON.stringify(error), () => {
    const value = syntheticResizeJournal(); value.actions[0].error = error;
    assert.throws(() => report(value));
});
for (const [name, mutate] of [
    ['missing frame', value => value.frames.pop()],
    ['missing action', value => value.actions.pop()],
    ['wrong frame hash', value => { value.frames[3].sha256 = '0'.repeat(64); }],
    ['unpaired state', value => { value.frames[3].afterScreen.cursorCol++; }],
    ['reordered stage', value => { value.frames[3].stageId = 'restored'; }],
    ['reordered action', value => { value.actions.reverse(); }],
    ['unplanned target', value => { value.actions[0].target.cols++; }],
    ['unsettled App facts', value => { value.actions[0].observed.app.layoutSettled = false; }],
    ['inexact App facts', value => { value.actions[0].observed.app.exact = false; }],
    ['different App grid', value => { value.actions[0].observed.app.cols++; }],
    ['different kernel grid', value => { value.actions[0].observed.terminalOutput.description = '12 rows; 54 columns;\n-opost'; }],
    ['translated output', value => { value.actions[0].observed.terminalOutput.description += ' opost'; }],
    ['another tool', value => { value.actions[0].observed.terminalOutput.tool = 'stty'; }],
    ['another launch', value => { value.actions[0].observed.runId = 'b'.repeat(32); }],
    ['another action pane', value => { value.actions[0].observed.paneId = '22222222-2222-2222-2222-222222222222'; }],
    ['missing timestamp', value => { delete value.actions[0].observed.observedAt; }],
    ['wrong action link', value => { value.frames[3].actionIndex = 1; }],
    ['missing ordering fence', value => { value.frames[3].barrier = null; }],
    ['wrong nonce', value => { change(value.frames[1], screen => { screen.lines[0] = 'NTILDE_RZ_' + 'b'.repeat(32); }); }],
    ['failed global restoration', value => { change(value.frames.at(-1), screen => { screen.cursorCol = 1; }); }],
]) test(name + ' cannot qualify a complete resize report', () => {
    const value = syntheticResizeJournal(); mutate(value); assert.throws(() => report(value));
});
test('a coherent observed mismatch is a negative measurement with full controls retained', () => {
    const value = syntheticResizeJournal(); change(value.frames[3], screen => { screen.lines[0] = 'WRONG'; });
    const result = report(value);
    assert.equal(result.observations[0].result, 'failed');
    assert.equal(result.observations[0].controls[0].passed, true);
    assert.equal(result.observations[1].result, 'passed');
});
test('a failed before-control remains inconclusive even when the after state matches', () => {
    const value = syntheticResizeJournal(); change(value.frames[2], screen => { screen.lines[0] = 'WRONG'; });
    const result = report(value);
    assert.equal(result.observations[0].result, 'inconclusive');
    assert.equal(result.observations[0].assertions[0].passed, true);
});
test('restoration of each logical fixture is an independent assertion', () => {
    const value = syntheticResizeJournal(); change(value.frames[7], screen => { screen.lines[0] = 'UNRESTORED'; });
    const result = report(value);
    assert.equal(result.observations[1].result, 'failed');
    assert.equal(result.observations[1].assertions[0].passed, true);
    assert.equal(result.observations[1].assertions[1].passed, false);
});

test('published committed-source resize evidence rederives all comparisons and its v6 binding', () => {
    const data = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-03-resize.json', import.meta.url)));
    const value = data.resizeReport;
    assert.deepEqual(buildResizeReport(value.snapshots, value.actions, data.context.runId), value);
    assert.equal(value.snapshots.length, 12); assert.equal(value.actions.length, 6);
    assert.deepEqual(value.observations.map(item => item.result), ['passed', 'passed', 'passed']);
    assert.deepEqual(data.resizeOwnership.actions, value.actions);
    assert.deepEqual(data.resizeOwnership.originalGeometry, { rows: 40, cols: 140 });
    assert.deepEqual(value.snapshots[0].screen, value.snapshots.at(-1).screen);
    assert.equal(data.receipt.schemaVersion, 6); assert.equal(data.receipt.fixtureRemoved, true);
    assert.deepEqual(data.receipt.resizeEvidence.counts, { passed: 3 });
    assert.equal(data.receipt.resizeEvidence.sha256,
        createHash('sha256').update(JSON.stringify(value, null, 2) + '\n').digest('hex'));
    assert.equal(data.appBuildSource, data.adapterSource);
    assert.equal(data.appBuildSource, '595f957413986e1d9fc62c62754e2e44729d243a');
    assert.deepEqual(data.stockCollector.counts, { supported: 78, inconclusive: 177 });
    assert.equal(data.stockCollector.suiteComplete, false);
    assert.ok(data.stockCollector.ungradedDiagnostics['cursor.reverse-wrap']);
});
