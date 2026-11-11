import assert from 'node:assert/strict';
import test from 'node:test';
import { snapshotHash } from './agent-observer.mjs';
import { syntheticTitleStackEvidence } from './fixtures/title-stack-evidence.mjs';
import { metadataProjection } from './metadata.mjs';
import { buildTitleStackGradingReport, evaluateRead, matchesTitleStackBaseline, observeFrame, titleStackCounts } from './title-stack-grading.mjs';
import { makeTitleStackExperiments, simulateTitleStack, titleStackCaseIds, titleStackFixture } from './title-stack-contracts.mjs';

const runId = 'a'.repeat(32);
const push = 'device.xtwinops-22', pop = 'device.xtwinops-23';
const build = evidence => buildTitleStackGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
const grade = adjust => build(syntheticTitleStackEvidence({ adjust }));
const caseOf = (report, id) => report.cases.find(item => item.caseId === id);
const outcomes = report => Object.fromEntries(report.cases.map(item => [item.caseId, item.effectContract.outcome]));
/** Re-seal a frame after an edit so the semantic change, not a stale hash, is under test. */
const coherent = (frame, mutate) => { mutate(frame.before); frame.after = structuredClone(frame.before); frame.sha256 = snapshotHash(metadataProjection(frame.before)); frame.afterSha256 = frame.sha256; };
/** The frame of one labelled read: frames follow the plan order after the start and bind frames. */
function readFrame(evidence, id, label) {
    let index = 2;
    for (const item of evidence.run.experiments) for (const read of item.reads) { if (item.caseId === id && read.kind === label) return evidence.frames[index]; index++; }
    throw new Error(`no read ${id} ${label}`);
}
const at = (id, label, change) => (experiment, name, state) => { if (experiment === id && name === label) change(state); };
const with_ = (...changes) => (experiment, name, state) => { for (const change of changes) change(experiment, name, state); };

test('a model App that follows the oracle: both pinned termless verdicts are supported, both effect contracts are supported and the terminal path is inconclusive by design', () => {
    const report = build(syntheticTitleStackEvidence());
    assert.deepEqual(outcomes(report), { [push]: 'supported', [pop]: 'supported' });
    assert.deepEqual(titleStackCounts(report), { 'supported/supported': 2 });
    assert.equal(report.snapshots.length, 61);
    assert.equal(report.restoredSnapshot, 60);
    for (const item of report.cases) {
        assert.equal(item.conclusion.outcome, 'supported');
        assert.equal(item.conclusion.grading, 'pinned-termless-replayed-over-owned-observations');
        assert.equal(item.pinned.termless.observation.evidence, 'parser-state');
        assert.equal(item.pinned.terminal.observation.outcome, 'inconclusive');
        assert.equal(item.pinned.terminal.observation.evidence, 'consumed');
        assert.deepEqual(item.divergences.map(entry => entry.kind), ['pinned-terminal-inconclusive-by-design']);
        assert.match(item.effectContract.attribution, /not the pinned probe/);
        assert.ok(item.effectContract.validity.every(entry => entry.held));
        assert.ok(item.effectContract.required.every(entry => entry.held));
        assert.equal(item.divergence, undefined, 'agreeing termless and effect verdicts record no singular divergence');
    }
    assert.notEqual(caseOf(report, push).effectContract.id, caseOf(report, pop).effectContract.id, 'the two effect contracts are distinct');
    assert.equal(caseOf(report, push).effectContract.limits.outcome, 'supported');
    assert.equal(caseOf(report, pop).effectContract.limits, undefined, 'the depth policy belongs to the push case only');
    for (const item of report.cases) assert.equal(item.effectContract.boundaries.outcome, 'supported');
    assert.match(report.scope, /raw icon-name state is not host icon presentation/);
});

test('the report re-derives identically from its own retained evidence', () => {
    const evidence = syntheticTitleStackEvidence();
    assert.deepEqual(build(evidence), build(structuredClone(evidence)));
});

test('an App that ignores the pop: the saved title never returns, which is unsupported for the effect and inconclusive for the pinned termless path (a recorded divergence)', () => {
    const report = grade(with_(
        at(`${push}/effect`, 'popped', state => Object.assign(state, { title: state.title.replace('t0', 't1'), icon: state.icon.replace('i0', 'i1') })),
        at(`${push}/stock`, 'pinned-restored', state => { state.title = 'new-title'; })));
    assert.equal(caseOf(report, push).effectContract.outcome, 'unsupported');
    assert.deepEqual(caseOf(report, push).effectContract.failed, ['stock', 'effect']);
    assert.equal(caseOf(report, push).conclusion.outcome, 'inconclusive');
    assert.equal(caseOf(report, push).pinned.termless.note, 'Title push/pop did not restore the old title; the failing operation is unknown');
    assert.deepEqual(caseOf(report, push).divergences.map(entry => entry.kind), ['pinned-terminal-inconclusive-by-design', 'pinned-termless-vs-effect']);
    assert.equal(caseOf(report, pop).effectContract.outcome, 'supported', 'the other case keeps its own verdict');
});

test('an App that ignores the push: a pop with nothing saved restores nothing, so the pinned restore read shows the new title', () => {
    const report = grade(at(`${pop}/stock`, 'pinned-restored', state => { state.title = 'new-title'; }));
    assert.equal(caseOf(report, pop).effectContract.outcome, 'unsupported');
    assert.equal(caseOf(report, pop).conclusion.outcome, 'inconclusive');
    assert.equal(caseOf(report, push).effectContract.outcome, 'supported', 'the push case keeps its own fixtures and verdict');
});

test('a push alone that replaces the current values is unsupported', () => {
    const report = grade(at(`${push}/effect`, 'pushed', state => { state.title = 'replaced'; }));
    assert.equal(caseOf(report, push).effectContract.outcome, 'unsupported');
    assert.deepEqual(caseOf(report, push).effectContract.failed, ['effect']);
});

test('wrong selectors: a title-only or icon-only operation that touches the unselected half is unsupported', () => {
    const cases = [
        [push, 'selectors', 'title-restored', state => { state.icon = state.icon.replace('i1', 'i0'); }, 'push title-only restored the icon too'],
        [push, 'selectors', 'icon-restored', state => { state.title = state.title.replace('t3', 't0'); }, 'push icon-only restored the title too'],
        [pop, 'selectors', 'icon-popped', state => { state.title = state.title.replace('t1', 't0'); }, 'icon-only pop restored the title'],
        [pop, 'selectors', 'title-popped', state => { state.icon = state.icon.replace('i3', 'i0'); }, 'title-only pop restored the icon'],
        [pop, 'selectors', 'title-after-consumed', state => { state.title = state.title.replace('t1', 't0'); }, 'a second pop found an entry the first consumed'],
        [pop, 'selectors', 'icon-after-consumed', state => { state.icon = state.icon.replace('i3', 'i1'); }, 'a second pop found an entry the first consumed (icon)'],
    ];
    for (const [owner, name, label, change, why] of cases) {
        const report = grade(at(`${owner}/${name}`, label, change));
        assert.equal(caseOf(report, owner).effectContract.outcome, 'unsupported', why);
        assert.deepEqual(caseOf(report, owner).effectContract.failed, [name], why);
        assert.equal(caseOf(report, owner === push ? pop : push).effectContract.outcome, 'supported', why);
    }
});

test('a mixed-selector sequence that ignores the documented one-sided copy rule is unsupported', () => {
    const report = grade(at(`${pop}/mixed`, 'popped-1', state => { state.title = state.title.replace('t0', 't1'); }));
    assert.equal(caseOf(report, pop).effectContract.outcome, 'unsupported');
    assert.deepEqual(caseOf(report, pop).effectContract.failed, ['mixed']);
});

test('wrong order: first-in-first-out restoration is unsupported for nested and LIFO sequences', () => {
    const fifoNested = grade(at(`${push}/nested`, 'popped-1', state => Object.assign(state, { title: state.title.replace('t1', 't0'), icon: state.icon.replace('i1', 'i0') })));
    assert.equal(caseOf(fifoNested, push).effectContract.outcome, 'unsupported');
    const fifoLifo = grade(at(`${pop}/lifo`, 'popped-1', state => Object.assign(state, { title: state.title.replace('t1', 't0'), icon: state.icon.replace('i1', 'i0') })));
    assert.equal(caseOf(fifoLifo, pop).effectContract.outcome, 'unsupported');
    assert.deepEqual(caseOf(fifoLifo, pop).effectContract.failed, ['lifo']);
});

test('stale values: an old value where a new one was measured is a failed control or a wrong restoration, never support', () => {
    const staleBase = grade(at(`${push}/effect`, 'base', state => { state.title = 'stale-from-another-experiment'; }));
    assert.equal(caseOf(staleBase, push).effectContract.outcome, 'inconclusive', 'a stale prepared baseline invalidates the experiment');
    assert.deepEqual(caseOf(staleBase, push).effectContract.validity.map(entry => entry.held), [false]);
    const unobservedMutation = grade(at(`${pop}/effect`, 'changed', state => { state.title = state.title.replace('t1', 't0'); }));
    assert.equal(caseOf(unobservedMutation, pop).effectContract.outcome, 'inconclusive', 'a mutation that was not observed leaves nothing to restore');
    const staleRestore = grade(at(`${pop}/effect`, 'restored', state => { state.title = state.title.replace('t0', 't1'); }));
    assert.equal(caseOf(staleRestore, pop).effectContract.outcome, 'unsupported');
});

test('a pop on an empty stack that restores anything, and one that is not silent, is unsupported', () => {
    const restoresBaseline = grade(at(`${pop}/empty`, 'empty-pop', state => Object.assign(state, { title: state.title.replace('t1', 't0'), icon: state.icon.replace('i1', 'i0') })));
    assert.equal(caseOf(restoresBaseline, pop).effectContract.outcome, 'unsupported');
    assert.deepEqual(caseOf(restoresBaseline, pop).effectContract.failed, ['empty']);
    const clearsTitle = grade(at(`${pop}/empty`, 'empty-pop', state => { state.title = ''; }));
    assert.equal(caseOf(clearsTitle, pop).effectContract.outcome, 'unsupported');
});

test('incorrect depth policy: a stack deeper or shallower than ten, or an overflow that keeps the oldest entries, fails the separately reported limits only', () => {
    const attempts = {
        'eleven entries (the oldest of twelve survives one more pop)': at(`${push}/overflow`, 'pop-11', state => { state.title = state.title.replace('t2', 't1'); state.icon = state.icon.replace('i2', 'i1'); }),
        'the oldest entries kept and the newest dropped': at(`${push}/overflow`, 'pop-1', state => { state.title = state.title.replace('t11', 't9'); state.icon = state.icon.replace('i11', 'i9'); }),
        'a shallower stack': at(`${push}/overflow`, 'pop-10', state => { state.title = state.title.replace('t2', 't3'); state.icon = state.icon.replace('i2', 'i3'); }),
        'a pop below the retained depth that is not silent': at(`${push}/overflow`, 'pop-11', state => { state.title = ''; }),
    };
    for (const [name, adjust] of Object.entries(attempts)) {
        const report = grade(adjust), item = caseOf(report, push);
        assert.equal(item.effectContract.limits.outcome, 'unsupported', name);
        assert.deepEqual(item.effectContract.limits.failed, ['overflow'], name);
        assert.equal(item.effectContract.outcome, 'supported', `${name}: the depth policy is reported separately from the stack semantics`);
    }
});

test('boundaries: a stack that does not survive an alternate screen or DECSTR, or that survives RIS, fails the separately reported boundaries only', () => {
    const attempts = [
        [push, 'alternate-screen', 'popped-1', state => { state.title = state.title.replace('t0', 't1'); }, 'lost across the alternate screen'],
        [push, 'alternate-screen', 'popped-2', state => { state.title = state.title.replace('t0', 't2'); }, 'a push made inside the alternate screen was lost'],
        [push, 'decstr', 'popped', state => { state.title = state.title.replace('t0', 't1'); state.icon = state.icon.replace('i0', 'i1'); }, 'cleared by DECSTR'],
        [pop, 'ris', 'after-ris', state => { state.title = state.title.replace('t1', 't0'); state.icon = state.icon.replace('i1', 'i0'); }, 'RIS retitled'],
        [pop, 'ris', 'after-pop', state => { state.title = state.title.replace('t1', 't0'); state.icon = state.icon.replace('i1', 'i0'); }, 'a stack that survived RIS popped'],
    ];
    for (const [owner, name, label, change, why] of attempts) {
        const item = caseOf(grade(at(`${owner}/${name}`, label, change)), owner);
        assert.equal(item.effectContract.boundaries.outcome, 'unsupported', why);
        assert.deepEqual(item.effectContract.boundaries.failed, [name], why);
        assert.equal(item.effectContract.outcome, 'supported', `${why}: boundaries are supplemental`);
    }
});

test('the raw title, the raw icon name and the actual tab text are separate observations', () => {
    const evidence = syntheticTitleStackEvidence();
    const frame = structuredClone(readFrame(evidence, `${push}/effect`, 'popped'));
    const observed = observeFrame(frame);
    assert.match(observed.title, /-e2-t0$/); assert.match(observed.icon, /-e2-i0$/); assert.equal(observed.displayTitle, observed.title); assert.ok(observed.tabText.includes(observed.title));
    // The raw title is right but the displayed tab text is stale: a read that fails because of presentation, never because of parser state.
    const staleTab = grade(at(`${push}/effect`, 'popped', () => {}));
    assert.equal(caseOf(staleTab, push).effectContract.outcome, 'supported');
    const tampered = syntheticTitleStackEvidence();
    coherent(readFrame(tampered, `${push}/effect`, 'popped'), value => { value.metadata.presentation.tabText = 'stale caption'; });
    const report = build(tampered), read = caseOf(report, push).experiments.find(entry => entry.name === 'effect').reads.find(entry => entry.label === 'popped');
    assert.equal(read.evaluation.required.title.match, true);
    assert.equal(read.evaluation.required.icon.match, true);
    assert.equal(read.evaluation.required.tabText.match, false);
    assert.equal(caseOf(report, push).effectContract.outcome, 'unsupported');
    const wrongIcon = syntheticTitleStackEvidence();
    coherent(readFrame(wrongIcon, `${push}/effect`, 'popped'), value => { value.metadata.buffer.requestedIconName = 'other-icon'; });
    wrongIcon.run.experiments.find(entry => entry.caseId === `${push}/effect`).reads.find(entry => entry.kind === 'popped').facts.icon = 'other-icon';
    assert.equal(caseOf(build(wrongIcon), push).effectContract.outcome, 'unsupported', 'a wrong icon name is not hidden by a right title');
});

test('unrelated state: a changed cell, cursor, cwd, runtime color, link, input window or history generation invalidates the read', () => {
    const evidence = syntheticTitleStackEvidence(), fixture = titleStackFixture(runId), boundFrame = evidence.frames[1];
    const base = readFrame(evidence, `${push}/effect`, 'popped');
    const feeds = [];
    for (const step of makeTitleStackExperiments(runId).find(item => item.caseId === `${push}/effect`).plan) {
        if (step.feed !== undefined) feeds.push(step.feed); else if (step.read === 'popped') break;
    }
    const model = simulateTitleStack(feeds, fixture);
    const check = mutate => { const frame = structuredClone(base); coherent(frame, mutate); return evaluateRead(frame, model, boundFrame, fixture); };
    assert.equal(check(() => {}).passed, true, 'the untouched frame matches the oracle');
    for (const [name, mutate] of Object.entries({
        'a stray cell': value => { value.screen.lines[5] = 'stray'; },
        'a moved cursor': value => { value.screen.cursorCol = 4; value.metadata.buffer.cursorCol = 4; },
        'a changed cwd': value => { value.metadata.workingDirectory = '/elsewhere'; },
        'a changed runtime color': value => { value.screen.colorBinding = { ...value.screen.colorBinding, paletteOverrideIndices: [] }; },
        'a history generation change': value => { value.metadata.buffer.historyGeneration++; },
        'a link': value => { value.metadata.buffer.links = [{ row: 0, col: 0, length: 1, identity: 0, uri: 'https://example.invalid/', id: null }]; },
        'an open input window': value => { value.metadata.buffer.acceptingCommandInput = true; },
    })) assert.equal(check(mutate).passed, false, name);
    // A stray cell in a restored-title read is an effect failure even though the string came back.
    const stray = syntheticTitleStackEvidence();
    coherent(readFrame(stray, `${push}/effect`, 'popped'), value => { value.screen.lines[5] = 'stray'; });
    assert.equal(caseOf(build(stray), push).effectContract.outcome, 'unsupported');
});

test('RIS is graded with cleared cells at home and any history generation at or above the first; other scopes demand the exact generation', () => {
    const evidence = syntheticTitleStackEvidence(), fixture = titleStackFixture(runId), boundFrame = evidence.frames[1];
    const frame = structuredClone(readFrame(evidence, `${pop}/ris`, 'after-ris'));
    const feeds = [];
    for (const step of makeTitleStackExperiments(runId).find(item => item.caseId === `${pop}/ris`).plan) {
        if (step.feed !== undefined) feeds.push(step.feed); else if (step.read === 'after-ris') break;
    }
    const model = simulateTitleStack(feeds, fixture);
    assert.equal(evaluateRead(frame, model, boundFrame, fixture, 'cleared').passed, true);
    coherent(frame, value => { value.metadata.buffer.historyGeneration += 1; });
    assert.equal(evaluateRead(frame, model, boundFrame, fixture, 'cleared').passed, true);
    assert.equal(evaluateRead(frame, model, boundFrame, fixture, 'strict').passed, false);
    coherent(frame, value => { value.metadata.buffer.historyGeneration = boundFrame.before.metadata.buffer.historyGeneration - 1; });
    assert.equal(evaluateRead(frame, model, boundFrame, fixture, 'cleared').passed, false, 'a generation below the first is never accepted');
    const uncleared = structuredClone(readFrame(evidence, `${pop}/ris`, 'after-ris'));
    coherent(uncleared, value => { value.screen.lines[0] = 'residue'; });
    assert.equal(evaluateRead(uncleared, model, boundFrame, fixture, 'cleared').passed, false);
});

test('cross-case substitution: another case\'s frames, pinned results or experiments cannot stand in for this one', () => {
    const swapped = syntheticTitleStackEvidence();
    const a = swapped.run.experiments.findIndex(entry => entry.caseId === `${push}/stock`), b = swapped.run.experiments.findIndex(entry => entry.caseId === `${pop}/stock`);
    [swapped.run.experiments[a], swapped.run.experiments[b]] = [swapped.run.experiments[b], swapped.run.experiments[a]];
    assert.throws(() => build(swapped), /order differs/);
    const pinned = syntheticTitleStackEvidence();
    [pinned.run.pinned[0], pinned.run.pinned[1]] = [pinned.run.pinned[1], pinned.run.pinned[0]];
    assert.throws(() => build(pinned), Error);
    const frames = syntheticTitleStackEvidence();
    const x = readFrame(frames, `${push}/stock`, 'pinned-restored'), y = readFrame(frames, `${pop}/stock`, 'pinned-restored');
    [frames.frames[x.index], frames.frames[y.index]] = [y, x];
    assert.throws(() => build(frames), Error, 'frames out of plan order');
    const forgedTerminal = syntheticTitleStackEvidence();
    forgedTerminal.run.pinned[1].terminal = structuredClone(forgedTerminal.run.pinned[0].terminal);
    assert.throws(() => build(forgedTerminal), /terminal grading does not match|writes|Missing pinned/);
    const terminalSupported = syntheticTitleStackEvidence();
    terminalSupported.run.pinned[0].terminal.result = { pass: true, note: 'x', observation: { outcome: 'supported', evidence: 'parser-state' } };
    assert.throws(() => build(terminalSupported), /terminal grading does not match/);
});

test('tampered journals, run records and identities are refused', () => {
    const attempts = {
        'missing frame': e => e.frames.pop(),
        'extra frame': e => e.frames.push(structuredClone(e.frames.at(-1))),
        'reordered frames': e => { [e.frames[5], e.frames[6]] = [e.frames[6], e.frames[5]]; },
        'forged hash': e => { e.frames[7].sha256 = '0'.repeat(64); },
        'unpaired host': e => { e.frames[7].after.metadata.oscTitle = 'other'; },
        'unpaired icon': e => { e.frames[7].after.metadata.buffer.requestedIconName = 'other'; },
        'unpaired cells': e => { e.frames[7].after.screen.lines[1] = 'unexpected'; },
        'wrong pane': e => { e.frames[7].paneId = '22222222-2222-2222-2222-222222222222'; },
        'missing barrier': e => { e.frames[7].barrier = null; },
        'wrong prefix': e => { e.frames[7].kind = 'metadata-read'; },
        'a missing raw icon observation': e => coherent(e.frames[7], value => { delete value.metadata.buffer.requestedIconName; }),
        'a missing raw title observation': e => coherent(e.frames[7], value => { value.metadata.oscTitle = null; }),
        'wrong nonce': e => coherent(e.frames[1], value => { value.screen.lines[9] = 'NTILDE_TS_' + 'b'.repeat(32); }),
        'failed prepared baseline': e => coherent(e.frames[1], value => { value.metadata.oscTitle = 'unprepared'; }),
        'missing runtime color': e => coherent(e.frames[1], value => { value.screen.colorBinding = { ...value.screen.colorBinding, paletteOverrideIndices: [] }; }),
        'incomplete cell restoration': e => coherent(e.frames.at(-1), value => { value.screen.lines[2] = 'residue'; }),
        'incomplete title restoration': e => coherent(e.frames.at(-1), value => { value.metadata.oscTitle = 'RIS-left-this'; }),
        'incomplete icon restoration': e => coherent(e.frames.at(-1), value => { value.metadata.buffer.requestedIconName = 'left-over-icon'; }),
        'a pop that restored a stale entry at the end (the stack was not empty)': e => coherent(e.frames.at(-1), value => { value.metadata.oscTitle = 'stale'; value.metadata.displayTitle = 'stale'; value.metadata.presentation.tabText = 'stale'; }),
        'residual runtime color': e => coherent(e.frames.at(-1), value => { value.screen.colorBinding = { ...value.screen.colorBinding, paletteOverrideIndices: [7] }; }),
        'run record experiment order': e => { [e.run.experiments[0], e.run.experiments[1]] = [e.run.experiments[1], e.run.experiments[0]]; },
        'run record feed': e => { e.run.experiments[3].feeds[1] = '\x1b[22;1t'; },
        'run record barrier': e => { e.run.experiments[3].barriers[1] = 'not-a-barrier'; },
        'run record read kind': e => { e.run.experiments[1].reads[0].kind = 'pinned'; },
        'run record cursor': e => { e.run.experiments[1].reads[0].cursor = [9, 9]; },
        'run record title fact': e => { e.run.experiments[3].reads[0].facts.title = 'forged'; },
        'run record icon fact': e => { e.run.experiments[3].reads[0].facts.icon = 'forged'; },
        'run record pinned feed': e => { e.run.pinned[0].termless.feeds[0] = '\x1b]2;other-title\x07'; },
        'run record pinned verdict': e => { e.run.pinned[1].termless.result.observation.outcome = 'inconclusive'; },
        'run record pinned response': e => { e.run.pinned[0].termless.result.response = '{}'; },
        'run record missing case': e => { e.run.pinned.pop(); },
        'adapter identity': e => { e.adapter.id = 'other'; },
        'adapter derivation': e => { e.adapter.derivedFromAdapterId = 'other'; },
        'adapter digest': e => { e.adapter.bundleSha256 = 'short'; },
        'adapter probes': e => { e.run.probeIds = [push]; },
    };
    for (const [name, mutate] of Object.entries(attempts)) {
        const evidence = syntheticTitleStackEvidence();
        mutate(evidence);
        assert.throws(() => build(evidence), Error, name);
    }
    const evidence = syntheticTitleStackEvidence();
    assert.throws(() => buildTitleStackGradingReport(evidence.frames, evidence.run, 'b'.repeat(32), evidence.adapter), Error, 'run identity');
    assert.throws(() => buildTitleStackGradingReport(evidence.frames, evidence.run, 'short', evidence.adapter), Error, 'malformed run identity');
});

test('the baseline predicate demands the prepared title and icon at bind and the exact initial screen with the prepared title and icon at the end', () => {
    const evidence = syntheticTitleStackEvidence(), first = evidence.frames[0].before;
    assert.equal(matchesTitleStackBaseline(evidence.frames[1].before, first, runId, false), true);
    assert.equal(matchesTitleStackBaseline(evidence.frames.at(-1).before, first, runId, true), true);
    assert.equal(matchesTitleStackBaseline(evidence.frames[1].before, first, runId, true), false, 'the bound screen (nonce, runtime color) is not the restored one');
    assert.equal(matchesTitleStackBaseline(evidence.frames.at(-1).before, first, runId, false), false, 'the restored screen has no nonce');
    assert.equal(matchesTitleStackBaseline(evidence.frames[1].before, first, 'c'.repeat(32), false), false, 'a different run identity does not bind');
    const iconLeft = structuredClone(evidence.frames.at(-1).before); iconLeft.metadata.buffer.requestedIconName = 'left-over';
    assert.equal(matchesTitleStackBaseline(iconLeft, first, runId, true), false, 'an icon that was not restored');
});

test('the report names the observed table of every read beside each verdict, as observations', () => {
    const report = build(syntheticTitleStackEvidence()), overflow = caseOf(report, push).observed.experiments.find(entry => entry.id === `${push}/overflow`);
    assert.deepEqual(overflow.reads.map(entry => entry.label), ['base', 'changed', 'pop-1', 'pop-10', 'pop-11']);
    assert.match(overflow.reads[2].title, /-o2-t11$/); assert.match(overflow.reads[3].title, /-o2-t2$/); assert.match(overflow.reads[4].title, /-o2-t2$/);
    for (const item of report.cases) for (const experiment of item.observed.experiments) for (const read of experiment.reads) assert.equal(read.held, true);
    assert.deepEqual(titleStackCaseIds, [push, pop]);
});
