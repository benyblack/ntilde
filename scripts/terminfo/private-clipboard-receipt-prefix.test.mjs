// The schema12 private clipboard receipt tests, in three files: private-clipboard-receipt.test.mjs,
// private-clipboard-receipt-refusals.test.mjs and private-clipboard-receipt-prefix.test.mjs. Every
// test re-derives the complete receipt (about 4 s, twice that on windows) and the tests of one file
// run one after another, so as one file of 27 tests they held the owned private clipboard CI job
// to six minutes on windows; as three, `node --test` runs them in parallel processes.
// This file: changed original prefix grades and original unavailable markers.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { privateClipboardReceiptFixture } from './fixtures/private-clipboard-receipt.mjs';
import { rgbCases } from './rgb.mjs';
import { renditionCases } from './rendition.mjs';
import { cursorCases } from './cursor.mjs';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');

for (const prefix of ['cell', 'sgr', 'rgb', 'rendition', 'cursor']) test('schema12 refuses a coherently rehashed ' + prefix + ' grade change with unchanged original captures', () => privateClipboardReceiptFixture(f => {
    const file = prefix + '-observations.json', report = JSON.parse(readFileSync(join(f.output, file)));
    const observation = prefix === 'cursor' ? report.observations.find(value => value.featureId === 'cursor.style.0') : report.observations[0];
    if (prefix === 'cursor') assert.equal(observation.result, 'failed', 'the genuine original cursor negative must be preserved');
    observation.result = observation.result === 'passed' ? 'failed' : 'passed';
    f.write(file, report);
    const binding = prefix === 'cell' ? f.evidence : f.evidence[prefix + 'Evidence'];
    binding.sha256 = hash(readFileSync(join(f.output, file))); binding.counts = {};
    for (const item of report.observations) binding.counts[item.result] = (binding.counts[item.result] ?? 0) + 1;
    assert.throws(f.finish);
}));

for (const [prefix, cases] of [['rgb', rgbCases], ['rendition', renditionCases], ['cursor', cursorCases]])
    test('schema12 refuses simultaneous observations beside a valid ' + prefix + ' unavailable marker', () => privateClipboardReceiptFixture(f => {
        const observations = readFileSync(join(f.output, prefix + '-observations.json'));
        rmSync(join(f.output, prefix + '-observations.json'));
        f.write(prefix + '-unavailable.json', { schemaVersion: 1, runId: f.context.runId,
            suiteId: f.evidence[prefix + 'Evidence'].suiteId, status: 'unavailable', reason: 'screenDetailsUnavailable',
            fixtureStarted: false, plannedExperiments: cases.length });
        writeFileSync(join(f.output, prefix + '-snapshots.jsonl'), '');
        Object.assign(f.evidence[prefix + 'Evidence'], { file: prefix + '-unavailable.json', status: 'unavailable',
            reason: 'screenDetailsUnavailable', counts: {}, sha256: hash(readFileSync(join(f.output, prefix + '-unavailable.json'))) });
        assert.equal(f.finish().schemaVersion, 42, 'the marker alone is a valid original pre-start refusal');
        rmSync(join(f.output, 'cell-receipt.json'));
        writeFileSync(join(f.output, prefix + '-observations.json'), observations);
        assert.throws(f.finish);
    }));
