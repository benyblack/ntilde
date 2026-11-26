import assert from 'node:assert/strict';
import { readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { deriveRun } from './osc633-metadata-owned.mjs';
import { caseIds } from './osc633-metadata-contracts.mjs';
import { buildPublication, publication, verifySources, withMetadataArchive } from './osc633-metadata-publication.mjs';
import { readJson } from './unicode-publication-inputs.mjs';

test('reissues the receipt and replays both pinned callbacks for each case', async () => {
    const { summary, report } = await buildPublication();
    assert.deepEqual(summary, readJson(publication, 'summary.json'));
    assert.deepEqual(report, readJson(publication, 'report.json'));
    assert.deepEqual(report.cases.map(row => row.caseId), caseIds);
    for (const row of report.cases) {
        assert.equal(row.contractResult.outcome, 'supported');
        assert.equal(row.contractResult.controls, true);
        assert.equal(row.stock.outcome, 'inconclusive');
        assert.equal(row.stock.rerun, false);
        assert.equal(row.pinned.evidence.cell, 'X');
        assert.equal(row.pinned.evidence.reply, '\x1b[1;2R');
        for (const path of ['termless', 'terminal']) {
            assert.equal(row.pinned[path].pass, false);
            assert.equal(row.pinned[path].observation.outcome, 'inconclusive');
            assert.equal(row.pinned[path].observation.reason, 'insufficient-evidence');
        }
    }
    const [e, p] = report.cases.map(row => Object.fromEntries(row.contractResult.observations.map(item => [item.phase, item])));
    assert.equal(e['e-reported'].command, null);
    assert.equal(e['e-running'].command, 'echo reported;\\part');
    assert.equal(e['e-no-report'].command, null);
    assert.notEqual(p['p-first'].cwd, p['p-second'].cwd);
    assert.match(p['p-second'].cwd, /^C:\\QA-633-[a-f\d]{32}\\x64;west=two$/);
    assert.equal(p['p-malformed'].cwd, p['p-second'].cwd);
    assert.equal(p['p-unknown'].cwd, p['p-second'].cwd);
});

test('the two submissions have identical grid text and distinct reported command facts', () => withMetadataArchive(directory => {
    const { frames } = deriveRun(directory);
    const first = frames.find(row => row.stage === 'e-running').before;
    const second = frames.find(row => row.stage === 'e-no-report').before;
    assert.equal(first.screen.lines[0], 'grid command');
    assert.equal(second.screen.lines[0], first.screen.lines[0]);
    assert.equal(first.rawStatus.currentCommand, 'echo reported;\\part');
    assert.equal(second.rawStatus.currentCommand ?? null, null);
}));

test('rejects changed fixtures, stale fences, cross-pane reads and incomplete restoration', () => withMetadataArchive(directory => {
    const file = join(directory, 'osc633-metadata-frames.jsonl'), bytes = readFileSync(file, 'utf8');
    const mutations = [
        frames => { frames.find(row => row.stage === 'e-reported').writes[0] = 'ordinary output'; },
        frames => { frames.find(row => row.stage === 'e-running').sample = frames.find(row => row.stage === 'e-reported').sample; },
        frames => { frames.find(row => row.stage === 'p-second').after.rawStatus.paneId = frames[0].ownership.tty; },
        frames => { frames.find(row => row.stage === 'p-terminal').sample.hex = '1b5b313b3152'; },
        frames => { frames.find(row => row.stage === 'finish').after.metadata.workingDirectory = '/unexpected'; },
        frames => { frames.pop(); },
    ];
    for (const mutate of mutations) {
        const frames = bytes.trimEnd().split('\n').map(JSON.parse);
        mutate(frames);
        writeFileSync(file, frames.map(row => JSON.stringify(row)).join('\n') + '\n');
        assert.throws(() => deriveRun(directory));
    }
    writeFileSync(file, bytes);
    const interrupted = join(directory, 'failure.json');
    writeFileSync(interrupted, '{}');
    assert.throws(() => deriveRun(directory), /cleanup/);
    rmSync(interrupted);
    assert.doesNotThrow(() => deriveRun(directory));
}));

test('rejects a changed measured source despite identical observations', () => withMetadataArchive(directory => {
    const { context } = deriveRun(directory);
    writeFileSync(join(directory, 'sources', 'osc633-metadata-worker.mjs'), 'changed worker');
    assert.throws(() => verifySources(directory, context));
}));
