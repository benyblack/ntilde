import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { modernRoot, serializeModern } from './modern-results.mjs';
import { caseId } from './modern-osc7-cwd.mjs';
import { verifyOscCwdRun } from './modern-osc7-cwd-owned.mjs';
import { buildModernOscCwdPublication, modernOscCwdOutputPaths, oscCwdPublication, readableModernOscCwd } from './modern-osc7-cwd-results.mjs';

test('rederive the archived OSC 7 App result and preserve all 68 other rows', () => {
    const report = buildModernOscCwdPublication();
    const paths = modernOscCwdOutputPaths(modernRoot);
    assert.equal(readFileSync(paths.json, 'utf8').replaceAll('\r\n', '\n'), serializeModern(report));
    assert.equal(readFileSync(paths.markdown, 'utf8').replaceAll('\r\n', '\n'), readableModernOscCwd(report));
    assert.equal(report.requirementsCount, 69);
    assert.deepEqual(report.totals, { pass: 56, fail: 0, inconclusive: 13 });
    const previous = JSON.parse(readFileSync(join(modernRoot, report.predecessor.publication, 'report.json')));
    assert.deepEqual(report.predecessor.totals, previous.totals);
    const [changed, ...others] = report.rows.filter((row, index) => JSON.stringify(row) !== JSON.stringify(previous.rows[index]));
    assert.equal(others.length, 0);
    assert.equal(changed.id, caseId);
    assert.equal(changed.conclusion, 'pass');
    assert.equal(changed.observed.negativeHeld, true);
    assert.equal(changed.observed.restored, true);
});

test('archived freshness, independently observed restoration, and native screenshots are required', () => {
    const directory = join(modernRoot, oscCwdPublication);
    const scratch = mkdtempSync(join(tmpdir(), 'ntilde-osc7-evidence-refusal-'));
    try {
        const manifest = JSON.parse(readFileSync(join(directory, 'manifest.json')));
        extractOwnedArchive(directory, manifest, scratch, join(directory, 'owned-run.zip'));
        const journal = join(scratch, 'osc7-cwd-journal.jsonl');
        const original = readFileSync(journal, 'utf8');
        const frames = () => original.trimEnd().split('\n').map(line => JSON.parse(line));
        const rewrite = entries => writeFileSync(journal, entries.map(item => JSON.stringify(item)).join('\n') + '\n');

        const stale = frames();
        stale.at(-1).native = stale.find(frame => frame.phase === 'incoming').native;
        rewrite(stale);
        assert.throws(() => verifyOscCwdRun(scratch), /stale or out-of-order/);

        const incomplete = frames();
        const last = incomplete.at(-1);
        last.native.lines = last.native.lines.filter(line => !line.text.startsWith('/'));
        rewrite(incomplete);
        assert.throws(() => verifyOscCwdRun(scratch), /incomplete verified fixture setup or restoration/);

        writeFileSync(journal, original);
        writeFileSync(join(scratch, 'tooltip-target-b.png'), 'wrong image');
        assert.throws(() => verifyOscCwdRun(scratch), /changed screenshot bytes/);
    } finally {
        rmSync(scratch, { recursive: true, force: true });
    }
});
