import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createServer } from 'node:net';
import { mkdtempSync, rmSync, readFileSync, writeFileSync, symlinkSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { captureRequest, agentRequest } from './agent-observer.mjs';
import { OwnedPixelObserver, validatePixelCapture, retainedPixel } from './pixels-capture.mjs';
import { pixelCapture, pixelScreen, pixelPaneId } from './fixtures/pixel-capture.mjs';
import { pixelPlan, collectPixels, pixelScenes } from './pixels-cases.mjs';
import { regionPixels, countColorCoverage } from './pixels.mjs';
const barrier = '\x1b[?62;4;22c';
const runId = 'a'.repeat(32), nonce = `NTILDE_PX_${runId}`;

test('fixed render transport sends only bounded non-acting controlled capture parameters', async () => {
    let request;
    const server = createServer(socket => socket.once('data', bytes => {
        request = JSON.parse(bytes); socket.end(JSON.stringify({ v: 1, id: 1, result: pixelCapture() }) + '\n');
    }));
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const endpoint = { host: '127.0.0.1', port: server.address().port };
    try {
        await captureRequest(endpoint, pixelPaneId, 530, 1000);
        assert.deepEqual(request, { v: 1, id: 1, method: 'captureScreen', params: { paneId: pixelPaneId, mode: 'render', inline: true,
            maxWidth: 0, scale: 1, includeRenderDetails: true, presentationTimeMs: 530 } });
        assert.throws(() => captureRequest(endpoint, 'other', 0, 1000));
        assert.throws(() => captureRequest(endpoint, pixelPaneId, 60001, 1000));
        assert.throws(() => captureRequest(endpoint, pixelPaneId, -1, 1000));
        assert.throws(() => agentRequest(endpoint, 'captureScreen', {}, 1000), /not read-only/);
    } finally { await new Promise(resolve => server.close(resolve)); }
});

test('wire validation accepts synthetic phases but rejects metadata, image and clock substitutions', () => {
    const value = pixelCapture();
    assert.equal(validatePixelCapture(value, pixelScreen(), 0).image.width, 600);
    for (const mutate of [
        r => { r.mode = 'live'; }, r => { r.scale = 2; }, r => { r.downscaled = true; }, r => { r.inlineOmitted = true; },
        r => { delete r.pngBase64; }, r => { r.pngBase64 += '\n'; }, r => { r.byteCount++; }, r => { r.width++; },
        r => { r.rows++; }, r => { r.presentationTimeMs = 530; }, r => { r.renderDetails.presentation.timeMs = 530; },
        r => { r.renderDetails.presentation.textBlinkVisible = false; }, r => { r.renderDetails.presentation.cursorVisible = false; },
        r => { r.renderDetails.font.primarySha256 = null; }, r => { r.renderDetails.font.weight = 0; },
        r => { r.renderDetails.font.fallbackFamilies = Array(65).fill('a'); }, r => { r.renderDetails.grid.cellWidthDip++; },
        r => { r.renderDetails.theme.ansiPaletteArgb.pop(); }, r => { r.renderDetails.theme.foregroundOverride = true; },
        r => { r.renderDetails.minimumContrast = 2; }, r => { r.renderDetails.enableLigatures = true; },
        r => { r.renderDetails.schemaVersion = 2; }, r => { r.renderDetails.platform = 'unknown'; },
    ]) { const changed = structuredClone(value); mutate(changed); assert.throws(() => validatePixelCapture(changed, pixelScreen(), 0)); }
    const screen = pixelScreen(); screen.cursorMode.isDefault = false;
    const changed = pixelCapture(screen); changed.renderDetails.presentation.cursorStyle = 0;
    assert.throws(() => validatePixelCapture(changed, screen, 0));
    for (const time of [0, 529, 530, 1059, 1060]) assert.ok(validatePixelCapture(pixelCapture(pixelScreen(), time), pixelScreen(), time));
});

async function fixture(action) {
    const output = mkdtempSync(join(tmpdir(), 'nt-pixel-'));
    let screen = pixelScreen(), sessions = [{ paneId: pixelPaneId, kind: 'local', profileName: 'fixture', rows: 10, cols: 60 }];
    let captureHook = () => {};
    const observer = new OwnedPixelObserver({ output, appData: '/never/discover', ownerPid: 42, profileName: 'fixture', nonce,
        endpoint: () => 'private-fixture', expectedPaneId: pixelPaneId,
        rpc: async (_, method) => method === 'listSessions' ? { sessions } : screen,
        captureRpc: async (_, pane, time) => { assert.equal(pane, pixelPaneId); const result = pixelCapture(screen, time); captureHook(result); return result; } });
    const call = (kind, extra = {}) => observer.handle({ kind, barrier: kind === 'pixel-start' ? null : barrier, timeMs: 0, ...extra }, () => 1000);
    try { await action({ observer, output, call, setScreen: value => { screen = value; },
        setSessions: value => { sessions = value; }, setCapture: hook => { captureHook = hook; } }); }
    finally { rmSync(output, { recursive: true, force: true }); }
}
async function bind(f) {
    await f.call('pixel-start');
    const screen = pixelScreen(); screen.lines[0] = nonce; screen.cursorCol = nonce.length;
    f.setScreen(screen); await f.call('pixel-bind');
}

test('pixel observer retains immutable PNGs and refuses out-of-order/early-finish requests', async () => fixture(async f => {
    await assert.rejects(f.call('pixel-read', pixelPlan[0]), /not started/);
    await bind(f);
    const frame = f.observer.frames[0];
    assert.equal(retainedPixel(f.output, frame).width, 600);
    await assert.rejects(f.call('pixel-read', { sceneId: 'other' }));
    await assert.rejects(f.call('pixel-finish'));
    for (const plan of pixelPlan) await f.call('pixel-read', plan);
    f.setScreen(pixelScreen()); await f.call('pixel-finish');
    assert.equal(f.observer.restored, true);
    await assert.rejects(f.call('pixel-start'), /complete/);
    assert.equal(f.observer.frames.length, pixelPlan.length + 3);
    const path = join(f.output, frame.pixel.file);
    const bytes = readFileSync(path); bytes[10] ^= 1; writeFileSync(path, bytes);
    assert.throws(() => retainedPixel(f.output, frame));
}));
for (const [name, change] of [
    ['changed screen during capture', f => f.setCapture(() => { const s = pixelScreen(); s.lines[0] = 'changed'; f.setScreen(s); })],
    ['changed font', f => f.setCapture(r => { r.renderDetails.font.primarySha256 = 'b'.repeat(64); })],
    ['another pane', f => f.setSessions([{ paneId: '22222222-2222-2222-2222-222222222222', kind: 'local', profileName: 'fixture', rows: 10, cols: 60 }])],
    ['changed geometry', f => f.setSessions([{ paneId: pixelPaneId, kind: 'local', profileName: 'fixture', rows: 11, cols: 60 }])],
    ['unfenced read', () => {}],
]) test('pixel observer refuses ' + name, async () => fixture(async f => {
    await bind(f); change(f);
    await assert.rejects(f.call('pixel-read', { ...pixelPlan[0], ...(name === 'unfenced read' ? { barrier: null } : {}) }));
    assert.equal(f.observer.frames.length, 2);
    assert.equal(f.observer.restored, undefined);
}));

test('collector captures all phase points without rewriting phase inputs and always restores', async () => {
    const writes = [], calls = [];
    await collectPixels({ config: { runId }, fence: { write: async value => { writes.push(value); return barrier; } },
        owner: async (kind, fence, extra) => { calls.push({ kind, fence, extra }); return { screen: pixelScreen() }; } });
    assert.equal(writes.length, pixelScenes.length + 2);
    assert.equal(calls.length, pixelPlan.length + 3);
    assert.deepEqual(calls.filter(call => call.kind === 'pixel-read').map(call => call.extra), pixelPlan);
    assert.equal(writes.at(-1), '\x1b[?1049l\x1b[!p');
    const failureWrites = [];
    await assert.rejects(collectPixels({ config: { runId }, fence: { write: async value => { failureWrites.push(value); return barrier; } },
        owner: async kind => { if (kind === 'pixel-read') throw new Error('capture failed'); return { screen: pixelScreen() }; } }), /capture failed/);
    assert.equal(failureWrites.at(-1), '\x1b[?1049l\x1b[!p');
});

test('coverage distinguishes antialiased green from background, gray and unrelated colors', () => {
    assert.equal(countColorCoverage(Buffer.from([24, 120, 56, 255, 32, 224, 96, 255, 16, 16, 16, 255,
        128, 128, 128, 255, 24, 120, 140, 255]), [32, 224, 96, 255]), 2);
    const image = { width: 2, height: 2, pixels: Buffer.alloc(16, 7) };
    assert.equal(regionPixels(image, { originXPx: 0, originYPx: 0, cellWidthPx: 1, cellHeightPx: 1 }, { row: 1, col: 1, rows: 1, cols: 1 }).length, 4);
    assert.throws(() => regionPixels(image, { originXPx: 0, originYPx: 0, cellWidthPx: 1, cellHeightPx: 1 }, { row: 1, col: 2, rows: 1, cols: 1 }));
});
