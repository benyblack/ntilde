import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { snapshotHash } from './agent-observer.mjs';
import { buildSgrReport, collectSgr } from './sgr.mjs';

const runId = 'a'.repeat(32), paneId = '11111111-1111-1111-1111-111111111111';
const normal = [768, -1, -1], combined = [49167, 1, 4];
// Independent projection expectations and literal VT sequences; no VT emulator.
const samples = [
    ['sgr.bold', [['\x1b[1mA', 'A', [[769, -1, -1]]]]],
    ['sgr.italic', [['\x1b[3mA', 'A', [[772, -1, -1]]]]],
    ['sgr.underline', [['\x1b[4mA', 'A', [[776, -1, -1]]]]],
    ['sgr.inverse', [['\x1b[7mA', 'A', [[770, -1, -1]]]]],
    ...[['bold', 22, 49166], ['italic', 23, 49163], ['underline', 24, 49159], ['inverse', 27, 49165]]
        .map(([name, off, flags]) => ['sgr.selective-reset.' + name, [
            ['\x1b[1;3;4;7;31;44mA', 'A', [combined]], [`\x1b[${off}mB`, 'AB', [combined, [flags, 1, 4]]],
        ]]),
    ['sgr.reset', [['\x1b[1;3;4;7;31;44mA', 'A', [combined]], ['\x1b[0mB', 'AB', [combined, normal]]]],
    ['sgr.fg.standard', [['\x1b[30mA\x1b[31mB\x1b[32mC\x1b[33mD\x1b[34mE\x1b[35mF\x1b[36mG\x1b[37mH',
        'ABCDEFGH', [0, 1, 2, 3, 4, 5, 6, 7].map(index => [16896, index, -1])]]],
    ['sgr.fg.bright', [['\x1b[90mA\x1b[91mB\x1b[92mC\x1b[93mD\x1b[94mE\x1b[95mF\x1b[96mG\x1b[97mH',
        'ABCDEFGH', [8, 9, 10, 11, 12, 13, 14, 15].map(index => [16896, index, -1])]]],
    ['sgr.fg.256', [['\x1b[38;5;0mA\x1b[38;5;1mB\x1b[38;5;15mC\x1b[38;5;16mD\x1b[38;5;231mE\x1b[38;5;232mF\x1b[38;5;255mG',
        'ABCDEFG', [0, 1, 15, 16, 231, 232, 255].map(index => [16896, index, -1])]]],
    ['sgr.bg.standard', [['\x1b[40mA\x1b[41mB\x1b[42mC\x1b[43mD\x1b[44mE\x1b[45mF\x1b[46mG\x1b[47mH',
        'ABCDEFGH', [0, 1, 2, 3, 4, 5, 6, 7].map(index => [33024, -1, index])]]],
    ['sgr.bg.bright', [['\x1b[100mA\x1b[101mB\x1b[102mC\x1b[103mD\x1b[104mE\x1b[105mF\x1b[106mG\x1b[107mH',
        'ABCDEFGH', [8, 9, 10, 11, 12, 13, 14, 15].map(index => [33024, -1, index])]]],
    ['sgr.bg.256', [['\x1b[48;5;0mA\x1b[48;5;1mB\x1b[48;5;15mC\x1b[48;5;16mD\x1b[48;5;231mE\x1b[48;5;232mF\x1b[48;5;255mG',
        'ABCDEFG', [0, 1, 15, 16, 231, 232, 255].map(index => [33024, -1, index])]]],
    ['sgr.fg.default', [['\x1b[1;3;4;7;31;44mA', 'A', [combined]], ['\x1b[39mB', 'AB', [combined, [33039, -1, 4]]]]],
    ['sgr.bg.default', [['\x1b[1;3;4;7;31;44mA', 'A', [combined]], ['\x1b[49mB', 'AB', [combined, [16911, 1, -1]]]]],
];

function screen(first = '', attributes = [], control = 'KEEP!') {
    const runs = [];
    for (let index = 0; index < 180; index++) {
        const [flags, fg, bg] = attributes[index] ?? normal;
        const last = runs.at(-1);
        if (last && last.flags === flags && last.fg === fg && last.bg === bg) last.count++;
        else runs.push({ count: 1, flags, fg, bg });
    }
    return { rows: 3, cols: 60, lines: [first, control, ''], cursorRow: 0, cursorCol: first.length,
        cursorVisible: true, attributeRuns: runs };
}

function frames() {
    const values = [screen('', [], ''), screen('NTILDE_SG_' + runId, [], ''),
        ...samples.flatMap(([, steps]) => [screen(), ...steps.map(([, text, attributes]) => screen(text, attributes))]), screen('', [], '')];
    return values.map((value, index) => ({ index, paneId, screen: value, sha256: snapshotHash(value),
        kind: index === 0 ? 'sgr-start' : index === 1 ? 'sgr-bind' : index === values.length - 1 ? 'sgr-finish' : 'sgr-read',
        barrier: index === 0 ? null : '\x1b[?62;4;22c' }));
}

const rehash = frames => { for (const frame of frames) frame.sha256 = snapshotHash(frame.screen); };

test('all 17 SGR cases derive from independent flag/index snapshots', () => {
    const report = buildSgrReport(frames(), runId);
    assert.deepEqual(report.observations.map(item => item.featureId), samples.map(([id]) => id));
    assert.ok(report.observations.every(item => item.result === 'passed'));
    assert.equal(report.snapshots.length, 44);
    assert.match(report.scope, /No RGB.*pixels.*stock/);
});

for (const [name, change, index, result] of [
    ['correct text without bold', f => { f[3].screen = screen('A'); }, 0, 'failed'],
    ['bad control with matching target', f => { f[2].screen = screen('', [[769, -1, -1]]); }, 0, 'inconclusive'],
    ['unaffected cell flag changes', f => { f[3].screen = screen('A', [[769, -1, -1], [772, -1, -1]]); }, 0, 'failed'],
    ['reset clears unrelated style or color', f => { f[12].screen = screen('AB', [combined, normal]); }, 4, 'failed'],
    ['wrong palette index', f => { f[26].screen = screen('ABCDEFGH', [0, 1, 2, 3, 4, 5, 6, 8].map(i => [16896, i, -1])); }, 9, 'failed'],
    ['unaffected row attribute changes', f => {
        const attributes = Array.from({ length: 61 }, () => normal); attributes[0] = [769, -1, -1]; attributes[60] = [772, -1, -1];
        f[3].screen = screen('A', attributes);
    }, 0, 'failed'],
]) test(name, () => {
    const input = frames(); change(input); rehash(input);
    assert.equal(buildSgrReport(input, runId).observations[index].result, result);
});

for (const [name, change] of [
    ['missing attributes', f => { delete f[3].screen.attributeRuns; }],
    ['bad attribute count', f => { f[3].screen.attributeRuns[0].count++; }],
    ['changed hash', f => { f[3].sha256 = '0'.repeat(64); }],
    ['another pane', f => { f[3].paneId = 'other'; }],
    ['missing barrier', f => { f[3].barrier = null; }],
    ['wrong sample kind', f => { f[3].kind = 'cell-read'; }],
    ['unrestored main attributes', f => { f.at(-1).screen = screen('', [[769, -1, -1]], ''); rehash(f); }],
]) test('SGR report rejects ' + name, () => { const input = frames(); change(input); assert.throws(() => buildSgrReport(input, runId)); });

test('SGR writes match literal experiments and remain serial before reads and restoration', async () => {
    const writes = [], reads = []; let pending = false;
    await collectSgr({ config: { runId }, owner: async (kind, barrier) => {
        reads.push(kind); if (kind !== 'sgr-start') assert.equal(barrier, '\x1b[?62;4;22c');
        await Promise.resolve(); pending = false;
    }, fence: { write: async sequence => {
        assert.equal(pending, false, 'A previous sample must complete before another write.');
        pending = true; writes.push(sequence); return '\x1b[?62;4;22c';
    } } });
    assert.ok(writes[0].endsWith('NTILDE_SG_' + runId));
    let offset = 1;
    for (const [, steps] of samples) {
        assert.ok(writes[offset++].includes('KEEP!'));
        assert.deepEqual(writes.slice(offset, offset + steps.length), steps.map(([sequence]) => sequence));
        offset += steps.length;
    }
    assert.ok(writes.at(-1).endsWith('\x1b[?1049l'));
    assert.equal(reads.at(-1), 'sgr-finish');
});

test('published SGR evidence re-derives and matches its final receipt', () => {
    const published = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-02-sgr-indices.json', import.meta.url)));
    const report = published.sgrReport, receipt = published.completionReceipt;
    assert.deepEqual(buildSgrReport(report.snapshots, report.runId), report);
    assert.equal(createHash('sha256').update(JSON.stringify(report, null, 2) + '\n').digest('hex'), receipt.sgrEvidence.sha256);
    assert.equal(report.runId, receipt.runId);
    assert.equal(receipt.sgrEvidence.runId, report.runId);
    assert.equal(receipt.sgrEvidence.suiteId, report.suiteId);
    assert.equal(receipt.sgrEvidence.file, 'sgr-observations.json');
    assert.equal(receipt.fixtureRemoved, true);
    assert.deepEqual(receipt.sgrEvidence.counts, { passed: 17 });
    assert.deepEqual(published.cellEvidence.counts, { passed: 8 });
    assert.equal(published.cellEvidence.runId, receipt.runId);
    assert.equal(published.cellEvidence.suiteId, receipt.suiteId);
    assert.equal(published.cellEvidence.file, receipt.file);
    assert.equal(published.cellEvidence.sha256, receipt.sha256);
    assert.deepEqual(published.stockCollector.counts, { inconclusive: 177, supported: 78 });
    assert.equal(published.stockCollector.rawSha256, receipt.rawSha256);
});
