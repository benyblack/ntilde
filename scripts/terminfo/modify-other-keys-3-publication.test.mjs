import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { buildPublication, publication, withRun } from './modify-other-keys-3-publication.mjs';
import { deriveRun } from './modify-other-keys-3-owned.mjs';
import { readJson } from './unicode-publication-inputs.mjs';

test('all three receipts replay, both pinned paths are inconclusive, and the child distinguishes the same four plain keys', async () => {
    const { report, summary } = await buildPublication(), [item] = report.cases;
    assert.deepEqual(summary, readJson(publication, 'summary.json'));
    assert.deepEqual(report, readJson(publication, 'report.json'));
    const prior = report.inconclusiveAttempt;
    assert.equal(prior.receipt.verdict, 'inconclusive');
    assert.deepEqual(prior.mismatches.map(row => [row.id, row.role, row.expectedHex, row.actualHex]),
        [['level2-a', 'control', '61', '7f61']]);
    assert.equal(item.contractResult.outcome, 'supported');
    assert.equal(item.pinned.termless.observation.outcome, 'inconclusive');
    assert.equal(item.pinned.terminal.observation.outcome, 'inconclusive');
    assert.equal(item.pinned.termless.response, '{"declaredModifyOtherKeys":false}');
    const rows = item.contractResult.observations;
    assert.equal(rows.length, 24);
    for (const [id, bytes] of [['level3-missing-symbol-text', '61'], ['level3-missing-symbol-key-only', '']]) {
        const row = rows.find(item => item.id === id);
        assert.equal(row.event.keySymbol, undefined);
        assert.deepEqual([row.handled, row.actualHex], [false, bytes]);
    }
    for (const [key, plain, frame] of [['a', 'a', '97'], ['digit2', '2', '50'], ['semicolon', ';', '59'], ['space', ' ', '32']]) {
        const level2 = rows.find(r => r.id === 'level2-' + key), level3 = rows.find(r => r.id === 'level3-' + key);
        assert.deepEqual(level2.event, level3.event);
        assert.equal(level3.event.keySymbol, plain);
        assert.equal(level2.actualHex, Buffer.from(plain).toString('hex'));
        assert.equal(level3.actualHex, Buffer.from('\x1b[27;1;' + frame + '~').toString('hex'));
        assert.deepEqual([level2.handled, level3.handled, level3.textHandled], [false, true, true]);
    }
});

test('a duplicated text byte fails the effect, a broken delivery control is inconclusive, and a missing fence invalidates the receipt', () => withRun('fixed', dir => {
    const file = join(dir, 'mok3-journal.json'), raw = readFileSync(file);
    function changeCapture(id, mutate) {
        const journal = JSON.parse(raw);
        mutate(journal.frames.find(f => f.caseId === id && f.kind === 'input-read').capture);
        writeFileSync(file, JSON.stringify(journal));
        return deriveRun(dir).receipt.verdict;
    }
    const appendText = sample => { sample.hex += '61'; sample.byteLength++; };
    assert.equal(changeCapture('level3-a', appendText), 'unsupported');
    assert.equal(changeCapture('level2-a', appendText), 'inconclusive');
    assert.throws(() => changeCapture('level3-key-up', sample => { sample.barrier = ''; }));
}));
