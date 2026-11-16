import assert from 'node:assert/strict';
import { test } from 'node:test';
import { xsvRecordingContext, xsvExpectedCalls } from './xsv-collector.mjs';
import { xsvContracts } from './xsv-contracts.mjs';

const da = '\x1b[?62;4;22c';
const byId = Object.fromEntries(xsvContracts.map(item => [item.caseId, item]));
const save = byId['modes.xtsave'], restore = byId['modes.xtrestore'];
const exchange = { barrier: da, replies: [] };

test('the save termless context serves its capture, the DA1 barrier and accepts only the pinned calls in order', () => {
    const context = xsvRecordingContext(save, 'termless', exchange, [0, 0]);
    assert.equal(context.feedCapture('\x1b[?7s'), '');
    assert.equal(context.feedCapture('\x1b[c'), da);
    context.feed('\x1b[?7r');
    assert.equal(context.callCount, 3);
    assert.throws(() => context.feed('\x1b[?7r'), /differs from the contract/, 'a fourth call is refused');
});

test('the restore termless context feeds the save first, captures the restore and then the DA1 barrier', () => {
    const context = xsvRecordingContext(restore, 'termless', { barrier: da, replies: ['\x1b[?0;0#Q'] }, [0, 0]);
    context.feed('\x1b[?7s');
    assert.equal(context.feedCapture('\x1b[?7r'), '\x1b[?0;0#Q', 'output of the restore is served byte for byte');
    assert.equal(context.feedCapture('\x1b[c'), da);
    assert.equal(context.callCount, 3);
});

test('contexts refuse a different call kind, text or order and any read', async () => {
    assert.throws(() => xsvRecordingContext(save, 'termless', exchange, [0, 0]).feed('\x1b[?7s'), /differs from the contract/, 'the save is captured, not plain-fed');
    assert.throws(() => xsvRecordingContext(save, 'termless', exchange, [0, 0]).feedCapture('\x1b[?7r'), /differs from the contract/);
    assert.throws(() => xsvRecordingContext(restore, 'termless', exchange, [0, 0]).feedCapture('\x1b[?7r'), /differs from the contract/, 'the restore captures only after the save');
    const context = xsvRecordingContext(save, 'terminal', exchange, [0, 0]);
    for (const [name, what] of [['getCell', /cells/], ['getCursor', /the cursor/], ['getText', /text/], ['getTitle', /the title/]]) assert.throws(() => context[name](), what);
    await assert.rejects(context.queryCursorPosition(), /differs from the contract/, 'the cursor query comes after the save write');
});

test('the terminal contexts serve the read cursor as a one-based position, in the pinned order', async () => {
    const saved = xsvRecordingContext(save, 'terminal', exchange, [2, 5]);
    saved.write('\x1b[?7s');
    assert.deepEqual(await saved.queryCursorPosition(), { row: 3, col: 6 });
    saved.write('\x1b[?7r');
    assert.equal(saved.callCount, 3);
    const restored = xsvRecordingContext(restore, 'terminal', exchange, [0, 0]);
    restored.write('\x1b[?7s');
    restored.write('\x1b[?7r');
    assert.deepEqual(await restored.queryCursorPosition(), { row: 1, col: 1 });
    assert.equal(await xsvRecordingContext(restore, 'terminal', exchange, null).queryCursorPosition === undefined, false);
    const absent = xsvRecordingContext(restore, 'terminal', exchange, null);
    absent.write('\x1b[?7s'); absent.write('\x1b[?7r');
    assert.equal(await absent.queryCursorPosition(), null, 'no cursor means no position');
});

test('the expected call count is the full script, or the early invalid-reply return of the termless path', () => {
    const noisy = { barrier: da, replies: ['\x1b[?0;0#Q'] };
    assert.equal(xsvExpectedCalls(save, 'termless', exchange), 3);
    assert.equal(xsvExpectedCalls(save, 'termless', noisy), 1, 'the save probe stops at its first capture');
    assert.equal(xsvExpectedCalls(restore, 'termless', noisy), 2, 'the restore probe stops at its capture after the save feed');
    for (const contract of [save, restore]) assert.equal(xsvExpectedCalls(contract, 'terminal', noisy), 3, 'the terminal path makes every call whatever came back');
    assert.equal(xsvExpectedCalls(restore, 'termless', null), 3);
    const context = xsvRecordingContext(save, 'termless', noisy, [0, 0]);
    assert.equal(context.feedCapture('\x1b[?7s'), '\x1b[?0;0#Q');
    assert.equal(context.callCount, xsvExpectedCalls(save, 'termless', noisy));
});

test('the driver replays every plan through a fence, journals every read and runs both pinned paths over the journaled exchange', async () => {
    const { collectXsvGrading } = await import('./xsv-collector.mjs');
    const { xsvContracts, xsvCaseIds, replayXsvPinned, xsvPlanSeed } = await import('./xsv-contracts.mjs');
    const { parseXsvFrame } = await import('./xsv-report.mjs');
    const { planFeeds } = await import('./graded-plan.mjs');
    const runId = 'c'.repeat(32), feeds = [], kinds = [];
    const fence = { write: async text => { feeds.push(text); return da; }, exchange: async (text, parse) => { feeds.push(text); return parse(da); } };
    const owner = async kind => { kinds.push(kind); return { screen: { cursorRow: 7, cursorCol: 1 } }; };
    // A stand-in for the derived bundle that makes exactly the pinned calls of each path over the context it is served.
    const served = (path, caseId, context) => {
        const contract = xsvContracts.find(item => item.caseId === caseId);
        const pending = [];
        for (const [kind, text] of contract.pinnedScript[path]) pending.push(kind === 'cursor' ? context.queryCursorPosition() : context[kind](text));
        return { pending, result: { probeId: caseId, ...replayXsvPinned(caseId, path, { barrier: da, position: { row: 8, col: 2 } }) } };
    };
    const termless = (caseId, context) => served('termless', caseId, context).result;
    const terminal = async (caseId, context) => { const { pending, result } = served('terminal', caseId, context); await Promise.all(pending); return result; };
    const gradingModule = { ntildeXsvAdapter: { id: 'stub', probeIds: [...xsvCaseIds] }, ntildeRunXsvTermless: termless, ntildeRunXsvTerminal: terminal };
    const result = await collectXsvGrading({ config: { runId }, fence, owner, gradingModule });
    assert.deepEqual(result.cases.map(item => item.caseId), [...xsvCaseIds]);
    assert.equal(kinds[0], 'xsv-start');
    assert.equal(kinds.at(-1), 'xsv-finish');
    assert.equal(kinds.filter(kind => kind === 'xsv-read').length, xsvContracts.reduce((sum, item) => sum + item.roles.length, 0));
    for (const item of result.cases) {
        const contract = xsvContracts.find(entry => entry.caseId === item.caseId);
        assert.deepEqual(item.feeds, planFeeds(contract));
        assert.equal(item.reads.length, contract.roles.length);
        assert.deepEqual(item.reads[contract.pinnedRead].report, parseXsvFrame(da), 'the pinned read journals its own silent exchange');
        assert.equal(item.pinned.termless.observation.outcome, 'inconclusive');
    }
    assert.ok(feeds[0].includes(xsvPlanSeed(runId).slice(0, 8)) || feeds[0].startsWith('\x1b[?1049h'), 'the bind opens the alternate screen with the seed');
});
