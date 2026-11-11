// The schema12 private clipboard receipt tests, in three files: private-clipboard-receipt.test.mjs,
// private-clipboard-receipt-refusals.test.mjs and private-clipboard-receipt-prefix.test.mjs. Every
// test re-derives the complete receipt (about 4 s, twice that on windows) and the tests of one file
// run one after another, so as one file of 27 tests they held the owned private clipboard CI job
// to six minutes on windows; as three, `node --test` runs them in parallel processes.
// This file: the complete private suffix, its raw packets and its schema11 boundary.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { privateClipboardReceiptFixture } from './fixtures/private-clipboard-receipt.mjs';
import { capturedV11SourceFiles } from './owned-sources.mjs';
import { historicalArchive } from './fixtures/captured-sources.mjs';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');

test('schema12 rederives the complete private suffix and preserves every original sixteen-suite binding and negative', () => privateClipboardReceiptFixture(f => {
    const receipt = f.finish(); assert.equal(receipt.schemaVersion, 40);
    for (const key of Object.keys(f.evidence).filter(key => !['privateClipboardEvidence', 'gradingEvidence', 'sgrColorsEvidence', 'sgrRgbEvidence', 'sgrAttrsEvidence', 'sgrPaletteEvidence', 'sgrTruecolorEvidence', 'truecolorFindingEvidence', 'sgrUnderlineEvidence', 'sgrUnderlineColorEvidence', 'underlineResolvedEvidence', 'sgrResetEvidence', 'sgrClosureEvidence', 'eraseLineEvidence', 'charEditEvidence', 'eraseDisplayEvidence', 'lineEditEvidence', 'charsetsEvidence', 'scrollbackEvidence', 'savedResetEvidence', 'eraseAttrsRegionEvidence', 'attrRectEvidence', 'cursorQualEvidence', 'eraseCharsHistoryEvidence', 'selectiveEraseEvidence', 'rectEditEvidence', 'colEditEvidence', 'rectChecksumEvidence', 'lrMarginsEvidence', 'queryEvidence'].includes(key))) assert.deepEqual(receipt[key], f.old[key]);
    // A v33 run turns the off-by-default DECRQCRA report on, so the archived query witness is re-derived with its reply: only the report digest moves.
    const { sha256: changed, ...queryBinding } = receipt.queryEvidence, { sha256: original, ...oldQueryBinding } = f.old.queryEvidence;
    assert.deepEqual(queryBinding, oldQueryBinding); assert.notEqual(changed, original);
    assert.deepEqual(f.report.observations.map(value => value.originalPass), [false, true, false]);
    assert.equal(f.report.frames.length, 22); assert.equal(f.report.controller.events.at(-1).request.operation, 'close');
}));

for (const [label, mutate] of [
    ['foreign raw target', f => { f.raw.target.id = 'unrelated'; f.write('raw.json', f.raw); }],
    ['lost raw ACK', f => { const trace = JSON.parse(f.raw.rawReplies['extensions.osc52-write']); trace.queries[0].raw = ''; f.raw.rawReplies['extensions.osc52-write'] = JSON.stringify(trace); f.write('raw.json', f.raw); }],
    ['read permission promotion', f => { f.raw.target.permissions = { clipboard: 'allow' }; f.write('raw.json', f.raw); }],
    ['nonempty read reply', f => { f.raw.rawReplies['extensions.osc52-read.callbackResponse'] = '\x1b]52;c;cGVyc29uYWw=\x1b\\'; f.write('raw.json', f.raw); }],
    ['extra raw packet', f => { const trace = JSON.parse(f.raw.rawReplies['extensions.osc52-write']); trace.writes.push('\x1b[c'); f.raw.rawReplies['extensions.osc52-write'] = JSON.stringify(trace); f.write('raw.json', f.raw); }],
]) test('schema12 refuses ' + label, () => privateClipboardReceiptFixture(f => { mutate(f); assert.throws(f.finish); }));

test('raw JSON object-key order is not confused with a changed original packet or grader', () => privateClipboardReceiptFixture(f => {
    const reorder = value => value && typeof value === 'object' ? Array.isArray(value) ? value.map(reorder)
        : Object.fromEntries(Object.entries(value).reverse().map(([key, item]) => [key, reorder(item)])) : value;
    for (const id of ['extensions.osc52-clipboard', 'extensions.osc52-write', 'extensions.osc52-read', 'collector.ntildePrivateClipboard'])
        f.raw.rawReplies[id] = JSON.stringify(reorder(JSON.parse(f.raw.rawReplies[id])));
    f.write('raw.json', reorder(f.raw)); assert.equal(f.finish().schemaVersion, 40);
}));

test('coherent private suffix stripping cannot downgrade the importing graph to frozen schema11', () => privateClipboardReceiptFixture(f => {
    const archive = historicalArchive('2026-11-05-query-observations');
    for (const key of Object.keys(f.context)) if (/privateClipboard/i.test(key)) delete f.context[key];
    delete f.evidence.privateClipboardEvidence; delete f.proof.proofs[0].privateClipboard;
    for (const file of ['private-clipboard-observations.json', 'private-clipboard-journal.json', 'collector-original.mjs']) rmSync(join(f.output, file));
    for (const file of Object.values(JSON.parse(readFileSync(join(f.output, 'sources', 'owned-source-inventory.json')))))
        if (!Object.values(capturedV11SourceFiles).includes(file)) rmSync(join(f.output, 'sources', file));
    const inventory = JSON.stringify(capturedV11SourceFiles, null, 2) + '\n'; writeFileSync(join(f.output, 'sources', 'owned-source-inventory.json'), inventory);
    f.context.sourceInventorySha256 = hash(inventory); f.context.sourceSnapshotPolicy = 'readonly-owned-source-graph-v4';
    f.proof.sourceSnapshot.policy = f.context.sourceSnapshotPolicy;
    f.context.terminalOutput = JSON.parse(archive.get('context.json')).terminalOutput;
    f.context.clipboard = 'disabled; no clipboard fixture'; delete f.context.retainedOriginalCollector;
    f.manifest.policy = f.context.sourceSnapshotPolicy;
    f.manifest.hashes = Object.fromEntries(Object.keys(capturedV11SourceFiles).map(key => [key, f.context[key]]));
    f.write('source-snapshot.json', f.manifest); f.context.sourceSnapshotManifestSha256 = hash(readFileSync(join(f.output, 'source-snapshot.json')));
    f.write('context.json', f.context); f.write('ownership-proof.json', f.proof);
    assert.throws(f.finish, /Invalid|outside its versioned scope/);
}));
