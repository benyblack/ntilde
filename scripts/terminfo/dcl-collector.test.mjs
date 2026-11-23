import assert from 'node:assert/strict';
import { test } from 'node:test';
import { collectDclGrading, dclBind, dclRecordingContext, dclTerminalContext } from './dcl-collector.mjs';
import { dclContracts, dclFeeds, dclFinish, dclPlanSeed, dclReads, replayDclTermless, replayDclTerminal, dclCaseIds, dclPinnedScripts } from './dcl-contracts.mjs';
import { dclAdapterId } from './dcl-grading-collector.mjs';
import { syntheticDclExchanges } from './fixtures/dcl-evidence.mjs';

const [contract] = dclContracts;
const caseId = contract.caseId;
const da = '\x1b[?62;4;22c';
const reply = '\x1b[?3;0$y';
const exchange = { barrier: da, replies: [reply] };
const [setThree, queryThree, resetThree] = contract.pinnedFeeds;

test('the termless context serves the captured reply for exactly the pinned calls and reports the App width', () => {
    const served = dclRecordingContext(contract, exchange, 60);
    served.context.feed(setThree);
    assert.equal(served.context.feedCapture(queryThree), reply);
    served.context.feed(resetThree);
    assert.equal(served.callCount, 3);
    assert.equal(served.expectedCalls, 3);
    assert.equal(served.context.cols, 60);
    const absent = dclRecordingContext(contract, null, 60);
    absent.context.feed(setThree);
    assert.equal(absent.context.feedCapture(queryThree), '', 'an absent exchange is the empty capture');
});

test('the termless context refuses calls out of order, other bytes and every read the pinned code must not make', () => {
    assert.throws(() => dclRecordingContext(contract, exchange, 60).context.feed(resetThree), /differs from the contract/, 'the first call must be the set');
    const served = dclRecordingContext(contract, exchange, 60);
    served.context.feed(setThree);
    assert.throws(() => served.context.feedCapture('\x1b[?4$p'), /differs from the contract/);
    assert.throws(() => served.context.feed(queryThree), /differs from the contract/, 'a plain feed where a capture is contracted');
    const complete = dclRecordingContext(contract, exchange, 60);
    for (const text of [setThree]) complete.context.feed(text);
    complete.context.feedCapture(queryThree);
    complete.context.feed(resetThree);
    assert.throws(() => complete.context.feed(setThree), /differs from the contract/, 'a fourth call is refused');
    for (const name of ['getText', 'getCell', 'getCursor', 'getMode', 'queryMode']) assert.throws(() => served.context[name], /Unexpected pinned context access/, name);
});

test('the terminal context answers the mode state of the journaled replies, once, for mode 3 only', async () => {
    const answers = [[exchange, 'unknown'], [null, null], [{ barrier: da, replies: ['\x1b[?3;1$y'] }, 'set'], [{ barrier: da, replies: ['\x1b[?3;2$y'] }, 'reset'],
        [{ barrier: da, replies: ['\x1b[?3;3$y'] }, 'set'], [{ barrier: da, replies: ['\x1b[?3;4$y'] }, 'reset'], [{ barrier: da, replies: ['\x1b[?40;0$y'] }, null]];
    for (const [served, expected] of answers) {
        const terminal = dclTerminalContext(contract, served);
        assert.equal(await terminal.context.queryMode(3), expected);
        assert.equal(terminal.askedCount, 1);
    }
    const twice = dclTerminalContext(contract, exchange);
    await twice.context.queryMode(3);
    await assert.rejects(twice.context.queryMode(3), /unexpected mode query/);
    await assert.rejects(dclTerminalContext(contract, exchange).context.queryMode(40), /unexpected mode query/);
    assert.throws(() => dclTerminalContext(contract, exchange).context.write, /Unexpected pinned context access/);
});

/** A fake owned fence and owner that serve the fixture's hand-written replies group by group and record every call. */
function fakeIo({ failAt = null, failFinish = false } = {}) {
    const calls = [], roles = contract.groups.map(group => group.role);
    let exchanges = 0, reads = 0;
    const fence = {
        write: async text => { calls.push(['write', text]); return da; },
        exchange: async (text, parse) => {
            calls.push(['exchange', text]);
            const replies = syntheticDclExchanges[roles[exchanges++ % roles.length]] ?? [];
            const parsed = parse(replies.join('') + da);
            assert.deepEqual(parsed, { barrier: da, replies }, 'the frame parser accepts the retained replies');
            return parsed;
        },
    };
    const owner = async (kind, barrier, extras = {}) => {
        calls.push(['owner', kind, barrier ?? null, extras.report ? 'report' : null]);
        if (kind === 'dcl-finish' && failFinish) throw new Error('finish failed');
        if (kind === 'dcl-read' && reads++ === failAt) throw new Error('read failed');
        return { screen: { cursorRow: 0, cursorCol: 0, rows: 13, cols: 60 } };
    };
    return { fence, owner, calls };
}

/** A stand-in for the layered bundle: its pinned functions replay the same results, called with the real recording contexts. */
const gradingModule = {
    ntildeDclAdapter: { id: dclAdapterId, probeIds: [...dclCaseIds] },
    ntildeRunDclTermless: (probeId, ctx) => {
        ctx.feed(setThree);
        const text = ctx.feedCapture(queryThree);
        ctx.feed(resetThree);
        return { probeId, ...replayDclTermless(probeId, text, ctx.cols) };
    },
    ntildeRunDclTerminal: async (probeId, ctx) => ({ probeId, ...replayDclTerminal(probeId, [(await ctx.queryMode(3)) === 'unknown' ? reply : '']) }),
};
const config = { runId: 'a'.repeat(32) };

test('the driver runs start, bind, every plan step, the pinned replays and finish, in order', async () => {
    const { fence, owner, calls } = fakeIo();
    const run = await collectDclGrading({ config, fence, owner, gradingModule });
    assert.equal(run.adapterId, dclAdapterId);
    assert.deepEqual(run.probeIds, dclCaseIds);
    assert.equal(run.cases.length, dclContracts.length);
    const [item] = run.cases;
    assert.equal(item.caseId, contract.caseId);
    assert.deepEqual(item.feeds, dclFeeds(contract), 'the recorded feeds are the contract\'s plan feeds, byte for byte');
    assert.equal(item.barriers.length, item.feeds.length);
    assert.equal(item.reads.length, dclReads(contract));
    assert.deepEqual(item.reads.map(read => read.kind), contract.plan.filter(step => step.read).map(step => step.read));
    assert.ok(item.reads.every(read => JSON.stringify(read.size) === JSON.stringify([13, 60])), 'every read journals the App grid it was observed at');
    assert.equal(item.pinned.termless.observation.outcome, 'inconclusive');
    assert.equal(item.pinned.terminal.observation.outcome, 'inconclusive');
    assert.equal(item.pinned.termless.observation.evidence, 'parser-state');
    assert.equal(item.pinned.terminal.observation.evidence, 'query');
    const kinds = calls.filter(call => call[0] === 'owner').map(call => call[1]);
    assert.equal(kinds[0], 'dcl-start');
    assert.equal(kinds[1], 'dcl-bind');
    assert.equal(kinds.at(-1), 'dcl-finish');
    assert.equal(kinds.filter(kind => kind === 'dcl-read').length, dclReads(contract));
    assert.deepEqual(calls.find(call => call[0] === 'write'), ['write', '\x1b[?1049h' + dclPlanSeed(config.runId)]);
    assert.equal(dclBind(config.runId), '\x1b[?1049h' + dclPlanSeed(config.runId));
    assert.deepEqual(calls.at(-2), ['write', dclFinish(config.runId)]);
    assert.equal(calls.filter(call => call[0] === 'exchange').length, contract.groups.length, 'one fenced exchange per group');
});

test('a collection failure still restores, and a restoration failure is reported with it', async () => {
    const failing = fakeIo({ failAt: 3 });
    await assert.rejects(collectDclGrading({ config, fence: failing.fence, owner: failing.owner, gradingModule }), /read failed/);
    assert.equal(failing.calls.at(-1)[1], 'dcl-finish', 'the finish ran after the failure');
    const both = fakeIo({ failAt: 3, failFinish: true });
    await assert.rejects(collectDclGrading({ config, fence: both.fence, owner: both.owner, gradingModule }), error => error instanceof AggregateError && /deccolm collection and restoration failed/.test(error.message));
    const onlyFinish = fakeIo({ failFinish: true });
    await assert.rejects(collectDclGrading({ config, fence: onlyFinish.fence, owner: onlyFinish.owner, gradingModule }), /finish failed/);
});

test('a layered bundle whose pinned code makes different calls is refused', async () => {
    const extra = { ...gradingModule, ntildeRunDclTermless: (probeId, ctx) => { ctx.feed(setThree); ctx.feedCapture(queryThree); ctx.feed(resetThree); ctx.feed(setThree); return { probeId }; } };
    await assert.rejects(async () => { const io = fakeIo(); await collectDclGrading({ config, fence: io.fence, owner: io.owner, gradingModule: extra }); }, /differs from the contract/, 'an extra pinned call is refused by the context');
    const skipped = { ...gradingModule, ntildeRunDclTermless: probeId => ({ probeId, ...replayDclTermless(probeId, reply, 60) }) };
    await assert.rejects(async () => { const io = fakeIo(); await collectDclGrading({ config, fence: io.fence, owner: io.owner, gradingModule: skipped }); }, /different number of calls/, 'a pinned path that skips its calls is refused');
    const silentTerminal = { ...gradingModule, ntildeRunDclTerminal: async probeId => ({ probeId, ...replayDclTerminal(probeId, [reply]) }) };
    await assert.rejects(async () => { const io = fakeIo(); await collectDclGrading({ config, fence: io.fence, owner: io.owner, gradingModule: silentTerminal }); }, /different number of calls/, 'a terminal path that never asks is refused');
    assert.deepEqual(dclPinnedScripts[caseId].terminal, [['queryMode', 3]]);
});
