// The schema12 private clipboard receipt tests, in three files: private-clipboard-receipt.test.mjs,
// private-clipboard-receipt-refusals.test.mjs and private-clipboard-receipt-prefix.test.mjs. Every
// test re-derives the complete receipt (about 4 s, twice that on windows) and the tests of one file
// run one after another, so as one file of 27 tests they held the owned private clipboard CI job
// to six minutes on windows; as three, `node --test` runs them in parallel processes.
// This file: a changed private report, journal, proof or source, and a changed original prefix replay.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { privateClipboardReceiptFixture } from './fixtures/private-clipboard-receipt.mjs';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Coherently rehash synthetic report, journal and raw bindings so refusal tests cannot pass merely because an outer digest is stale. */
function rewriteReport(f) {
    f.write('private-clipboard-observations.json', f.report);
    f.evidence.privateClipboardEvidence.sha256 = hash(readFileSync(join(f.output, 'private-clipboard-observations.json')));
    const journal = JSON.parse(readFileSync(join(f.output, 'private-clipboard-journal.json')));
    for (const key of ['binding', 'controller', 'frames', 'callbackResults']) journal[key] = structuredClone(f.report[key]);
    f.write('private-clipboard-journal.json', journal);
    const summary = JSON.parse(f.raw.rawReplies['collector.ntildePrivateClipboard']); summary.binding = f.evidence.privateClipboardEvidence;
    f.raw.rawReplies['collector.ntildePrivateClipboard'] = JSON.stringify(summary); f.write('raw.json', f.raw);
}
for (const [label, mutate] of [
    ['coherent accepted-write promotion', f => { f.report.controller.events.at(-1).result.acceptedWrites = 1; rewriteReport(f); }],
    ['coherent state drift', f => { f.report.frames[4].after.screen.cursorCol++; rewriteReport(f); }],
    ['missing journal', f => rmSync(join(f.output, 'private-clipboard-journal.json'))],
    ['open fixture', f => { f.report.controller.complete = false; rewriteReport(f); }],
    ['wrong exact original mode', f => { f.proof.proofs[0].privateClipboard.restoredTerminalState += ':changed'; f.write('ownership-proof.json', f.proof); }],
    ['substituted original source', f => writeFileSync(join(f.output, 'collector-original.mjs'), '// substituted')],
    ['source input substitution', f => writeFileSync(join(f.output, 'sources', 'clipboard-pane.cs'), '// substituted')],
    ['late private start', f => { f.report.frames[0].before.beforeOwnership.at = '2026-11-04T00:00:00.000Z'; rewriteReport(f); }],
]) test('schema12 refuses ' + label, () => privateClipboardReceiptFixture(f => { mutate(f); assert.throws(f.finish); }));

for (const [label, mutate] of [
    ['changed SGR suite identity', f => { f.evidence.sgrEvidence.suiteId = 'unrelated'; }],
    ['changed cursor counts', f => { f.evidence.cursorEvidence.counts = { passed: 9 }; }],
    ['missing SGR journal entry', f => { const file = join(f.output, 'sgr-snapshots.jsonl'); const lines = readFileSync(file, 'utf8').trimEnd().split('\n'); lines.pop(); writeFileSync(file, lines.join('\n') + '\n'); }],
]) test('schema12 original prefix replay refuses ' + label, () => privateClipboardReceiptFixture(f => { mutate(f); assert.throws(f.finish); }));
