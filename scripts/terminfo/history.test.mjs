import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { ownedOutputPolicy, validateOutputDescription, validateOutputGeometry } from './terminal-output.mjs';
import { makeHistoryCases } from './history-cases.mjs';
import { buildHistoryReport } from './history.mjs';
import { syntheticCellPlanJournal } from './fixtures/editing-journal.mjs';
import { snapshotHash } from './agent-observer.mjs';
const runId = 'a'.repeat(32);
const frames = () => syntheticCellPlanJournal('history', runId, { casesFor: makeHistoryCases, nonce: 'NTILDE_HS_' });
function coherent(frame, change) {
    change(frame.screen); frame.sha256 = snapshotHash(frame.screen);
    frame.afterScreen = structuredClone(frame.screen); frame.afterSha256 = frame.sha256;
}
test('history report compares the complete known plan including actual history and origin mode', () => {
    const report = buildHistoryReport(frames(), runId);
    assert.equal(report.snapshots.length, 63); assert.equal(report.observations.length, 30);
    assert.ok(report.observations.every(item => item.result === 'passed'));
    const origin = report.inputs.find(item => item.id === 'history.margins.origin-home');
    assert.equal(origin.before.bufferState.originMode, true); assert.equal(origin.after.cursorRow, 2);
    assert.equal(report.inputs[0].before.history.totalLines, 0);
});
for (const [name, change] of [
    ['missing frame', values => values.pop()],
    ['wrong pane', values => { values[3].paneId = '22222222-2222-2222-2222-222222222222'; }],
    ['reordered case', values => { values[4].caseId = values[2].caseId; }],
    ['unfenced target', values => { values[3].barrier = null; }],
    ['changed bracket', values => { values[3].afterScreen.cursorCol++; }],
    ['invented history count', values => { values[2].screen.bufferState.scrollbackLines = null; }],
    ['partial history', values => { values[8].screen.history.lines.pop(); }],
    ['restoration changes', values => coherent(values.at(-1), screen => { screen.cursorCol = 1; })],
]) test('history report refuses ' + name, () => { const values = frames(); change(values); assert.throws(() => buildHistoryReport(values, runId)); });
test('correct viewport cannot hide a wrong ordered history result', () => {
    const values = frames(); coherent(values[3], screen => { screen.history.lines[0] = 'wrong'; });
    const report = buildHistoryReport(values, runId); assert.equal(report.observations[0].result, 'failed');
    assert.equal(report.observations[0].controls[0].passed, true);
});
test('wrong history control is inconclusive rather than a support score', () => {
    const values = frames(); const control = values.find(frame => frame.caseId === 'history.scroll-up.main.2' && frame.stageId === 'control');
    coherent(control, screen => { screen.history.lines.reverse(); });
    const report = buildHistoryReport(values, runId); assert.equal(report.observations.find(item => item.featureId === control.caseId).result, 'inconclusive');
});

test('published committed-source history measurement rederives all 30 comparisons and its receipt hash', () => {
    const data = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-03-history.json', import.meta.url)));
    const report = buildHistoryReport(data.historyReport.snapshots, data.context.runId);
    assert.deepEqual(report, data.historyReport); assert.equal(report.snapshots.length, 63);
    assert.equal(report.observations.length, 30); assert.ok(report.observations.every(value => value.result === 'passed'));
    assert.equal(data.receipt.schemaVersion, 5); assert.equal(data.receipt.fixtureRemoved, true);
    assert.equal(data.receipt.historyEvidence.sha256, createHash('sha256').update(JSON.stringify(report, null, 2) + '\n').digest('hex'));
    assert.deepEqual(data.receipt.historyEvidence.counts, { passed: 30 });
    assert.equal(data.context.terminalOutput, ownedOutputPolicy); assert.equal(data.terminalOutputProof.outputProcessing, false);
    validateOutputDescription(data.terminalOutputProof.description);
    validateOutputGeometry(data.terminalOutputProof.description, report.snapshots[0].screen.rows, report.snapshots[0].screen.cols);
    assert.equal(data.appBuildSource, data.adapterSource); assert.ok(data.context.appVersion.includes(data.appBuildSource));
    assert.deepEqual(report.snapshots[0].screen, report.snapshots.at(-1).screen);
    assert.deepEqual(data.stockCollector.counts, { inconclusive: 177, supported: 78 });
    assert.equal(data.stockCollector.suiteComplete, false); assert.ok(data.stockCollector.ungradedDiagnostics['cursor.reverse-wrap']);
});
