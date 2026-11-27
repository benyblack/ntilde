import assert from 'node:assert/strict';
import { appendFileSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { deriveFlash } from './osc-flash-owned.mjs';
import { readJson, withOwnedArchive } from './unicode-publication-inputs.mjs';
import { classify } from './osc-flash-contract.mjs';
import { combineDisabled, effectVerdict, publication, publish } from './osc-flash-publication.mjs';

test('all four owned receipts, the exact pinned callback and every published frame rederive', async () => {
    const { cases: [result], runs } = await publish(true);
    assert.equal(result.termless, null);
    assert.equal(result.pinned.observation.outcome, 'inconclusive');
    assert.equal(result.contractResult.outcome, 'inconclusive');
    assert.equal(result.stock.outcome, 'inconclusive');
    const states = name => runs[name].captures.map(c => c.state);
    assert.deepEqual(states('baseline'), ['flashed', 'inconclusive', 'steady']);
    assert.deepEqual(states('enabled'), ['flashed', 'flashed', 'flashed']);
    assert.deepEqual(states('disabled'), ['steady', 'inconclusive', 'steady']);
    assert.deepEqual(states('disabled-repeat'), ['steady', 'inconclusive', 'inconclusive']);
});

test('a late or sparse capture, an unrelated pixel change, and a missing control cannot qualify the flash', () => {
    const steady = { name: 'pinned', state: 'steady', changedFrames: 0 };
    assert.equal(combineDisabled({ ...steady, state: 'inconclusive' }, steady), 'steady');
    assert.equal(combineDisabled({ ...steady, state: 'inconclusive', changedFrames: 1 }, steady), 'inconclusive');
    const frames = Array.from({ length: 24 }, (_, i) => ({
        start: 10 * i, end: 10 * i + 3, pixels: 1000, changed: 0, visible: 0,
    }));
    assert.equal(classify(frames, 31, 35).state, 'steady');
    assert.equal(classify(frames.filter(f => f.start <= 20 || f.start >= 120), 31, 35).state, 'inconclusive');
    frames[5].changed = frames[5].visible = 2;
    assert.equal(classify(frames, 31, 35).state, 'inconclusive');
    frames[5].changed = frames[5].visible = 1000;
    assert.equal(classify(frames, 31, 35).state, 'flashed');
    frames.at(-1).changed = 1;
    assert.equal(classify(frames, 31, 35).state, 'unrestored');
    assert.equal(effectVerdict(['steady', 'flashed', 'flashed'], ['steady', 'steady', 'steady']), 'inconclusive');
    assert.equal(effectVerdict(['flashed', 'flashed', 'flashed'], ['steady', 'flashed', 'steady']), 'inconclusive');
});

test('receipt replay refuses swapped trials, a changed palette, and a modified PNG', () => {
    const manifest = readJson(publication, 'manifest.json').runs.enabled;
    withOwnedArchive(join(publication, 'enabled'), manifest, dir => {
        const log = join(dir, 'flash.jsonl'), original = readFileSync(log, 'utf8');
        const corrupt = change => {
            const entries = original.trimEnd().split('\n').map(line => JSON.parse(line));
            change(entries);
            writeFileSync(log, entries.map(entry => JSON.stringify(entry)).join('\n') + '\n');
            assert.throws(() => deriveFlash(dir));
        };
        corrupt(entries => { entries.find(e => e.stage === 'pinned').writes[1] = '\x07'; });
        corrupt(entries => { entries.find(e => e.stage === 'foot').screen.colorBinding.ansiPaletteArgb.reverse(); });
        writeFileSync(log, original);
        appendFileSync(join(dir, 'flash-pinned-0.png'), 'changed');
        assert.throws(() => deriveFlash(dir), /changed PNG/);
    });
});
