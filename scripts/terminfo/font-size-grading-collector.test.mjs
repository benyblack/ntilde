import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { testWithPinnedCollector } from './real-bundle-test.mjs';
import { adaptCollector, upstreamBundleSha256 } from './collector.mjs';
import { adaptFontSizeCollector, fontSizeAdapterId, fontSizeSeams } from './font-size-grading-collector.mjs';
import { fontSizeContext } from './font-size-collector.mjs';
import { replayFontSizePinned, fontSizeSuiteId } from './font-size-contracts.mjs';
import { buildFontSizeReport, fontSizeCounts } from './font-size-grading.mjs';
import { checkFontSizeEvidence, includesFontSizeEvidence } from './font-size-receipt.mjs';
import { syntheticFontSizeEvidence } from './fixtures/font-size-evidence.mjs';

const hash = data => createHash('sha256').update(data).digest('hex');

test('pinned contexts refuse a changed request and a second query on either path', async () => {
    const report = { replies: [], barrier: '\x1b[?62c' };
    const termless = fontSizeContext(1, report);
    assert.throws(() => termless.feedCapture('\x1b]7777\x07'), /request differs/);
    assert.equal(termless.feedCapture('\x1b]7777;?\x07'), '');
    assert.throws(() => termless.feedCapture('\x1b]7777;?\x07'), /request differs/);
    const terminal = fontSizeContext(1, report);
    await assert.rejects(terminal.queryWithSentinelOutcome('\x1b]7777\x07', /x/), /request differs/);
    assert.deepEqual(await terminal.queryWithSentinelOutcome('\x1b]7777;?\x07', /x/), { raw: report.barrier, match: null, reason: 'sentinel' });
    await assert.rejects(terminal.queryWithSentinelOutcome('\x1b]7777;?\x07', /x/), /request differs/);
});

testWithPinnedCollector('real frozen OSC font-size probes preserve their own criteria and the retained receipt refuses mutations', async bundleUrl => {
    const upstream = readFileSync(bundleUrl, 'utf8');
    const base = adaptCollector(upstream, fileURLToPath(new URL('./ownership.mjs', import.meta.url)));
    const source = adaptFontSizeCollector(base);
    assert.ok(source.startsWith(base));
    for (const [start] of fontSizeSeams) {
        assert.throws(() => adaptFontSizeCollector(base.replace(start, 'changed')), /pinned/);
        assert.throws(() => adaptFontSizeCollector(base + start), /pinned/);
    }
    assert.throws(() => adaptFontSizeCollector(base.replace('Complete OSC 7770 font-size reply', 'Changed criterion')), /bytes changed/);
    const file = fileURLToPath(new URL('./ntilde-font-size-test.mjs', bundleUrl));
    writeFileSync(file, source);
    const module = await import(pathToFileURL(file).href);
    const audited = module.ntildeFontSelectionProbe();
    assert.equal(audited.termless, null);
    const unused = new Proxy({}, { get() { assert.fail('OSC710 frozen probe must not touch a terminal'); } });
    assert.deepEqual(await audited.term(unused), {
        pass: false, note: 'OSC 710 font selection was not attempted; this probe has no effect readback or prior-state restoration',
        observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'none',
            note: 'OSC 710 font selection was not attempted; this probe has no effect readback or prior-state restoration' },
    });
    const e = syntheticFontSizeEvidence();
    e.adapter = { id: fontSizeAdapterId, bundleSha256: hash(source), upstreamBundleSha256 };
    for (const [index, item] of e.run.cases.entries()) {
        const report = item.exchanges.find(value => value.id === 'pinned').report;
        for (const replies of [report.replies, [], index === 0 ? ['\x1b]1337;ReportFontSize=17.5;8.5\x07'] : ['\x1b]776;17;35;25\x07']]) {
            const exchange = { replies, barrier: report.barrier };
            const termless = module.ntildeRunFontSizeTermless(item.caseId, fontSizeContext(index, exchange));
            const terminal = await module.ntildeRunFontSizeTerminal(item.caseId, fontSizeContext(index, exchange));
            assert.deepEqual(termless, { probeId: item.caseId, ...replayFontSizePinned(index, replies.join('')) });
            assert.deepEqual(terminal, { probeId: item.caseId, ...replayFontSizePinned(index, replies.join('') + report.barrier) });
        }
    }
    const report = buildFontSizeReport(e.frames, e.run, e.runId, e.adapter, { replayModule: module });
    const root = mkdtempSync(join(tmpdir(), 'nt-font-size-receipt-'));
    const context = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v69', fontSizeAdapterId,
        fontSizeSuiteId, fontSizeContractsVersion: 1, fontSizeBundleSha256: hash(source), adapterBundleSha256: hash(base), upstreamBundleSha256 };
    const runBytes = JSON.stringify(e.run), reportBytes = JSON.stringify(report);
    const binding = { runId: e.runId, suiteId: fontSizeSuiteId, contractsVersion: 1, file: 'font-size-observations.json', runFile: 'font-size-run.json',
        sha256: hash(reportBytes), runSha256: hash(runBytes), adapterBundleSha256: hash(source), counts: fontSizeCounts(report), provisionalUntil: 'cell-receipt.json' };
    const evidence = { fontSizeEvidence: binding };
    const files = { 'collector-adapter.mjs': base, 'collector-font-size.mjs': source, 'font-size-run.json': runBytes,
        'font-size-observations.json': reportBytes, 'font-size-snapshots.jsonl': e.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n' };
    try {
        assert.equal(includesFontSizeEvidence(root, {}, { sourceSnapshotPolicy: context.sourceSnapshotPolicy }), true);
        assert.equal(includesFontSizeEvidence(root, {}, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v62' }), false);
        for (const [name, bytes] of Object.entries(files)) writeFileSync(join(root, name), bytes);
        assert.doesNotThrow(() => checkFontSizeEvidence(root, evidence, e.runId, context));
        assert.throws(() => checkFontSizeEvidence(root, {}, e.runId, context));
        assert.throws(() => checkFontSizeEvidence(root, evidence, 'b'.repeat(32), context));
        assert.throws(() => checkFontSizeEvidence(root, evidence, e.runId, { ...context, fontSizeSuiteId: 'other' }));
        assert.throws(() => checkFontSizeEvidence(root, { fontSizeEvidence: { ...binding, counts: { supported: 1 } } }, e.runId, context));
        for (const [name, bytes] of Object.entries(files)) {
            writeFileSync(join(root, name), 'changed');
            assert.throws(() => checkFontSizeEvidence(root, evidence, e.runId, context), name);
            writeFileSync(join(root, name), bytes);
        }
        const forged = structuredClone(report); forged.cases[0].effectContract.outcome = 'unsupported';
        const forgedBytes = JSON.stringify(forged); writeFileSync(join(root, binding.file), forgedBytes);
        assert.throws(() => checkFontSizeEvidence(root, { fontSizeEvidence: { ...binding, sha256: hash(forgedBytes) } }, e.runId, context));
    } finally { rmSync(root, { recursive: true, force: true }); }
});
