import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { modernRoot, serializeModern } from './modern-results.mjs';
import { requirementId } from './modern-viewport.mjs';
import { verifyViewportRun } from './modern-viewport-owned.mjs';
import { buildModernViewportPublication, modernViewportOutputPaths, readableModernViewport, viewportPublication } from './modern-viewport-results.mjs';

test('archived viewport runs rederive the sole Modern delta from the blink assessment', () => {
    const report = buildModernViewportPublication();
    const paths = modernViewportOutputPaths(modernRoot);
    assert.equal(readFileSync(paths.json, 'utf8').replaceAll('\r\n', '\n'), serializeModern(report));
    assert.equal(readFileSync(paths.markdown, 'utf8').replaceAll('\r\n', '\n'), readableModernViewport(report));
    assert.equal(report.requirementsCount, 69);
    assert.deepEqual(report.totals, { pass: 53, fail: 0, inconclusive: 16 });
    const previous = JSON.parse(readFileSync(join(modernRoot, report.predecessor.publication, 'report.json')));
    assert.deepEqual(report.predecessor.totals, previous.totals);
    const changed = report.rows.filter((row, index) => JSON.stringify(row) !== JSON.stringify(previous.rows[index]));
    assert.deepEqual(changed.map(row => row.id), [requirementId]);
    assert.deepEqual(changed[0].observed.phases.map(phase => phase.conclusion), ['pass', 'pass']);
    assert.deepEqual(report.viewportMeasurement.baseline.observed.phases.map(phase => phase.conclusion), ['inconclusive', 'inconclusive']);
});

test('changed retained viewport data fails replay', () => {
    const directory = join(modernRoot, viewportPublication), scratch = mkdtempSync(join(tmpdir(), 'ntilde-viewport-refusal-'));
    try {
        const manifest = JSON.parse(readFileSync(join(directory, 'manifest.json')));
        extractOwnedArchive(directory, manifest, scratch, join(directory, 'owned-run.zip'));
        const journal = join(scratch, 'viewport-journal.jsonl');
        const original = readFileSync(journal, 'utf8');
        const frames = original.trimEnd().split('\n').map(line => JSON.parse(line));
        const sync = frames.find(frame => frame.kind === 'output' && frame.phase === 'synchronized');
        sync.requestBytes = sync.requestBytes.slice('\x1b[?2026h'.length, -'\x1b[?2026l'.length);
        writeFileSync(journal, frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
        assert.throws(() => verifyViewportRun(scratch), /changed PTY write or barrier/);
        writeFileSync(journal, original + '{}\n');
        assert.throws(() => verifyViewportRun(scratch), /failed observation or pane changed/);
    } finally {
        rmSync(scratch, { recursive: true, force: true });
    }
});
