import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { readJson } from './unicode-publication-inputs.mjs';
import { pixelDimensions, planFor, verifyReply } from './xtwinops-pixels-contracts.mjs';
import { deriveRun } from './xtwinops-pixels-owned.mjs';
import { replayXtwinopsPixels } from './xtwinops-pixels-pinned.mjs';
import { buildPublication, publication, readPinnedProbes, renderReport, withXtwinopsArchive } from './xtwinops-pixels-publication.mjs';

test('the raw child replies, exact dimensions, refit, and cleanup rederive from the owned archive', async () => {
    const report = await buildPublication();
    assert.deepEqual(report, readJson(publication, 'report.json'));
    assert.equal(renderReport(report), readFileSync(join(publication, 'report.md'), 'utf8').replaceAll('\r\n', '\n'));
    assert.deepEqual(report.pinnedReplies.map(row => row.cases.map(item => item.terminal.observation.outcome)),
        Array.from({ length: 4 }, () => ['supported', 'supported']));
    assert.deepEqual(report.ownedGeometry.stages.map(row => [row.grid.rows, row.grid.cols, row.font.fontSizeDip]),
        [[40, 140, 14], [26, 84, 14], [24, 79, 15], [40, 140, 14]]);
    assert.deepEqual(report.ownedGeometry.stages.map(row => row.replies.map(item => item.reply)), [
        ['\x1b[4;1480;2380t', '\x1b[6;37;17t', '\x1b[?2;0;2000;1480S'],
        ['\x1b[4;962;1428t', '\x1b[6;37;17t', '\x1b[?2;0;1428;962S'],
        ['\x1b[4;960;1422t', '\x1b[6;40;18t', '\x1b[?2;0;1422;960S'],
        ['\x1b[4;1480;2380t', '\x1b[6;37;17t', '\x1b[?2;0;2000;1480S'],
    ]);
    assert.ok(report.ownedGeometry.restored && report.ownedGeometry.stages.every(row => row.replies.every(item => item.outcome === 'passed')));
    assert.deepEqual(report.attempts.at(-1).view, { rows: 24, cols: 79 });
    assert.deepEqual(report.attempts.at(-1).bufferAndKernel, { rows: 24, cols: 80 });
});

const framesFile = 'xtwinops-frames.jsonl';
const readFrames = directory => readFileSync(join(directory, framesFile), 'utf8').trimEnd().split('\n').map(line => JSON.parse(line));

test('positive but stale or axis-swapped frames pass only the pinned criterion', async () => {
    const [pane, cell] = readPinnedProbes();
    const { frames, runId } = withXtwinopsArchive(dir => ({ frames: readFrames(dir), runId: readJson(dir, 'context.json').runId }));
    for (const [probe, stage, raw] of [[pane, 'resized-0', '\x1b[4;1480;2380t'], [cell, 'font-changed-1', '\x1b[6;18;40t']]) {
        const frame = frames.find(row => row.stage === stage), step = planFor(runId).find(row => row.stage === stage);
        const bytes = Buffer.from(raw + '\x1b[?62;4;22c');
        frame.samples[0].sample.hex = bytes.toString('hex');
        frame.samples[0].sample.byteLength = bytes.length;
        const reply = verifyReply(frame.samples, step, new Set(), pixelDimensions(frame.metadata.cellGeometry, frame.window));
        assert.equal(reply.outcome, 'failed');
        assert.equal((await replayXtwinopsPixels(probe, reply)).observation.outcome, 'supported');
    }
});

test('the exact oracle rounds the grid before converting its result to an integer', () => {
    const cell = { viewCols: 11, viewRows: 5, cellWidthDip: 4.25, cellHeightDip: 6.75, renderScaling: 2, backingScale: 2 };
    const expected = pixelDimensions(cell, [1, 2, 0, 0, 51, 37]);
    assert.deepEqual(expected.area, { width: 94, height: 68 });
    assert.deepEqual(expected.cell, { width: 8, height: 14 });
});

test('offline receipt derivation rejects query mutations and independent kernel disagreements', () => {
    withXtwinopsArchive(dir => {
        const frames = readFrames(dir);
        const target = frames.find(row => row.stage === 'font-changed-1');
        const rewrite = () => writeFileSync(join(dir, framesFile), frames.map(row => JSON.stringify(row)).join('\n') + '\n');
        const original = target.settledOwnership.terminalOutput.description;
        target.settledOwnership.terminalOutput.description = original.replace('79 columns', '80 columns');
        rewrite();
        assert.throws(() => deriveRun(dir), /PTY geometry does not match/);
        target.settledOwnership.terminalOutput.description = original;
        target.metadata.presentation.tabText = 'Changed while querying';
        rewrite();
        assert.throws(() => deriveRun(dir), /query changed/);
    });
});
