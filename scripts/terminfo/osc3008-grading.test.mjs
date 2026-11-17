import assert from 'node:assert/strict';
import test from 'node:test';
import { syntheticOsc3008Evidence, withContexts } from './fixtures/osc3008-evidence.mjs';
import { osc3008CaseId } from './osc3008-contracts.mjs';
import { buildOsc3008GradingReport, matchesOsc3008Baseline, osc3008Counts, verifyOsc3008PinnedCode } from './osc3008-grading.mjs';

const build = (evidence = syntheticOsc3008Evidence()) => buildOsc3008GradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
const outcome = report => report.cases[0].effectContract.outcome;

test('a model-consistent run is supported while the pinned verdict stays inconclusive by design', () => {
    const report = build(), item = report.cases[0];
    assert.equal(item.caseId, osc3008CaseId);
    assert.equal(outcome(report), 'supported');
    assert.deepEqual(item.conclusion, { outcome: 'inconclusive', grading: 'pinned-terminal-unmeasured-preserved-by-design' });
    assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'supported', kind: 'pinned-inconclusive-by-design' });
    assert.deepEqual(osc3008Counts(report), { 'inconclusive/supported': 1 });
    assert.ok(item.effectContract.required.every(entry => entry.held));
    assert.ok(item.effectContract.policy.length > 0);
});

test('an App that ignores a valid start is unsupported, and a stale or extra context is too', () => {
    for (const mutate of [
        ({ contexts }, label) => { if (label === 'opened') contexts.stack = []; },
        ({ contexts }, label) => { if (label === 'nested') contexts.stack.pop(); },
        ({ contexts }, label) => { if (label === 'ignored') contexts.stack.push({ id: 'foreign' }); },
        ({ contexts }, label) => { if (label === 'fields' && contexts.stack[0]) contexts.stack[0].cwd = '/wrong'; },
        ({ contexts }, label) => { if (label === 'after-soft-reset') contexts.stack = []; },
        ({ contexts }, label) => { if (label === 'crash') contexts.ended.at(-1).status = '1'; },
    ]) {
        const report = build(syntheticOsc3008Evidence({ adjust: (id, label, value) => mutate(value, label) }));
        assert.equal(outcome(report), 'unsupported');
        assert.equal(report.cases[0].divergence.contractOutcome, 'unsupported');
    }
});

test('policy differences are recorded but never decide', () => {
    const report = build(syntheticOsc3008Evidence({ adjust: (id, label, { contexts }) => { if (label === 'full') contexts.stack.pop(); } }));
    assert.equal(outcome(report), 'supported');
    assert.ok(report.cases[0].effectContract.policy.some(entry => entry.id === 'depth/full' && entry.match === false));
});

test('a changed screen, shell state or unreadable contexts make the effect inconclusive, never unsupported', () => {
    for (const mutate of [
        ({ state }, label) => { if (label === 'opened') state.rows[3] = 'leaked sequence text'; },
        ({ state }, label) => { if (label === 'input-window') state.accepting = false; },
    ]) {
        assert.equal(outcome(build(syntheticOsc3008Evidence({ adjust: (id, label, value) => mutate(value, label) }))), 'inconclusive');
    }
    const evidence = syntheticOsc3008Evidence();
    const index = evidence.frames.findIndex(frame => frame.kind === 'ocx-read');
    delete evidence.frames[index].before.metadata.buffer.contexts;
    evidence.frames[index] = { ...evidence.frames[index], sha256: evidence.frames[index].sha256 };
    assert.throws(() => build(evidence));
});

test('the journal, run log, pinned replay, baseline and restoration are all checked', () => {
    const mutations = [
        e => { e.run.experiments[1].feeds[1] += 'x'; }, e => { e.run.experiments.pop(); }, e => { e.run.pinned[0].cursor = [9, 9]; },
        e => { e.run.pinned[0].terminal.pass = true; }, e => { e.adapter.derivedFromAdapterId = 'other'; }, e => { e.frames.pop(); },
        e => { e.frames[1] = withContexts(e.frames[1], { stack: [{ id: 'stale' }], ended: [] }); },
        e => { e.frames.push(e.frames.at(-1)); }, e => { e.run.experiments[2].reads[0].kind = 'other'; },
    ];
    for (const mutate of mutations) {
        const evidence = syntheticOsc3008Evidence();
        mutate(evidence);
        assert.throws(() => build(evidence));
    }
    const evidence = syntheticOsc3008Evidence();
    assert.equal(matchesOsc3008Baseline(evidence.frames[1].before, evidence.frames[0].before, evidence.runId, false), true);
    assert.equal(matchesOsc3008Baseline(withContexts(evidence.frames[1], { stack: [{ id: 'x' }], ended: [] }).before, evidence.frames[0].before, evidence.runId, false), false);
});

test('the pinned terminal path is verified against the retained pinned code', async () => {
    const { run } = syntheticOsc3008Evidence();
    const module = { ntildeOsc3008HasTermless: () => false, ntildeRunOsc3008Terminal: async (probeId, ctx) => {
        ctx.write('\x1b[1;1H\x1b[2K'); ctx.write('\x1b]3008;type=test\x07');
        const position = await ctx.queryCursorPosition();
        return { probeId, pass: false, response: `${position.row};${position.col}`, note: 'Cursor response does not measure OSC 3008 systemd context',
            observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'query' } };
    } };
    await verifyOsc3008PinnedCode(run, module);
    await assert.rejects(verifyOsc3008PinnedCode(run, { ...module, ntildeOsc3008HasTermless: () => true }), /termless presence/);
    await assert.rejects(verifyOsc3008PinnedCode(run, { ...module, ntildeRunOsc3008Terminal: async probeId => ({ probeId, pass: true, response: '1;1', observation: { outcome: 'supported' } }) }));
});
