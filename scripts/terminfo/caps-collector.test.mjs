import assert from 'node:assert/strict';
import { test } from 'node:test';
import { collectCapsGrading, capsBind, capsRecordingContext } from './caps-collector.mjs';
import { capsContracts, capsFeeds, capsFinish, capsPinnedPattern, capsPinnedQuery, capsPlanSeed, capsReads, replayCapsPinned } from './caps-contracts.mjs';
import { capsAdapterId } from './caps-grading-collector.mjs';
import { syntheticCapsExchanges } from './fixtures/caps-evidence.mjs';

const [contract] = capsContracts;
const caseId = contract.caseId;
const da = '\x1b[?62;4;22c';
const frame = '\x1b]1337;Capabilities=T3CwLrMSc7UTs3BFGsGoSyHNoP\x1b\\';
const silent = { barrier: da, replies: [] };

test('the termless context serves the captured reply for exactly the pinned request and counts its calls', () => {
    const context = capsRecordingContext(contract, { barrier: da, replies: [frame] });
    assert.equal(context.feedCapture(capsPinnedQuery), frame);
    assert.equal(context.fedCount, 1);
    assert.equal(context.askedCount, 0);
    assert.throws(() => context.feedCapture(capsPinnedQuery), /out of order/, 'a second call is refused');
    assert.throws(() => capsRecordingContext(contract, silent).feedCapture('\x1b]1337;other\x07'), /differs from the contract/);
    assert.equal(capsRecordingContext(contract, null).feedCapture(capsPinnedQuery), '', 'an absent exchange is the empty capture');
});

test('the terminal context searches replies plus the DA barrier with the pinned frame pattern only', async () => {
    const context = capsRecordingContext(contract, { barrier: da, replies: [frame] });
    const reply = await context.queryWithSentinelOutcome(capsPinnedQuery, capsPinnedPattern);
    assert.equal(reply.reason, 'reply');
    assert.equal(reply.raw, frame + da);
    assert.equal(reply.match[0], frame);
    assert.equal(context.fedCount, 1);
    assert.equal(context.askedCount, 1);
    const none = await capsRecordingContext(contract, silent).queryWithSentinelOutcome(capsPinnedQuery, capsPinnedPattern);
    assert.equal(none.match, null);
    await assert.rejects(capsRecordingContext(contract, silent).queryWithSentinelOutcome(capsPinnedQuery, new RegExp(String.raw`\x1b\]1337;Capabilities=(.*)`)), /different frame pattern/);
    await assert.rejects(capsRecordingContext(contract, silent).queryWithSentinelOutcome(capsPinnedQuery, new RegExp(capsPinnedPattern.source, 'g')), /different frame pattern/);
    await assert.rejects(capsRecordingContext(contract, silent).queryWithSentinelOutcome(capsPinnedQuery, 'x'), /different frame pattern/);
    await assert.rejects(capsRecordingContext(contract, silent).queryWithSentinelOutcome('\x1b]1337;other\x07', capsPinnedPattern), /differs from the contract/);
});

test('contexts refuse every read the pinned code must not make', () => {
    const context = capsRecordingContext(contract, silent);
    for (const [name, what] of [['getCell', /cells/], ['getCursor', /the cursor/], ['getText', /text/], ['getTitle', /the title/]]) assert.throws(() => context[name](), what);
});

/** A fake owned fence and owner that serve the fixture's hand-written replies group by group and record every call. */
function fakeIo({ failAt = null, failFinish = false } = {}) {
    const calls = [], roles = contract.groups.map(group => group.role);
    let exchanges = 0, reads = 0;
    const fence = {
        write: async text => { calls.push(['write', text]); return da; },
        exchange: async (text, parse) => {
            calls.push(['exchange', text]);
            const replies = syntheticCapsExchanges[roles[exchanges++]] ?? [];
            const parsed = parse(replies.join('') + da);
            assert.deepEqual(parsed, { barrier: da, replies }, 'the frame parser accepts the retained replies');
            return parsed;
        },
    };
    const owner = async (kind, barrier, extras = {}) => {
        calls.push(['owner', kind, barrier ?? null, extras.report ? 'report' : null]);
        if (kind === 'caps-finish' && failFinish) throw new Error('finish failed');
        if (kind === 'caps-read' && reads++ === failAt) throw new Error('read failed');
        return { screen: { cursorRow: 0, cursorCol: 0 } };
    };
    return { fence, owner, calls };
}

/** A stand-in for the layered bundle: its pinned functions are the replay itself, called with the real recording contexts. */
/** The layered bundle's exported names, exactly as production provides them to collectCapsGrading. */
const gradingModule = {
    ntildeCapsAdapter: { id: capsAdapterId, probeIds: [caseId] },
    ntildeRunCapsTermless: (probeId, ctx) => ({ probeId, ...replayCapsPinned(probeId, 'termless', { replies: ctx.feedCapture(capsPinnedQuery) }) }),
    ntildeRunCapsTerminal: async (probeId, ctx) => {
        const reply = await ctx.queryWithSentinelOutcome(capsPinnedQuery, capsPinnedPattern);
        return { probeId, ...replayCapsPinned(probeId, 'terminal', { replies: reply.raw, barrier: '' }) };
    },
};
const config = { runId: 'a'.repeat(32) };

test('the driver runs start, bind, every plan step, the pinned replays and finish, in order', async () => {
    const { fence, owner, calls } = fakeIo();
    const run = await collectCapsGrading({ config, fence, owner, gradingModule });
    assert.equal(run.adapterId, capsAdapterId);
    assert.deepEqual(run.probeIds, [caseId]);
    const [item] = run.cases;
    assert.deepEqual(item.feeds, capsFeeds(contract), 'the recorded feeds are the contract\'s plan feeds, byte for byte');
    assert.equal(item.barriers.length, item.feeds.length);
    assert.equal(item.reads.length, capsReads(contract));
    assert.deepEqual(item.reads.map(read => read.kind), contract.plan.filter(step => step.read).map(step => step.read));
    assert.equal(item.pinned.termless.observation.outcome, 'supported');
    assert.equal(item.pinned.terminal.observation.outcome, 'supported');
    const kinds = calls.filter(call => call[0] === 'owner').map(call => call[1]);
    assert.equal(kinds[0], 'caps-start');
    assert.equal(kinds[1], 'caps-bind');
    assert.equal(kinds.at(-1), 'caps-finish');
    assert.equal(kinds.filter(kind => kind === 'caps-read').length, capsReads(contract));
    assert.deepEqual(calls.find(call => call[0] === 'write'), ['write', '\x1b[?1049h' + capsPlanSeed(config.runId)]);
    assert.equal(capsBind(config.runId), '\x1b[?1049h' + capsPlanSeed(config.runId));
    assert.deepEqual(calls.at(-2), ['write', capsFinish(config.runId)]);
    assert.equal(calls.filter(call => call[0] === 'exchange').length, contract.groups.length, 'one fenced exchange per group');
    assert.ok(calls.filter(call => call[0] === 'owner' && call[3] === 'report').length === contract.groups.length, 'every report read carries its replies');
});

test('a collection failure still restores, and a restoration failure is reported with it', async () => {
    const failing = fakeIo({ failAt: 3 });
    await assert.rejects(collectCapsGrading({ config, fence: failing.fence, owner: failing.owner, gradingModule }), /read failed/);
    assert.equal(failing.calls.at(-1)[1], 'caps-finish', 'the finish ran after the failure');
    const both = fakeIo({ failAt: 3, failFinish: true });
    await assert.rejects(collectCapsGrading({ config, fence: both.fence, owner: both.owner, gradingModule }), error => error instanceof AggregateError && /feature-reporting collection and restoration failed/.test(error.message) && error.errors.length === 2);
    const onlyFinish = fakeIo({ failFinish: true });
    await assert.rejects(collectCapsGrading({ config, fence: onlyFinish.fence, owner: onlyFinish.owner, gradingModule }), /finish failed/);
});

test('a layered bundle whose pinned code makes a different number of calls is refused', async () => {
    const { fence, owner } = fakeIo();
    const extra = { ...gradingModule, ntildeRunCapsTermless: (probeId, ctx) => ({ probeId, ...replayCapsPinned(probeId, 'termless', { replies: ctx.feedCapture(capsPinnedQuery) + ctx.feedCapture(capsPinnedQuery) }) }) };
    await assert.rejects(collectCapsGrading({ config, fence, owner, gradingModule: extra }), /out of order/, 'an extra pinned call is refused by the context');
    const skipped = { ...gradingModule, ntildeRunCapsTermless: probeId => ({ probeId, ...replayCapsPinned(probeId, 'termless', { replies: '' }) }) };
    const first = fakeIo();
    await assert.rejects(collectCapsGrading({ config, fence: first.fence, owner: first.owner, gradingModule: skipped }), /different number of calls/, 'a pinned path that skips its call is refused');
    const fewer = { ...gradingModule, ntildeRunCapsTerminal: async probeId => ({ probeId, ...replayCapsPinned(probeId, 'terminal', { replies: '', barrier: '' }) }) };
    const second = fakeIo();
    await assert.rejects(collectCapsGrading({ config, fence: second.fence, owner: second.owner, gradingModule: fewer }), /different number of calls/);
});
