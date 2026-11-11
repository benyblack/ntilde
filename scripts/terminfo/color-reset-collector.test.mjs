import assert from 'node:assert/strict';
import { test } from 'node:test';
import { collectColorResetGrading, colorResetRecordingContext, pinnedReplies } from './color-reset-collector.mjs';
import { colorResetContracts, colorResetBind, colorResetFinish, colorResetNoncePrefix, colorResetReadPlan, colorResetFeeds, colorResetReads } from './color-reset-contracts.mjs';
import { pinnedColorResetProbe } from './fixtures/color-reset-pinned.mjs';
import { applyColorWrites, noOverrides, effectiveColors, queriedColor, channels16 } from './color-reset-model.mjs';
import { syntheticBaseline, syntheticColorResetEvidence } from './fixtures/color-reset-evidence.mjs';

const screen = syntheticColorResetEvidence().frames[2].screen;

const barrier = '\x1b[?62;4;22c', runId = 'c'.repeat(32);
const colorResetProbe = await pinnedColorResetProbe();
const pinnedArgs = { 'extensions.osc104-reset-palette': [4, 104, 0], 'extensions.osc110-reset-fg': [10, 110], 'extensions.osc111-reset-bg': [11, 111], 'extensions.osc112-reset-cursor': [12, 112] };
/** The pinned probe, from the pinned seams, as the layered module would export it. */
const gradingModule = Object.freeze({ ntildeColorResetAdapter: { id: 'stub', probeIds: colorResetContracts.map(item => item.caseId) },
    ntildeRunColorResetTermless: (probeId, ctx) => ({ probeId, ...colorResetProbe(...pinnedArgs[probeId])(ctx) }) });

/** A terminal that answers color queries from the model's overrides over the synthetic baseline; every fence call is recorded. */
function world({ failAt = null, failOwner = null, failFinish = false, dropReplies = false } = {}) {
    const calls = [], baseline = syntheticBaseline();
    let state = noOverrides(), failed = false, exchanges = 0;
    const apply = bytes => { state = applyColorWrites(state, bytes); };
    const fence = {
        get failed() { return failed; },
        async write(bytes) { calls.push(['write', bytes]); apply(bytes); return barrier; },
        async colorExchange(bytes, spec) {
            calls.push(['exchange', bytes]);
            if (failAt === ++exchanges) { failed = true; throw new Error('Cell barrier timed out.'); }
            apply(bytes);
            const color = queriedColor(effectiveColors(baseline, state), spec);
            return { barrier, reply: dropReplies ? null : { raw: `\x1b]${spec.code};${spec.code === 4 ? spec.index + ';' : ''}rgb:${channels16(color).map(value => value.toString(16).padStart(4, '0')).join('/')}\x1b\\`, channels16: channels16(color) } };
        },
        async writeCleanup(bytes) { calls.push(['cleanup', bytes]); },
    };
    let reads = 0;
    const owner = async (kind, at, extra) => {
        calls.push(['owner', kind, extra]);
        if (failOwner !== null && kind === 'crst-read' && ++reads === failOwner) throw new Error('observer refused');
        if (kind === 'crst-finish' && failFinish) throw new Error('finish refused');
        return { screen };
    };
    return { calls, fence, owner };
}

test('a complete collection replays every plan step serially, journals each reply with its read and runs the pinned probe over the pinned replies', async () => {
    const w = world();
    const run = await collectColorResetGrading({ config: { runId }, fence: w.fence, owner: w.owner, gradingModule });
    assert.deepEqual(run.cases.map(item => item.caseId), colorResetContracts.map(item => item.caseId));
    for (const [index, item] of run.cases.entries()) {
        const contract = colorResetContracts[index];
        assert.deepEqual(item.feeds, colorResetFeeds(contract));
        assert.equal(item.barriers.length, item.feeds.length);
        assert.equal(item.reads.length, colorResetReads(contract));
        assert.ok(item.reads.every(read => read.kind === 'state' && read.cursor[0] === 8 && read.queries.length > 0));
        assert.equal(item.pinned.observation.outcome, 'supported');
        assert.equal(item.pinned.probeId, contract.caseId);
        assert.equal(pinnedReplies(contract, item.reads).length, 3);
        assert.ok(pinnedReplies(contract, item.reads).every(reply => reply.raw.startsWith('\x1b]' + ['4;0;', '10;', '11;', '12;'][index] + 'rgb:')));
    }
    const kinds = w.calls.filter(call => call[0] === 'owner').map(call => call[1]);
    assert.deepEqual([kinds[0], kinds[1], kinds.at(-1)], ['crst-start', 'crst-bind', 'crst-finish']);
    assert.equal(kinds.filter(kind => kind === 'crst-read').length, colorResetReadPlan.length);
    assert.equal(w.calls.find(call => call[0] === 'write')[1], colorResetBind + colorResetNoncePrefix + runId);
    assert.equal(w.calls.filter(call => call[0] === 'write').at(-1)[1], colorResetFinish);
    assert.ok(colorResetFinish.startsWith('\x1b]104') && colorResetFinish.endsWith('\x1b[?1049l\x1b[!p'), 'runtime colors are cleared explicitly before the alternate screen is left');
    // Every read carries the replies asked since the previous read, in order, with the exact experiment and stage.
    const reads = w.calls.filter(call => call[0] === 'owner' && call[1] === 'crst-read').map(call => call[2]);
    assert.deepEqual(reads.map(read => [read.caseId, read.experimentId, read.stageId]), colorResetReadPlan.map(item => [item.caseId, item.experimentId, item.stageId]));
    assert.ok(reads.every((read, index) => JSON.stringify(read.queries.map(query => query.spec)) === JSON.stringify(colorResetReadPlan[index].queries)));
});

test('the exact pinned bytes reach the terminal in the pinned order, ST controls elsewhere', async () => {
    const w = world();
    await collectColorResetGrading({ config: { runId }, fence: w.fence, owner: w.owner, gradingModule });
    const sent = w.calls.filter(call => call[0] === 'write' || call[0] === 'exchange').map(call => call[1]);
    for (const contract of colorResetContracts) {
        const at = sent.findIndex((bytes, index) => bytes === contract.pinnedFeeds[0] && sent[index + 1] === contract.pinnedFeeds[1]);
        assert.deepEqual(sent.slice(at, at + 5), [...contract.pinnedFeeds], contract.caseId);
    }
    assert.ok(sent.includes('\x1b]10;?\x1b\\'), 'the controls query with the ST spelling');
});

test('a missing reply is retained as an explicit absence and the pinned probe is inconclusive after its first capture rather than the run failing', async () => {
    const w = world({ dropReplies: true });
    const run = await collectColorResetGrading({ config: { runId }, fence: w.fence, owner: w.owner, gradingModule });
    for (const item of run.cases) {
        assert.equal(item.pinned.observation.outcome, 'inconclusive');
        assert.equal(item.pinned.observation.reason, 'no-response');
        assert.ok(item.reads.every(read => read.queries.every(query => query.reply === null)));
    }
});

test('a pinned probe that stops early for any other reason is an incomplete fixture', async () => {
    const w = world();
    const stopping = { ...gradingModule, ntildeRunColorResetTermless: (probeId, ctx) => { ctx.feedCapture(colorResetContracts[0].pinnedFeeds[0]); return { probeId, pass: false, response: '', observation: { outcome: 'supported', evidence: 'behavior' } }; } };
    await assert.rejects(collectColorResetGrading({ config: { runId }, fence: w.fence, owner: w.owner, gradingModule: stopping }), /did not replay its complete fixture/);
});

test('after a fatal fence error only a bounded best-effort cleanup is written; it neither finishes the observation nor hides the failure', async () => {
    const w = world({ failAt: 5 });
    await assert.rejects(collectColorResetGrading({ config: { runId }, fence: w.fence, owner: w.owner, gradingModule }), /Cell barrier timed out/);
    const owners = w.calls.filter(call => call[0] === 'owner').map(call => call[1]);
    assert.ok(!owners.includes('crst-finish'), 'a failed fence cannot qualify a finish snapshot');
    assert.deepEqual(w.calls.at(-1), ['cleanup', colorResetFinish]);
});

test('an observer failure with a healthy fence still restores through a verified finish, and the primary error survives', async () => {
    const w = world({ failOwner: 3 });
    await assert.rejects(collectColorResetGrading({ config: { runId }, fence: w.fence, owner: w.owner, gradingModule }), /observer refused/);
    assert.equal(w.calls.at(-1)[1], 'crst-finish');
});

test('a collection failure and a restoration failure are both reported', async () => {
    const w = world({ failOwner: 3, failFinish: true });
    await assert.rejects(collectColorResetGrading({ config: { runId }, fence: w.fence, owner: w.owner, gradingModule }), error => {
        assert.ok(error instanceof AggregateError);
        assert.deepEqual(error.errors.map(item => item.message), ['observer refused', 'finish refused']);
        return true;
    });
    const only = world({ failFinish: true });
    await assert.rejects(collectColorResetGrading({ config: { runId }, fence: only.fence, owner: only.owner, gradingModule }), /finish refused/);
});

test('the recording context is built from the contract and replays exactly the pinned feeds', () => {
    const contract = colorResetContracts[0], context = colorResetRecordingContext(contract, [{ raw: 'a' }, { raw: 'b' }, { raw: 'c' }]);
    assert.equal(context.feedCapture(contract.pinnedFeeds[0]), 'a');
    context.feed(contract.pinnedFeeds[1]);
    assert.equal(context.feedCapture(contract.pinnedFeeds[2]), 'b');
    context.feed(contract.pinnedFeeds[3]);
    assert.equal(context.feedCapture(contract.pinnedFeeds[4]), 'c');
    assert.equal(context.fedCount, 5);
    assert.throws(() => context.feedCapture(contract.pinnedFeeds[0]), /feed 5 differs/);
});
