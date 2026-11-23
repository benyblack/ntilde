import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { buildModernCursorPublication, modernCursorOutputPaths, verifyModernCursorRun } from './modern-cursor-results.mjs';
import { modernOutputPaths, modernRoot, readableModern, serializeModern } from './modern-results.mjs';

test('rederives both owned receipts and changes exactly four of the same 69 Modern criteria', () => {
    const report = buildModernCursorPublication();
    const original = JSON.parse(readFileSync(modernOutputPaths(modernRoot).json));
    const paths = modernCursorOutputPaths(modernRoot);
    assert.equal(readFileSync(paths.json, 'utf8').replaceAll('\r\n', '\n'), serializeModern(report));
    assert.equal(readFileSync(paths.markdown, 'utf8').replaceAll('\r\n', '\n'), readableModern(report));
    assert.deepEqual(report.rows.map(row => [row.id, row.requirement]), original.rows.map(row => [row.id, row.requirement]));
    assert.deepEqual(report.totals, { pass: 49, fail: 0, inconclusive: 20 });
    assert.deepEqual(report.stock, original.stock);
    assert.deepEqual(report.measurement, original.measurement);
    assert.equal(report.cursorMeasurement.fixtureRemoved, true);
    const changed = report.rows.filter((row, index) => row.conclusion !== original.rows[index].conclusion);
    assert.deepEqual(changed.map(row => [row.id, row.conclusion, row.evidence.sourceId]), [
        ['cursor.horizontal-absolute', 'pass', 'cha'],
        ['cursor.next-line', 'pass', 'cnl'],
        ['cursor.ansi-save', 'pass', 'ansi-round-trip'],
        ['cursor.ansi-restore', 'pass', 'ansi-round-trip'],
    ]);
    assert.equal(changed[2].evidence.recordSha256, changed[3].evidence.recordSha256);
    for (const [index, row] of report.rows.entries()) {
        const predecessor = original.rows[index];
        assert.equal(row.stockVerdict, predecessor.stockVerdict);
        assert.equal(row.stockEvidence, predecessor.stockEvidence);
        if (!changed.includes(row)) assert.deepEqual(row, predecessor);
    }
});

test('the Modern publication requires a complete receipt and refuses error-marked collections', () => {
    const directory = mkdtempSync(join(tmpdir(), 'modern-cursor-incomplete-'));
    try {
        assert.throws(() => verifyModernCursorRun(directory, {}), /missing or failed cursor collection receipt/);
        writeFileSync(join(directory, 'cell-receipt.json'), '{}');
        writeFileSync(join(directory, 'failure.json'), '{}');
        assert.throws(() => verifyModernCursorRun(directory, {}), /missing or failed cursor collection receipt/);
    } finally { rmSync(directory, { recursive: true, force: true }); }
});
