import test from 'node:test';
import assert from 'node:assert/strict';
import { cpSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { root, reportText } from '../terminfo-core-results.mjs';
import { buildTextCursorPublication, loadTextCursorMeasurement, readableTextCursorReport, textCursorPublication,
    verifyTextCursorOutput } from '../terminfo-core-text-cursor-results.mjs';

test('published 80 rows and totals rederive from the owned receipt and both archives are pinned', () => {
    const { report, mapping } = buildTextCursorPublication();
    assert.equal(report.rows.length, 80);
    assert.equal(new Set(report.rows.map(r => r.id)).size, 80);
    const json = reportText(report), markdown = readableTextCursorReport(report, mapping);
    assert.doesNotThrow(() => verifyTextCursorOutput(readFileSync(join(root, textCursorPublication, 'report.json'), 'utf8'), json, 'report.json'));
    assert.doesNotThrow(() => verifyTextCursorOutput(readFileSync(join(root, textCursorPublication, 'report.md'), 'utf8'), markdown, 'report.md'));
    const wrong = structuredClone(report); wrong.totals.pass++;
    assert.throws(() => verifyTextCursorOutput(reportText(wrong), json, 'report.json'), /stale report/);
    assert.ok(!markdown.includes('[object Object]'));
});

test('tampered archive and metadata are refused before any result is used', () => {
    const dir = mkdtempSync(join(tmpdir(), 'nt-tc-publication-'));
    try {
        const path = join(dir, textCursorPublication);
        cpSync(join(root, textCursorPublication), path, { recursive: true });
        const archive = join(path, 'owned-run.zip');
        writeFileSync(archive, Buffer.concat([readFileSync(archive), Buffer.from('changed')]));
        assert.throws(() => loadTextCursorMeasurement(dir), /archive changed/);
        const meta = join(path, 'measurement.json'); writeFileSync(meta, readFileSync(meta, 'utf8') + ' ');
        assert.throws(() => loadTextCursorMeasurement(dir), /manifest changed/);
    } finally { rmSync(dir, { recursive: true, force: true }); }
});
