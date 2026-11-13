import assert from 'node:assert/strict';
import { test } from 'node:test';
import { collectInsertReplace } from './insert-replace-collector.mjs';
import { insertReplaceContract, insertReplacePinnedFeeds, insertReplaceTerminalWrites } from './insert-replace-contracts.mjs';
import { syntheticLineEditScreen, rowOf } from './fixtures/line-edit-evidence.mjs';

const screen = (text, col) => {
    const value = syntheticLineEditScreen([rowOf(text)], [0, col]);
    value.horizontalMargins = { leftRightMarginMode: false, leftMargin: 0, rightMargin: value.cols - 1 };
    return value;
};
const start = () => { const item = screen('', 0); item.bufferState.alternateScreen = false; item.bufferState.insertMode = true; return item; };
const module_ = { ntildeInsertReplaceAdapter: { id: 'ntilde-owned-insert-replace-grading-v1', probeIds: ['modes.insert-replace'] },
    ntildeRunInsertReplaceTermless(_id, ctx) {
        ctx.feed(insertReplacePinnedFeeds[0]); assert.equal(ctx.getCell(0, 0).char, 'A');
        ctx.feed(insertReplacePinnedFeeds[1]); assert.equal(ctx.getCursor().x, 0);
        ctx.feed(insertReplacePinnedFeeds[2]); ctx.feed(insertReplacePinnedFeeds[3]); assert.equal(ctx.getCell(0, 0).char, 'X');
        ctx.feed(insertReplacePinnedFeeds[4]); return { observation: { outcome: 'supported' } };
    },
    async ntildeRunInsertReplaceTerminal(_id, ctx) {
        for (const bytes of insertReplaceTerminalWrites) ctx.write(bytes);
        assert.deepEqual(await ctx.queryCursorPosition(), { row: 1, col: 3 });
        return { observation: { outcome: 'inconclusive' } };
    } };

function fixture(failRead = false, failFinish = false) {
    const writes = [], observed = [];
    let index = 0;
    const owner = async kind => {
        observed.push(kind);
        if (kind === 'irm-start') return { screen: start() };
        if (kind === 'irm-finish' && failFinish) throw new Error('restore observation failed');
        if (kind !== 'irm-read') return { screen: screen('', 0) };
        if (failRead) throw new Error('capture failed');
        const role = insertReplaceContract.roles[index++];
        return { screen: role === 'seed' ? screen('ABC', 3) : role === 'home' || role === 'enabled' ? screen('ABC', 0)
            : role === 'inserted' || role === 'disabled' ? screen('XABC', 1) : role === 'terminal' ? screen('AXBCD', 2) : screen('', 0) };
    };
    const fence = { async write(bytes) { writes.push(bytes); return '\x1b[?1;2c'; } };
    return { writes, observed, owner, fence };
}

test('serial collection replays both paths and restores the measured original modes', async () => {
    const f = fixture();
    const result = await collectInsertReplace({ config: { runId: 'a'.repeat(32) }, owner: f.owner, fence: f.fence, gradingModule: module_ });
    assert.equal(result.reads.length, insertReplaceContract.roles.length);
    assert.deepEqual(result.original, { insertMode: true, autoWrap: true });
    assert.match(f.writes.at(-1), /\x1b\[\?1049l\x1b\[4h\x1b\[\?7h$/);
    assert.equal(f.observed.at(-1), 'irm-finish');
    assert.match(result.terminalCursorEvidence, /not a PTY cursor reply/);
});

test('a failed read still attempts restoration; simultaneous failures are retained', async () => {
    const f = fixture(true);
    await assert.rejects(collectInsertReplace({ config: { runId: 'a'.repeat(32) }, owner: f.owner, fence: f.fence, gradingModule: module_ }), /capture failed/);
    assert.equal(f.observed.at(-1), 'irm-finish');
    const both = fixture(true, true);
    await assert.rejects(collectInsertReplace({ config: { runId: 'a'.repeat(32) }, owner: both.owner, fence: both.fence, gradingModule: module_ }), AggregateError);
});

test('an original non-default horizontal margin is refused before writing', async () => {
    const f = fixture();
    const owner = async kind => {
        if (kind === 'irm-start') {
            const value = start(); value.horizontalMargins.leftRightMarginMode = true;
            return { screen: value };
        }
        return f.owner(kind);
    };
    await assert.rejects(collectInsertReplace({ config: { runId: 'a'.repeat(32) }, owner, fence: f.fence, gradingModule: module_ }), /full-width margin/);
    assert.equal(f.writes.length, 0);
});

test('both originally disabled modes are restored from observation rather than assumed defaults', async () => {
    const f = fixture();
    const owner = async kind => {
        if (kind === 'irm-start') {
            const value = start(); value.bufferState.insertMode = false; value.bufferState.autoWrap = false;
            return { screen: value };
        }
        return f.owner(kind);
    };
    const result = await collectInsertReplace({ config: { runId: 'a'.repeat(32) }, owner, fence: f.fence, gradingModule: module_ });
    assert.deepEqual(result.original, { insertMode: false, autoWrap: false });
    assert.match(f.writes.at(-1), /\x1b\[\?1049l\x1b\[4l\x1b\[\?7l$/);
});

test('the pinned terminal geometry guard retains its inconclusive result without writes', async () => {
    const f = fixture();
    let reads = 0;
    const owner = async kind => {
        const result = await f.owner(kind);
        if (kind === 'irm-read' && insertReplaceContract.roles[reads++] === 'terminal') result.screen.cols = 4;
        return result;
    };
    const gradingModule = { ...module_, async ntildeRunInsertReplaceTerminal(_id, ctx) {
        assert.equal(ctx.cols, 4);
        return { probeId: 'modes.insert-replace', pass: false,
            observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'none',
                note: `Insert-replace fixture needs at least 1x5, measured ${ctx.rows}x${ctx.cols}` } };
    } };
    const result = await collectInsertReplace({ config: { runId: 'a'.repeat(32) }, owner, fence: f.fence, gradingModule });
    assert.equal(result.terminalPinned.observation.outcome, 'inconclusive');
    assert.equal(f.observed.at(-1), 'irm-finish');
});
