import assert from 'node:assert/strict';
import { test } from 'node:test';
import { collectDslGrading, dslBind, dslRecordingContext, dslTerminalContext, heldFence } from './dsl-collector.mjs';
import { dslContracts, dslFeeds, dslFinish, dslPlanSeed, dslReads, replayDslTermless, replayDslTerminal, dslCaseIds, dslPinnedScripts } from './dsl-contracts.mjs';
import { dslAdapterId } from './dsl-grading-collector.mjs';
import { syntheticDslExchanges } from './fixtures/dsl-evidence.mjs';

const [contract] = dslContracts;
const caseId = contract.caseId;
const da = '\x1b[?62;4;22c';
const reply = '\x1b[?4;0$y';
const exchange = { barrier: da, replies: [reply] };
const [setFour, queryFour, resetFour] = contract.pinnedFeeds;

test('the termless context serves the captured reply for exactly the pinned calls', () => {
    const served = dslRecordingContext(contract, exchange);
    served.context.feed(setFour);
    assert.equal(served.context.feedCapture(queryFour), reply);
    served.context.feed(resetFour);
    assert.equal(served.callCount, 3);
    assert.equal(served.expectedCalls, 3);
    const absent = dslRecordingContext(contract, null);
    absent.context.feed(setFour);
    assert.equal(absent.context.feedCapture(queryFour), '', 'an absent exchange is the empty capture');
});

test('the termless context refuses calls out of order, other bytes and every read the pinned code must not make', () => {
    assert.throws(() => dslRecordingContext(contract, exchange).context.feed(resetFour), /differs from the contract/, 'the first call must be the set');
    const served = dslRecordingContext(contract, exchange);
    served.context.feed(setFour);
    assert.throws(() => served.context.feedCapture('\x1b[?5$p'), /differs from the contract/);
    assert.throws(() => served.context.feed(queryFour), /differs from the contract/, 'a plain feed where a capture is contracted');
    const complete = dslRecordingContext(contract, exchange);
    for (const text of [setFour]) complete.context.feed(text);
    complete.context.feedCapture(queryFour);
    complete.context.feed(resetFour);
    assert.throws(() => complete.context.feed(setFour), /differs from the contract/, 'a fourth call is refused');
    for (const name of ['getText', 'getCell', 'getCursor', 'getMode', 'queryMode']) assert.throws(() => served.context[name], /Unexpected pinned context access/, name);
});

test('the terminal context answers the mode state of the journaled replies, once, for mode 4 only', async () => {
    const answers = [[exchange, 'unknown'], [null, null], [{ barrier: da, replies: ['\x1b[?4;1$y'] }, 'set'], [{ barrier: da, replies: ['\x1b[?4;2$y'] }, 'reset'],
        [{ barrier: da, replies: ['\x1b[?4;3$y'] }, 'set'], [{ barrier: da, replies: ['\x1b[?4;4$y'] }, 'reset'], [{ barrier: da, replies: ['\x1b[?3;0$y'] }, null]];
    for (const [served, expected] of answers) {
        const terminal = dslTerminalContext(contract, served);
        assert.equal(await terminal.context.queryMode(4), expected);
        assert.equal(terminal.askedCount, 1);
    }
    const twice = dslTerminalContext(contract, exchange);
    await twice.context.queryMode(4);
    await assert.rejects(twice.context.queryMode(4), /unexpected mode query/);
    await assert.rejects(dslTerminalContext(contract, exchange).context.queryMode(3), /unexpected mode query/);
    assert.throws(() => dslTerminalContext(contract, exchange).context.write, /Unexpected pinned context access/);
});

/** A fake owned fence and owner that serve the fixture's hand-written replies group by group and record every call. */
function fakeIo({ failAt = null, failFinish = false } = {}) {
    const calls = [], roles = contract.groups.map(group => group.role);
    let exchanges = 0, reads = 0;
    const fence = {
        write: async text => { calls.push(['write', text]); return da; },
        exchange: async (text, parse) => {
            calls.push(['exchange', text]);
            const replies = syntheticDslExchanges[roles[exchanges++ % roles.length]] ?? [];
            const parsed = parse(replies.join('') + da);
            assert.deepEqual(parsed, { barrier: da, replies }, 'the frame parser accepts the retained replies');
            return parsed;
        },
    };
    const owner = async (kind, barrier, extras = {}) => {
        calls.push(['owner', kind, barrier ?? null, extras.report ? 'report' : null]);
        if (kind === 'dsl-finish' && failFinish) throw new Error('finish failed');
        if (kind === 'dsl-read' && reads++ === failAt) throw new Error('read failed');
        return { screen: { cursorRow: 0, cursorCol: 0, rows: 13, cols: 60 } };
    };
    return { fence, owner, calls };
}

/** A stand-in for the layered bundle: its pinned functions replay the same results, called with the real recording contexts. */
const gradingModule = {
    ntildeDslAdapter: { id: dslAdapterId, probeIds: [...dslCaseIds] },
    ntildeRunDslTermless: (probeId, ctx) => {
        ctx.feed(setFour);
        const text = ctx.feedCapture(queryFour);
        ctx.feed(resetFour);
        return { probeId, ...replayDslTermless(probeId, text) };
    },
    ntildeRunDslTerminal: async (probeId, ctx) => ({ probeId, ...replayDslTerminal(probeId, [(await ctx.queryMode(4)) === 'unknown' ? reply : '']) }),
};
const config = { runId: 'a'.repeat(32) };

test('the driver runs start, bind, every plan step, the pinned replays and finish, in order', async () => {
    const { fence, owner, calls } = fakeIo();
    const run = await collectDslGrading({ config, fence, owner, gradingModule });
    assert.equal(run.adapterId, dslAdapterId);
    assert.deepEqual(run.probeIds, dslCaseIds);
    assert.equal(run.cases.length, dslContracts.length);
    const [item] = run.cases;
    assert.equal(item.caseId, contract.caseId);
    assert.deepEqual(item.feeds, dslFeeds(contract), 'the recorded feeds are the contract\'s plan feeds, byte for byte');
    assert.equal(item.barriers.length, item.feeds.length);
    assert.equal(item.reads.length, dslReads(contract));
    assert.deepEqual(item.reads.map(read => read.kind), contract.plan.filter(step => step.read).map(step => step.read));
    assert.ok(item.reads.every(read => JSON.stringify(read.size) === JSON.stringify([13, 60])), 'every read journals the App grid it was observed at');
    assert.equal(item.pinned.termless.observation.outcome, 'inconclusive');
    assert.equal(item.pinned.terminal.observation.outcome, 'inconclusive');
    assert.equal(item.pinned.termless.observation.evidence, 'parser-state');
    assert.equal(item.pinned.terminal.observation.evidence, 'query');
    const kinds = calls.filter(call => call[0] === 'owner').map(call => call[1]);
    assert.equal(kinds[0], 'dsl-start');
    assert.equal(kinds[1], 'dsl-bind');
    assert.equal(kinds.at(-1), 'dsl-finish');
    assert.equal(kinds.filter(kind => kind === 'dsl-read').length, dslReads(contract));
    assert.deepEqual(calls.find(call => call[0] === 'write'), ['write', '\x1b[?1049h' + dslPlanSeed(config.runId)]);
    assert.equal(dslBind(config.runId), '\x1b[?1049h' + dslPlanSeed(config.runId));
    assert.deepEqual(calls.at(-2), ['write', dslFinish(config.runId)]);
    assert.equal(calls.filter(call => call[0] === 'exchange').length, contract.groups.length, 'one fenced exchange per group');
});

test('a collection failure still restores, and a restoration failure is reported with it', async () => {
    const failing = fakeIo({ failAt: 3 });
    await assert.rejects(collectDslGrading({ config, fence: failing.fence, owner: failing.owner, gradingModule }), /read failed/);
    assert.equal(failing.calls.at(-1)[1], 'dsl-finish', 'the finish ran after the failure');
    const both = fakeIo({ failAt: 3, failFinish: true });
    await assert.rejects(collectDslGrading({ config, fence: both.fence, owner: both.owner, gradingModule }), error => error instanceof AggregateError && /decsclm collection and restoration failed/.test(error.message));
    const onlyFinish = fakeIo({ failFinish: true });
    await assert.rejects(collectDslGrading({ config, fence: onlyFinish.fence, owner: onlyFinish.owner, gradingModule }), /finish failed/);
});

test('a layered bundle whose pinned code makes different calls is refused', async () => {
    const extra = { ...gradingModule, ntildeRunDslTermless: (probeId, ctx) => { ctx.feed(setFour); ctx.feedCapture(queryFour); ctx.feed(resetFour); ctx.feed(setFour); return { probeId }; } };
    await assert.rejects(async () => { const io = fakeIo(); await collectDslGrading({ config, fence: io.fence, owner: io.owner, gradingModule: extra }); }, /differs from the contract/, 'an extra pinned call is refused by the context');
    const skipped = { ...gradingModule, ntildeRunDslTermless: probeId => ({ probeId, ...replayDslTermless(probeId, reply) }) };
    await assert.rejects(async () => { const io = fakeIo(); await collectDslGrading({ config, fence: io.fence, owner: io.owner, gradingModule: skipped }); }, /different number of calls/, 'a pinned path that skips its calls is refused');
    const silentTerminal = { ...gradingModule, ntildeRunDslTerminal: async probeId => ({ probeId, ...replayDslTerminal(probeId, [reply]) }) };
    await assert.rejects(async () => { const io = fakeIo(); await collectDslGrading({ config, fence: io.fence, owner: io.owner, gradingModule: silentTerminal }); }, /different number of calls/, 'a terminal path that never asks is refused');
    assert.deepEqual(dslPinnedScripts[caseId].terminal, [['queryMode', 4]]);
});

test('only the contracted instrument control is held after its exchange, by exactly its contracted time', async () => {
    const sleeps = [], seen = [];
    const fence = { write: async text => { seen.push(['write', text]); return da; }, exchange: async (text, parse) => { seen.push(['exchange', text]); return parse(da); } };
    const held = heldFence(fence, contract, async ms => { sleeps.push(ms); seen.push(['sleep', ms]); });
    for (const group of contract.groups) await held.exchange(group.request, () => ({ barrier: da, replies: [] })); // NOSONAR - serial by design
    await held.write('x');
    assert.deepEqual(sleeps, [2500], 'only the paced group waits');
    const pacedAt = seen.findIndex(call => call[0] === 'sleep');
    assert.equal(seen[pacedAt - 1][1], contract.groups.find(group => group.role === 'paced').request, 'the wait follows the exchange it holds, never precedes it');
    assert.deepEqual(seen.at(-1), ['write', 'x'], 'writes pass straight through');
});

test('an exchange that is not the contracted request of its position is refused before anything is sent', async () => {
    const fence = { write: async () => da, exchange: async () => { throw new Error('must not be sent'); } };
    await assert.rejects(heldFence(fence, contract).exchange('\x1b[c', () => null), /is not the contracted request/);
    const held = heldFence({ write: async () => da, exchange: async (text, parse) => parse(da) }, contract);
    for (const group of contract.groups) await held.exchange(group.request, () => ({ barrier: da, replies: [] })); // NOSONAR - serial by design
    await assert.rejects(held.exchange(contract.groups[0].request, () => null), /is not the contracted request/, 'a thirteenth exchange has no group');
});
