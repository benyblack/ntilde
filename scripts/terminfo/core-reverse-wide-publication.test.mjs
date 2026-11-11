import test from 'node:test';
import assert from 'node:assert/strict';
import { cpSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { root, reportText } from '../terminfo-core-results.mjs';
import { readableReverseWideReport, previousPublicationReport, previousPublicationReportSha256 } from '../terminfo-core-reverse-wide-results.mjs';
import { buildPublication, loadMeasurement, publication, verifyOutput } from '../terminfo-core-reverse-wide-publication.mjs';
import { reverseWideIds } from './core-reverse-wide.mjs';

test('published 80 unique rows rederive from the owned receipt, with earlier conclusions and totals retained', () => {
    const { report, mapping } = buildPublication();
    assert.equal(report.rows.length, 80); assert.equal(new Set(report.rows.map(r => r.id)).size, 80);
    assert.equal(Object.values(report.totals).reduce((a, b) => a + b, 0), 80);
    assert.equal(report.corePassPercentage, report.totals.pass * 100 / 80);
    const json = reportText(report), md = readableReverseWideReport(report, mapping);
    verifyOutput(readFileSync(join(root, publication, 'report.json'), 'utf8'), json, 'report.json');
    verifyOutput(readFileSync(join(root, publication, 'report.md'), 'utf8'), md, 'report.md');
    const altered = structuredClone(report); altered.totals.pass++;
    assert.throws(() => verifyOutput(reportText(altered), json, 'report.json'), /stale report/);
    assert.ok(!md.includes('[object Object]'));
    const earlier = JSON.parse(readFileSync(join(root, previousPublicationReport)));
    assert.equal(createHash('sha256').update(readFileSync(join(root, previousPublicationReport))).digest('hex'), previousPublicationReportSha256);
    for (const row of earlier.rows) if (!reverseWideIds.includes(row.id)) assert.equal(report.rows.find(r => r.id === row.id).conclusion, row.conclusion, row.id);
    for (const id of reverseWideIds) {
        const row = report.rows.find(r => r.id === id);
        assert.equal(row.exactFixtureEffects.length, 1);
        assert.equal(row.historicalPinnedProbe.stockVerdict, 'supported'); assert.deepEqual(row.pinnedProbe.qualified, []);
        assert.ok(row.controls['rv:set'] && row.controls.asFound && row.controls.restored);
    }
});
test('changed measurement, evidence archive or retained attempt is refused', () => {
    const dir = mkdtempSync(join(tmpdir(), 'nt-rw-pub-'));
    try {
        const path = join(dir, publication); cpSync(join(root, publication), path, { recursive: true });
        const attempts = loadMeasurement().attempts.filter(a => a.archive).map(a => a.archive);
        for (const file of ['owned-run.zip', 'app-source-manifest.json', 'measurement.json', ...attempts]) {
            const f = join(path, file), original = readFileSync(f); writeFileSync(f, Buffer.concat([original, Buffer.from('x')]));
            assert.throws(() => loadMeasurement(dir), /changed/); writeFileSync(f, original);
        }
    } finally { rmSync(dir, { recursive: true, force: true }); }
});
