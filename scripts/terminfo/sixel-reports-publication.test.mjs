import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { smallerGrid, verifySample } from './sixel-reports-contracts.mjs';
import { Collection } from './sixel-reports-owned.mjs';
import { readPinnedBundle, replaySixelReport } from './sixel-reports-pinned.mjs';
import { buildPublication, publication, renderReport, withSixelArchive } from './sixel-reports-publication.mjs';
import { readJson } from './unicode-publication-inputs.mjs';

const qa = join(publication, '..');

test('the archived Sixel replies, resize journal and supplemental geometry rederive together', async () => {
    const report = await buildPublication();
    assert.deepEqual(report, readJson(publication, 'report.json'));
    assert.equal(renderReport(report), readFileSync(join(publication, 'report.md'), 'utf8').replaceAll('\r\n', '\n'));
    assert.deepEqual(report.history.map(row => row.cases.map(item => item.outcome)),
        [['supported', 'inconclusive'], ['supported', 'supported']]);
    for (const stage of report.pinnedReplies) {
        assert.equal(stage.cases[0].exchange.raw, '\x1b[?62;4;22c');
        assert.ok(stage.cases.every(item => item.terminal.observation.outcome === 'supported'));
    }
    const [before, after, restored] = report.ownedGeometry.geometry;
    assert.deepEqual([before.reference.width, before.reference.height], [2380, 1480]);
    assert.deepEqual(before.observed, { width: 2000, height: 1480 });
    assert.deepEqual(after.observed, { width: 1428, height: 962 });
    assert.deepEqual(restored.observed, before.observed);
    assert.ok(report.ownedGeometry.restored && report.ownedGeometry.geometry.every(row => row.outcome === 'passed'));
});

test('a stale but well-formed geometry reply can pass the pinned probe and fails the resize comparison', async () => {
    const { initial, resized } = withSixelArchive(directory => {
        const frames = readFileSync(join(directory, 'sixel-frames.jsonl'), 'utf8').trimEnd().split('\n').map(line => JSON.parse(line));
        return Object.fromEntries(frames.filter(row => ['initial', 'resized'].includes(row.stage)).map(row => [row.stage, row]));
    });
    resized.samples[1].sample.hex = initial.samples[1].sample.hex;
    resized.samples[1].sample.byteLength = initial.samples[1].sample.byteLength;
    const stale = verifySample(resized.samples, new Set(), resized.metadata.cellGeometry);
    assert.equal(stale.geometry.outcome, 'failed');
    const [, geometryProbe] = readPinnedBundle(qa);
    const verdict = await replaySixelReport(geometryProbe, stale.pinnedExchanges[1]);
    assert.equal(verdict.observation.outcome, 'supported');
});

test('a wrong text-area reply cannot serve as the reference geometry', async () => {
    const resized = withSixelArchive(directory => {
        const frames = readFileSync(join(directory, 'sixel-frames.jsonl'), 'utf8').trimEnd().split('\n').map(line => JSON.parse(line));
        return frames.find(frame => frame.stage === 'resized');
    });
    const stale = Buffer.from('\x1b[4;1480;2380t');
    resized.samples[2].sample.hex = stale.toString('hex');
    resized.samples[2].sample.byteLength = stale.length;
    assert.throws(() => verifySample(resized.samples, new Set(), resized.metadata.cellGeometry),
        /text-area control disagrees with independently observed App geometry/);
});

test('the smaller grid meets the observer limit and fits the pixel budget', () => {
    const cell = { viewRows: 40, viewCols: 79, cellWidthDip: 10, cellHeightDip: 20, renderScaling: 1 };
    assert.deepEqual(smallerGrid(cell), { rows: 26, cols: 48 });
    assert.throws(() => smallerGrid({ ...cell, cellWidthDip: 40 }), /bounded smaller grid unavailable/);
    assert.throws(() => smallerGrid({ ...cell, viewCols: 48 }), /bounded smaller grid unavailable/);
});

test('abort rejects rollback when the App and private PTY disagree on the restored grid', async t => {
    const report = readJson(publication, 'report.json');
    const [resize, restoration] = report.ownedGeometry.actions;
    const owner = new Collection({ app: { pid: 2 }, runId: report.measurement.runId });
    owner.frames.push({ paneId: restoration.observed.paneId, screen: restoration.target });
    owner.resizeAttempted = true;
    t.mock.method(owner, 'resize', async (stage, target) => {
        assert.equal(stage, 'abort');
        assert.deepEqual(target, restoration.target);
        return { stage, target, observed: { ...restoration.observed, terminalOutput: resize.observed.terminalOutput } };
    });
    await assert.rejects(() => owner.observe({ kind: 'sxreport-abort' }, () => 500),
        /Private PTY geometry does not match the observed App screen/);
    assert.equal(owner.aborted, true);
    assert.equal(owner.restored, false);
});

test('the report summary follows the comparisons and the observed width cap crossing', () => {
    const report = readJson(publication, 'report.json');
    const [initial, resized] = report.ownedGeometry.geometry;
    initial.reference.width = 1900;
    resized.outcome = 'failed';
    const markdown = renderReport(report);
    assert.match(markdown, /The check column records the failed or inconclusive geometry stages/);
    assert.doesNotMatch(markdown, /The Sixel report matched|width crossed that cap/);
});
