import assert from 'node:assert/strict';
import test from 'node:test';
import { snapshotHash } from './agent-observer.mjs';
import { syntheticOsc133Evidence } from './fixtures/osc133-evidence.mjs';
import { metadataProjection } from './metadata.mjs';
import { buildOsc133GradingReport, evaluateRead, matchesOsc133Baseline, observeFrame, osc133Counts } from './osc133-grading.mjs';
import { makeOsc133Experiments, osc133CaseIds, osc133Fixture, osc133Prompt, simulateOsc133 } from './osc133-contracts.mjs';

const runId = 'a'.repeat(32);
const build = evidence => buildOsc133GradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
const grade = (adjust, caseId) => { const report = build(syntheticOsc133Evidence({ adjust })); return caseId ? report.cases.find(item => item.caseId === caseId) : report; };
const outcomes = report => Object.fromEntries(report.cases.map(item => [item.caseId, item.effectContract.outcome]));
/** Re-seal a frame after an edit so the semantic change, not a stale hash, is under test. */
const coherent = (frame, mutate) => { mutate(frame.before); frame.after = structuredClone(frame.before); frame.sha256 = snapshotHash(metadataProjection(frame.before)); frame.afterSha256 = frame.sha256; };
const readFrame = (evidence, id, label) => {
    // frames are in plan order after the start and bind frames
    let index = 2;
    for (const item of evidence.run.experiments) for (const read of item.reads) { if (item.caseId === id && read.kind === label) return evidence.frames[index]; index++; }
    throw new Error(`no read ${id} ${label}`);
};

test('a model App that follows the oracle: every pinned verdict stays inconclusive and every effect contract is supported, with recorded divergences', () => {
    const report = build(syntheticOsc133Evidence());
    assert.deepEqual(outcomes(report), Object.fromEntries(osc133CaseIds.map(id => [id, 'supported'])));
    assert.deepEqual(osc133Counts(report), { 'inconclusive/supported': 4 });
    assert.equal(report.snapshots.length, 49);
    assert.equal(report.restoredSnapshot, 48);
    for (const item of report.cases) {
        assert.equal(item.conclusion.outcome, 'inconclusive');
        assert.equal(item.conclusion.grading, 'pinned-consumption-preserved-by-design');
        assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'supported', kind: 'pinned-inconclusive-by-design' });
        assert.match(item.effectContract.attribution, /not the pinned probe/);
        assert.equal(item.pinned.termless.observation.evidence, 'consumed');
        assert.ok(item.effectContract.validity.every(entry => entry.held));
        assert.ok(item.effectContract.required.every(entry => entry.held));
        assert.equal(item.effectContract.targetShown, true);
    }
    assert.match(report.scope, /no Bash, Zsh, Fish, PowerShell, SSH or remote delivery claim/);
});

test('the report re-derives identically from its own retained evidence', () => {
    const evidence = syntheticOsc133Evidence();
    assert.deepEqual(build(evidence), build(structuredClone(evidence)));
});

test('A: a marker that leaves the running state unchanged is unsupported; the stable wrong state is not waited away', () => {
    const report = grade((id, label, state) => { if (id === 'extensions.osc133-a/effect' && label === 'pinned') Object.assign(state, { status: 'running', command: 'echo a-run' }); });
    assert.equal(outcomes(report)['extensions.osc133-a'], 'unsupported');
    assert.deepEqual(report.cases[0].effectContract.failed, ['pinned-fixture-effect']);
    assert.deepEqual(Object.values(outcomes(report)).slice(1), ['supported', 'supported', 'supported']);
});

test('A: stale command text after the prompt-start marker is unsupported', () => {
    const report = grade((id, label, state) => { if (id === 'extensions.osc133-a/effect' && label === 'pinned') state.command = 'echo a-run'; });
    assert.equal(outcomes(report)['extensions.osc133-a'], 'unsupported');
});

test('A: what an unterminated command leaves in the anchors is recorded as implementation policy and never decides the verdict', () => {
    const item = grade((id, label, state) => { if (id === 'extensions.osc133-a/effect' && label === 'pinned') Object.assign(state, { accepting: true, outputStart: null }); }, 'extensions.osc133-a');
    assert.equal(item.effectContract.outcome, 'supported');
    const read = item.experiments.find(entry => entry.name === 'effect').reads.find(entry => entry.label === 'pinned');
    assert.equal(read.evaluation.policy.accepting.match, false);
    assert.equal(read.evaluation.policy.outputStart.match, false);
    assert.equal(read.evaluation.passed, true);
});

test('B: a command-input boundary one column off, or a window that never opened, is unsupported', () => {
    for (const adjust of [
        (id, label, state) => { if (id === 'extensions.osc133-b/boundary' && label === 'input') state.commandStart = [3, osc133Prompt.length + 1]; },
        (id, label, state) => { if (id === 'extensions.osc133-b/boundary' && label === 'input') state.commandStart = [4, osc133Prompt.length]; },
        (id, label, state) => { if (id === 'extensions.osc133-b/effect' && label === 'pinned') state.accepting = false; },
        (id, label, state) => { if (id === 'extensions.osc133-b/effect' && label === 'pinned') state.commandStart = null; },
    ]) assert.equal(outcomes(grade(adjust))['extensions.osc133-b'], 'unsupported');
});

test('B: a generic prompt state without the input window and anchor is not a measured boundary', () => {
    const report = grade((id, label, state) => { if (['boundary', 'effect', 'cycles'].some(name => id === `extensions.osc133-b/${name}`) && !['prior', 'ordinary', 'after-d'].includes(label)) Object.assign(state, { accepting: false, commandStart: null }); });
    assert.equal(outcomes(report)['extensions.osc133-b'], 'unsupported');
});

test('B: a prior consecutive cycle\'s anchor reused for the second prompt is unsupported', () => {
    const report = grade((id, label, state) => { if (id === 'extensions.osc133-b/cycles' && label === 'second') state.commandStart = [3, osc133Prompt.length]; });
    assert.equal(outcomes(report)['extensions.osc133-b'], 'unsupported');
});

test('C: the typed input text confused with the command identity, or a missing output anchor, is unsupported', () => {
    for (const adjust of [
        (id, label, state) => { if (id === 'extensions.osc133-c/payload' && label === 'accepted') state.command = 'echo c-typed'; },
        (id, label, state) => { if (id === 'extensions.osc133-c/payload' && label === 'accepted') state.command = null; },
        (id, label, state) => { if (id === 'extensions.osc133-c/payload' && label === 'accepted') state.outputStart = [2, osc133Prompt.length]; },
        (id, label, state) => { if (id === 'extensions.osc133-c/payload' && label === 'output') state.outputStart = [3, 8]; },
        (id, label, state) => { if (id === 'extensions.osc133-c/effect' && label === 'pinned') state.status = 'prompt'; },
        (id, label, state) => { if (id === 'extensions.osc133-c/payload' && label === 'accepted') state.accepting = true; },
    ]) assert.equal(outcomes(grade(adjust))['extensions.osc133-c'], 'unsupported');
});

test('C: a second cycle that keeps the first command text or output anchor is unsupported', () => {
    for (const adjust of [
        (id, label, state) => { if (id === 'extensions.osc133-c/cycles' && label === 'second') state.command = 'echo c-one'; },
        (id, label, state) => { if (id === 'extensions.osc133-c/cycles' && label === 'second') state.outputStart = [4, 0]; },
    ]) assert.equal(outcomes(grade(adjust))['extensions.osc133-c'], 'unsupported');
});

test('D: a completion that leaves the running state, the anchors or the window is unsupported', () => {
    for (const adjust of [
        (id, label, state) => { if (id === 'extensions.osc133-d/effect' && label === 'pinned') state.status = 'running'; },
        (id, label, state) => { if (id === 'extensions.osc133-d/effect' && label === 'pinned') state.outputStart = [3, 0]; },
        (id, label, state) => { if (id === 'extensions.osc133-d/effect' && label === 'pinned') state.commandStart = [2, osc133Prompt.length]; },
        (id, label, state) => { if (id === 'extensions.osc133-d/effect' && label === 'pinned') state.accepting = true; },
        (id, label, state) => { if (id === 'extensions.osc133-d/effect' && label === 'pinned') state.command = 'echo d-run'; },
        (id, label, state) => { if (id === 'extensions.osc133-d/cycles' && label === 'after-d2') state.commandStart = [6, osc133Prompt.length]; },
    ]) assert.equal(outcomes(grade(adjust))['extensions.osc133-d'], 'unsupported');
});

test('D: the exit payload and the child process exit code are never interpreted as one another', () => {
    const evidence = syntheticOsc133Evidence();
    const frame = readFrame(evidence, 'extensions.osc133-d/exit-payload', 'post');
    coherent(frame, value => { value.status.exitCode = 7; value.rawStatus.exitCode = 7; });
    const item = build(evidence).cases.find(entry => entry.caseId === 'extensions.osc133-d');
    assert.equal(item.effectContract.outcome, 'supported', 'an observed exit code does not widen or narrow the graded claim');
    const read = item.experiments.find(entry => entry.name === 'exit-payload').reads.find(entry => entry.label === 'post');
    assert.equal(read.evaluation.observed.exitCode, 7, 'retained separately as an observation');
    assert.equal(item.effectContract.outcome, 'supported');
});

test('a no-op: the unmarked control changing the target state makes the case inconclusive, never supported', () => {
    for (const [caseId, adjust] of [
        ['extensions.osc133-a', (id, label, state) => { if (id === 'extensions.osc133-a/effect' && label === 'ordinary') Object.assign(state, { status: 'prompt', command: null }); }],
        ['extensions.osc133-b', (id, label, state) => { if (id === 'extensions.osc133-b/effect' && label === 'ordinary') Object.assign(state, { accepting: true, commandStart: [0, 0] }); }],
        ['extensions.osc133-c', (id, label, state) => { if (id === 'extensions.osc133-c/effect' && label === 'ordinary') Object.assign(state, { status: 'running', accepting: false, outputStart: [3, 0] }); }],
        ['extensions.osc133-d', (id, label, state) => { if (id === 'extensions.osc133-d/effect' && label === 'ordinary') Object.assign(state, { status: 'prompt', command: null, commandStart: null, outputStart: null }); }],
    ]) assert.equal(outcomes(grade(adjust))[caseId], 'inconclusive', caseId);
});

test('stale prior metadata: a prior state that is not the measured one makes the case inconclusive', () => {
    const report = grade((id, label, state) => { if (id === 'extensions.osc133-a/effect' && label === 'prior') Object.assign(state, { status: 'prompt', command: null }); });
    assert.equal(outcomes(report)['extensions.osc133-a'], 'inconclusive');
    assert.ok(report.cases[0].effectContract.invalid.includes('prior-state'));
});

test('cross-case substitution: another marker producing a case\'s target transition invalidates its controls', () => {
    for (const [caseId, adjust] of [
        ['extensions.osc133-a', (id, label, state) => { if (id === 'extensions.osc133-a/gate-b' && label === 'post') Object.assign(state, { status: 'prompt', command: null }); }],
        ['extensions.osc133-b', (id, label, state) => { if (id === 'extensions.osc133-b/gate-c' && label === 'post') Object.assign(state, { accepting: true, commandStart: [0, 0] }); }],
        ['extensions.osc133-c', (id, label, state) => { if (id === 'extensions.osc133-c/gate-a' && label === 'post') Object.assign(state, { status: 'running', accepting: false }); }],
        ['extensions.osc133-d', (id, label, state) => { if (id === 'extensions.osc133-d/gate-a' && label === 'post') Object.assign(state, { commandStart: null, outputStart: null, accepting: false }); }],
    ]) {
        const item = grade(adjust, caseId);
        assert.equal(item.effectContract.outcome, 'inconclusive', caseId);
        assert.equal(item.effectContract.invalid.length, 1);
    }
});

test('A\'s disclosed shared observable with D never gates its verdict', () => {
    const item = grade(null, 'extensions.osc133-a');
    const disclosure = item.experiments.find(entry => entry.role === 'disclosure');
    assert.equal(disclosure.name, 'disclose-d');
    assert.equal(disclosure.reads[0].evaluation.observed.status, 'prompt');
    assert.equal(item.effectContract.outcome, 'supported');
    const changed = grade((id, label, state) => { if (id === 'extensions.osc133-a/disclose-d') state.status = 'running'; }, 'extensions.osc133-a');
    assert.equal(changed.effectContract.outcome, 'supported', 'a disclosure that does not hold is recorded, not required');
});

test('unrelated state: a changed cell, cursor, title, cwd, runtime color or confidence invalidates the read', () => {
    const evidence = syntheticOsc133Evidence(), fixture = osc133Fixture(runId), boundFrame = evidence.frames[1];
    const base = readFrame(evidence, 'extensions.osc133-b/effect', 'ordinary');
    const feeds = [];
    for (const step of makeOsc133Experiments(runId).find(item => item.caseId === 'extensions.osc133-b/effect').plan) {
        if (step.feed !== undefined) feeds.push(step.feed); else if (step.read === 'ordinary') break;
    }
    const model = simulateOsc133(feeds, fixture);
    const check = mutate => { const frame = structuredClone(base); coherent(frame, mutate); return evaluateRead(frame, model, boundFrame, fixture); };
    assert.equal(check(() => {}).passed, true, 'the untouched frame matches the oracle');
    for (const [name, mutate] of Object.entries({
        'a stray cell': value => { value.screen.lines[5] = 'stray'; },
        'a moved cursor': value => { value.screen.cursorCol = 4; value.metadata.buffer.cursorCol = 4; },
        'a changed title': value => { value.metadata.oscTitle = 'changed'; value.metadata.displayTitle = 'changed'; },
        'a changed cwd': value => { value.metadata.workingDirectory = '/elsewhere'; },
        'a changed runtime color': value => { value.screen.colorBinding = { ...value.screen.colorBinding, paletteOverrideIndices: [] }; },
        'a lost precision': value => { value.status.confidence = 'heuristic'; value.rawStatus.confidence = 'heuristic'; },
        'a history generation change': value => { value.metadata.buffer.historyGeneration++; },
        'a link': value => { value.metadata.buffer.links = [{ row: 0, col: 0, length: 1, identity: 0, uri: 'https://example.invalid/', id: null }]; },
    })) assert.equal(check(mutate).passed, false, name);
});

test('observations: raw anchors are read under their documented coordinate convention, and an unconventional anchor never matches', () => {
    const evidence = syntheticOsc133Evidence();
    const frame = structuredClone(readFrame(evidence, 'extensions.osc133-b/boundary', 'input'));
    assert.deepEqual(observeFrame(frame).commandStart, [3, osc133Prompt.length]);
    for (const mutate of [value => { value.metadata.buffer.commandStart.absoluteRow += 1; }, value => { value.metadata.buffer.commandStart.generation += 1; },
        value => { value.metadata.buffer.commandStart.alternateScreen = true; }]) {
        const copy = structuredClone(frame); mutate(copy.before);
        assert.ok(observeFrame(copy).commandStart.invalid, 'a mark in another coordinate space is flagged, never read as a viewport position');
    }
    const grid = evidence.frames.find(entry => entry.kind === 'shi-read');
    assert.equal(observeFrame(grid).statusRaw, 'awaitingInput');
});

test('tampered journals, run records and identities are refused', () => {
    const attempts = {
        'missing frame': e => e.frames.pop(),
        'extra frame': e => e.frames.push(structuredClone(e.frames.at(-1))),
        'reordered frames': e => { [e.frames[5], e.frames[6]] = [e.frames[6], e.frames[5]]; },
        'forged hash': e => { e.frames[7].sha256 = '0'.repeat(64); },
        'unpaired host': e => { e.frames[7].after.metadata.oscTitle = 'other'; },
        'unpaired cells': e => { e.frames[7].after.screen.lines[1] = 'unexpected'; },
        'wrong pane': e => { e.frames[7].paneId = '22222222-2222-2222-2222-222222222222'; },
        'missing barrier': e => { e.frames[7].barrier = null; },
        'wrong prefix': e => { e.frames[7].kind = 'metadata-read'; },
        'wrong nonce': e => coherent(e.frames[1], value => { value.screen.lines[9] = 'NTILDE_SH_' + 'b'.repeat(32); }),
        'failed prepared baseline': e => coherent(e.frames[1], value => { value.metadata.oscTitle = 'unprepared'; }),
        'missing runtime color': e => coherent(e.frames[1], value => { value.screen.colorBinding = { ...value.screen.colorBinding, paletteOverrideIndices: [] }; }),
        'incomplete cell restoration': e => coherent(e.frames.at(-1), value => { value.screen.lines[2] = 'residue'; }),
        'incomplete lifecycle restoration': e => coherent(e.frames.at(-1), value => { value.metadata.buffer.acceptingCommandInput = true; }),
        'residual runtime color': e => coherent(e.frames.at(-1), value => { value.screen.colorBinding = { ...value.screen.colorBinding, paletteOverrideIndices: [7] }; }),
        'run record experiment order': e => { [e.run.experiments[0], e.run.experiments[1]] = [e.run.experiments[1], e.run.experiments[0]]; },
        'run record feed': e => { e.run.experiments[3].feeds[1] = '\x1b[1;1H'; },
        'run record barrier': e => { e.run.experiments[3].barriers[1] = 'not-a-barrier'; },
        'run record read kind': e => { e.run.experiments[1].reads[0].kind = 'pinned'; },
        'run record cursor': e => { e.run.experiments[1].reads[0].cursor = [9, 9]; },
        'run record pinned feed': e => { e.run.pinned[0].termless.feeds[0] = '\x1b]133;A\x07Y'; },
        'run record pinned verdict': e => { e.run.pinned[1].termless.result.observation.outcome = 'supported'; },
        'run record pinned terminal verdict': e => { e.run.pinned[2].terminal.result.pass = true; },
        'run record missing case': e => { e.run.pinned.pop(); },
        'adapter identity': e => { e.adapter.id = 'other'; },
        'adapter derivation': e => { e.adapter.derivedFromAdapterId = 'other'; },
        'adapter digest': e => { e.adapter.bundleSha256 = 'short'; },
        'adapter probes': e => { e.run.probeIds = ['extensions.osc133-a']; },
    };
    for (const [name, mutate] of Object.entries(attempts)) {
        const evidence = syntheticOsc133Evidence();
        mutate(evidence);
        assert.throws(() => build(evidence), Error, name);
    }
    assert.throws(() => buildOsc133GradingReport(syntheticOsc133Evidence().frames, syntheticOsc133Evidence().run, 'b'.repeat(32), syntheticOsc133Evidence().adapter), Error, 'run identity');
    assert.throws(() => buildOsc133GradingReport(syntheticOsc133Evidence().frames, syntheticOsc133Evidence().run, 'short', syntheticOsc133Evidence().adapter), Error, 'malformed run identity');
});

test('the baseline predicate demands the prepared state at bind and the exact initial screen at the end', () => {
    const evidence = syntheticOsc133Evidence(), first = evidence.frames[0].before;
    assert.equal(matchesOsc133Baseline(evidence.frames[1].before, first, runId, false), true);
    assert.equal(matchesOsc133Baseline(evidence.frames.at(-1).before, first, runId, true), true);
    assert.equal(matchesOsc133Baseline(evidence.frames[1].before, first, runId, true), false, 'the bound screen (nonce, runtime color) is not the restored one');
    assert.equal(matchesOsc133Baseline(evidence.frames.at(-1).before, first, runId, false), false, 'the restored screen has no nonce');
    assert.equal(matchesOsc133Baseline(evidence.frames[1].before, first, 'c'.repeat(32), false), false, 'a different run identity does not bind');
});

test('the report names the observed boundary, identity and exit-payload facts beside each verdict, as observations only', () => {
    const report = build(syntheticOsc133Evidence()), facts = Object.fromEntries(report.cases.map(item => [item.caseId, item.observed]));
    assert.deepEqual(facts['extensions.osc133-a'].transition, { from: 'running', to: 'prompt' });
    assert.deepEqual(facts['extensions.osc133-a'].commandText, { before: 'echo a-run', after: null });
    assert.equal(facts['extensions.osc133-a'].sharedObservableWithD, true, 'D legitimately shares the status observable and is disclosed');
    assert.deepEqual(facts['extensions.osc133-b'].boundary, { anchor: [3, osc133Prompt.length], promptText: osc133Prompt, inputText: 'echo b-input', commandExposed: null });
    assert.deepEqual(facts['extensions.osc133-c'].commandIdentity, { typedText: 'echo c-typed', payload: 'echo c-payload', exposed: 'echo c-payload' });
    assert.equal(facts['extensions.osc133-c'].outputAnchor.firstOutputRow, 'c-output');
    assert.deepEqual(facts['extensions.osc133-c'].payloadShapes, { attribute: null, plainText: 'echo c-plain' });
    assert.deepEqual({ ...facts['extensions.osc133-d'].exitPayload, note: undefined }, { sent: 7, sessionStatusExitCode: null, interpreted: false, note: undefined });
    assert.match(facts['extensions.osc133-d'].exitPayload.note, /child process is a separate fact/);
    for (const item of Object.values(facts)) assert.equal(item.variants.stTerminator.targetShown, true);
    const changed = grade((id, label, state) => { if (id === 'extensions.osc133-d/exit-payload' && label === 'post') state.status = 'running'; }, 'extensions.osc133-d');
    assert.equal(changed.effectContract.outcome, 'supported', 'an exit-payload observation never gates the graded claim');
});
