import assert from 'node:assert/strict';
import { copyFileSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { deriveRun } from './osc66-owned.mjs';
import { pinnedOsc66, replayOsc66 } from './osc66-pinned.mjs';
import { buildPublication, publication, renderReport, upstreamBundle, withOsc66Archive } from './osc66-publication.mjs';
import { readJson } from './unicode-publication-inputs.mjs';

test('the OSC 66 archive rederives historical grades, exact cursor replies and independent pixel effects', async () => {
    const report = await buildPublication();
    assert.deepEqual(report, readJson(publication, 'report.json'));
    assert.equal(renderReport(report), readFileSync(join(publication, 'report.md'), 'utf8').replaceAll('\r\n', '\n'));
    assert.equal(report.stock, 'supported');
    assert.equal(report.historical.before.outcome, 'unsupported');
    assert.equal(report.historical.after.outcome, 'supported');
    assert.deepEqual(report.historical.before.replies, Array(3).fill('\x1b[1;1R'));
    assert.deepEqual(report.pinnedReplies.exchanges.map(exchange => exchange.raw), ['\x1b[1;1R', '\x1b[1;3R', '\x1b[1;5R']);
    assert.deepEqual(report.historical.after.replies, report.pinnedReplies.exchanges.map(exchange => exchange.raw));
    assert.equal(report.pinnedReplies.terminal.observation.outcome, 'supported');
    const { width, scale, restoredMain, restoredFixture, frames } = report.ownedPixels;
    assert.equal(width.outcome, 'supported');
    assert.equal(scale.outcome, 'supported');
    assert.deepEqual([width.measurement.bounds.width, width.ordinary.bounds.width], [18, 35]);
    assert.deepEqual([scale.measurement.bounds.height, scale.ordinary.bounds.height], [21, 11]);
    assert.equal(width.measurement.outside + scale.measurement.outside, 0);
    assert.ok(restoredMain && restoredFixture && frames.length === 8 && frames.every(frame => frame.pngs.length === 2));
});

test('a missing scale reply yields a pinned inconclusive outcome with the pixel result intact', async () => {
    const report = await buildPublication();
    const exchanges = structuredClone(report.pinnedReplies.exchanges);
    exchanges[2].raw = null;
    const { rows, cols } = report.measurement.geometry;
    const upstream = pinnedOsc66(readFileSync(upstreamBundle));
    const verdict = await replayOsc66(upstream, rows, cols, exchanges);
    assert.equal(verdict.observation.outcome, 'inconclusive');
    assert.equal(verdict.observation.reason, 'no-response');
    assert.equal(report.ownedPixels.scale.outcome, 'supported');
});

test('a stale ordinary PNG cannot stand in for the scaled glyph', () => {
    withOsc66Archive(directory => {
        const report = readJson(directory, 'osc66-report.json');
        const byStage = new Map(report.ownedPixels.frames.map(frame => [frame.stage, frame.pngs[0]]));
        copyFileSync(join(directory, byStage.get('ordinary')), join(directory, byStage.get('scale-ink')));
        assert.throws(() => deriveRun(directory), /Invalid or changed controlled pixel capture/);
    });
});
