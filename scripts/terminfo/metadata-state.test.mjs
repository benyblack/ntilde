import test from 'node:test';
import assert from 'node:assert/strict';
import { copyPaneMetadata, copyMetadataStatus } from './metadata-state.mjs';
const paneId = '00000000-0000-4000-8000-000000000001';
export function wire() {
    return { paneId, displayTitle: 'Owned fixture', buffer: { rows: 12, cols: 60, cursorRow: 0, cursorCol: 0,
        alternateScreen: false, scrollbackLines: 0, historyGeneration: 4, rowsEvicted: 0, acceptingCommandInput: false, links: [] },
    presentation: { windowActive: false, windowVisible: true, paneActive: true, focusMode: false, notificationsEnabled: true,
        progressEnabled: true, tabText: 'Owned fixture', tabTextVisible: true, toastVisible: false, toastTitle: '', toastMessage: '' } };
}
test('omitted nullable fields normalize without inventing metadata', () => {
    const value = copyPaneMetadata(wire());
    assert.equal(value.oscTitle, null); assert.equal(value.workingDirectory, null);
    assert.equal(value.buffer.commandStart, null); assert.equal(value.presentation.progressBar, null);
});
test('actual hidden progress and toast control properties survive the projection', () => {
    const raw = wire(); Object.assign(raw.presentation, { progressBar: { visible: false, indeterminate: true, value: 42 },
        toastTitle: 'Previous title', toastMessage: 'Previous message' });
    const observed = copyPaneMetadata(raw);
    assert.deepEqual(observed.presentation.progressBar, raw.presentation.progressBar);
    assert.equal(observed.presentation.toastMessage, 'Previous message');
    raw.presentation.progressBar.value = 9; assert.equal(observed.presentation.progressBar.value, 42);
});
test('raw anchors remain raw and snapshot-local hyperlink identities are preserved', () => {
    const raw = wire(); raw.buffer.commandStart = { row: -1, col: 2, absoluteRow: 3, generation: 2, alternateScreen: true };
    raw.buffer.links = [{ row: 1, col: 2, length: 3, identity: 0, uri: 'https://example.invalid/a', id: 'same' },
        { row: 2, col: 2, length: 3, identity: 0, uri: 'https://example.invalid/a', id: 'same' },
        { row: 2, col: 8, length: 1, identity: 1, uri: 'https://example.invalid/a', id: 'different' }];
    assert.deepEqual(copyPaneMetadata(raw).buffer.commandStart, raw.buffer.commandStart);
    assert.deepEqual(copyPaneMetadata(raw).buffer.links, raw.buffer.links);
});
const mutations = {
    'missing presentation': value => delete value.presentation,
    'unknown property': value => value.action = 'sendInput',
    'missing actual setting': value => delete value.presentation.notificationsEnabled,
    'wrong flag type': value => value.presentation.windowActive = 0,
    'bad pane': value => value.paneId = '',
    'nonfinite bar': value => value.presentation.progressBar = { visible: false, indeterminate: false, value: NaN },
    'oversized field': value => value.oscTitle = 't'.repeat(4097),
    'outside grid': value => value.buffer.cursorCol = 60,
    'oversized grid': value => { value.buffer.rows = 128; value.buffer.cols = 256; },
    'negative history': value => value.buffer.rowsEvicted = -1,
    'incomplete anchor': value => value.buffer.commandStart = { row: 0 },
    'wrong state': value => value.presentation.progressKind = 'Failed',
    'bad progress owner': value => value.presentation.progressOwner = 'unknown',
    'bad percent': value => value.presentation.progressPercent = 101,
};
for (const [name, mutate] of Object.entries(mutations)) test('rejects ' + name, () => {
    const value = wire(); mutate(value); assert.throws(() => copyPaneMetadata(value));
});
for (const [name, mutate] of Object.entries({
    overlap: values => values.push({ ...values[0], identity: 1, col: 3 }),
    'outside row': values => values[0].row = 12,
    'outside column': values => values[0].length = 60,
    'skipped identity': values => values[0].identity = 1,
    'substituted URI identity': values => values.push({ ...values[0], col: 8, uri: 'https://example.invalid/b' }),
    'too many links': values => { while (values.length <= 128) values.push({ ...values[0] }); },
})) test('rejects link ' + name, () => {
    const value = wire(); value.buffer.links = [{ row: 1, col: 2, length: 3, identity: 0, uri: 'https://example.invalid/a' }];
    mutate(value.buffer.links); assert.throws(() => copyPaneMetadata(value));
});
test('aggregate text cap covers repeated link text', () => {
    const value = wire(); const uri = 'https://example.invalid/' + 'x'.repeat(2000);
    value.buffer.links = Array.from({ length: 17 }, (_, col) => ({ row: 0, col: col * 2, length: 1, identity: 0, uri }));
    assert.throws(() => copyPaneMetadata(value)); value.buffer.links.pop(); assert.equal(copyPaneMetadata(value).buffer.links.length, 16);
});
test('status projects stable facts but validates required original timestamps', () => {
    const raw = { paneId, status: 'running', confidence: 'precise', statusSinceMs: 1, lastOutputAtMs: 2,
        isStalled: false, stallThresholdSeconds: 30, idleThresholdSeconds: 2 };
    const value = copyMetadataStatus(raw, paneId); assert.equal(value.status, 'running'); assert.equal(value.exitCode, null);
    assert.equal(Object.hasOwn(value, 'lastOutputAtMs'), false);
    assert.throws(() => copyMetadataStatus(raw, 'wrong'));
    delete raw.lastOutputAtMs; assert.throws(() => copyMetadataStatus(raw, paneId));
});
