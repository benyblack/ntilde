import assert from 'node:assert/strict';

export const suiteId = 'ntilde-owned-osc555-visual-flash-v1';
export const contractsVersion = 1;
export const caseIds = ['extensions.osc555-flash'];
export const scopeFor = visual => ({ id: suiteId, persistence: 'Off', deadlineMs: 90000, visual });
export const clockMs = () => Number(process.hrtime.bigint()) / 1e6;
export const homeAndErase = '\x1b[1;1H\x1b[2K';
export const cpr = '\x1b[6n';
export const nonce = runId => 'FLASH_' + runId;
export const prepare = runId => '\x1b[?47h\x1b[?25l\x1b[2J\x1b[4;3H\x1b[32m' + nonce(runId) + '\x1b[0m' + homeAndErase;
export const restore = '\x1b[2J\x1b[H\x1b[?25h\x1b[?47l';
export const trials = [
    { name: 'control', writes: ['\x07'] },
    { name: 'pinned', writes: [homeAndErase, '\x1b]555\x07'], query: cpr },
    { name: 'foot', writes: ['\x1b]555\x1b\\'] },
];

export function frameDifference(baseline, image) {
    assert.equal(image.width, baseline.width);
    assert.equal(image.height, baseline.height);
    let changed = 0, visible = 0;
    const a = baseline.pixels, b = image.pixels;
    for (let i = 0; i < a.length; i += 4) {
        if (a.readUInt32BE(i) !== b.readUInt32BE(i)) changed++;
        if (Math.max(...[0, 1, 2].map(c => Math.abs(a[i + c] - b[i + c]))) >= 8) visible++;
    }
    return { changed, visible, pixels: image.width * image.height };
}

// start/end enclose the rasterization and PNG transfer. Use the worst gap between
// possible raster instants on either side of the write before accepting silence.
export function classify(frames, sent, fenced) {
    assert.ok(Number.isFinite(sent) && Number.isFinite(fenced) && sent <= fenced);
    for (const [i, f] of frames.entries()) assert.ok(Number.isFinite(f.start) && Number.isFinite(f.end)
        && f.start <= f.end && (!i || f.start >= frames[i - 1].end), 'invalid frame timing');
    const before = frames.findLastIndex(f => f.end < sent);
    const restored = frames.findIndex(f => f.start >= fenced + 170);
    const during = frames.findIndex(f => f.start >= sent && f.end <= fenced + 160 && f.visible >= f.pixels * 0.9);
    const sample = frames.findIndex(f => f.start >= fenced && f.end <= sent + 80);
    const observed = { before, during: during >= 0 ? during : sample, restored };
    if (before < 0 || restored < 0 || frames.slice(0, before + 1).some(f => f.changed)) {
        return { state: 'inconclusive', reason: 'missing stable baseline or late frame', ...observed };
    }
    if (frames.slice(restored).some(f => f.changed)) return { state: 'unrestored', ...observed };
    if (during >= 0) return { state: 'flashed', ...observed };
    const window = frames.slice(before, restored + 1);
    const maxGapMs = Math.max(...window.slice(1).map((f, i) => f.end - window[i].start));
    if (sample < 0 || maxGapMs > 60 || frames.some(f => f.changed)) {
        return { state: 'inconclusive', reason: 'flash interval or pane-wide contrast unresolved', maxGapMs, ...observed };
    }
    return { state: 'steady', maxGapMs, ...observed };
}

