import assert from 'node:assert/strict';
import { test } from 'node:test';
import { collectXtgettcapGrading, xtgettcapRecordingContext } from './xtgettcap-collector.mjs';
import { xtgettcapContracts, xtgettcapCaseIds, xtgettcapFixture, oracleReplies, xtgettcapNoncePrefix, replayXtgettcapPinned } from './xtgettcap-contracts.mjs';
import { syntheticXtgettcapEvidence } from './fixtures/xtgettcap-evidence.mjs';
import { adaptXtgettcapCollector, xtgettcapAdapterId } from './xtgettcap-grading-collector.mjs';
import { buildXtgettcapGradingReport } from './xtgettcap-grading.mjs';

const contract = xtgettcapContracts[0];
const da = '\x1b[?62;4;22c';
const runId = 'a'.repeat(32);
const nonce = xtgettcapNoncePrefix + runId;

/** A fake fence that answers every request with the oracle's replies for its name list (or the DA barrier alone). */
function oracleFence() {
    return {
        writes: [],
        async write(bytes) { this.writes.push(bytes); return da; },
        async exchange(bytes, parse) {
            this.writes.push(bytes);
            if (!bytes.startsWith('\x1bP+q')) throw new Error('unexpected exchange bytes');
            const nameList = bytes.slice(4, -2);
            const replies = oracleReplies(nameList);
            if (replies === null) throw new Error('the plan must only send string name lists');
            return parse(replies.join('') + da);
        },
    };
}

/** A fake owner that journals one synthetic seeded screen per read. */
function ownerOver(screen) {
    return async (kind, barrier, extras = {}) => ({ screen: structuredClone(screen), ...extras });
}

/** A grading module stand-in: the adapter identity plus pinned runners over the recording context (the real module is exercised
 *  by the layered-derivation test against the pinned bundle). */
const gradingModule = {
    ntildeXtgettcapAdapter: { id: xtgettcapAdapterId, probeIds: [...xtgettcapCaseIds] },
    ntildeRunXtgettcapTermless: (probeId, context) => {
        const exchange = context.feedCapture(contract.pinnedFeeds[0]);
        return { probeId, ...replayXtgettcapPinned(exchange) };
    },
    ntildeRunXtgettcapTerminal: async (probeId, context) => {
        const raw = await context.queryWithSentinelOutcome(contract.pinnedFeeds[0], /\x1bP0\+r\x1b\\/);
        return { probeId, ...replayXtgettcapPinned(raw.raw) };
    },
};

test('the collector replays the exact plan through the fence and journals every reply list', async () => {
    const fence = oracleFence();
    const screens = syntheticXtgettcapEvidence().frames;
    const seededScreen = screens[2].screen;
    const run = await collectXtgettcapGrading({ config: { runId, output: '/tmp/none' }, fence, owner: ownerOver(seededScreen), gradingModule });
    assert.equal(run.adapterId, xtgettcapAdapterId);
    assert.deepEqual(run.probeIds, [...xtgettcapCaseIds]);
    const [item] = run.cases;
    assert.equal(item.caseId, contract.caseId);
    // Every plan feed went through the fence in order, the pinned request first among exchanges.
    const requests = item.feeds.filter(feed => feed !== xtgettcapFixture.groups && feed.startsWith('\x1bP+q'));
    assert.equal(requests[0], contract.pinnedFeeds[0]);
    assert.equal(item.feeds.length, contract.plan.filter(step => step.feed).length);
    // Every report read carries the journaled replies of its own exchange, in plan order.
    for (const [index, read] of item.reads.entries()) {
        const role = contract.roles[index];
        if (role === 'initial') { assert.equal(read.report, undefined); continue; }
        const expected = oracleReplies(contract.requests.find(request => request.role === role).nameList);
        assert.deepEqual(read.report.replies, expected, role);
        assert.equal(read.report.barrier, da);
    }
    assert.deepEqual(item.pinned.termless.observation, replayXtgettcapPinned(oracleReplies('544e').join('')).observation);
    assert.equal(item.pinned.termless.observation.outcome, 'inconclusive');
    // The complete run rederives through the parent report builder when the journal mirrors the reads.
    const evidence = syntheticXtgettcapEvidence(runId);
    const frames = evidence.frames.map((frame, index) => (index >= 2 && index < 2 + item.reads.length
        ? { ...frame, report: item.reads[index - 2].report }
        : frame));
    assert.doesNotThrow(() => buildXtgettcapGradingReport(frames, run, runId, evidence.adapter));
});

test('a dropped reply is journaled honestly and the report builder grades it unsupported, not silently', async () => {
    const fence = oracleFence();
    fence.exchange = async (bytes, parse) => {
        const nameList = bytes.slice(4, -2);
        const replies = oracleReplies(nameList);
        // A terminal that drops the last reply of every list: the exchange still completes (the DA barrier closes it), so the
        // collection journals the short list and the mismatch is the grader's conclusion, never a hidden repair.
        return parse(replies.slice(0, replies.length - 1).join('') + da);
    };
    const seededScreen = syntheticXtgettcapEvidence().frames[2].screen;
    const run = await collectXtgettcapGrading({ config: { runId, output: '/tmp/none' }, fence, owner: ownerOver(seededScreen), gradingModule });
    assert.equal(run.cases[0].reads[contract.roles.indexOf('pinned')].report.replies.length, 0, 'the refused reply was dropped');
    const evidence = syntheticXtgettcapEvidence(runId);
    const frames = evidence.frames.map((frame, index) => (index >= 2 && index < 2 + run.cases[0].reads.length
        ? { ...frame, report: run.cases[0].reads[index - 2].report }
        : frame));
    const [item] = buildXtgettcapGradingReport(frames, run, runId, evidence.adapter).cases;
    assert.equal(item.effectContract.outcome, 'unsupported');
    assert.match(item.effectContract.note, /was not answered/);
    assert.equal(item.observed.requests.pinned.received.length, 0);
});

test('the recording context serves the journaled exchange to both pinned paths', async () => {
    const exchange = { barrier: da, replies: oracleReplies('544e') };
    const context = xtgettcapRecordingContext(contract, exchange);
    assert.equal(context.feedCapture(contract.pinnedFeeds[0]), exchange.replies.join(''));
    const terminal = await xtgettcapRecordingContext(contract, exchange).queryWithSentinelOutcome(contract.pinnedFeeds[0], /\x1bP0\+r\x1b\\/);
    assert.equal(terminal.reason, 'reply');
    assert.equal(terminal.raw, exchange.replies.join('') + da);
    assert.equal(terminal.match[0], '\x1bP0+r\x1b\\');
});
