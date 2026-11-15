import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { buildModernReport, readableModern, serializeModern, modernOutputPaths, modernRoot } from './modern-results.mjs';

test('all 69 Modern conclusions rederive from the frozen criteria and complete retained receipt', () => {
    const report = buildModernReport(), paths = modernOutputPaths(modernRoot);
    assert.equal(readFileSync(paths.json, 'utf8').replaceAll('\r\n', '\n'), serializeModern(report));
    assert.equal(readFileSync(paths.markdown, 'utf8').replaceAll('\r\n', '\n'), readableModern(report));
    assert.equal(report.rows.length, 69);
    assert.equal(new Set(report.rows.map(row => row.id)).size, 69);
    assert.equal(Object.values(report.totals).reduce((a, b) => a + b), 69);
    assert.equal(report.measurement.newMeasurement, false);
    assert.equal(report.measurement.receiptSchema, 64);
    assert.equal(report.measurement.fixtureRemoved, true);
    assert.deepEqual(report.stock.counts, { inconclusive: 162, supported: 92, unsupported: 1 });
    for (const row of report.rows) {
        if (row.conclusion === 'inconclusive') assert.ok(row.blocker, row.id);
        else assert.ok(row.evidence?.recordSha256, row.id);
    }
    assert.equal(report.rows.find(row => row.id === 'modes.focus-tracking').conclusion, 'inconclusive');
    assert.equal(report.rows.find(row => row.id === 'sgr.blink').conclusion, 'inconclusive');
    const core = JSON.parse(readFileSync(join(modernRoot, 'docs/qa/terminfo/2026-11-11-core-reverse-wide-results/report.json')));
    assert.deepEqual(core.totals, { pass: 80, fail: 0, inconclusive: 0 });
});
