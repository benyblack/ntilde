import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdtempSync, rmSync, writeFileSync, readFileSync, existsSync, symlinkSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { buildPixelReport } from './pixels.mjs';
import { pixelSuiteId } from './pixels-cases.mjs';
import { pngHash } from './pixels-capture.mjs';
import { syntheticPixelJournal } from './fixtures/pixel-journal.mjs';
import { completeOwnedReceipt } from './receipt.mjs';
const runId = 'a'.repeat(32);
function fixture(action) {
    const output = mkdtempSync(join(tmpdir(), 'nt-pixel-report-'));
    try { action({ output, frames: syntheticPixelJournal(output, runId) }); }
    finally { rmSync(output, { recursive: true, force: true }); }
}
test('blank synthetic captures cannot qualify glyphs, underlines, blink or cursor shapes', () => fixture(({ output, frames }) => {
    const report = buildPixelReport(frames, runId, output);
    assert.equal(report.observations.length, 25);
    assert.ok(report.observations.every(item => item.result !== 'passed'));
    assert.equal(report.complete, true);
    assert.equal(report.inputs.length, 24);
}));
for (const [name, mutate] of [
    ['missing capture', f => f.frames.pop()], ['another pane', f => { f.frames[2].paneId = '22222222-2222-2222-2222-222222222222'; }],
    ['reordered scene', f => { f.frames[2].sceneId = 'bold'; }], ['changed clock', f => { f.frames[2].pixel.presentationTimeMs = 530; }],
    ['unfenced sample', f => { f.frames[2].barrier = null; }], ['changed font', f => { f.frames[2].pixel.renderDetails.font.primarySha256 = 'b'.repeat(64); }],
    ['decoded suite budget exhausted', f => { for (const frame of f.frames) { frame.pixel.width = 8192; frame.pixel.height = 8192; } }],
    ['changed host cursor preference', f => { f.frames[2].pixel.renderDetails.presentation.cursorStyle = 1; }],
    ['changed state', f => { f.frames[2].screen.lines[0] = 'leftover'; }], ['changed after state', f => { f.frames[2].afterScreen.cursorCol++; }],
    ['changed after hash', f => { f.frames[2].afterSha256 = 'b'.repeat(64); }], ['escaped PNG path', f => { f.frames[2].pixel.file = '../outside.png'; }],
    ['changed PNG bytes', f => writeFileSync(join(f.output, f.frames[2].pixel.file), 'not a PNG')],
]) test('report refuses ' + name, () => fixture(f => { mutate(f); assert.throws(() => buildPixelReport(f.frames, runId, f.output)); }));

test('PNG symlinks cannot substitute retained images', { skip: process.platform === 'win32' }, () => fixture(f => {
    const path = join(f.output, f.frames[2].pixel.file); rmSync(path);
    symlinkSync(join(f.output, f.frames[0].pixel.file), path);
    assert.throws(() => buildPixelReport(f.frames, runId, f.output));
}));

function receiptFixture(action) {
    fixture(f => {
        const write = (file, value) => writeFileSync(join(f.output, file), JSON.stringify(value) + '\n');
        write('context.json', { runId, pixelSuiteId }); write('ownership-proof.json', { completed: true, cleanedUpAt: '2026-11-03T00:00:00Z' }); write('raw.json', {});
        const report = buildPixelReport(f.frames, runId, f.output);
        write('pixel-observations.json', report);
        writeFileSync(join(f.output, 'pixel-snapshots.jsonl'), f.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
        const binding = (prefix, suiteId = 'test-only') => {
            const file = `${prefix}-observations.json`;
            if (prefix !== 'pixel') write(file, { runId });
            return { runId, suiteId, file, sha256: pngHash(readFileSync(join(f.output, file))) };
        };
        const evidence = { ...binding('cell'), pixelEvidence: binding('pixel', pixelSuiteId) };
        for (const name of ['sgr', 'rgb', 'rendition', 'cursor']) evidence[name + 'Evidence'] = binding(name);
        action({ ...f, evidence, report, write, binding, finish: () => completeOwnedReceipt(f.output, join(f.output, 'removed'), evidence) });
    });
}
test('v2 receipt re-derives all observations and image bindings after cleanup', () => receiptFixture(f => {
    const result = f.finish(); assert.equal(result.schemaVersion, 2); assert.deepEqual(result.pixelEvidence, f.evidence.pixelEvidence);
}));
for (const [name, mutate] of [
    ['missing pixels binding', f => { delete f.evidence.pixelEvidence; }],
    ['changed PNG after report', f => writeFileSync(join(f.output, f.frames[2].pixel.file), 'changed')],
    ['changed journal', f => writeFileSync(join(f.output, 'pixel-snapshots.jsonl'), '')],
    ['invented passing result with matching report digest', f => { f.report.observations[0].result = 'passed'; f.write('pixel-observations.json', f.report); f.evidence.pixelEvidence = f.binding('pixel', pixelSuiteId); }],
]) test('v2 finalizer refuses ' + name, () => receiptFixture(f => {
    mutate(f); assert.throws(f.finish); assert.equal(existsSync(join(f.output, 'cell-receipt.json')), false);
}));

test('published pixel measurements re-derive from original PNGs and match their final receipt', () => {
    const data = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-03-pixels.json', import.meta.url)));
    const output = new URL('../../docs/qa/terminfo/2026-11-03-pixels/', import.meta.url);
    // path helpers accept filesystem paths; the evidence tree has no private App paths.
    const root = fileURLToPath(output);
    const derived = buildPixelReport(data.pixelReport.snapshots, data.context.runId, root);
    assert.deepEqual(derived, data.pixelReport);
    assert.equal(pngHash(Buffer.from(JSON.stringify(derived, null, 2) + '\n')), data.receipt.pixelEvidence.sha256);
    assert.equal(data.receipt.fixtureRemoved, true);
    assert.equal(data.receipt.schemaVersion, 2);
    assert.equal(data.receipt.runId, data.context.runId);
    assert.deepEqual(data.receipt.pixelEvidence.counts, { failed: 1, passed: 24 });
    assert.deepEqual(data.stockCollector.counts, { inconclusive: 177, supported: 78 });
    assert.equal(data.stockCollector.suiteComplete, false);
    assert.ok(data.stockCollector.ungradedDiagnostics['cursor.reverse-wrap']);
    assert.deepEqual(derived.observations.filter(item => item.result !== 'passed').map(item => item.featureId), ['rendition.bold']);
});
