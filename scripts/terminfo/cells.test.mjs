import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { snapshotHash } from './agent-observer.mjs';
import { buildCellReport, collectCells } from './cells.mjs';

const runId = 'a'.repeat(32), paneId = '11111111-1111-1111-1111-111111111111';
const screen = (text = '', row = 0, col = 0, control = 'KEEP!') => ({ rows: 3, cols: 60,
    lines: [text, control, ''], cursorRow: row, cursorCol: col, cursorVisible: true });

// Independent expected samples for the eight experiments, not a VT emulator.
const samples = [
    [['', 0, 0], ['Hello', 0, 5]],
    [['ABCDE', 0, 2], ['ABZDE', 0, 3]],
    [['ABCDE', 0, 0], ['ABCDE', 1, 3], ['ABCDE', 0, 1]],
    [['ABCDE', 0, 0], ['ABCDE', 0, 1], ['ABCDE', 1, 1]],
    [['ABCDE', 0, 2], ['AB', 0, 2]],
    [['ABCDE', 0, 2], ['   DE', 0, 2]],
    [['ABCDE', 0, 2], ['', 0, 2]],
    [['ABCD', 0, 1], ['AXBCD', 0, 2], ['AXYCD', 0, 3]],
];

function frames() {
    const values = [screen('', 0, 0, ''), screen(`NTILDE_QA_${runId}`, 0, 42, ''),
        ...samples.flat().map(value => screen(...value)), screen('', 0, 0, '')];
    return values.map((value, index) => ({ index, paneId, screen: value, sha256: snapshotHash(value),
        kind: index === 0 ? 'cell-start' : index === 1 ? 'cell-bind' : index === values.length - 1 ? 'cell-finish' : 'cell-read',
        barrier: index === 0 ? null : '\x1b[?62;4;22c' }));
}

test('derives all eight ASCII experiments from independent cell and cursor samples', () => {
    const report = buildCellReport(frames(), runId);
    assert.equal(report.observations.length, 8);
    assert.ok(report.observations.every(item => item.result === 'passed'));
    assert.match(report.scope, /not pixels or stock collector grades/);
    assert.equal(report.observations.at(-1).featureId, 'modes.insert-replace');
    assert.equal(report.observations.at(-1).assertions.length, 2);
});

for (const [name, change, result] of [
    ['DA reply alone without Hello cells', f => { f[3].screen.lines[0] = ''; }, 'failed'],
    ['unexpected change to an unaffected row', f => { f[3].screen.lines[1] = 'CHANGED'; }, 'failed'],
    ['unexpected change outside the sampled text', f => { f[3].screen.lines[2] = 'EXTRA'; }, 'failed'],
    ['unqualified control despite a matching target', f => { f[2].screen.lines[0] = 'already present'; }, 'inconclusive'],
]) {
    test(name, () => {
        const input = frames(); change(input);
        for (const item of input) item.sha256 = snapshotHash(item.screen);
        assert.equal(buildCellReport(input, runId).observations[0].result, result);
    });
}

test('cursor-only and insert/replace failures are observed independently of unchanged text', () => {
    const input = frames(); input[7].screen.cursorRow = 0;
    input.at(-3).screen.lines[0] = 'AXCD';
    for (const item of input) item.sha256 = snapshotHash(item.screen);
    const results = buildCellReport(input, runId).observations;
    assert.equal(results[2].result, 'failed');
    assert.equal(results[7].result, 'failed');
});

for (const [name, change, error] of [
    ['missing experiment', f => f.splice(3, 1), /Incomplete/],
    ['changed hash', f => { f[3].sha256 = '0'.repeat(64); }, /Changed/],
    ['changed pane', f => { f[3].paneId = 'other'; }, /Changed/],
    ['empty lines', f => { f[3].screen.lines = []; }, /Invalid/],
    ['resize', f => { f[3].screen.cols = 61; }, /Changed/],
    ['missing barrier', f => { f[3].barrier = null; }, /ordering barrier/],
    ['wrong nonce cursor', f => { f[1].screen.cursorCol = 0; f[1].sha256 = snapshotHash(f[1].screen); }, /not bound/],
    ['unrestored main screen', f => { f.at(-1).screen.lines[0] = 'leftover'; f.at(-1).sha256 = snapshotHash(f.at(-1).screen); }, /not restored/],
]) {
    test('refuses ' + name, () => { const input = frames(); change(input); assert.throws(() => buildCellReport(input, runId), error); });
}

test('rejects another run nonce', () => assert.throws(() => buildCellReport(frames(), 'b'.repeat(32)), /not bound/));

test('collection fences each write before requesting parent-owned evidence and always restores', async () => {
    const events = [];
    await collectCells({ config: { cellNonce: 'nonce' },
        owner: async (kind, barrier) => events.push({ kind, barrier }),
        fence: { write: async sequence => { events.push({ sequence }); return '\x1b[?62;4;22c'; } } });
    assert.equal(events[0].kind, 'cell-start');
    assert.equal(events.at(-1).kind, 'cell-finish');
    assert.match(events.at(-2).sequence, /\x1b\[\?1049l$/);
    for (let index = 1; index < events.length; index += 2) {
        assert.equal(typeof events[index].sequence, 'string');
        assert.equal(events[index + 1].barrier, '\x1b[?62;4;22c');
    }
});

test('a failed sample still attempts main-screen restoration', async () => {
    const calls = [];
    await assert.rejects(collectCells({ config: { cellNonce: 'nonce' },
        owner: async kind => { calls.push(kind); if (kind === 'cell-read') throw new Error('read failed'); },
        fence: { write: async () => '\x1b[?62;4;22c' } }), /read failed/);
    assert.equal(calls.at(-1), 'cell-finish');
});

test('published measurement re-derives from retained snapshots and matches its report digest', () => {
    const published = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-02-ascii-cells.json', import.meta.url)));
    const report = published.cellReport, receipt = published.cellReceipt;
    assert.deepEqual(buildCellReport(report.snapshots, report.runId), report);
    const bytes = JSON.stringify(report, null, 2) + '\n';
    assert.equal(createHash('sha256').update(bytes).digest('hex'), receipt.sha256);
    assert.equal(receipt.runId, report.runId);
    assert.equal(receipt.fixtureRemoved, true);
    assert.deepEqual(receipt.counts, { passed: 8 });
    assert.deepEqual(published.stockCollector.counts, { inconclusive: 177, supported: 78 });
    assert.equal(published.stockCollector.rawSha256, receipt.rawSha256);
});
