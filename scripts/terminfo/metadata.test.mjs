import test from 'node:test';
import assert from 'node:assert/strict';
import { buildMetadataReport, metadataProjection } from './metadata.mjs';
import { syntheticMetadataJournal } from './fixtures/metadata-journal.mjs';
import { snapshotHash } from './agent-observer.mjs';
const runId = 'a'.repeat(32);
function coherent(frame, mutate) {
    mutate(frame.before); frame.after = structuredClone(frame.before);
    frame.sha256 = snapshotHash(metadataProjection(frame.before)); frame.afterSha256 = frame.sha256;
}
test('13 independent controls and 46 paired frames preserve the explicit empty-title negative and two unqualified P probes', () => {
    const report = buildMetadataReport(syntheticMetadataJournal(), runId);
    assert.equal(report.inputs.length, 13); assert.equal(report.snapshots.length, 46);
    assert.ok(report.observations.every(value => value.control.passed));
    assert.equal(report.observations.filter(value => value.result === 'passed').length, 10);
    assert.equal(report.observations.filter(value => value.result === 'failed').length, 1);
    assert.equal(report.observations.filter(value => value.result === 'inconclusive').length, 2);
    assert.equal(report.observations.find(value => value.result === 'failed').featureId, 'metadata.title.empty');
    assert.equal(report.snapshots[0].before.metadata.oscTitle, null);
    assert.notEqual(report.snapshots.at(-1).before.metadata.oscTitle, null);
    assert.deepEqual(report.snapshots[0].before.screen, report.snapshots.at(-1).before.screen);
});
for (const [name, mutate] of Object.entries({
    'missing stage': values => values.pop(),
    'reordered stage': values => values[3].stageId = 'invented',
    'wrong pane': values => values[3].paneId = '22222222-2222-2222-2222-222222222222',
    'unpaired host': values => values[3].after.metadata.oscTitle = 'other',
    'unpaired cells': values => values[3].after.screen.lines[1] = 'unexpected',
    'forged hash': values => values[3].sha256 = '0'.repeat(64),
    'missing barrier': values => values[3].barrier = null,
    'error-bearing frame': values => values[3].error = 'lost observation',
    'wrong nonce': values => coherent(values[1], value => value.screen.lines[0] = 'NTILDE_MD_' + 'b'.repeat(32)),
    'failed prepared restoration': values => coherent(values.at(-1), value => value.metadata.oscTitle = 'unrestored'),
    'failed cell restoration': values => coherent(values.at(-1), value => { value.screen.cursorCol = 1; value.metadata.buffer.cursorCol = 1; }),
    'cross-read cursor mismatch': values => coherent(values[3], value => value.metadata.buffer.cursorCol++),
    'generation change': values => coherent(values[3], value => value.metadata.buffer.historyGeneration++),
    'settings disabled at bind': values => coherent(values[1], value => value.metadata.presentation.notificationsEnabled = false),
})) test('rejects ' + name, () => { const values = syntheticMetadataJournal(); mutate(values); assert.throws(() => buildMetadataReport(values, runId)); });
test('matching raw status must be present; a copied status summary cannot replace its source', () => {
    const values = syntheticMetadataJournal(); coherent(values[3], value => value.status.status = 'running');
    assert.throws(() => buildMetadataReport(values, runId));
});
test('coherent target mismatch remains a negative while its independent control stays passing', () => {
    const values = syntheticMetadataJournal(); coherent(values[3], value => value.metadata.oscTitle = 'wrong');
    const item = buildMetadataReport(values, runId).observations[0]; assert.equal(item.control.passed, true); assert.equal(item.result, 'failed');
});
test('failed positive control becomes inconclusive', () => {
    const values = syntheticMetadataJournal(); coherent(values[2], value => value.metadata.oscTitle = 'wrong');
    assert.equal(buildMetadataReport(values, runId).observations[0].result, 'inconclusive');
});
test('actual focused window prevents a false positive notification delivery grade', () => {
    const report = buildMetadataReport(syntheticMetadataJournal(runId, { focused: true }), runId);
    const item = report.observations.at(-1); assert.equal(item.control.passed, true);
    assert.equal(item.positiveDeliveryPreconditions, false); assert.equal(item.result, 'inconclusive');
});
test('missing exact hyperlink identity is a negative even with unchanged visible text', () => {
    const values = syntheticMetadataJournal(); const frame = values.find(value => value.caseId === 'metadata.links.osc8' && value.stageId === 'observed');
    coherent(frame, value => value.metadata.buffer.links[0].id = 'other');
    assert.equal(buildMetadataReport(values, runId).observations.find(value => value.featureId === frame.caseId).result, 'failed');
});
test('elapsed status timestamps can differ while every stable fact remains paired', () => {
    const values = syntheticMetadataJournal(); values[3].after.rawStatus.lastOutputAtMs++;
    assert.doesNotThrow(() => buildMetadataReport(values, runId));
});

for (const [name, feature, stage, mutate] of [
    ['cwd is independently observed', 'metadata.cwd.osc7', 'observed', value => value.metadata.workingDirectory = '/wrong'],
    ['actual display title is independently observed', 'metadata.title.osc2', 'observed', value => value.metadata.displayTitle = 'wrong'],
    ['actual tab text is independently observed', 'metadata.title.osc2', 'observed', value => value.metadata.presentation.tabText = 'wrong'],
    ['visible progress suffix is required', 'metadata.progress.normal', 'observed', value => value.metadata.presentation.tabText = value.metadata.displayTitle],
    ['progress ownership is required', 'metadata.progress.normal', 'observed', value => value.metadata.presentation.progressOwner = null],
    ['actual toast must become visible', 'metadata.notification.osc9', 'observed', value => value.metadata.presentation.toastVisible = false],
    ['actual toast must hide', 'metadata.notification.osc9', 'auto-hidden', value => value.metadata.presentation.toastVisible = true],
    ['B anchor is not a consumed-marker claim', 'metadata.shell.lifecycle', 'B', value => value.metadata.buffer.commandStart.col++],
    ['C output anchor is independently observed', 'metadata.shell.lifecycle', 'C', value => value.metadata.buffer.outputStart = null],
    ['C command status is independently observed', 'metadata.shell.lifecycle', 'C', value => {
        value.status.currentCommand = 'wrong'; value.rawStatus.currentCommand = 'wrong'; }],
    ['D clears the input anchor', 'metadata.shell.lifecycle', 'D', value => value.metadata.buffer.commandStart = {
        row: 2, col: 4, absoluteRow: 2, generation: 4, alternateScreen: false }],
    ['unchanged cells outside the witness remain required', 'metadata.title.osc2', 'observed', value => value.screen.lines[8] = 'stray'],
]) test(name, () => {
    const values = syntheticMetadataJournal(); coherent(values.find(value => value.caseId === feature && value.stageId === stage), mutate);
    const item = buildMetadataReport(values, runId).observations.find(value => value.featureId === feature);
    assert.equal(item.control.passed, true); assert.equal(item.result, 'failed');
});

test('inactive control followed by focused suppressed delivery is inconclusive', () => {
    const values = syntheticMetadataJournal(), frame = values.find(value => value.caseId === 'metadata.notification.osc9' && value.stageId === 'observed');
    coherent(frame, value => { value.metadata.presentation.windowActive = true; value.metadata.presentation.toastVisible = false; });
    const item = buildMetadataReport(values, runId).observations.at(-1);
    assert.equal(item.control.passed, true); assert.equal(item.positiveDeliveryPreconditions, false); assert.equal(item.result, 'inconclusive');
});
