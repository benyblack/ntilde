import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { modernRoot, serializeModern } from './modern-results.mjs';
import { osc0, osc2 } from './modern-osc-titles.mjs';
import { verifyOscTitlesRun } from './modern-osc-titles-owned.mjs';
import { buildModernOscTitlesPublication, modernOscTitlesOutputPaths, oscTitlesPublication, readableModernOscTitles } from './modern-osc-titles-results.mjs';

test('archived OSC title cases rederive both Modern verdicts and preserve the other 67 rows', () => {
    const report = buildModernOscTitlesPublication();
    const paths = modernOscTitlesOutputPaths(modernRoot);
    assert.equal(readFileSync(paths.json, 'utf8').replaceAll('\r\n', '\n'), serializeModern(report));
    assert.equal(readFileSync(paths.markdown, 'utf8').replaceAll('\r\n', '\n'), readableModernOscTitles(report));
    assert.equal(report.requirementsCount, 69);
    assert.deepEqual(report.totals, { pass: 55, fail: 0, inconclusive: 14 });
    const previous = JSON.parse(readFileSync(join(modernRoot, report.predecessor.publication, 'report.json')));
    assert.deepEqual(report.predecessor.totals, previous.totals);
    const changed = report.rows.filter((row, index) => JSON.stringify(row) !== JSON.stringify(previous.rows[index]));
    assert.deepEqual(changed.map(row => row.id), [osc2, osc0]);
    for (const row of changed) {
        assert.equal(row.conclusion, 'pass');
        assert.deepEqual(row.observed.restoration, { title: 'verified', icon: 'verified' });
    }
    assert.equal(changed[0].observed.negativeHeld, true);
    assert.equal(changed[1].observed.titleTarget, 'exact');
    assert.equal(changed[1].observed.iconTarget, 'exact');
});

test('changed retained setter bytes or a changed restoration reply fail rederivation', () => {
    const directory = join(modernRoot, oscTitlesPublication);
    const scratch = mkdtempSync(join(tmpdir(), 'ntilde-osc-title-refusal-'));
    try {
        const manifest = JSON.parse(readFileSync(join(directory, 'manifest.json')));
        extractOwnedArchive(directory, manifest, scratch, join(directory, 'owned-run.zip'));
        const journal = join(scratch, 'osc-titles-journal.jsonl');
        const original = readFileSync(journal, 'utf8');
        const frames = () => original.trimEnd().split('\n').map(line => JSON.parse(line));
        const write = entries => writeFileSync(journal, entries.map(item => JSON.stringify(item)).join('\n') + '\n');

        const changedRequest = frames();
        changedRequest.find(frame => frame.phase === 'target-b').action.request = '\x1b]2;Wrong target\x07\x1b[c';
        write(changedRequest);
        assert.throws(() => verifyOscTitlesRun(scratch), /changed action bytes/);

        const changedRead = frames();
        const final = changedRead.at(-1);
        final.reads[0].exchange.replies[1] = '\x1b]Lleft changed\x1b\\';
        final.reads[1].exchange.replies[0] = '\x1b]Lleft changed\x1b\\';
        write(changedRead);
        assert.throws(() => verifyOscTitlesRun(scratch), /restoration was not proved/);
    } finally {
        rmSync(scratch, { recursive: true, force: true });
    }
});
