// Frozen real-App publication. Synthetic fixtures do not replace this measurement; every digest below is checked against the
// committed evidence. The publication is split into reviewable files (summary, run log, common frames with the report header, the
// case file and one file per read frame); the test reassembles the exact journal order.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, readdirSync } from 'node:fs';
import { buildRectChecksumGradingReport } from './rect-checksum-grading.mjs';
import { rectChecksumAdapterId } from './rect-checksum-grading-collector.mjs';
import { rectChecksumSuiteId, rectChecksumContracts, rectChecksumCaseIds } from './rect-checksum-contracts.mjs';
import { rectChecksumCounts } from './rect-checksum-receipt.mjs';
import { benchmarkSuites } from './qualification-inventory.mjs';

const ID = 'editing.decrqcra';
const dir = new URL('../../docs/qa/terminfo/2026-11-10-rectangle-checksum/', import.meta.url);
const read = name => JSON.parse(readFileSync(new URL(name, dir)));
const load = () => {
    const summary = read('summary.json'), run = read('run-log.json'), common = read('journal-common.json'), header = common.reportHeader;
    const snapshots = [common.frames.start, common.frames.bind];
    for (const contract of rectChecksumContracts) {
        for (let index = 0; index < contract.roles.length; index++) {
            const journal = read(`journal-${contract.caseId}-read${index}.json`);
            assert.equal(journal.caseId, contract.caseId);
            assert.equal(journal.read, index);
            snapshots.push(journal.frame);
        }
    }
    snapshots.push(common.frames.finish);
    const cases = rectChecksumCaseIds.map(id => read(`case-${id}.json`));
    return { summary, run, report: { ...header, cases, snapshots } };
};
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const esc = raw => raw.replaceAll('\\e', '\x1b');

test('the published evidence re-derives exactly from its retained journal and run log', () => {
    const { summary, run, report } = load();
    const derived = buildRectChecksumGradingReport(report.snapshots, run, summary.context.runId, report.adapter, { contractsVersion: report.contractsVersion });
    assert.equal(JSON.stringify(report), JSON.stringify(derived));
    assert.equal(report.suiteId, rectChecksumSuiteId);
    assert.equal(report.adapter.id, rectChecksumAdapterId);
    assert.equal(report.adapter.derivedFromAdapterId, 'ntilde-owned-column-editing-grading-v1');
    assert.equal(report.snapshots.length, 3 + rectChecksumContracts[0].roles.length);
    const [item] = report.cases;
    assert.equal(item.caseId, ID);
    assert.deepEqual([item.conclusion.outcome, item.pinned.observation.outcome], ['supported', 'supported']);
    assert.equal(item.divergence, undefined, 'pinned and contract verdicts agree');
});

test('the pinned fixture bytes and pinned response are the exact upstream ones', () => {
    const { run, report } = load();
    const [item] = report.cases;
    assert.deepEqual(item.pinnedFeeds, ['ABCDE\x1b[1;1H', '\x1b[1;1;1;1;1;5*y']);
    for (const feed of item.pinnedFeeds) assert.ok(run.cases[0].feeds.includes(feed), JSON.stringify(feed));
    assert.equal(item.pinned.response, '\x1bP1!~FEB1\x1b\\');
    assert.deepEqual(item.pinned.observation, { outcome: 'supported', evidence: 'query' });
    assert.equal(item.pinned.note, 'Checks framed reply and request id; checksum arithmetic is not verified');
});

test('every exchange returned the exact model reply over the 40x140 screen and changed nothing', () => {
    const { report } = load();
    const record = report.cases[0].observed;
    assert.deepEqual(record.geometry, { rows: 40, cols: 140 });
    assert.equal(record.reverseVideo, false);
    assert.deepEqual(record.seeds, { pinned: true, attributes: true, origin: { margins: [2, 5], origin: true, cursor: [2, 0] } });
    assert.equal(Object.keys(record.requests).length, 30);
    for (const [role, request] of Object.entries(record.requests)) {
        assert.equal(request.matches, true, role);
        assert.equal(request.unchanged, true, role);
        assert.equal(request.received === null, !request.admitted, role);
    }
    const reply = role => esc(record.requests[role].received);
    assert.equal(reply('pinned'), '\x1bP1!~FEB1\x1b\\');
    assert.equal(reply('terminal'), '\x1bP1!~FFBF\x1b\\', 'the checksum of A');
    // B + bold 0x80 = 0xC2, negated FF3E; K + blink 0x40 = 0x8B -> FF75; R + reverse 0x20 = 0x72 -> FF8E; U/C/D + underline 0x10.
    assert.deepEqual(['attr-bold', 'attr-blink', 'attr-reverse', 'attr-underline', 'attr-curly', 'attr-double', 'attr-allFour', 'attr-reverseSpace'].map(reply),
        ['FF3E', 'FF75', 'FF8E', 'FF9B', 'FFAD', 'FFAC', 'FEB8', 'FFC0'].map(hex => `\x1bP2!~${hex}\x1b\\`));
    assert.equal(reply('page-omitted'), reply('page-zero'));
    assert.equal(reply('page-omitted'), reply('page-oversized'));
    assert.equal(reply('page-parameter'), '\x1bP7!~FEB1\x1b\\', 'the page parameter is not interpreted');
    assert.equal(reply('id-omitted'), '\x1bP0!~FEB1\x1b\\');
    assert.match(reply('id-largest'), /^\x1bP65535!~/);
    assert.equal(reply('inverted-rows'), '\x1bP7!~0000\x1b\\');
    assert.equal(reply('inverted-cols'), '\x1bP7!~0000\x1b\\');
    assert.deepEqual(record.requests['origin-clip'].rectangle, { top: 2, left: 0, bottom: 39, right: 139 }, 'clipped to the page, not the margins');
    assert.equal(reply('origin-clip'), reply('origin-defaults'));
    for (const role of ['identity-private', 'identity-greater', 'identity-seventh', 'identity-colon', 'identity-dollar', 'identity-space']) {
        assert.equal(record.requests[role].received, null, role);
    }
});

test('the run turned the off-by-default report on, and the exact-query witness owed and received the FFB2 reply', () => {
    const { summary } = load();
    assert.match(summary.note, /off-by-default AllowRectangleChecksumReport/);
    assert.deepEqual({ ...summary.queryWitness, orderingNonces: undefined }, { id: 'q07.editing.decrqcra', featureId: ID, expectedEvidence: 'positive-reply',
        observedEvidence: 'positive-reply', contractMatched: true, expectedHex: '1b5031217e464642321b5c', receivedHex: '1b5031217e464642321b5c', byteLength: 11,
        orderingNonces: undefined });
    // The seven other query mismatches are the same frozen expectations every earlier run records (features implemented since).
    assert.deepEqual(summary.queryCounts, { matched: 33, mismatched: 7 });
});

test('the receipt binds the run, adapters and counts, and every predecessor and finding reconfirm in the same run', async () => {
    const { summary, run, report } = load();
    const { receipt } = summary;
    assert.equal(receipt.schemaVersion, 39);
    assert.equal(receipt.rectChecksumEvidence.suiteId, rectChecksumSuiteId);
    assert.equal(receipt.rectChecksumEvidence.runId, summary.context.runId);
    const counts = rectChecksumCounts(report);
    assert.deepEqual(counts, { supported: 1 });
    assert.deepEqual(receipt.rectChecksumEvidence.counts, counts);
    assert.equal(receipt.rectChecksumEvidence.adapterBundleSha256, summary.context.rectChecksumBundleSha256);
    const observationsHash = hash(JSON.stringify(report, null, 2) + '\n');
    const runHash = hash(JSON.stringify(run) + '\n');
    const journalHash = hash(report.snapshots.map(frame => JSON.stringify(frame) + '\n').join(''));
    assert.equal(receipt.rectChecksumEvidence.sha256, observationsHash);
    assert.equal(receipt.rectChecksumEvidence.runSha256, runHash);
    assert.equal(summary.retained.rectChecksumObservationsSha256, observationsHash);
    assert.equal(summary.retained.rectChecksumRunSha256, runHash);
    assert.equal(summary.retained.rectChecksumJournalSha256, journalHash);
    assert.equal(summary.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v33');
    assert.equal(receipt.fixtureRemoved, true);
    assert.equal(summary.ownedRunId, summary.context.runId);
    assert.notEqual(summary.stockCollectorRunId, summary.ownedRunId);
    const predecessors = benchmarkSuites.slice(0, benchmarkSuites.findIndex(item => item.suite === 'rect-checksum'));
    const expected = [];
    for (const suite of predecessors) {
        const module = await import(new URL(suite.module, import.meta.url));
        expected.push(...module[suite.exports[2]]);
    }
    const unique = new Set(Object.values(summary.sameRun).flatMap(suite => suite.cases.map(item => item.caseId)));
    assert.deepEqual([...unique].sort(), [...new Set(expected)].sort());
    assert.equal(Object.keys(summary.sameRun).length, predecessors.length, 'one same-run entry per merged predecessor suite');
    for (const [name, suite] of Object.entries(summary.sameRun)) {
        if (suite.cases.some(item => item.effectContract)) assert.ok(suite.cases.every(item => item.effectContract === 'supported'), name);
        else assert.ok(suite.cases.every(item => item.outcome === 'supported'), name);
    }
    assert.ok(!unique.has(ID), 'the new case is counted separately');
    assert.ok(summary.sameRun['col-edit'].cases.every(item => item.outcome === 'supported'));
    assert.equal(summary.truecolorFinding.cases.length, 10);
    assert.ok(summary.truecolorFinding.cases.every(item => item.outcome === 'supported'));
    assert.equal(summary.underlineResolved.cases.length, 3);
    assert.ok(summary.underlineResolved.cases.every(item => item.outcome === 'supported'));
});

test('the stock run of the same settings answers the pinned terminal path: editing.decrqcra is supported there', () => {
    const { summary } = load();
    assert.deepEqual(summary.stockCollector.counts, { inconclusive: 167, supported: 87, unsupported: 1 });
    assert.equal(summary.stockCollector.probeHash, '16a690f34c0e');
    assert.deepEqual([summary.stockCollector.probes[ID].outcome, summary.stockCollector.probes[ID].evidence], ['supported', 'query']);
});

test('the App revision and the adapter (scripts) revision are recorded separately', () => {
    const { summary } = load();
    assert.match(summary.appRevision, /^[0-9a-f]{40}$/);
    assert.match(summary.scriptsRevision, /^[0-9a-f]{40}$/);
    assert.notEqual(summary.appRevision, summary.scriptsRevision);
    assert.equal(summary.context.appVersion, '0.13.0');
});

test('every publication file stays small enough to review in one piece', () => {
    for (const name of readdirSync(dir)) {
        const lines = readFileSync(new URL(name, dir), 'utf8').split('\n').length;
        assert.ok(lines <= 1500, `${name} has ${lines} lines`);
    }
});

test('prior publications still rederive beside this batch and the only attempt is the published run', () => {
    const earlier = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-10-column-editing/summary.json', import.meta.url)));
    assert.equal(earlier.receipt.schemaVersion, 38);
    assert.equal(earlier.receipt.rectChecksumEvidence, undefined);
    const { attempts, context } = load().summary;
    assert.equal(attempts.length, 1);
    assert.equal(attempts[0].runId, context.runId);
    assert.equal(attempts[0].receiptCompleted, true);
});
