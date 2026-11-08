import test from 'node:test';
import assert from 'node:assert/strict';
import { privateClipboardReceiptFixture } from './fixtures/private-clipboard-receipt.mjs';

test('schema12 rederives the complete private suffix and preserves every original sixteen-suite binding and negative', () => privateClipboardReceiptFixture(f => {
    const receipt = f.finish(); assert.equal(receipt.schemaVersion, 19);
    for (const key of Object.keys(f.evidence).filter(key => !['privateClipboardEvidence', 'gradingEvidence', 'sgrColorsEvidence', 'sgrRgbEvidence', 'sgrAttrsEvidence', 'sgrPaletteEvidence', 'sgrTruecolorEvidence', 'truecolorFindingEvidence'].includes(key))) assert.deepEqual(receipt[key], f.old[key]);
    assert.deepEqual(f.report.observations.map(value => value.originalPass), [false, true, false]);
    assert.equal(f.report.frames.length, 22); assert.equal(f.report.controller.events.at(-1).request.operation, 'close');
}));

import { readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { capturedV11SourceFiles } from './owned-sources.mjs';
import { historicalArchive } from './fixtures/captured-sources.mjs';
import { rgbCases } from './rgb.mjs';
import { renditionCases } from './rendition.mjs';
import { cursorCases } from './cursor.mjs';
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
    ['foreign raw target', f => { f.raw.target.id = 'unrelated'; f.write('raw.json', f.raw); }],
    ['lost raw ACK', f => { const trace = JSON.parse(f.raw.rawReplies['extensions.osc52-write']); trace.queries[0].raw = ''; f.raw.rawReplies['extensions.osc52-write'] = JSON.stringify(trace); f.write('raw.json', f.raw); }],
    ['read permission promotion', f => { f.raw.target.permissions = { clipboard: 'allow' }; f.write('raw.json', f.raw); }],
    ['nonempty read reply', f => { f.raw.rawReplies['extensions.osc52-read.callbackResponse'] = '\x1b]52;c;cGVyc29uYWw=\x1b\\'; f.write('raw.json', f.raw); }],
    ['extra raw packet', f => { const trace = JSON.parse(f.raw.rawReplies['extensions.osc52-write']); trace.writes.push('\x1b[c'); f.raw.rawReplies['extensions.osc52-write'] = JSON.stringify(trace); f.write('raw.json', f.raw); }],
    ['coherent accepted-write promotion', f => { f.report.controller.events.at(-1).result.acceptedWrites = 1; rewriteReport(f); }],
    ['coherent state drift', f => { f.report.frames[4].after.screen.cursorCol++; rewriteReport(f); }],
    ['missing journal', f => rmSync(join(f.output, 'private-clipboard-journal.json'))],
    ['open fixture', f => { f.report.controller.complete = false; rewriteReport(f); }],
    ['wrong exact original mode', f => { f.proof.proofs[0].privateClipboard.restoredTerminalState += ':changed'; f.write('ownership-proof.json', f.proof); }],
    ['substituted original source', f => writeFileSync(join(f.output, 'collector-original.mjs'), '// substituted')],
    ['source input substitution', f => writeFileSync(join(f.output, 'sources', 'clipboard-pane.cs'), '// substituted')],
    ['late private start', f => { f.report.frames[0].before.beforeOwnership.at = '2026-11-04T00:00:00.000Z'; rewriteReport(f); }],
]) test('schema12 refuses ' + label, () => privateClipboardReceiptFixture(f => { mutate(f); assert.throws(f.finish); }));

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

test('raw JSON object-key order is not confused with a changed original packet or grader', () => privateClipboardReceiptFixture(f => {
    const reorder = value => value && typeof value === 'object' ? Array.isArray(value) ? value.map(reorder)
        : Object.fromEntries(Object.entries(value).reverse().map(([key, item]) => [key, reorder(item)])) : value;
    for (const id of ['extensions.osc52-clipboard', 'extensions.osc52-write', 'extensions.osc52-read', 'collector.ntildePrivateClipboard'])
        f.raw.rawReplies[id] = JSON.stringify(reorder(JSON.parse(f.raw.rawReplies[id])));
    f.write('raw.json', reorder(f.raw)); assert.equal(f.finish().schemaVersion, 19);
}));

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

for (const [label, mutate] of [
    ['changed SGR suite identity', f => { f.evidence.sgrEvidence.suiteId = 'unrelated'; }],
    ['changed cursor counts', f => { f.evidence.cursorEvidence.counts = { passed: 9 }; }],
    ['missing SGR journal entry', f => { const file = join(f.output, 'sgr-snapshots.jsonl'); const lines = readFileSync(file, 'utf8').trimEnd().split('\n'); lines.pop(); writeFileSync(file, lines.join('\n') + '\n'); }],
]) test('schema12 original prefix replay refuses ' + label, () => privateClipboardReceiptFixture(f => { mutate(f); assert.throws(f.finish); }));

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
        assert.equal(f.finish().schemaVersion, 19, 'the marker alone is a valid original pre-start refusal');
        rmSync(join(f.output, 'cell-receipt.json'));
        writeFileSync(join(f.output, prefix + '-observations.json'), observations);
        assert.throws(f.finish);
    }));
