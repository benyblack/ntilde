import assert from 'node:assert/strict';
import { copyFileSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { deriveRun } from './font-ligatures-owned.mjs';
import { buildPublication, publication, renderReport, withLigatureArchive } from './font-ligatures-publication.mjs';
import { readJson } from './unicode-publication-inputs.mjs';

test('retained font ligature observations independently rederive a bounded owned result', () => {
    const report = buildPublication();
    assert.deepEqual(report, readJson(publication, 'report.json'));
    assert.equal(renderReport(report), readFileSync(join(publication, 'report.md'), 'utf8').replaceAll('\r\n', '\n'));
    assert.equal(report.pinned.probe, 'unprobed');
    assert.equal(report.pinned.stock, null);
    assert.equal(report.pinned.commonHarness, null);
    assert.equal(report.owned.outcome, 'supported');
    assert.equal(report.owned.baseline, 'unsupported');
    assert.equal(report.owned.changedPixelsOutsideOperators, 0);
    assert.ok(report.owned.observations.every(effect => effect.selectionMatchesOrdinary));
    const { off, on } = report.runs;
    assert.deepEqual(off.cursors, on.cursors);
    assert.deepEqual(on.cursors.map(cursor => cursor.raw), ['\x1b[3;4R', '\x1b[3;5R', '\x1b[5;4R', '\x1b[5;5R']);
});

test('a substituted PNG cannot stand in for the unselected shaped capture', () => {
    withLigatureArchive(directory => {
        const root = join(directory, 'on');
        copyFileSync(join(root, 'ligature-pixels/frame-021.png'), join(root, 'ligature-pixels/frame-020.png'));
        assert.throws(() => deriveRun(root), /Invalid or changed controlled pixel capture/);
    });
});
