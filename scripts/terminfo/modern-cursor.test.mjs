import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { snapshotHash } from './agent-observer.mjs';
import { buildModernCursorReport, collectModernCursor, modernCursorSuiteId } from './modern-cursor.mjs';
import { checkModernCursorEvidence, deriveModernCursorEvidence, includesModernCursorEvidence } from './modern-cursor-receipt.mjs';
import { terminalFence } from './terminal-fence.mjs';

const runId = 'a'.repeat(32);
const daRequest = '\x1b[c', daReply = '\x1b[?64;1c';
const reset = '\x1b[?6l\x1b[?69l\x1b[r\x1b[4l\x1b[?7h\x1b[0m\x1b[2J\x1b[H\x1b7';
const nonce = 'NTILDE_MCUR_' + runId;
const expected = [
    ['\x1b[?47h' + reset + nonce, null, 0, nonce.length, nonce],
    [reset, '1;1', 0, 0, ''],
    ['ABCDE', '1;6', 0, 5, 'ABCDE'],
    ['\x1b[3G', '1;3', 0, 2, 'ABCDE'],
    [reset, '1;1', 0, 0, ''],
    ['ABC', '1;4', 0, 3, 'ABC'],
    ['\x1b[2E', '3;1', 2, 0, 'ABC'],
    [reset, '1;1', 0, 0, ''],
    ['\x1b[4;6H', '4;6', 3, 5, ''],
    ['\x1b[s', '4;6', 3, 5, ''],
    ['\x1b[11;11H', '11;11', 10, 10, ''],
    ['\x1b[u', '4;6', 3, 5, ''],
    [reset, '1;1', 0, 0, ''],
    ['\x1b[?47l', null, 5, 5, 'main'],
];

async function fixture() {
    const screen = (cursorRow, cursorCol, text, alternateScreen) => ({ rows: 12, cols: 48,
        lines: [text, ...Array(11).fill('')], cursorRow, cursorCol, cursorVisible: true,
        bufferState: { scrollTop: 0, scrollBottom: 11, originMode: false, autoWrap: true, insertMode: false,
            pendingWrap: false, alternateScreen, scrollbackLines: 0 }, history: { startLine: 0, totalLines: 0, lines: [] },
        horizontalMargins: { leftRightMarginMode: false, leftMargin: 0, rightMargin: 47 } });
    const input = Object.assign(new EventEmitter(), {
        setRawMode() {}, resume() {}, isPaused: () => false,
    });
    let index = 0;
    const fence = terminalFence(input, { write(actual, callback) {
        const [bytes, cpr] = expected[index - 1];
        assert.equal(actual, bytes + (cpr ? '\x1b[6n' : '') + daRequest);
        callback();
        input.emit('data', Buffer.from((cpr ? '\x1b[' + cpr + 'R' : '') + daReply));
    } });
    const frames = [];
    try {
        const run = await collectModernCursor({ config: { runId }, fence, owner(kind, barrier = null, extra = {}) {
            const [,, row, col, text] = expected[index - 1] ?? [];
            const alt = kind !== 'mcur-start' && kind !== 'mcur-finish';
            const value = alt ? screen(row, col, text, true) : screen(5, 5, 'main', false);
            frames.push({ kind, barrier, index: frames.length, paneId: 'b62422e1-5645-4510-8f1a-530e9ea8f42e',
                screen: value, pairedScreen: structuredClone(value), sha256: snapshotHash(value),
                request: extra.requestBytes ?? null, ...(extra.response ? { response: extra.response } : {}) });
            index++;
            return { screen: value };
        } });
        assert.equal(index, expected.length + 1);
        return { frames, run };
    } finally { fence.dispose(); }
}

test('worker sends exact fixture and query bytes over the ordered fence, with a shared round trip', async () => {
    const { frames, run } = await fixture();
    const report = buildModernCursorReport(frames, run, runId);
    assert.deepEqual(report.cases.map(({ id, traceId, conclusion }) => [id, traceId, conclusion]), [
        ['cursor.horizontal-absolute', 'cha', 'pass'], ['cursor.next-line', 'cnl', 'pass'],
        ['cursor.ansi-save', 'ansi-round-trip', 'pass'], ['cursor.ansi-restore', 'ansi-round-trip', 'pass'],
    ]);
    assert.deepEqual(report.traces[2].transitions.map(value => value.position), [[0, 0], [3, 5], [3, 5], [10, 10], [3, 5]]);
});

for (const [label, mutate, error] of [
    ['a wrong movement despite matching reports', ({ frames, run }) => {
        frames[4].request = run.reads[2].request = '\x1b[15G\x1b[6n\x1b[c';
    }, /wrong request bytes/],
    ['a stale reply copied into both sides of the journal', ({ frames, run }) => {
        frames[12].response = run.reads[10].response = frames[11].response;
    }, /stale CPR/],
    ['a missing intermediate move', ({ frames, run }) => {
        frames.splice(11, 1);
        run.reads.splice(9, 1);
    }, /Incomplete Modern cursor snapshot/],
    ['an intermediate observation with no displacement', ({ frames, run }) => {
        frames[11].screen = frames[10].screen;
        frames[11].pairedScreen = frames[10].pairedScreen;
        frames[11].sha256 = frames[10].sha256;
        frames[11].response = run.reads[9].response = frames[10].response;
    }, /movement or setup control failed at displaced/],
    ['a missing paired observation', ({ frames }) => { delete frames[3].pairedScreen; }, /paired App reads/],
    ['origin mode on', ({ frames }) => {
        frames[2].screen.bufferState.originMode = frames[2].pairedScreen.bufferState.originMode = true;
        frames[2].sha256 = snapshotHash(frames[2].screen);
    }, /invalid modes or margins/],
    ['left\/right margin mode on', ({ frames }) => {
        frames[8].screen.horizontalMargins.leftRightMarginMode = frames[8].pairedScreen.horizontalMargins.leftRightMarginMode = true;
        frames[8].sha256 = snapshotHash(frames[8].screen);
    }, /invalid modes or margins/],
]) test('refuses ' + label, async () => {
    const input = await fixture();
    mutate(input);
    assert.throws(() => buildModernCursorReport(input.frames, input.run, runId), error);
});

test('current receipt policy refuses stripped evidence and binds the complete report and journal', async () => {
    const { frames, run } = await fixture();
    const output = mkdtempSync(join(tmpdir(), 'modern-cursor-receipt-'));
    try {
        const policy = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v73' };
        assert.equal(includesModernCursorEvidence(output, {}, policy), true);
        assert.equal(includesModernCursorEvidence(output, {}, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v72' }), false);
        const context = { ...policy, modernCursorSuiteId };
        const evidence = { textCursorEvidence: {} };
        assert.throws(() => checkModernCursorEvidence(output, evidence, runId, context), /binding missing/);
        writeFileSync(join(output, 'mcur-run.json'), JSON.stringify(run) + '\n');
        writeFileSync(join(output, 'mcur-snapshots.jsonl'), frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
        writeFileSync(join(output, 'cell-observations.json'), JSON.stringify({ snapshots: [frames[0]] }));
        const { report, binding } = deriveModernCursorEvidence(output, runId);
        evidence.modernCursorEvidence = binding;
        assert.throws(() => checkModernCursorEvidence(output, evidence, runId, context), /missing.*mcur-observations/);
        writeFileSync(join(output, 'mcur-observations.json'), JSON.stringify(report, null, 2) + '\n');
        checkModernCursorEvidence(output, evidence, runId, context);
        writeFileSync(join(output, 'cell-observations.json'), JSON.stringify({ snapshots: [{ ...frames[0], paneId: 'different' }] }));
        assert.throws(() => checkModernCursorEvidence(output, evidence, runId, context), /another pane or grid/);
        writeFileSync(join(output, 'mcur-failed-observations.jsonl'), '{}\n');
        assert.throws(() => checkModernCursorEvidence(output, evidence, runId, context), /failed observation/);
    } finally { rmSync(output, { recursive: true, force: true }); }
});
