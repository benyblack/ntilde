import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { cursorPublication } from './modern-cursor-results.mjs';
import { buildModernModesPublication, modernModesOutputPaths, readableModernModes, verifyModernModesRun } from './modern-modes-results.mjs';
import { modernRoot, serializeModern } from './modern-results.mjs';

test('the retained publication reissues its full receipt and changes only the two frozen mode rows', () => {
    const report = buildModernModesPublication();
    const previous = JSON.parse(readFileSync(join(modernRoot, cursorPublication, 'report.json')));
    const paths = modernModesOutputPaths(modernRoot);
    assert.equal(readFileSync(paths.json, 'utf8').replaceAll('\r\n', '\n'), serializeModern(report));
    assert.equal(readFileSync(paths.markdown, 'utf8').replaceAll('\r\n', '\n'), readableModernModes(report));
    assert.equal(report.requirementsCount, 69);
    assert.deepEqual(report.totals, { pass: 51, fail: 0, inconclusive: 18 });
    assert.deepEqual(report.predecessor.totals, { pass: 49, fail: 0, inconclusive: 20 });
    assert.deepEqual(report.rows.map(row => row.id), previous.rows.map(row => row.id));
    assert.deepEqual(report.stock, previous.stock);
    const changed = report.rows.filter((row, index) => JSON.stringify(row) !== JSON.stringify(previous.rows[index]));
    assert.deepEqual(changed.map(row => row.id), ['modes.synchronized-output', 'modes.color-scheme-reporting']);
    for (const row of changed) {
        const original = previous.rows.find(item => item.id === row.id);
        assert.equal(row.requirement, original.requirement);
        assert.equal(row.stockVerdict, original.stockVerdict);
        assert.deepEqual(row.stockEvidence, original.stockEvidence);
        assert.equal(row.conclusion, 'pass');
        assert.deepEqual(row.observed.exchanges.flatMap(exchange => exchange.replies.map(reply => reply.pm)), [2, 1, 2]);
    }
});

test('publication refuses an absent receipt or an error-marked collection', () => {
    const directory = mkdtempSync(join(tmpdir(), 'ntilde-modern-modes-incomplete-'));
    try {
        assert.throws(() => verifyModernModesRun(directory, {}), /missing or failed mode collection receipt/);
        writeFileSync(join(directory, 'cell-receipt.json'), '{}\n');
        writeFileSync(join(directory, 'cleanup-failure.json'), '{}\n');
        assert.throws(() => verifyModernModesRun(directory, {}), /missing or failed mode collection receipt/);
    } finally { rmSync(directory, { recursive: true, force: true }); }
});
