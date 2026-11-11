import assert from 'node:assert/strict';
import test from 'node:test';
import { collectOsc78 } from './osc78-collector.mjs';
import { syntheticOsc78Evidence } from './fixtures/osc78-evidence.mjs';
import { makeOsc78Experiments, osc78CaseIds, osc78Fixture, osc78Pinned, osc78Prefix, osc7CaseId, replayOsc7Pinned, replayOsc8Terminal, replayOsc8Termless } from './osc78-contracts.mjs';
import { osc78AdapterId } from './osc78-grading-collector.mjs';
import { buildOsc78GradingReport } from './osc78-grading.mjs';

const runId = 'a'.repeat(32), barrier = '\x1b[?1;2c';
/** The shape of the derived module: a stand-in that performs exactly the pinned calls (the real bundle is replayed in the layer test). */
const fakeGradingModule = {
    ntildeOsc78Adapter: Object.freeze({ id: osc78AdapterId, probeIds: Object.freeze([...osc78CaseIds]) }),
    ntildeRunOsc78Termless(probeId, ctx) {
        if (probeId === osc7CaseId) return { probeId, ...replayOsc7Pinned().termless };
        ctx.feed(osc78Pinned[probeId].termlessFeeds[0]);
        const cells = Array.from({ length: 6 }, (_, col) => ({ char: ctx.getCell(0, col).char, uri: ctx.getHyperlinkAt(0, col) }));
        return { probeId, ...replayOsc8Termless(cells) };
    },
    async ntildeRunOsc78Terminal(probeId, ctx) {
        if (probeId === osc7CaseId) return { probeId, ...replayOsc7Pinned().terminal };
        for (const bytes of osc78Pinned[probeId].terminalWrites) ctx.write(bytes);
        const position = await ctx.queryCursorPosition();
        return { probeId, ...replayOsc8Terminal({ rows: ctx.rows, cols: ctx.cols }, position) };
    },
};

/** A fence and owner pair that replay the synthetic App: every write is ordered, every read serves the synthetic frame and its hyperlink runs, as the parent does. */
function harness({ failAt = null, startError = null, links = frame => structuredClone(frame.before.metadata.buffer.links) } = {}) {
    const { frames } = syntheticOsc78Evidence(), writes = [], requests = [];
    let next = 0;
    const fence = { write: async bytes => { writes.push(bytes); return barrier; } };
    const owner = async (kind, at = null) => {
        requests.push({ kind, barrier: at });
        if (kind === `${osc78Prefix}-start` && startError) throw startError;
        if (failAt === requests.length) throw new Error('lost observation');
        const frame = frames[next++];
        assert.equal(frame.kind, kind, 'the journal advances in lockstep with the driver');
        return { screen: frame.before.screen, paneId: frame.paneId, ...(kind === `${osc78Prefix}-read` ? { links: links(frame) } : {}) };
    };
    return { fence, owner, writes, requests, frames };
}

test('the driver executes every experiment\'s exact plan serially through the fence, in order, and journals one read per plan read', async () => {
    const h = harness();
    const result = await collectOsc78({ config: { runId }, fence: h.fence, owner: h.owner, gradingModule: fakeGradingModule });
    assert.equal(result.status, 'complete');
    const experiments = makeOsc78Experiments(runId), fixture = osc78Fixture(runId);
    const expectedWrites = [fixture.bind, ...experiments.flatMap(item => item.plan.filter(step => step.feed !== undefined).map(step => step.feed)), fixture.finish];
    assert.deepEqual(h.writes, expectedWrites);
    assert.equal(h.requests.filter(request => request.kind === `${osc78Prefix}-read`).length, 23);
    assert.deepEqual(h.requests.map(request => request.kind).filter(kind => kind !== `${osc78Prefix}-read`), [`${osc78Prefix}-start`, `${osc78Prefix}-bind`, `${osc78Prefix}-finish`]);
    assert.equal(result.run.experiments.length, 14);
    assert.deepEqual(result.run.probeIds, [...osc78CaseIds]);
    assert.deepEqual(result.run.pinned.map(item => item.caseId), [...osc78CaseIds]);
    assert.deepEqual(result.run.pinned[0].termless.feeds, [], 'the pinned OSC 7 probe feeds nothing');
    assert.deepEqual(result.run.pinned[1].terminal.writes, osc78Pinned['extensions.osc8'].terminalWrites);
});

test('the run record re-derives into a report with both pinned verdicts and effect verdicts', async () => {
    const h = harness();
    const { run } = await collectOsc78({ config: { runId }, fence: h.fence, owner: h.owner, gradingModule: fakeGradingModule });
    const adapter = { id: osc78AdapterId, derivedFromAdapterId: 'ntilde-owned-left-right-margins-grading-v1', bundleSha256: 'f'.repeat(64), upstreamBundleSha256: 'a'.repeat(64) };
    const report = buildOsc78GradingReport(h.frames, run, runId, adapter);
    assert.deepEqual(report.cases.map(item => [item.conclusion.outcome, item.effectContract.outcome]), [['inconclusive', 'supported'], ['supported', 'supported']]);
});

test('the pinned OSC 8 termless probe reads the hyperlink runs the parent attached to the stock read, not any other read', async () => {
    const h = harness();
    const { run } = await collectOsc78({ config: { runId }, fence: h.fence, owner: h.owner, gradingModule: fakeGradingModule });
    assert.equal(run.pinned[1].termless.result.observation.outcome, 'supported');
    const unlinked = harness({ links: () => [] });
    const result = await collectOsc78({ config: { runId }, fence: unlinked.fence, owner: unlinked.owner, gradingModule: fakeGradingModule });
    assert.equal(result.run.pinned[1].termless.result.observation.outcome, 'inconclusive', 'without link metadata the linked text is not observable');
    const missing = harness({ links: () => undefined });
    await assert.rejects(collectOsc78({ config: { runId }, fence: missing.fence, owner: missing.owner, gradingModule: fakeGradingModule }), /Missing extensions\.osc7-cwd stock-context hyperlink runs/);
});

test('a typed refusal of the first observation is the only optional outcome; any other failure is fatal and still attempts restoration', async () => {
    for (const code of ['paneMetadataUnavailable', 'screenDetailsUnavailable', 'metadataGeometryUnavailable']) {
        const h = harness({ startError: Object.assign(new Error('refused'), { code }) });
        assert.deepEqual(await collectOsc78({ config: { runId }, fence: h.fence, owner: h.owner, gradingModule: fakeGradingModule }), { status: 'unavailable', reason: code });
    }
    const untyped = harness({ startError: new Error('boom') });
    await assert.rejects(collectOsc78({ config: { runId }, fence: untyped.fence, owner: untyped.owner, gradingModule: fakeGradingModule }), /boom/);
    const lost = harness({ failAt: 9 });
    await assert.rejects(collectOsc78({ config: { runId }, fence: lost.fence, owner: lost.owner, gradingModule: fakeGradingModule }));
    assert.equal(lost.writes.at(-1), osc78Fixture(runId).finish, 'restoration is attempted after a failed observation');
});

test('an adapter that does not name the contracted probes, or a pinned probe that feeds anything else, is refused', async () => {
    const h = harness();
    await assert.rejects(collectOsc78({ config: { runId }, fence: h.fence, owner: h.owner,
        gradingModule: { ...fakeGradingModule, ntildeOsc78Adapter: { id: osc78AdapterId, probeIds: ['extensions.osc8'] } } }), /does not name the contracted probes/);
    const other = harness();
    const wrong = { ...fakeGradingModule, ntildeRunOsc78Termless(probeId, ctx) { if (probeId === osc7CaseId) return { probeId }; ctx.feed('\x1b]8;;https://example.com/other\x07'); return { probeId }; } };
    await assert.rejects(collectOsc78({ config: { runId }, fence: other.fence, owner: other.owner, gradingModule: wrong }), /feed differs from the contract/);
    const reading = harness();
    const early = { ...fakeGradingModule, ntildeRunOsc78Termless(probeId, ctx) { if (probeId === osc7CaseId) return { probeId, ...replayOsc7Pinned().termless }; ctx.getCell(0, 0); return { probeId }; } };
    await assert.rejects(collectOsc78({ config: { runId }, fence: reading.fence, owner: reading.owner, gradingModule: early }), /read differs from the contract/);
});

test('the parent alone retains the typed unavailable marker: the worker must not create it a second time (the parent writes it before answering)', async () => {
    const { readFileSync } = await import('node:fs');
    const worker = readFileSync(new URL('./worker.mjs', import.meta.url), 'utf8');
    assert.ok(!worker.includes('lnk-unavailable.json'), 'a second exclusive create of the marker would abort the run with EEXIST');
    assert.ok(worker.includes("osc78Result.status === 'complete'") && worker.includes('lnk-run.json'));
});
