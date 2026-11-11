import assert from 'node:assert/strict';
import test from 'node:test';
import { collectTitleStack, exposeTitleStackFacts } from './title-stack-collector.mjs';
import { syntheticTitleStackEvidence } from './fixtures/title-stack-evidence.mjs';
import { countTitleStackFeeds, countTitleStackReads, makeTitleStackExperiments, titleStackCaseIds, titleStackFixture, titleStackPinned, titleStackPrefix, titleStackReadCount,
    replayTitleStackTerminal, replayTitleStackTermless } from './title-stack-contracts.mjs';
import { titleStackAdapterId } from './title-stack-grading-collector.mjs';
import { buildTitleStackGradingReport } from './title-stack-grading.mjs';

const runId = 'a'.repeat(32), barrier = '\x1b[?1;2c';
/** The shape of the derived module: the pinned code as a stand-in that performs exactly the pinned calls (the real bundle is replayed in the layer test). */
const fakeGradingModule = {
    ntildeTitleStackAdapter: Object.freeze({ id: titleStackAdapterId, probeIds: Object.freeze([...titleStackCaseIds]) }),
    ntildeRunTitleStackTermless(probeId, ctx) {
        const [first, second, third, fourth] = titleStackPinned[probeId].termlessFeeds;
        ctx.feed(first); const original = ctx.getTitle(); ctx.feed(second); ctx.feed(third); const changed = ctx.getTitle(); ctx.feed(fourth); const restored = ctx.getTitle();
        return { probeId, ...replayTitleStackTermless(probeId, { 'pinned-original': original, 'pinned-changed': changed, 'pinned-restored': restored }) };
    },
    async ntildeRunTitleStackTerminal(probeId, ctx) {
        for (const write of titleStackPinned[probeId].terminalWrites) ctx.write(write);
        const pos = await ctx.queryCursorPosition();
        return { probeId, ...replayTitleStackTerminal(probeId, pos !== null) };
    },
};

/** A fence and owner pair that replay the synthetic App: every write is ordered, every read serves the synthetic frame and its exposed facts. */
function harness({ failAt = null, startError = null } = {}) {
    const { frames } = syntheticTitleStackEvidence(), writes = [], requests = [];
    let next = 0;
    const fence = { write: async bytes => { writes.push(bytes); return barrier; } };
    const owner = async (kind, at = null) => {
        requests.push({ kind, barrier: at });
        if (kind === `${titleStackPrefix}-start` && startError) throw startError;
        if (failAt === requests.length) throw new Error('lost observation');
        const frame = frames[next++];
        assert.equal(frame.kind, kind, 'the journal advances in lockstep with the driver');
        return { screen: frame.before.screen, paneId: frame.paneId, facts: exposeTitleStackFacts(frame.before) };
    };
    return { fence, owner, writes, requests, frames };
}

test('the plan is exactly 15 experiments, 58 reads and bounded by the 64-capture ceiling with three captures spare for settling retries, with unique run-bound strings per experiment', () => {
    assert.equal(makeTitleStackExperiments(runId).length, 15);
    assert.equal(countTitleStackReads(runId), 58);
    assert.equal(titleStackReadCount, 58);
    assert.ok(countTitleStackReads(runId) + 3 + 3 <= 64, 'headroom for settling retries');
    assert.ok(countTitleStackFeeds(runId) > 58);
    const tags = makeTitleStackExperiments(runId).map(item => item.tag);
    assert.equal(new Set(tags).size, tags.length, 'every experiment owns distinct synthetic strings');
    const other = makeTitleStackExperiments('b'.repeat(32));
    assert.notDeepEqual(makeTitleStackExperiments(runId).map(item => item.plan[1]), other.map(item => item.plan[1]), 'the strings are bound to the run');
});

test('the driver executes every experiment\'s exact plan serially through the fence, in order, and journals one read per plan read', async () => {
    const h = harness();
    const result = await collectTitleStack({ config: { runId }, fence: h.fence, owner: h.owner, gradingModule: fakeGradingModule });
    assert.equal(result.status, 'complete');
    const experiments = makeTitleStackExperiments(runId), fixture = titleStackFixture(runId);
    const expectedWrites = [fixture.bind, ...experiments.flatMap(item => item.plan.filter(step => step.feed !== undefined).map(step => step.feed)), fixture.finish];
    assert.deepEqual(h.writes, expectedWrites);
    assert.equal(h.requests.filter(request => request.kind === `${titleStackPrefix}-read`).length, 58);
    assert.deepEqual(h.requests.map(request => request.kind).filter(kind => kind !== `${titleStackPrefix}-read`), [`${titleStackPrefix}-start`, `${titleStackPrefix}-bind`, `${titleStackPrefix}-finish`]);
    assert.equal(result.run.experiments.length, 15);
    assert.deepEqual(result.run.probeIds, [...titleStackCaseIds]);
    assert.deepEqual(result.run.pinned.map(item => item.caseId), [...titleStackCaseIds]);
    assert.ok(result.run.experiments.every(item => item.reads.every(read => typeof read.facts.title === 'string' && typeof read.facts.icon === 'string')), 'every read keeps the raw title and icon of its own observation');
});

test('the run record re-derives into a report with both pinned paths preserved', async () => {
    const h = harness();
    const { run } = await collectTitleStack({ config: { runId }, fence: h.fence, owner: h.owner, gradingModule: fakeGradingModule });
    const adapter = { id: titleStackAdapterId, derivedFromAdapterId: 'ntilde-owned-left-right-margins-grading-v1', bundleSha256: 'f'.repeat(64), upstreamBundleSha256: 'a'.repeat(64) };
    const report = buildTitleStackGradingReport(h.frames, run, runId, adapter);
    assert.deepEqual(report.cases.map(item => [item.conclusion.outcome, item.effectContract.outcome, item.pinned.terminal.observation.outcome]), titleStackCaseIds.map(() => ['supported', 'supported', 'inconclusive']));
});

test('the pinned terminal path of the push case is read after its own three writes and the pop case after its own two-write experiment', async () => {
    const h = harness();
    const { run } = await collectTitleStack({ config: { runId }, fence: h.fence, owner: h.owner, gradingModule: fakeGradingModule });
    const push = run.experiments.find(item => item.caseId === 'device.xtwinops-22/stock'), pop = run.experiments.find(item => item.caseId === 'device.xtwinops-23/terminal-path');
    assert.deepEqual(push.feeds.slice(-4, -1), [...titleStackPinned['device.xtwinops-22'].terminalWrites]);
    assert.deepEqual(pop.feeds.slice(-2), [...titleStackPinned['device.xtwinops-23'].terminalWrites]);
    assert.equal(pop.reads.length, 1);
});

test('a typed refusal of the first observation is the only optional outcome; any other failure is fatal and still attempts restoration', async () => {
    for (const code of ['paneMetadataUnavailable', 'screenDetailsUnavailable', 'metadataGeometryUnavailable']) {
        const h = harness({ startError: Object.assign(new Error('refused'), { code }) });
        assert.deepEqual(await collectTitleStack({ config: { runId }, fence: h.fence, owner: h.owner, gradingModule: fakeGradingModule }), { status: 'unavailable', reason: code });
    }
    const untyped = harness({ startError: new Error('boom') });
    await assert.rejects(collectTitleStack({ config: { runId }, fence: untyped.fence, owner: untyped.owner, gradingModule: fakeGradingModule }), /boom/);
    const lost = harness({ failAt: 9 });
    await assert.rejects(collectTitleStack({ config: { runId }, fence: lost.fence, owner: lost.owner, gradingModule: fakeGradingModule }));
    assert.equal(lost.writes.at(-1), titleStackFixture(runId).finish, 'restoration (the prepared title and icon, a drained stack) is attempted after a failed observation');
});

test('an adapter that does not name the contracted probes, or a pinned probe that feeds anything else, is refused', async () => {
    const h = harness();
    await assert.rejects(collectTitleStack({ config: { runId }, fence: h.fence, owner: h.owner, gradingModule: { ...fakeGradingModule, ntildeTitleStackAdapter: { id: titleStackAdapterId, probeIds: ['device.xtwinops-22'] } } }), /does not name the contracted probes/);
    const other = harness();
    const wrong = { ...fakeGradingModule, ntildeRunTitleStackTermless(probeId, ctx) { ctx.feed('\x1b]2;other\x07'); return { probeId }; } };
    await assert.rejects(collectTitleStack({ config: { runId }, fence: other.fence, owner: other.owner, gradingModule: wrong }), /feed differs from the contract/);
    const early = harness();
    const readsEarly = { ...fakeGradingModule, ntildeRunTitleStackTermless(probeId, ctx) { ctx.getTitle(); return { probeId }; } };
    await assert.rejects(collectTitleStack({ config: { runId }, fence: early.fence, owner: early.owner, gradingModule: readsEarly }), /outside its contracted points/);
});

test('an observer that exposes no facts cannot feed the pinned replay', async () => {
    const h = harness();
    const owner = async (kind, at) => { const { facts, ...rest } = await h.owner(kind, at); return rest; };
    await assert.rejects(collectTitleStack({ config: { runId }, fence: h.fence, owner, gradingModule: fakeGradingModule }));
});

test('the parent alone retains the typed unavailable marker: the worker must not create it a second time (the parent writes it before answering)', async () => {
    const { readFileSync } = await import('node:fs');
    const worker = readFileSync(new URL('./worker.mjs', import.meta.url), 'utf8');
    assert.ok(!worker.includes('tst-unavailable.json'), 'a second exclusive create of the marker would abort the run with EEXIST');
    assert.ok(worker.includes("titleStackResult.status === 'complete'") && worker.includes('tst-run.json'));
});
