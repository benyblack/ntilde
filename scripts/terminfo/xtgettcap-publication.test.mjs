// Frozen real-App publication. Synthetic fixtures do not replace this measurement; every digest below is checked against the committed evidence.
// The test reassembles the exact journal order from the split files and re-derives the graded report from those retained bytes.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { benchmarkSuites } from './qualification-inventory.mjs';
import { xtgettcapCaseIds, xtgettcapContracts, xtgettcapEffectContractId, xtgettcapSuiteId, oracleReplies } from './xtgettcap-contracts.mjs';
import { xtgettcapAdapterId } from './xtgettcap-grading-collector.mjs';
import { buildXtgettcapGradingReport } from './xtgettcap-grading.mjs';
import { xtgettcapCounts } from './xtgettcap-receipt.mjs';

const dir = new URL('../../docs/qa/terminfo/2026-11-12-xtgettcap/', import.meta.url);
const read = name => JSON.parse(readFileSync(new URL(name, dir)));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const contract = xtgettcapContracts[0];
let cached;
const load = () => cached ??= (() => {
    const summary = read('summary.json'), run = read('run-log.json'), common = read('journal-common.json');
    const frames = [common.frames.start, common.frames.bind];
    const [item] = run.cases;
    assert.equal(item.caseId, contract.caseId);
    for (const [index, readEntry] of item.reads.entries()) {
        const journal = read(`journal-${readEntry.kind}-${index}.json`);
        assert.equal(journal.caseId, item.caseId);
        assert.equal(journal.readIndex, index);
        assert.equal(journal.firstSnapshot, frames.length);
        frames.push(journal.frame);
    }
    const finish = read('journal-finish.json');
    assert.equal(finish.caseId, item.caseId);
    assert.equal(finish.firstSnapshot, frames.length);
    frames.push(finish.frame);
    return { summary, run, common, frames, report: { ...common.reportHeader, cases: [read(`case-${contract.caseId}.json`)] } };
})();
const derive = loaded => buildXtgettcapGradingReport(loaded.frames, loaded.run, loaded.summary.context.runId, loaded.report.adapter, { contractsVersion: loaded.report.contractsVersion });
const strip = ({ snapshots, ...rest }) => rest;

test('the published evidence re-derives exactly from its retained journal and run log, and every digest is the one the receipt bound', () => {
    const loaded = load(), { report, summary, run, frames } = loaded, derived = derive(loaded);
    assert.equal(JSON.stringify(report), JSON.stringify(strip(derived)));
    assert.equal(report.suiteId, xtgettcapSuiteId);
    assert.equal(report.adapter.id, xtgettcapAdapterId);
    assert.equal(report.adapter.derivedFromAdapterId, 'ntilde-owned-title-stack-grading-v1');
    assert.equal(frames.length, 3 + contract.roles.length);
    assert.deepEqual(frames.map(frame => frame.index), frames.map((_, index) => index));
    assert.equal(report.restoredSnapshot, frames.length - 1);
    const { receipt } = summary;
    assert.equal(receipt.xtgettcapEvidence.sha256, hash(JSON.stringify(derived, null, 2) + '\n'));
    assert.equal(receipt.xtgettcapEvidence.runSha256, hash(JSON.stringify(run) + '\n'));
    assert.equal(summary.retained.xtgettcapJournalSha256, hash(frames.map(frame => JSON.stringify(frame)).join('\n') + '\n'));
    assert.equal(summary.retained.xtgettcapObservationsSha256, receipt.xtgettcapEvidence.sha256);
    assert.equal(summary.retained.xtgettcapRunSha256, receipt.xtgettcapEvidence.runSha256);
    assert.equal(summary.retained.xtgettcapAdapterSha256, receipt.xtgettcapEvidence.adapterBundleSha256);
    assert.equal(receipt.xtgettcapEvidence.adapterBundleSha256, summary.context.xtgettcapBundleSha256);
    assert.equal(receipt.xtgettcapEvidence.adapterBundleSha256, report.adapter.bundleSha256);
});

test('the pinned verdicts stay inconclusive by design (the refused TN name), the terminal path replays the same bytes, and the effect contract is supported beside them', () => {
    const { report, run } = load();
    const [item] = report.cases;
    assert.equal(item.pinned.termless.observation.outcome, 'inconclusive');
    assert.equal(item.pinned.termless.observation.reason, 'insufficient-evidence');
    assert.equal(item.pinned.termless.observation.note, 'Requested setting or name refused; other settings or names unmeasured');
    assert.equal(item.pinned.terminal.observation.outcome, 'inconclusive');
    assert.equal(item.pinned.terminal.observation.reason, 'insufficient-evidence');
    assert.equal(item.pinned.terminal.response, item.pinned.termless.response + frames0Barrier(), 'the terminal replay sees the raw bytes with its DA barrier');
    assert.equal(item.conclusion.outcome, 'inconclusive');
    assert.equal(item.conclusion.grading, 'pinned-termless-preserved-by-design');
    assert.equal(item.effectContract.id, xtgettcapEffectContractId);
    assert.equal(item.effectContract.outcome, 'supported');
    assert.match(item.effectContract.attribution, /not the pinned probe/);
    assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'supported', kind: 'pinned-inconclusive-by-design' });
    assert.deepEqual(xtgettcapCounts(report), { inconclusive: 1 });
    // The worker log carries the live pinned gradings of both paths over the journaled refusal.
    const [logged] = run.cases;
    assert.equal(logged.pinned.termless.observation.outcome, 'inconclusive');
    assert.equal(logged.pinned.terminal.observation.outcome, 'inconclusive');
});

/** The DA barrier of the pinned exchange, read from the retained journal. */
function frames0Barrier() {
    const loaded = load();
    const pinnedFrame = loaded.frames[2 + contract.pinnedRead];
    return pinnedFrame.report.barrier;
}

test('the exact pinned request was sent and refused with the bare DCS 0 + r ST, byte for byte', () => {
    const { report, run } = load();
    const [item] = report.cases;
    assert.equal(run.cases[0].feeds[1], '\x1bP+q544e\x1b\\', 'the first exchange of the plan is the exact pinned request');
    const pinnedRead = item.reads[contract.pinnedRead];
    assert.deepEqual(pinnedRead.replies, ['\x1bP0+r\x1b\\'], 'the raw retained reply bytes');
    const printable = '\x1bP0+r\x1b\\'.replaceAll('\x1b', '\\e');
    assert.equal(item.observed.requests.pinned.demanded.join(''), printable, 'the printable spelling of the oracle demand');
    assert.equal(item.observed.requests.pinned.received.join(''), printable);
    assert.equal(item.observed.requests.pinned.matches, true);
    assert.equal(item.observed.pinnedReply, printable);
});

test('every control answered exactly what the independent oracle demanded, with unchanged state', () => {
    const { report } = load();
    const [item] = report.cases;
    for (const [role, request] of Object.entries(item.observed.requests)) {
        assert.equal(request.matches, true, role);
        assert.equal(request.unchanged, true, role);
        const expected = oracleReplies(contract.requests.find(entry => entry.role === role).nameList);
        assert.deepEqual(request.demanded, expected.map(raw => raw.replaceAll('\x1b', String.raw`\e`)), role);
        assert.equal(request.received.length, expected.length, role);
    }
    // The numeric, flag and string replies, the multi-name order, the unavailable names and the malformed lists are all in the plan.
    for (const role of ['numeric-co', 'numeric-colors', 'flag-rgb', 'flag-tc', 'string-smulx', 'list-order', 'list-witness',
        'unavailable-kcuu1', 'unknown', 'case-co', 'echo-upper', 'malformed-odd', 'malformed-hex', 'malformed-empty', 'malformed-mixed']) {
        assert.ok(item.observed.requests[role], role);
    }
    // Every malformed request is followed by a recovery that still answered exactly.
    for (const role of ['recovery-tc', 'recovery-co', 'recovery-pinned', 'recovery-string']) {
        assert.equal(item.observed.requests[role].matches, true, role);
    }
    assert.equal(item.observed.seed.cells, true);
    assert.equal(item.observed.seed.margins[0], 1);
    assert.equal(item.observed.seed.margins[1], 5);
});

test('restoration: the finish frame equals the start frame and the receipt completed', () => {
    const { frames, summary } = load();
    assert.equal(frames[0].sha256, frames.at(-1).sha256, 'the main screen is exactly restored');
    assert.equal(summary.receipt.fixtureRemoved, true);
    assert.equal(summary.receipt.completedAt !== undefined, true);
});

test('the receipt binds the run, adapter and counts, and every predecessor and finding reconfirms in the same run', async () => {
    const { summary, report } = load(), { receipt } = summary;
    assert.equal(receipt.schemaVersion, 55);
    assert.equal(receipt.xtgettcapEvidence.runId, summary.context.runId);
    assert.equal(receipt.xtgettcapEvidence.suiteId, xtgettcapSuiteId);
    assert.deepEqual(receipt.xtgettcapEvidence.counts, xtgettcapCounts(report));
    assert.equal(summary.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v49');
    const predecessors = benchmarkSuites.slice(0, benchmarkSuites.findIndex(item => item.suite === 'xtgettcap'));
    const expected = [];
    for (const suite of predecessors) expected.push(...(await import(new URL(suite.module, import.meta.url)))[suite.exports[2]]);
    const unique = new Set(Object.values(summary.sameRun).flatMap(suite => suite.cases.map(item => item.caseId)));
    assert.deepEqual([...unique].sort(), [...new Set(expected)].sort());
    assert.equal(Object.keys(summary.sameRun).length, predecessors.length);
    for (const [name, suite] of Object.entries(summary.sameRun)) {
        if (suite.cases.some(item => item.effectContract)) assert.ok(suite.cases.every(item => item.effectContract === 'supported' || item.outcome === 'supported'), name);
        else assert.ok(suite.cases.every(item => item.outcome === 'supported'), name);
    }
    for (const id of xtgettcapCaseIds) assert.ok(!unique.has(id));
    assert.deepEqual(summary.queryCounts, { matched: 33, mismatched: 7 }, 'the query suite reconfirms with the same frozen mismatches');
    assert.equal(summary.truecolorFinding.cases.length, 10);
    assert.equal(summary.underlineResolved.cases.length, 3);
});

test('the stock run (re-run in the same owned run) keeps device.xtgettcap inconclusive and no stock grade is rewritten or raised', () => {
    const { summary } = load();
    assert.deepEqual(summary.stockCollector.counts, { inconclusive: 167, supported: 87, unsupported: 1 });
    assert.equal(summary.stockCollector.suiteComplete, false);
    assert.equal(summary.stockCollector.probes['device.xtgettcap'].outcome, 'inconclusive');
    assert.equal(summary.stockCollector.probes['device.xtgettcap'].reason, 'insufficient-evidence');
    assert.match(summary.stockCollector.probes['device.xtgettcap'].note, /refused/);
});

test('revisions and attempts (every superseded one keeps its actual reason), and the archive stays reviewable', () => {
    const { summary } = load();
    assert.match(summary.appRevision, /^[0-9a-f]{40}$/);
    assert.match(summary.scriptsRevision, /^[0-9a-f]{40}$/);
    assert.ok(summary.attempts.length >= 1);
    assert.equal(summary.attempts.at(-1).runId, summary.context.runId);
    assert.equal(summary.attempts.at(-1).receiptCompleted, true);
    assert.equal(summary.attempts.at(-1).scriptsRevision, summary.scriptsRevision);
    assert.match(summary.attempts[0].outcome, /source-snapshot manifest .* v46 policy/);
    assert.match(summary.attempts[1].outcome, /metadata snapshot check .* one more v46-only allowlist/);
    assert.match(summary.attempts[2].outcome, /SonarCloud fixes changed four measured modules/);
    assert.match(summary.attempts[3].outcome, /Codex P2 finding/);
    assert.match(summary.attempts[3].outcome, /extended the unchanged-state key/);
    assert.match(summary.attempts[4].outcome, /now-re-exported fixture imports and the serial-replay NOSONAR placement/);
    assert.match(summary.attempts[5].outcome, /duplication gate .* shared single-copy core/);
    assert.match(summary.attempts[6].outcome, /symlinked journal escaped the regular-file validation/);
    assert.match(summary.attempts[7].outcome, /DECLRMM and the left\/right margins were unobserved/);
    assert.match(summary.attempts[8].outcome, /stored cursor mode .* was unobserved/);
    assert.equal(summary.attempts.filter(item => item.receiptCompleted).length, 9, 'nine runs completed their receipts; only the last is published');
    for (const number of [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]) assert.ok(existsSync(new URL(`superseded/attempt-${number}-superseded.zip`, dir)), number);
    for (const name of readdirSync(dir)) {
        if (name === 'superseded') continue;
        assert.ok(readFileSync(new URL(name, dir), 'utf8').split('\n').length <= (name === 'summary.json' ? 2100 : 1500), name);
    }
});
