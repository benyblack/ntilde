import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { verifySample } from './sixel-reports-contracts.mjs';
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
