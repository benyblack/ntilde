import test from 'node:test';
import assert from 'node:assert/strict';
import { cpSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { root, reportText } from '../terminfo-core-results.mjs';
import { readableWrapReport } from '../terminfo-core-wrap-results.mjs';
import { buildPublication, loadMeasurement, publication, verifyOutput } from '../terminfo-core-wrap-publication.mjs';

test('all 80 rows rederive with exact wrapping controls and concurrent tab-stop conclusions', () => {
    const { report, mapping } = buildPublication();
    assert.equal(report.rows.length, 80); assert.equal(new Set(report.rows.map(r => r.id)).size, 80);
    assert.deepEqual(report.totals, { pass: 75, fail: 0, inconclusive: 5 });
    assert.equal(report.corePassPercentage, report.totals.pass * 100 / 80);
    for (const id of ['text.wrap', 'modes.auto-wrap', 'text.tab', 'unicode.tab-stops']) assert.equal(report.rows.find(r => r.id === id).conclusion, 'pass');
    const row = report.rows.find(r => r.id === 'modes.auto-wrap');
    assert.deepEqual(row.wrapEffects.map(e => e.role), ['enabled-80', 'enabled-81', 'enabled-85', 'disabled-85']);
    assert.deepEqual(row.wrapEffects.map(e => e.bytes.length), [80, 81, 85, 85]);
    assert.equal(row.wrapGeometry.measured.cols, 80); assert.equal(row.wrapGeometry.restoredAutoWrap, row.wrapGeometry.originalAutoWrap);
    assert.equal(row.modeRecognition.reply, '\x1b[?7;1$y');
    const json = reportText(report), md = readableWrapReport(report, mapping);
    verifyOutput(readFileSync(join(root, publication, 'report.json'), 'utf8'), json, 'report.json');
    verifyOutput(readFileSync(join(root, publication, 'report.md'), 'utf8'), md, 'report.md');
    const altered = structuredClone(report); altered.totals.pass++;
    assert.throws(() => verifyOutput(reportText(altered), json, 'report.json'), /stale report/);
});
test('changed manifest, archive and source manifest are refused', () => {
    const dir = mkdtempSync(join(tmpdir(), 'nt-wrap-pub-'));
    try {
        const path = join(dir, publication); cpSync(join(root, publication), path, { recursive: true });
        for (const name of ['owned-run.zip', 'app-source-manifest.json', 'measurement.json', ...loadMeasurement().attempts.filter(a => a.archive).map(a => a.archive)]) {
            const file = join(path, name), bytes = readFileSync(file); writeFileSync(file, Buffer.concat([bytes, Buffer.from('x')]));
            assert.throws(() => loadMeasurement(dir), /changed/); writeFileSync(file, bytes);
        }
    } finally { rmSync(dir, { recursive: true, force: true }); }
});
