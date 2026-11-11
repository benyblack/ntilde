import test from 'node:test';
import assert from 'node:assert/strict';
import { cpSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { root, reportText } from '../terminfo-core-results.mjs';
import { readableEraseReport } from '../terminfo-core-erase-results.mjs';
import { buildPublication, loadMeasurement, publication, verifyOutput } from '../terminfo-core-erase-publication.mjs';

test('published 80 unique rows and totals rederive from the owned receipt', () => {
    const { report, mapping } = buildPublication();
    assert.equal(report.rows.length, 80); assert.equal(new Set(report.rows.map(r => r.id)).size, 80);
    assert.equal(Object.values(report.totals).reduce((a, b) => a + b, 0), 80);
    const json = reportText(report), md = readableEraseReport(report, mapping);
    verifyOutput(readFileSync(join(root, publication, 'report.json'), 'utf8'), json, 'report.json');
    verifyOutput(readFileSync(join(root, publication, 'report.md'), 'utf8'), md, 'report.md');
    const altered = structuredClone(report); altered.totals.pass++;
    assert.throws(() => verifyOutput(reportText(altered), json, 'report.json'), /stale report/);
    assert.ok(!md.includes('[object Object]'));
    for (const id of ['erase.line.right', 'erase.screen.above', 'erase.screen.below'])
        assert.equal(report.rows.find(r => r.id === id).exactFixtureEffects.length, 2);
});
test('changed measurement, evidence archive or retained failed attempt is refused', () => {
    const dir = mkdtempSync(join(tmpdir(), 'nt-ce-pub-'));
    try {
        const path = join(dir, publication); cpSync(join(root, publication), path, { recursive: true });
        for (const file of ['owned-run.zip', 'attempt-1-failed.zip', 'attempt-2-superseded.zip', 'attempt-3-superseded.zip', 'attempt-4-superseded.zip', 'measurement.json']) {
            const f = join(path, file), original = readFileSync(f); writeFileSync(f, Buffer.concat([original, Buffer.from('x')]));
            assert.throws(() => loadMeasurement(dir), /changed/); writeFileSync(f, original);
        }
    } finally { rmSync(dir, { recursive: true, force: true }); }
});
