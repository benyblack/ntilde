import assert from 'node:assert/strict';
import { copyFileSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { caseIds, deriveRun } from './kitty-graphics-owned.mjs';
import { replayPinnedGraphics } from './kitty-graphics-pinned.mjs';
import { buildPublication, publication, renderReport, withGraphicsArchive } from './kitty-graphics-publication.mjs';
import { readJson } from './unicode-publication-inputs.mjs';

test('the frozen Kitty graphics fixture rederives its receipt, pinned replies and paired PNG verdicts', async () => {
    const report = await buildPublication();
    assert.deepEqual(report, readJson(publication, 'report.json'));
    assert.equal(renderReport(report), readFileSync(join(publication, 'report.md'), 'utf8').replaceAll('\r\n', '\n'));
    assert.deepEqual(report.cases.map(row => row.id), caseIds);
    for (const row of report.cases) {
        assert.equal(row.stock, 'supported');
        for (const path of ['terminal', 'termless']) {
            assert.equal(row.pinnedReplies[path].observation.outcome, 'supported');
            assert.equal(row.pinnedReplies[path].observation.evidence, 'query');
        }
        assert.ok(row.ownedPixels.every(stage => stage.verdict === 'passed' && stage.measurement.outside === 0));
    }
    const [transmit, display] = report.cases;
    assert.deepEqual(transmit.ownedPixels.map(row => row.measurement.changed), [0, 0]);
    assert.deepEqual(display.ownedPixels.map(row => row.measurement.changed), [0, 324, 0]);
    assert.deepEqual(display.ownedPixels[1].measurement.bounds, { x: 31, y: 76, width: 18, height: 18 });
    assert.equal(report.pixelFrames.length, 8);
    assert.ok(report.pixelFrames.every(row => row.pngs.length === 2));
});

test('a correlated rejection makes pinned display inconclusive independently of pixel observations', async () => {
    const rows = withGraphicsArchive(directory => deriveRun(directory).report.observations);
    const display = rows.find(row => row.caseId === caseIds[1] && row.stage === 'place');
    const reply = Buffer.from(display.replyEvidence.replyHex, 'hex').toString('ascii');
    display.replyEvidence.replyHex = Buffer.from(reply.replace(';OK', ';ENOENT')).toString('hex');
    const bundle = readFileSync(new URL('../../docs/qa/terminfo/2026-11-16-static-images/terminfo-dev-4.0.0.bundle.mjs.gz', import.meta.url));
    const pinned = await replayPinnedGraphics(bundle, rows);
    assert.equal(pinned[caseIds[0]].terminal.observation.outcome, 'supported');
    assert.equal(pinned[caseIds[1]].terminal.observation.outcome, 'inconclusive');
    assert.equal(pinned[caseIds[1]].termless.observation.outcome, 'inconclusive');
    assert.equal(display.pixelEvidence.verdict, 'passed');
});

test('a prior PNG cannot replace the placed-image observation', () => {
    withGraphicsArchive(directory => {
        const name = index => join(directory, 'image-pixels', 'frame-00' + index + '-0.png');
        copyFileSync(name(4), name(5));
        assert.throws(() => deriveRun(directory), /Invalid or incomplete owned static-image evidence/);
    });
});
