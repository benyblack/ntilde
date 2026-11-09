import assert from 'node:assert/strict';
import { test } from 'node:test';
import { collectScrollbackGrading, requirePristineScrollbackStart } from './scrollback-collector.mjs';
import { scrollbackReset, scrollbackNoncePrefix } from './scrollback-contracts.mjs';
import { createScrollbackModel, feedScrollbackModel, scrollbackModelState } from './scrollback-model.mjs';
import { syntheticScrollbackScreen, scrollbackRows, scrollbackCols, blankState } from './fixtures/scrollback-evidence.mjs';

const runId = 'b'.repeat(32);
/** A fake owned fence over the independent model: writes mutate the model, reads return its state as a valid screen. */
function fakeWorld({ failRead = null, dirtyStart = false } = {}) {
    const model = createScrollbackModel(scrollbackRows, scrollbackCols);
    const writes = [], kinds = [];
    const fence = { async write(bytes) { writes.push(bytes); feedScrollbackModel(model, bytes); return '\x1b[?62;4;22c'; } };
    const owner = async (kind, barrier) => {
        kinds.push(kind);
        if (failRead !== null && kind === 'scrl-read' && kinds.filter(value => value === 'scrl-read').length === failRead) throw new Error('observer budget exhausted');
        const state = kind === 'scrl-start' && dirtyStart ? { ...blankState(), history: ['stale'] } : scrollbackModelState(model);
        return { screen: syntheticScrollbackScreen(state), barrier };
    };
    return { fence, owner, writes, kinds, model };
}
const stubModule = { ntildeScrollbackAdapter: { id: 'stub', probeIds: ['a'] },
    ntildeRunScrollbackTermless: (probeId, context) => { for (const feed of []) context.feed(feed); return { probeId, pass: false, observation: { outcome: 'inconclusive', evidence: 'none' } }; } };

test('the collector binds on the main screen, replays every contract plan serially and restores', async () => {
    const world = fakeWorld();
    const run = await collectScrollbackGrading({ config: { runId }, fence: world.fence, owner: world.owner, gradingModule: stubModule });
    assert.equal(world.writes[0], scrollbackReset + scrollbackNoncePrefix + runId, 'the bind write is the reset plus the nonce, on the main screen');
    assert.equal(world.writes.at(-1), scrollbackReset, 'the finish write restores the pristine main screen');
    assert.ok(!world.writes.slice(0, 1).join('').includes('\x1b[?1049h'), 'binding never enters the alternate screen');
    assert.equal(world.kinds[0], 'scrl-start');
    assert.equal(world.kinds[1], 'scrl-bind');
    assert.equal(world.kinds.at(-1), 'scrl-finish');
    assert.equal(world.kinds.filter(kind => kind === 'scrl-read').length, 45);
    assert.equal(run.cases.length, 9);
    assert.deepEqual(run.cases.map(item => item.caseId).slice(0, 3), ['scrollback.accumulate', 'scrollback.total-lines', 'scrollback.scroll-up']);
    for (const item of run.cases) {
        assert.equal(item.feeds.length, item.barriers.length);
        assert.ok(item.reads.every(read => read.kind === 'state'));
        assert.equal(item.feeds[0], scrollbackReset);
    }
    // The model the fake terminal ran is restored to a pristine state by the finish write.
    assert.deepEqual(scrollbackModelState(world.model).history, []);
    assert.ok(scrollbackModelState(world.model).lines.every(line => line === ''));
});

test('a dirty start is refused before any fixture byte is written', async () => {
    const world = fakeWorld({ dirtyStart: true });
    await assert.rejects(collectScrollbackGrading({ config: { runId }, fence: world.fence, owner: world.owner, gradingModule: stubModule }),
        /require a pristine private main screen with empty history/);
    assert.equal(world.writes.length, 1, 'only the restoring finish write is sent');
    assert.equal(world.writes[0], scrollbackReset);
});

test('a failed read still restores, and the collection failure is reported', async () => {
    const world = fakeWorld({ failRead: 7 });
    await assert.rejects(collectScrollbackGrading({ config: { runId }, fence: world.fence, owner: world.owner, gradingModule: stubModule }), /observer budget exhausted/);
    assert.equal(world.writes.at(-1), scrollbackReset);
    assert.equal(world.kinds.at(-1), 'scrl-finish');
});

test('pristine means a blank main screen, empty history, default margins and modes', () => {
    const good = syntheticScrollbackScreen(blankState());
    assert.doesNotThrow(() => requirePristineScrollbackStart(good));
    for (const edit of [screen => { screen.lines[0] = 'x'; }, screen => { screen.cursorRow = 1; }, screen => { screen.bufferState.alternateScreen = true; },
        screen => { screen.bufferState.scrollTop = 1; }, screen => { screen.bufferState.scrollBottom = 3; }, screen => { screen.bufferState.originMode = true; },
        screen => { screen.bufferState.insertMode = true; }, screen => { screen.bufferState.pendingWrap = true; },
        screen => { screen.bufferState.scrollbackLines = 1; screen.history = { startLine: 0, totalLines: 1, lines: ['x'] }; }, screen => { delete screen.history; }]) {
        const screen = structuredClone(good);
        edit(screen);
        assert.throws(() => requirePristineScrollbackStart(screen), /pristine private main screen/);
    }
});
