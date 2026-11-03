import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { cpSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pixelCapture, pixelScreen, pixelPaneId } from './fixtures/pixel-capture.mjs';
import { OwnedColorObserver, validateColorCapture, validateQueries } from './color-capture.mjs';
import { colorCases, colorPlan, colorSelection, clearColors, collectColors } from './color-cases.mjs';
import { buildColorReport } from './colors.mjs';
import { snapshotHash } from './agent-observer.mjs';
import { completeOwnedReceipt } from './receipt.mjs';
const runId = 'a'.repeat(32), barrier = '\x1b[?1;2c';
function capture(screen = pixelScreen(), selection = null) {
    const result = pixelCapture(screen);
    Object.assign(result.renderDetails.theme, { cursorOverride: false, selectionBackgroundOverride: false, selectionForegroundOverride: false,
        selectionBackgroundArgb: null, selectionForegroundArgb: null, effectiveSelectionBackgroundArgb: 0x660000ff,
        effectiveSelectionForegroundArgb: null, paletteOverrideIndices: [] });
    if (selection) { result.syntheticSelection = selection; result.renderDetails.syntheticSelection = structuredClone(selection); }
    return result;
}
function queries(plan) { return plan.queries.map(spec => ({ spec, barrier, reply: null })); }
async function fixture(action) {
    const output = mkdtempSync(join(tmpdir(), 'nt-colors-'));
    let screen = pixelScreen(), hook = () => {};
    const observer = new OwnedColorObserver({ output, nonce: `NTILDE_CL_${runId}`, profileName: 'fixture', expectedPaneId: pixelPaneId,
        endpoint: () => 'private', rpc: async (_, method) => method === 'listSessions'
            ? { sessions: [{ paneId: pixelPaneId, kind: 'local', profileName: 'fixture', rows: screen.rows, cols: screen.cols }] }
            : structuredClone(screen),
        captureRpc: async (_, pane, selection) => { assert.equal(pane, pixelPaneId); const result = capture(screen, selection); hook(result); return result; } });
    const call = (kind, extra = {}) => observer.handle({ kind, barrier: kind === 'color-start' ? null : barrier, ...extra }, () => 1000);
    try { await action({ output, observer, call, screen: value => { screen = value; }, hook: value => { hook = value; } }); }
    finally { rmSync(output, { recursive: true, force: true }); }
}
async function bind(f) {
    await f.call('color-start');
    const screen = pixelScreen(); screen.lines[0] = `NTILDE_CL_${runId}`; screen.cursorCol = 42;
    f.screen(screen); await f.call('color-bind'); f.screen(pixelScreen());
}
async function complete(f) {
    await bind(f);
    for (const plan of colorPlan) await f.call('color-read', { caseId: plan.caseId, stageId: plan.stageId, queries: queries(plan) });
    await f.call('color-finish');
}
test('new color capture requires explicit nullable source facts and exact actual selection', () => {
    assert.ok(validateColorCapture(capture(), pixelScreen(), null));
    assert.ok(validateColorCapture(capture(pixelScreen(), colorSelection), pixelScreen(), colorSelection));
    assert.throws(() => validateColorCapture(pixelCapture(), pixelScreen(), null));
    for (const mutate of [value => { delete value.renderDetails.theme.selectionForegroundArgb; },
        value => { value.renderDetails.theme.paletteOverrideIndices = [2, 1]; },
        value => { value.renderDetails.theme.paletteOverrideIndices = [1, 1]; },
        value => { value.renderDetails.theme.cursorOverride = null; },
        value => { value.renderDetails.syntheticSelection.endCol++; },
        value => { value.syntheticSelection = null; }]) {
        const value = capture(pixelScreen(), structuredClone(colorSelection)); mutate(value);
        assert.throws(() => validateColorCapture(value, pixelScreen(), colorSelection));
    }
});
test('query binding rejects reordered, substituted, malformed and invented RGB channels', () => {
    const spec = { code: 10 }, raw = '\x1b]10;rgb:1717/5b5b/adad\x1b\\';
    const reply = { raw, channels16: [0x1717, 0x5b5b, 0xadad] };
    assert.deepEqual(validateQueries([{ spec, barrier, reply }], [spec]), [{ spec, barrier, reply }]);
    for (const value of [[], [{ spec: { code: 11 }, barrier, reply }], [{ spec, barrier: '', reply }],
        [{ spec, barrier, reply: { ...reply, channels16: [23, 91, 173] } }]]) assert.throws(() => validateQueries(value, [spec]));
});
test('color observer enforces plan, brackets, immutable files and exact restoration', async () => fixture(async f => {
    await assert.rejects(f.call('color-read', { caseId: 'invented' }));
    await bind(f);
    await assert.rejects(f.call('color-finish'));
    await assert.rejects(f.call('color-read', { ...colorPlan[0], stageId: 'override', queries: queries(colorPlan[0]) }));
    for (const plan of colorPlan) await f.call('color-read', { caseId: plan.caseId, stageId: plan.stageId, queries: queries(plan) });
    await f.call('color-finish');
    assert.equal(f.observer.frames.length, 35); assert.equal(f.observer.restored, true);
    await assert.rejects(f.call('color-start'));
    const report = buildColorReport(f.observer.frames, runId, f.output);
    assert.equal(report.observations.length, 10);
    assert.ok(report.observations.every(value => value.result === 'inconclusive'), 'Blank synthetic PNGs cannot qualify real renderer support.');
    const frame = f.observer.frames[2];
    writeFileSync(join(f.output, frame.pixel.file), 'substituted');
    assert.throws(() => buildColorReport(f.observer.frames, runId, f.output));
}));
for (const [name, hook] of [
    ['font substitution', value => { value.renderDetails.font.primarySha256 = 'b'.repeat(64); }],
    ['missing provenance', value => { delete value.renderDetails.theme.selectionBackgroundArgb; }],
    ['selection mismatch', value => { value.syntheticSelection = colorSelection; }],
]) test('observer refuses ' + name, async () => fixture(async f => {
    await bind(f); f.hook(hook);
    await assert.rejects(f.call('color-read', { caseId: colorPlan[0].caseId, stageId: colorPlan[0].stageId, queries: queries(colorPlan[0]) }));
}));
test('collector queries serially and restores session-wide colors even after a failure', async () => {
    const writes = [], calls = [], expected = colorPlan.flatMap(plan => plan.queries);
    await collectColors({ config: { runId }, fence: { write: async value => { writes.push(value); return barrier; },
        query: async spec => { assert.deepEqual(spec, expected.shift()); return { barrier, reply: null }; } },
        owner: async (kind, ordering, extra) => { calls.push({ kind, extra }); return { screen: pixelScreen() }; } });
    assert.equal(expected.length, 0); assert.equal(calls.length, 35);
    assert.equal(writes.at(-1), clearColors + '\x1b[?1049l\x1b[!p');
    const failureWrites = [];
    await assert.rejects(collectColors({ config: { runId }, fence: { write: async value => { failureWrites.push(value); return barrier; }, query: async () => { throw new Error('bad query'); } },
        owner: async () => ({ screen: pixelScreen() }) }), /bad query/);
    assert.equal(failureWrites.at(-1), writes.at(-1));
    assert.equal(colorCases.length, 10);
});
test('v3 receipt requires color binding, rederives report and refuses changed journals after cleanup', async () => fixture(async f => {
    await complete(f);
    const report = buildColorReport(f.observer.frames, runId, f.output), evidence = { runId, suiteId: report.suiteId, file: 'color-observations.json' };
    const write = (file, value) => writeFileSync(join(f.output, file), JSON.stringify(value));
    write(evidence.file, report); evidence.sha256 = snapshotHash(report);
    write('context.json', { runId, colorSuiteId: report.suiteId });
    write('ownership-proof.json', { completed: true, cleanedUpAt: '2026-11-03', cleanupError: null }); write('raw.json', {});
    writeFileSync(join(f.output, 'color-snapshots.jsonl'), report.snapshots.map(frame => JSON.stringify(frame)).join('\n'));
    const bindings = {};
    for (const prefix of ['cell', 'sgr', 'rgb', 'rendition', 'cursor']) {
        const value = { runId, suiteId: prefix, file: `${prefix}-observations.json`, sha256: snapshotHash({}) }; write(value.file, {});
        if (prefix === 'cell') Object.assign(bindings, value); else bindings[prefix + 'Evidence'] = value;
    }
    assert.throws(() => completeOwnedReceipt(f.output, join(f.output, 'removed'), bindings));
    writeFileSync(join(f.output, 'color-snapshots.jsonl'), '[]');
    assert.throws(() => completeOwnedReceipt(f.output, join(f.output, 'removed'), { ...bindings, colorEvidence: evidence }), /journal/);
    writeFileSync(join(f.output, 'color-snapshots.jsonl'), report.snapshots.map(frame => JSON.stringify(frame)).join('\n'));
    const receipt = completeOwnedReceipt(f.output, join(f.output, 'removed'), { ...bindings, colorEvidence: evidence });
    assert.equal(receipt.schemaVersion, 3); assert.equal(receipt.fixtureRemoved, true);
    assert.equal(JSON.parse(readFileSync(join(f.output, 'cell-receipt.json'))).colorEvidence.sha256, evidence.sha256);
}));
for (const [name, mutate] of [
    ['another pane', f => { f.observer.frames[2].paneId = '22222222-2222-2222-2222-222222222222'; }],
    ['reordered stage', f => { f.observer.frames[2].stageId = 'override'; }],
    ['missing sample', f => { f.observer.frames.pop(); }],
    ['unfenced query', f => { f.observer.frames[2].queries[0].barrier = null; }],
    ['changed after state', f => { f.observer.frames[2].afterScreen.cursorCol++; }],
    ['changed after hash', f => { f.observer.frames[2].afterSha256 = 'b'.repeat(64); }],
    ['path traversal', f => { f.observer.frames[2].pixel.file = '../outside.png'; }],
    ['changed font', f => { f.observer.frames[2].pixel.renderDetails.font.primarySha256 = 'b'.repeat(64); }],
    ['changed selected draw', f => { f.observer.frames[23].pixel.renderDetails.syntheticSelection = null; }],
    ['changed restored theme', f => { f.observer.frames.at(-1).pixel.renderDetails.theme.cursorOverride = true; }],
    ['decoded budget exhausted', f => { for (const frame of f.observer.frames) { frame.pixel.width = 8192; frame.pixel.height = 8192; } }],
]) test('color report refuses ' + name, async () => fixture(async f => {
    await complete(f); mutate(f); assert.throws(() => buildColorReport(f.observer.frames, runId, f.output));
}));
test('initial optional refusal writes nothing, later refusals restore and remain fatal', async () => {
    const error = Object.assign(new Error('budget'), { code: 'screenDetailsUnavailable' });
    let writes = 0;
    const options = { config: { runId }, fence: { write: async () => { writes++; return barrier; } }, owner: async () => { throw error; } };
    assert.deepEqual(await collectColors(options), { status: 'unavailable', reason: error.code }); assert.equal(writes, 0);
    options.owner = async kind => { if (kind === 'color-bind') throw error; return { screen: pixelScreen() }; };
    await assert.rejects(collectColors(options), value => value === error); assert.equal(writes, 2);
});

test('published color measurements rederive all original PNGs and match the final v3 receipt', () => {
    const data = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-03-colors.json', import.meta.url)));
    const root = new URL('../../docs/qa/terminfo/2026-11-03-colors/', import.meta.url);
    const derived = buildColorReport(data.colorReport.snapshots, data.context.runId, fileURLToPath(root));
    assert.deepEqual(derived, data.colorReport);
    assert.equal(data.receipt.colorEvidence.sha256, createHash('sha256').update(JSON.stringify(derived, null, 2) + '\n').digest('hex'));
    assert.equal(data.receipt.schemaVersion, 3); assert.equal(data.receipt.fixtureRemoved, true);
    assert.deepEqual(data.receipt.colorEvidence.counts, { passed: 10 });
    assert.ok(derived.observations.every(item => item.result === 'passed'));
    assert.deepEqual(data.stockCollector.counts, { inconclusive: 177, supported: 78 });
    assert.equal(data.stockCollector.suiteComplete, false); assert.ok(data.stockCollector.ungradedDiagnostics['cursor.reverse-wrap']);
    const unset = derived.snapshots.find(frame => frame.caseId === 'selection-background' && frame.stageId === 'baseline');
    assert.equal(unset.pixel.renderDetails.theme.selectionBackgroundArgb, null); assert.equal(unset.queries[0].reply, null);
});

for (const [caseId, replacement] of [['selection.independent-117', 24], ['selection.independent-119', 28]]) {
    test(`wrong partial-reset paint cannot pass ${caseId} with correct query/theme metadata`, () => {
        const data = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-03-colors.json', import.meta.url)));
        const source = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-03-colors/', import.meta.url));
        const output = mkdtempSync(join(tmpdir(), 'nt-color-paint-'));
        try {
            cpSync(join(source, 'color-pixels'), join(output, 'color-pixels'), { recursive: true });
            const frames = structuredClone(data.colorReport.snapshots);
            const frame = frames.find(value => value.caseId === caseId && value.stageId === 'partial-reset');
            const bytes = readFileSync(join(source, frames[replacement].pixel.file));
            writeFileSync(join(output, frame.pixel.file), bytes);
            frame.pixel.byteCount = bytes.length; frame.pixel.sha256 = createHash('sha256').update(bytes).digest('hex');
            const report = buildColorReport(frames, data.context.runId, output);
            assert.equal(report.observations.find(value => value.featureId === caseId).result, 'failed');
        } finally { rmSync(output, { recursive: true, force: true }); }
    });
}
