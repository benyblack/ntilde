import assert from 'node:assert/strict';
import { test } from 'node:test';
import { snapshotHash } from './agent-observer.mjs';
import { buildRgbReport, collectRgb } from './rgb.mjs';

const runId = 'a'.repeat(32), paneId = '11111111-1111-1111-1111-111111111111';
const normal = [768, -1, -1, null, null, 0, -1, null];
const combined = [8, -1, -1, 0xff0180ff, 0xfffe0200, 3, -1, 0xff0c2238];
const combinedWrite = '\x1b[38;2;1;128;255;48;2;254;2;0;4:3;58;2;12;34;56mA';
// Literal sequences and independent expected numbers, never a simulated terminal.
const samples = [
    ['rgb.fg.semicolon', [['\x1b[38;2;1;128;255mA\x1b[38;2;254;2;0mB\x1b[38;2;0;0;0mC\x1b[38;2;255;255;255mD', 'ABCD', [0xff0180ff, 0xfffe0200, 0xff000000, 0xffffffff].map(argb => [512, -1, -1, argb, null, 0, -1, null])]]],
    ['rgb.fg.colon', [['\x1b[38:2:1:128:255mA\x1b[38:2:254:2:0mB\x1b[38:2:0:0:0mC\x1b[38:2:255:255:255mD', 'ABCD', [0xff0180ff, 0xfffe0200, 0xff000000, 0xffffffff].map(argb => [512, -1, -1, argb, null, 0, -1, null])]]],
    ['rgb.fg.colon-colorspace', [['\x1b[38:2::1:128:255mA\x1b[38:2::254:2:0mB\x1b[38:2::0:0:0mC\x1b[38:2::255:255:255mD', 'ABCD', [0xff0180ff, 0xfffe0200, 0xff000000, 0xffffffff].map(argb => [512, -1, -1, argb, null, 0, -1, null])]]],
    ['rgb.bg.semicolon', [['\x1b[48;2;1;128;255mA\x1b[48;2;254;2;0mB\x1b[48;2;0;0;0mC\x1b[48;2;255;255;255mD', 'ABCD', [0xff0180ff, 0xfffe0200, 0xff000000, 0xffffffff].map(argb => [256, -1, -1, null, argb, 0, -1, null])]]],
    ['rgb.bg.colon', [['\x1b[48:2:1:128:255mA\x1b[48:2:254:2:0mB\x1b[48:2:0:0:0mC\x1b[48:2:255:255:255mD', 'ABCD', [0xff0180ff, 0xfffe0200, 0xff000000, 0xffffffff].map(argb => [256, -1, -1, null, argb, 0, -1, null])]]],
    ['rgb.bg.colon-colorspace', [['\x1b[48:2::1:128:255mA\x1b[48:2::254:2:0mB\x1b[48:2::0:0:0mC\x1b[48:2::255:255:255mD', 'ABCD', [0xff0180ff, 0xfffe0200, 0xff000000, 0xffffffff].map(argb => [256, -1, -1, null, argb, 0, -1, null])]]],
    ['rgb.fg.reset', [[combinedWrite, 'A', [combined]], ['\x1b[39mB', 'AB', [combined, [264, -1, -1, null, 0xfffe0200, 3, -1, 0xff0c2238]]]]],
    ['rgb.bg.reset', [[combinedWrite, 'A', [combined]], ['\x1b[49mB', 'AB', [combined, [520, -1, -1, 0xff0180ff, null, 3, -1, 0xff0c2238]]]]],
    ...[1, 2, 3, 4, 5].map(style => [`underline.style.${style}`, [[`\x1b[4:${style}mA`, 'A', [[776, -1, -1, null, null, style, -1, null]]]]]),
    ['underline.double.sgr21', [['\x1b[21mA', 'A', [[776, -1, -1, null, null, 2, -1, null]]]]],
    ['underline.color.rgb-semicolon', [['\x1b[4;58;2;12;34;56mA', 'A', [[776, -1, -1, null, null, 1, -1, 0xff0c2238]]]]],
    ['underline.color.rgb-colon', [['\x1b[4:3;58:2::12:34:56mA', 'A', [[776, -1, -1, null, null, 3, -1, 0xff0c2238]]]]],
    ['underline.color.indexed', [['\x1b[4:5;58;5;123mA', 'A', [[776, -1, -1, null, null, 5, 123, null]]]]],
    ['underline.color.reset', [[combinedWrite, 'A', [combined]], ['\x1b[59mB', 'AB', [combined, [8, -1, -1, 0xff0180ff, 0xfffe0200, 3, -1, null]]]]],
    ['underline.disable', [[combinedWrite, 'A', [combined]], ['\x1b[24mB', 'AB', [combined, [0, -1, -1, 0xff0180ff, 0xfffe0200, 0, -1, null]]]]],
    ['rgb.underline.reset', [[combinedWrite, 'A', [combined]], ['\x1b[0mB', 'AB', [combined, normal]]]],
];

function screen(first = '', details = [], control = 'KEEP!') {
    const runs = [];
    for (let index = 0; index < 180; index++) {
        const [flags, fg, bg, foregroundArgb, backgroundArgb, underlineStyle, underlineColorIndex, underlineArgb] = details[index] ?? normal;
        const cell = { flags, fg, bg, foregroundArgb, backgroundArgb, underlineStyle, underlineColorIndex, underlineArgb };
        const last = runs.at(-1);
        if (last && Object.keys(cell).every(key => cell[key] === last[key])) last.count++;
        else runs.push({ count: 1, ...cell });
    }
    return { rows: 3, cols: 60, lines: [first, control, ''], cursorRow: 0, cursorCol: first.length,
        cursorVisible: true, cellDetails: runs };
}

function frames() {
    const values = [screen('', [], ''), screen('NTILDE_RG_' + runId, [], ''),
        ...samples.flatMap(([, steps]) => [screen(), ...steps.map(([, text, details]) => screen(text, details))]), screen('', [], '')];
    return values.map((value, index) => ({ index, paneId, screen: value, sha256: snapshotHash(value),
        kind: index === 0 ? 'rgb-start' : index === 1 ? 'rgb-bind' : index === values.length - 1 ? 'rgb-finish' : 'rgb-read',
        barrier: index === 0 ? null : '\x1b[?62;4;22c' }));
}
const rehash = frames => { for (const frame of frames) frame.sha256 = snapshotHash(frame.screen); };

test('20 RGB and underline experiments derive from independently supplied whole viewport details', () => {
    const report = buildRgbReport(frames(), runId);
    assert.equal(report.snapshots.length, 48);
    assert.deepEqual(report.observations.map(item => item.featureId), samples.map(([id]) => id));
    assert.ok(report.observations.every(item => item.result === 'passed'));
});

for (const [name, change, result] of [
    ['wrong literal color with correct text', f => { f[3].screen.cellDetails[0].foregroundArgb = 0xff000000; }, 'failed'],
    ['matching target with styled control', f => { f[2].screen = screen('', [[769, -1, -1, null, null, 0, -1, null]]); }, 'inconclusive'],
    ['unaffected cell changes', f => { f[3].screen.cellDetails.at(-1).flags |= 1; }, 'failed'],
    ['wrong cursor', f => { f[3].screen.cursorCol++; }, 'failed'],
]) test(name, () => {
    const input = frames(); change(input); rehash(input);
    assert.equal(buildRgbReport(input, runId).observations[0].result, result);
});

for (const [name, change] of [
    ['missing details', f => { delete f[3].screen.cellDetails; }],
    ['partial details', f => { f[3].screen.cellDetails[0].count++; }],
    ['changed hash', f => { f[3].sha256 = '0'.repeat(64); }],
    ['another pane', f => { f[3].paneId = 'other'; }],
    ['missing barrier', f => { f[3].barrier = null; }],
    ['wrong sample kind', f => { f[3].kind = 'sgr-read'; }],
    ['unrestored RGB', f => { f.at(-1).screen = screen('', [combined], ''); rehash(f); }],
]) test('report rejects ' + name, () => { const input = frames(); change(input); assert.throws(() => buildRgbReport(input, runId)); });

test('writes remain serial, match literal experiments and restore the main screen', async () => {
    const writes = [], reads = []; let pending = false;
    await collectRgb({ config: { runId }, owner: async (kind, barrier) => {
        reads.push(kind); if (kind !== 'rgb-start') assert.equal(barrier, '\x1b[?62;4;22c');
        await Promise.resolve(); pending = false;
    }, fence: { write: async sequence => {
        assert.equal(pending, false); pending = true; writes.push(sequence); return '\x1b[?62;4;22c';
    } } });
    assert.ok(writes[0].endsWith('NTILDE_RG_' + runId));
    let offset = 1;
    for (const [, steps] of samples) {
        assert.ok(writes[offset++].includes('KEEP!'));
        assert.deepEqual(writes.slice(offset, offset + steps.length), steps.map(([sequence]) => sequence)); offset += steps.length;
    }
    assert.ok(writes.at(-1).endsWith('\x1b[?1049l'));
    assert.equal(reads.at(-1), 'rgb-finish');
});
