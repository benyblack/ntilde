import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { CursorQualObserver, validateReadExtras } from './cursor-qual-capture.mjs';
import { retainedCursorCapture } from './pixels-capture.mjs';
import { cursorQualNoncePrefix, cursorQualReset } from './cursor-qual-contracts.mjs';
import { SimTerminal, cursorRows, cursorCols, encodePng, syntheticRenderDetails } from './fixtures/cursor-qual-evidence.mjs';

const paneId = '0f0e0d0c-0b0a-4009-8008-700600500400';
const barrier = '\x1b[?62;4;22c';
const runId = 'a'.repeat(32), nonce = cursorQualNoncePrefix + runId;

/** The wire response of readScreen for one simulated screen, with the opt-in projections the observer requests. */
const raw = screen => ({ ...screen, attributeLines: screen.lines.map(() => '0300:-1/-1|'.repeat(screen.cols)) });
const capture = (sim, timeMs) => {
    const image = sim.render(timeMs), png = encodePng(image), screen = sim.screen();
    return { width: image.width, height: image.height, rows: screen.rows, cols: screen.cols, byteCount: png.length, downscaled: false, inlineOmitted: false, mode: 'render', scale: 1,
        pngBase64: png.toString('base64'), presentationTimeMs: timeMs, renderDetails: syntheticRenderDetails(screen, timeMs, image.width, image.height) };
};

async function fixture(action) {
    const output = mkdtempSync(join(tmpdir(), 'nt-cursor-capture-'));
    const sim = new SimTerminal(), hooks = { capture: () => {}, sessions: undefined };
    const sessions = () => hooks.sessions ?? [{ paneId, kind: 'local', profileName: 'fixture', rows: cursorRows, cols: cursorCols }];
    const observer = new CursorQualObserver({ output, appData: '/never/discover', ownerPid: 42, profileName: 'fixture', nonce, endpoint: () => 'private-fixture', expectedPaneId: paneId,
        rpc: async (_, method) => (method === 'listSessions' ? { sessions: sessions() } : method === 'readScrollback' ? { startLine: 0, totalLines: 0, lines: [] } : raw(sim.screen())),
        captureRpc: async (_, pane, time) => { assert.equal(pane, paneId); hooks.capture(sim); return capture(sim, time); } });
    const call = (kind, extra = {}) => observer.handle({ kind, barrier: kind === 'curq-start' ? null : barrier, ...extra }, () => 1000);
    try { await action({ observer, output, sim, call, hooks }); } finally { rmSync(output, { recursive: true, force: true }); }
}
async function bind(f) {
    await f.call('curq-start');
    f.sim.feed('\x1b[?1049h'); f.sim.feed(nonce);
    await f.call('curq-bind');
}

test('read extras: a CPR exchange or a capture phase, never both, and only valid ones', () => {
    const report = { barrier, reply: { raw: '\x1b[2;3R', row: 2, col: 3 } };
    assert.deepEqual(validateReadExtras({ barrier }), {});
    assert.deepEqual(validateReadExtras({ barrier, cpr: report }), { cpr: report });
    assert.deepEqual(validateReadExtras({ barrier, capture: { timeMs: 530 } }), { capture: { timeMs: 530 } });
    assert.throws(() => validateReadExtras({ barrier, cpr: report, capture: { timeMs: 0 } }), /either a CPR exchange or a capture/);
    assert.throws(() => validateReadExtras({ barrier: '\x1b[?1c', cpr: report }), /not ordered by this read barrier/);
    assert.throws(() => validateReadExtras({ barrier, cpr: { ...report, reply: { ...report.reply, row: 9 } } }), /disagrees with its raw bytes/);
    for (const capture of [{ timeMs: 529 }, { timeMs: 0, extra: 1 }, null, {}]) assert.throws(() => validateReadExtras({ barrier, capture }), /Invalid cursor capture phase/);
});

test('the observer journals CPR exchanges and retains controlled captures bound to the observed state', async () => fixture(async f => {
    await assert.rejects(f.call('curq-read'), /not started/);
    await bind(f);
    f.sim.feed('\x1b[5;15r\x1b[?6h\x1b[1;1H');
    const [row, col] = f.sim.report();
    const cpr = { barrier, reply: { raw: `\x1b[${row};${col}R`, row, col } };
    const result = await f.call('curq-read', { cpr });
    assert.equal(result.screen.cursorRow, 4);
    assert.deepEqual(f.observer.frames.at(-1).cpr, cpr);
    f.sim.feed('\x1b[2 q\x1b[?25h');
    const visible = await f.call('curq-read', { capture: { timeMs: 0 } });
    assert.match(visible.pixel.sha256, /^[a-f\d]{64}$/);
    const frame = f.observer.frames.at(-1);
    assert.equal(frame.pixel.presentationTimeMs, 0);
    assert.equal(frame.pixel.file, 'cursor-pixels/frame-003.png');
    assert.equal(retainedCursorCapture(f.output, frame).image.width, cursorCols * 9);
    f.sim.feed('\x1b[?25l');
    await f.call('curq-read', { capture: { timeMs: 530 } });
    f.sim.feed(cursorQualReset); f.sim.feed('\x1b[?1049l');
    await f.call('curq-finish');
    assert.equal(f.observer.restored, true);
    await assert.rejects(f.call('curq-start'), /busy or complete/);
    const bytes = readFileSync(join(f.output, frame.pixel.file)); bytes[20] ^= 1; writeFileSync(join(f.output, frame.pixel.file), bytes);
    assert.throws(() => retainedCursorCapture(f.output, frame), /controlled pixel capture/);
}));

test('the observer refuses a CPR bound to another barrier, a report on a non-read, both extras, an unknown phase and an unfenced read', async () => fixture(async f => {
    await bind(f);
    const cpr = { barrier: '\x1b[?9c', reply: null };
    await assert.rejects(f.call('curq-read', { cpr }), /not ordered by this read barrier/);
    await assert.rejects(f.call('curq-finish', { cpr: { barrier, reply: null } }), /Only reads carry a report or capture/);
    await assert.rejects(f.call('curq-read', { cpr: { barrier, reply: null }, capture: { timeMs: 0 } }), /either a CPR exchange or a capture/);
    await assert.rejects(f.call('curq-read', { capture: { timeMs: 100 } }), /Invalid cursor capture phase/);
    await assert.rejects(f.observer.handle({ kind: 'curq-read', barrier: null }, () => 1000), /ordering barrier/);
    assert.equal(f.observer.frames.length, 2, 'refused requests journal nothing');
}));

test('the observer refuses a capture that changed the screen, a changed pane or geometry, and a changed render context', async () => fixture(async f => {
    await bind(f);
    f.hooks.capture = sim => sim.feed('changed');
    await assert.rejects(f.call('curq-read', { capture: { timeMs: 0 } }), /changed the observed screen/);
    f.hooks.capture = () => {};
    f.hooks.sessions = [{ paneId: '22222222-2222-2222-2222-222222222222', kind: 'local', profileName: 'fixture', rows: cursorRows, cols: cursorCols }];
    await assert.rejects(f.call('curq-read', { capture: { timeMs: 0 } }), /pane changed/);
    f.hooks.sessions = [{ paneId, kind: 'local', profileName: 'fixture', rows: cursorRows + 1, cols: cursorCols }];
    await assert.rejects(f.call('curq-read'), /changed size or pane/);
}));
