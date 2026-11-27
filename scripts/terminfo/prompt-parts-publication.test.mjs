import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { deriveRun } from './prompt-parts-owned.mjs';
import { buildPublication, publication, withArchive } from './prompt-parts-publication.mjs';
import { readJson } from './unicode-publication-inputs.mjs';

test('reissue the owned receipt and run both pinned probes over the captured cell and CPR', async () => {
    const { report, summary } = await buildPublication();
    assert.deepEqual(summary, readJson(publication, 'summary.json'));
    assert.deepEqual(report, readJson(publication, 'report.json'));
    const [result] = report.cases;
    assert.equal(result.caseId, 'extensions.osc133-p');
    assert.equal(result.pinned.evidence.termlessFeed, '\x1b]133;P;Cwd=/tmp\x07X');
    assert.equal(result.pinned.evidence.cell, 'X');
    assert.equal(result.pinned.evidence.reply, '\x1b[1;2R');
    for (const path of [result.pinned.terminal, result.pinned.termless])
        assert.deepEqual([path.pass, path.observation.outcome, path.observation.reason], [false, 'inconclusive', 'insufficient-evidence']);
    assert.equal(result.stock.outcome, 'inconclusive');
    assert.equal(result.contractResult.controls, true);
    assert.equal(result.contractResult.outcome, 'supported');
});

test('distinguish an incorrect kind, a stale boundary, a changed anchor and missing readback', () => withArchive(directory => {
    const path = join(directory, 'prompt-parts-frames.jsonl');
    const bytes = readFileSync(path, 'utf8');
    function change(stage, mutate) {
        const frames = bytes.trimEnd().split('\n').map(JSON.parse);
        const frame = frames.find(f => f.stage === stage);
        for (const state of [frame.before, frame.after]) mutate(state.metadata.buffer);
        writeFileSync(path, frames.map(f => JSON.stringify(f)).join('\n') + '\n');
    }
    for (const [stage, mutate] of [
        ['right', b => { b.promptParts[1].kind = 's'; }],
        ['relocated', b => { b.promptParts[1].col = 21; }],
        ['right', b => { b.commandStart.col = 21; }],
        ['reset', b => { b.promptParts = [{ kind: 'r', row: 0, col: 21 }]; }],
    ]) {
        change(stage, mutate);
        assert.equal(deriveRun(directory).report.cases[0].outcome, 'unsupported');
    }
    change('right', b => { delete b.promptParts; });
    assert.throws(() => deriveRun(directory), /part observation/);
    change('finish', b => { b.promptParts = [{ kind: 'r', row: 0, col: 1 }]; });
    assert.throws(() => deriveRun(directory), /final prompt parts/);
}));
