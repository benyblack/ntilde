import assert from 'node:assert/strict';
import { appendFileSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { buildPublication, publish, withMeasurement } from './unicode-width-publication.mjs';
import { verifyRun } from './unicode-width-owned.mjs';

test('the five-case Unicode publication rederives from the immutable owned run', () => {
    publish(true);
    const report = buildPublication();
    assert.deepEqual(report.rows.map(row => row.sampleWidth), [1, 2, 2, 2, 2]);
    assert.ok(report.rows.every(row => row.effect === 'supported' && row.terminal === 'supported'));
    assert.equal(report.baseline, 'unicode');
});

test('incomplete or changed measurement inputs cannot rederive a receipt', () => {
    withMeasurement(path => {
        const journal = join(path, 'unicode-width-frames.jsonl');
        appendFileSync(journal, readFileSync(journal, 'utf8').split('\n')[0] + '\n');
        assert.throws(() => verifyRun(path), /incomplete pane journal/);
    });
    withMeasurement(path => {
        appendFileSync(join(path, 'sources/unicode-width-contracts.mjs'), '\n// changed\n');
        assert.throws(() => verifyRun(path), /changed retained source/);
    });
});
