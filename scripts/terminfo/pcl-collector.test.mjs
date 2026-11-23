import assert from 'node:assert/strict';
import { test } from 'node:test';
import { collectPclGrading, pclBind, pclRecordingContext, pclTerminalContext } from './pcl-collector.mjs';
import { pclContracts, pclFeeds, pclFinish, pclPinned, pclPlanSeed, pclReads, replayPclTermless, replayPclTerminal, pclCaseIds } from './pcl-contracts.mjs';
import { pclAdapterId } from './pcl-grading-collector.mjs';
import { syntheticPclExchanges } from './fixtures/pcl-evidence.mjs';

const [contract] = pclContracts;
const caseId = contract.caseId;
const da = '\x1b[?62;4;22c';
const silent = { barrier: da, replies: [] };
const query = pclPinned[caseId].query;

test('the termless context serves the captured reply for exactly the pinned request and counts its calls', () => {
    const frame = '\x1b]13;rgb:1/2/3\x1b\\';
    const context = pclRecordingContext(contract, { barrier: da, replies: [frame] });
    assert.equal(context.feedCapture(query), frame);
    assert.equal(context.fedCount, 1);
    assert.equal(context.askedCount, 0);
    assert.throws(() => context.feedCapture(query), /out of order/, 'a second call is refused');
    assert.throws(() => pclRecordingContext(contract, silent).feedCapture('\x1b]14;?\x07'), /differs from the contract/);
    assert.equal(pclRecordingContext(contract, null).feedCapture(query), '', 'an absent exchange is the empty capture');
    assert.throws(() => pclRecordingContext(contract, silent).feed('\x1b]13;rgb:aa/bb/cc\x07'), /must not plain-feed/, 'the continuation fixture is never served');
});

test('contexts refuse every read the pinned code must not make', () => {
    const context = pclRecordingContext(contract, silent);
    for (const [name, what] of [['getCell', /cells/], ['getCursor', /the cursor/], ['getText', /text/], ['getTitle', /the title/]]) assert.throws(() => context[name](), what);
});

test('the terminal context records the two pinned writes and answers the cursor from the journal', async () => {
    for (const id of pclCaseIds) {
        const { context, writes, expectedWrites } = pclTerminalContext(id, [2, 5]);
        context.write(pclPinned[id].terminalWrites[0]);
        context.write(pclPinned[id].terminalWrites[1]);
        assert.deepEqual(writes, expectedWrites);
        assert.deepEqual(await context.queryCursorPosition(), { row: 3, col: 6 });
        assert.equal(await pclTerminalContext(id, null).context.queryCursorPosition(), null);
        assert.throws(() => context.feedCapture, /Unexpected pinned context access/);
        assert.throws(() => context.queryWithSentinelOutcome, /Unexpected pinned context access/);
    }
    assert.throws(() => pclTerminalContext('extensions.nope', [0, 0]), /Unknown pointer-color case/);
});

/** A fake owned fence and owner that serve the fixture's hand-written replies group by group and record every call. */
function fakeIo({ failAt = null, failFinish = false } = {}) {
    const calls = [], roles = contract.groups.map(group => group.role);
    let exchanges = 0, reads = 0;
    const fence = {
        write: async text => { calls.push(['write', text]); return da; },
        exchange: async (text, parse) => {
            calls.push(['exchange', text]);
            const replies = syntheticPclExchanges[roles[exchanges++ % roles.length]] ?? [];
            const parsed = parse(replies.join('') + da);
            assert.deepEqual(parsed, { barrier: da, replies }, 'the frame parser accepts the retained replies');
            return parsed;
        },
    };
    const owner = async (kind, barrier, extras = {}) => {
        calls.push(['owner', kind, barrier ?? null, extras.report ? 'report' : null]);
        if (kind === 'pcl-finish' && failFinish) throw new Error('finish failed');
        if (kind === 'pcl-read' && reads++ === failAt) throw new Error('read failed');
        return { screen: { cursorRow: 0, cursorCol: 0 } };
    };
    return { fence, owner, calls };
}

/** A stand-in for the layered bundle: its pinned functions replay the same results, called with the real recording contexts. */
const gradingModule = {
    ntildePclAdapter: { id: pclAdapterId, probeIds: [...pclCaseIds] },
    ntildeRunPclTermless: (probeId, ctx) => ({ probeId, ...replayPclTermless(probeId, ctx.feedCapture(pclPinned[probeId].query)) }),
    ntildeRunPclTerminal: async (probeId, ctx) => {
        ctx.write(pclPinned[probeId].terminalWrites[0]);
        ctx.write(pclPinned[probeId].terminalWrites[1]);
        const pos = await ctx.queryCursorPosition();
        return { probeId, ...replayPclTerminal(probeId, pos === null ? null : [pos.row - 1, pos.col - 1]) };
    },
};
const config = { runId: 'a'.repeat(32) };

test('the driver runs start, bind, every plan step of both cases, the pinned replays and finish, in order', async () => {
    const { fence, owner, calls } = fakeIo();
    const run = await collectPclGrading({ config, fence, owner, gradingModule });
    assert.equal(run.adapterId, pclAdapterId);
    assert.deepEqual(run.probeIds, pclCaseIds);
    assert.equal(run.cases.length, pclContracts.length);
    for (const [index, item] of run.cases.entries()) {
        const each = pclContracts[index];
        assert.equal(item.caseId, each.caseId);
        assert.deepEqual(item.feeds, pclFeeds(each), 'the recorded feeds are the contract\'s plan feeds, byte for byte');
        assert.equal(item.barriers.length, item.feeds.length);
        assert.equal(item.reads.length, pclReads(each));
        assert.deepEqual(item.reads.map(read => read.kind), each.plan.filter(step => step.read).map(step => step.read));
        assert.equal(item.pinned.termless.observation.outcome, 'inconclusive');
        assert.equal(item.pinned.terminal.observation.outcome, 'inconclusive');
    }
    const kinds = calls.filter(call => call[0] === 'owner').map(call => call[1]);
    assert.equal(kinds[0], 'pcl-start');
    assert.equal(kinds[1], 'pcl-bind');
    assert.equal(kinds.at(-1), 'pcl-finish');
    assert.equal(kinds.filter(kind => kind === 'pcl-read').length, pclContracts.reduce((sum, each) => sum + pclReads(each), 0));
    assert.deepEqual(calls.find(call => call[0] === 'write'), ['write', '\x1b[?1049h' + pclPlanSeed(config.runId)]);
    assert.equal(pclBind(config.runId), '\x1b[?1049h' + pclPlanSeed(config.runId));
    assert.deepEqual(calls.at(-2), ['write', pclFinish(config.runId)]);
    assert.equal(calls.filter(call => call[0] === 'exchange').length, contract.groups.length * pclContracts.length, 'one fenced exchange per group per case');
});

test('a collection failure still restores, and a restoration failure is reported with it', async () => {
    const failing = fakeIo({ failAt: 3 });
    await assert.rejects(collectPclGrading({ config, fence: failing.fence, owner: failing.owner, gradingModule }), /read failed/);
    assert.equal(failing.calls.at(-1)[1], 'pcl-finish', 'the finish ran after the failure');
    const both = fakeIo({ failAt: 3, failFinish: true });
    await assert.rejects(collectPclGrading({ config, fence: both.fence, owner: both.owner, gradingModule }), error => error instanceof AggregateError && /pointer-color collection and restoration failed/.test(error.message));
    const onlyFinish = fakeIo({ failFinish: true });
    await assert.rejects(collectPclGrading({ config, fence: onlyFinish.fence, owner: onlyFinish.owner, gradingModule }), /finish failed/);
});

test('a layered bundle whose pinned code makes different calls is refused', async () => {
    const extra = { ...gradingModule, ntildeRunPclTermless: (probeId, ctx) => ({ probeId, ...replayPclTermless(probeId, ctx.feedCapture(pclPinned[probeId].query) + ctx.feedCapture(pclPinned[probeId].query)) }) };
    const first = fakeIo();
    await assert.rejects(collectPclGrading({ config, fence: first.fence, owner: first.owner, gradingModule: extra }), /out of order/, 'an extra pinned call is refused by the context');
    const skipped = { ...gradingModule, ntildeRunPclTermless: probeId => ({ probeId, ...replayPclTermless(probeId, '') }) };
    const second = fakeIo();
    await assert.rejects(collectPclGrading({ config, fence: second.fence, owner: second.owner, gradingModule: skipped }), /different number of calls/, 'a pinned path that skips its call is refused');
    const wrongWrites = { ...gradingModule, ntildeRunPclTerminal: async (probeId, ctx) => { ctx.write('\x1b]113\x07'); return { probeId, ...replayPclTerminal(probeId, [0, 0]) }; } };
    const third = fakeIo();
    await assert.rejects(collectPclGrading({ config, fence: third.fence, owner: third.owner, gradingModule: wrongWrites }), /different writes/, 'other terminal writes are refused');
});
