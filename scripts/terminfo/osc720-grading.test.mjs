import assert from 'node:assert/strict';
import test from 'node:test';
import { buildOsc720Report, classifyOsc720, osc720Counts, replayOsc720TermlessStatic, screenProjectionSha256 } from './osc720-grading.mjs';
import { osc720CaseId, osc720Experiments, osc720Pinned } from './osc720-contracts.mjs';
import { syntheticHistory, syntheticOsc720Evidence } from './fixtures/osc720-evidence.mjs';

const build = evidence => buildOsc720Report(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
const named = (report, name) => report.cases[0].experiments.find(item => item.name === name);

test('an App that ignores OSC 720 is graded unsupported with the controls held and the pinned paths preserved', () => {
    const report = build(syntheticOsc720Evidence());
    const [item] = report.cases, effect = item.effectContract;
    assert.equal(effect.outcome, 'unsupported');
    assert.equal(effect.controlsHeld, true);
    assert.deepEqual(effect.protocolExperiments.map(entry => entry.observed), Array(7).fill('none'));
    assert.deepEqual(effect.controlExperiments.map(entry => [entry.name, entry.observed]), [['unmarked', 'none'], ['ed3-control', 'erase-saved'], ['wheel-control', 'view-up']]);
    assert.equal(item.pinned.terminal.observation.outcome, 'inconclusive');
    assert.equal(item.pinned.terminal.observation.evidence, 'consumed');
    assert.equal(item.pinned.termless.observation.outcome, 'unsupported');
    assert.equal(item.pinned.qualifiedOutcome, 'inconclusive');
    assert.deepEqual(item.divergences, [{ path: 'terminal', outcome: 'inconclusive' }, { path: 'termless-replay', outcome: 'unsupported' },
        { path: 'effect', pinnedOutcome: 'inconclusive', effectOutcome: 'unsupported' }]);
    assert.deepEqual(osc720Counts(report), { 'inconclusive/unsupported': 1 });
});

test('an App that follows the rxvt text is supported, and the pinned termless criterion cannot tell a history erase from a view move', () => {
    const report = build(syntheticOsc720Evidence({ behavior: 'rxvt' }));
    const [item] = report.cases;
    assert.equal(item.effectContract.outcome, 'supported');
    assert.equal(named(report, 'pinned-bare').observed.kind, 'none', 'urxvt does not dispatch the bare form');
    assert.equal(named(report, 'count-zero').observed.kind, 'erase-saved');
    assert.equal(named(report, 'count-empty').observed.kind, 'erase-saved');
    assert.equal(named(report, 'count-3').observed.kind, 'view-up');
    assert.equal(named(report, 'count-clamped').observations[1].scrollOffset, syntheticHistory);
    assert.equal(named(report, 'count-2-from-scrolled').observations[1].scrollOffset, named(report, 'count-2-from-scrolled').observations[0].scrollOffset + 2);
    // The pinned bare form changes nothing in the reference, so the pinned criterion fails on its own protocol owner.
    assert.equal(item.pinned.termless.observation.outcome, 'unsupported');
    // Over an erase (`;0`), viewportOffset falls to 0 and the same unchanged criterion would pass although no viewport moved.
    const [before, after] = named(report, 'count-zero').observations;
    const facts = record => ({ viewport: { scrollOffset: record.scrollOffset, maxScrollOffset: record.maxScrollOffset }, scrollbackLines: record.scrollbackLines });
    assert.equal(replayOsc720TermlessStatic(before.rows, facts(before), facts(after)).observation.outcome, 'supported');
});

test('a partial implementation is inconclusive, never a guess', () => {
    const evidence = syntheticOsc720Evidence({ behavior: 'rxvt', adjust: (contract, label, state) => {
        if (contract.name === 'count-3' && label === 'after') state.offset = 0;
        return state;
    } });
    const effect = build(evidence).cases[0].effectContract;
    assert.equal(effect.outcome, 'inconclusive');
    assert.equal(effect.reason, 'mixed-or-other-effect');
});

test('a failed observer control is inconclusive and a changed setup is refused as a control failure', () => {
    const ed3 = syntheticOsc720Evidence({ adjust: (contract, label, state) => { if (contract.name === 'ed3-control' && label === 'after') { state.scrollback = syntheticHistory; state.offset = 0; } return state; } });
    assert.equal(build(ed3).cases[0].effectContract.reason, 'validity-control-failed');
    const wheel = syntheticOsc720Evidence({ adjust: (contract, label, state) => { if (contract.name === 'wheel-control' && label === 'after') state.offset = 0; return state; } });
    assert.equal(build(wheel).cases[0].effectContract.outcome, 'inconclusive');
    const history = syntheticOsc720Evidence({ adjust: (contract, label, state) => { if (contract.name === 'count-3' && label === 'before') { state.scrollback = 12; state.offset = 0; } return state; } });
    const effect = build(history).cases[0].effectContract;
    assert.equal(effect.outcome, 'inconclusive');
    assert.equal(effect.controlsHeld, false);
});

test('an unrelated screen or history change is classified other', () => {
    const before = { screenSha256: 'a', historySha256: 'h', cursor: [1, 1], scrollbackLines: 10, historyGeneration: 3, rowsEvicted: 0, scrollOffset: 0 };
    assert.equal(classifyOsc720(before, { ...before, screenSha256: 'b' }).kind, 'other');
    assert.equal(classifyOsc720(before, { ...before, scrollbackLines: 11 }).kind, 'other');
    assert.equal(classifyOsc720(before, { ...before, historySha256: 'x' }).kind, 'other');
    assert.equal(classifyOsc720(before, { ...before, cursor: [2, 1], scrollOffset: 3 }).kind, 'other');
    assert.equal(classifyOsc720(before, { ...before, scrollOffset: 3 }).kind, 'view-up');
    assert.equal(classifyOsc720(before, { ...before, scrollbackLines: 0, historyGeneration: 4 }).kind, 'erase-saved');
});

test('the pinned termless replay follows the pinned guard and criterion', () => {
    const facts = (offset, scrollback = 100) => ({ viewport: { scrollOffset: offset, maxScrollOffset: scrollback }, scrollbackLines: scrollback });
    assert.equal(replayOsc720TermlessStatic(24, facts(0, 0), facts(0, 0)).observation.outcome, 'inconclusive');
    assert.equal(replayOsc720TermlessStatic(24, facts(100, 100), facts(100, 100)).observation.outcome, 'inconclusive');
    assert.equal(replayOsc720TermlessStatic(24, facts(0), facts(5)).observation.outcome, 'supported');
    assert.equal(replayOsc720TermlessStatic(24, facts(0), facts(0)).observation.outcome, 'unsupported');
    assert.equal(osc720Pinned.termless.feeds.length, 31);
});

test('a view move that also changes any other stored screen state is not credited as a clean effect', () => {
    const corrupted = syntheticOsc720Evidence({ behavior: 'rxvt', adjust: (contract, label, state) => { if (contract.name === 'count-3' && label === 'after') state.cursorStyle = 1; return state; } });
    const report = build(corrupted);
    assert.equal(named(report, 'count-3').observed.kind, 'other');
    assert.notEqual(report.cases[0].effectContract.outcome, 'supported');
    const { screen } = build(syntheticOsc720Evidence()).snapshots[2].before;
    const sha = screenProjectionSha256(screen);
    for (const change of [s => { s.cursorMode.blink = !s.cursorMode.blink; }, s => { s.bufferState.originMode = !s.bufferState.originMode; }, s => { s.lines[0] = 'x'; }, s => { s.cursorRow += 1; }]) {
        const copy = structuredClone(screen); change(copy);
        assert.notEqual(screenProjectionSha256(copy), sha);
    }
    const history = structuredClone(screen); history.history = { startLine: 0, totalLines: 0, lines: [] }; history.bufferState.scrollbackLines = 0;
    assert.equal(screenProjectionSha256(history), sha, 'history and its count are measured separately');
});

test('every refusal boundary rejects tampered or incomplete evidence', () => {
    const mutate = change => { const evidence = syntheticOsc720Evidence(); change(evidence); return () => build(evidence); };
    assert.throws(mutate(e => { e.frames.pop(); }), /Incomplete OSC 720 journal/);
    assert.throws(mutate(e => { e.run.experiments[0].feeds[2] = 'scroll-0\r\n'; }), /feeds do not match/);
    assert.throws(mutate(e => { e.run.pinned.terminal.note = 'x'; }), /differs from its replay/);
    assert.throws(mutate(e => { e.run.experiments[1].reads[0].facts.scrollbackLines += 1; }), /read identity changed/);
    assert.throws(mutate(e => { e.run.experiments.at(-1).actions[0].response.notches = []; }), /action record changed/);
    assert.throws(mutate(e => { e.run.experiments.at(-1).actions = []; }), /action record changed/);
    assert.throws(mutate(e => { e.run.experiments.at(-1).actions[0].response.notches[0].request.paneId = '00000000-0000-4000-8000-000000000001'; }), /action record changed/, 'a wheel request bound to another pane');
    assert.throws(mutate(e => { e.adapter.derivedFromAdapterId = 'x'; }), /Invalid OSC 720 run identity/);
    assert.throws(mutate(e => { e.frames[1].before.metadata.viewport = { scrollOffset: 0, maxScrollOffset: 1 }; }), /./);
    assert.throws(mutate(e => { e.frames.at(-1).before.metadata.buffer.scrollbackLines = 3; }), /./);
    assert.equal(osc720Experiments(syntheticOsc720Evidence().runId).length, 10);
    assert.equal(named(build(syntheticOsc720Evidence()), 'count-3').caseId, `${osc720CaseId}/count-3`);
});
