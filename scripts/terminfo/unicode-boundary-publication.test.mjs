import assert from 'node:assert/strict';
import { appendFileSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { buildPublication, publish, readRun, withMeasurement } from './unicode-boundary-publication.mjs';

test('the boundary publication rederives all three cases from the cleaned-up owned run', () => {
    publish(true);
    const { rows } = buildPublication();
    assert.deepEqual(rows.map(row => row.effect), ['supported', 'supported', 'supported']);
    assert.deepEqual(rows.map(row => row.terminal), ['supported', 'supported', 'supported']);
    const [ambiguous, grapheme, wrap] = rows;
    assert.equal(ambiguous.ptyWidth, 1);
    assert.equal(grapheme.ptyWidth, 2);
    assert.deepEqual(grapheme.observed.backspaceCursor, [0, 0]);
    assert.equal(wrap.observed.parserRow1, '中');
    assert.equal(wrap.observed.terminalReply.raw, '\x1b[2;3R');
});

test('an altered journal, measured source or reported verdict fails independent replay', () => {
    withMeasurement(directory => {
        appendFileSync(join(directory, 'unicode-boundary-frames.jsonl'), '\n');
        assert.throws(() => readRun(directory), /receipt digest differs/);
    });
    withMeasurement(directory => {
        appendFileSync(join(directory, 'sources', 'unicode-boundary-contracts.mjs'), '\n');
        assert.throws(() => readRun(directory), /retained source differs/);
    });
    withMeasurement(directory => {
        const path = join(directory, 'unicode-boundary-report.json');
        const report = JSON.parse(readFileSync(path));
        report.rows[0].effect = 'unsupported';
        writeFileSync(path, JSON.stringify(report));
        assert.throws(() => readRun(directory), /retained report did not rederive/);
    });
});
