import assert from 'node:assert/strict';
import { appendFileSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { verifyBlinkRun } from './modern-blink-owned.mjs';
import { blinkPublication, buildModernBlinkPublication, modernBlinkOutputPaths, readableModernBlink } from './modern-blink-results.mjs';
import { modernRoot, serializeModern } from './modern-results.mjs';

test('the archived blink run rederives the sole delta from the modes assessment', () => {
    const report = buildModernBlinkPublication();
    const paths = modernBlinkOutputPaths(modernRoot);
    assert.equal(readFileSync(paths.json, 'utf8').replaceAll('\r\n', '\n'), serializeModern(report));
    assert.equal(readFileSync(paths.markdown, 'utf8').replaceAll('\r\n', '\n'), readableModernBlink(report));
    assert.equal(report.requirementsCount, 69);
    assert.deepEqual(report.totals, { pass: 52, fail: 0, inconclusive: 17 });
    const previous = JSON.parse(readFileSync(join(modernRoot, report.predecessor.publication, 'report.json')));
    assert.deepEqual(report.predecessor.totals, previous.totals);
    assert.deepEqual(report.rows.filter((row, index) => JSON.stringify(row) !== JSON.stringify(previous.rows[index])).map(row => row.id), ['sgr.blink']);
});

test('a changed original PNG prevents receipt replay', () => {
    const directory = join(modernRoot, blinkPublication), scratch = mkdtempSync(join(tmpdir(), 'ntilde-blink-refusal-'));
    try {
        const manifest = JSON.parse(readFileSync(join(directory, 'manifest.json')));
        extractOwnedArchive(directory, manifest, scratch, join(directory, 'owned-run.zip'));
        appendFileSync(join(scratch, 'blink-00.png'), 'changed');
        assert.throws(() => verifyBlinkRun(scratch, true), /native image identity or bytes changed/);
    } finally {
        rmSync(scratch, { recursive: true, force: true });
    }
});
