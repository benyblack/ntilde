import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildAttrRectGradingReport, replayAttrRectPinned, reconcileAttrRect, validateAttrRectEvidence } from './attr-rect-grading.mjs';
import { attrRectContracts, attrRectSuiteId, attrRectContractsVersion, attrRectEffectContractId, attrRectOracle, pinnedNotes } from './attr-rect-contracts.mjs';
import { attrRectRecordingContext } from './attr-rect-collector.mjs';
import { snapshotHash } from './agent-observer.mjs';
import { syntheticAttrRectEvidence, syntheticAttrRectReads, syntheticAttrRectScreens, paletteBinding } from './fixtures/attr-rect-evidence.mjs';

const CCARA = 'editing.deccara', RARA = 'editing.decrara', SACE = 'editing.decsace';
const runId = 'c'.repeat(32);
const { applyOp, pinnedC, pinnedR } = attrRectOracle;
const evidenceWith = (caseId, edit, binding) => syntheticAttrRectEvidence(runId, 'f'.repeat(64), binding, id => {
    const reads = syntheticAttrRectReads(id);
    if (id === caseId && edit) edit(reads);
    return reads;
});
const build = evidence => buildAttrRectGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
/** A JSON round trip: structuredClone would keep the shared screen and binding objects shared between frames. */
const clone = value => JSON.parse(JSON.stringify(value));
const caseOf = (report, id) => report.cases.find(item => item.caseId === id);

test('a complete run rederives all three cases with the pinned verdicts and the effect verdicts kept apart', () => {
    const report = build(syntheticAttrRectEvidence(runId));
    assert.equal(report.suiteId, attrRectSuiteId);
    assert.equal(report.contractsVersion, attrRectContractsVersion);
    assert.deepEqual(report.cases.map(item => [item.caseId, item.pinned.observation.outcome, item.effectContract.outcome, item.conclusion.outcome]), [
        [CCARA, 'inconclusive', 'supported', 'inconclusive'], [RARA, 'inconclusive', 'supported', 'inconclusive'], [SACE, 'inconclusive', 'supported', 'inconclusive']]);
    assert.equal(report.snapshots.length, 3 + 16 + 14 + 13);
    assert.match(report.scope, /inconclusive by design/);
    assert.match(report.scope, /DECSACE setting is never read directly/);
});

test('the DECCARA and DECRARA pinned verdicts are preserved: the stream default fails the pinned flank control, and the rectangle is a labeled experiment', () => {
    const report = build(syntheticAttrRectEvidence(runId));
    for (const id of [CCARA, RARA]) {
        const item = caseOf(report, id);
        assert.equal(item.pinned.pass, false);
        assert.equal(item.pinned.note, pinnedNotes[id].control);
        assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'supported', kind: 'pinned-fixture-assumes-rectangle-extent' });
        assert.equal(item.conclusion.grading, 'pinned-termless-preserved-extent-default');
        assert.equal(item.conclusion.outcome, 'inconclusive');
        assert.equal(item.effectContract.id, attrRectEffectContractId);
        assert.equal(item.effectContract.defaultExtent, 'stream');
        assert.match(item.effectContract.attribution, /not the pinned probe/);
        assert.equal(item.rectangleExperiment.outcome, 'supported');
        assert.match(item.rectangleExperiment.policy, /labeled experiment, not a pinned grade/);
        assert.equal(item.conclusion.evidence, 'owned-stored-cells');
    }
    const decsace = caseOf(report, SACE);
    assert.equal(decsace.pinned.note, 'Sequence consumption does not measure attribute-change extent');
    assert.deepEqual(decsace.pinned.observation, { outcome: 'inconclusive', evidence: 'consumed', reason: 'insufficient-evidence' });
    assert.deepEqual(decsace.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'supported', kind: 'pinned-inconclusive-by-design' });
    assert.equal(decsace.conclusion.grading, 'pinned-termless-preserved-by-design');
    assert.equal(decsace.rectangleExperiment, undefined);
});

test('the observed records show the stream and rectangle marks of the pinned fixture literally', () => {
    const report = build(syntheticAttrRectEvidence(runId));
    const set = caseOf(report, CCARA).observed;
    assert.deepEqual(set.pinnedDefaultExtent.marks.slice(0, 3), ['IIIIII', 'IIIIII', 'IIIII.']);
    assert.deepEqual(set.rectangleExperiment.marks.slice(0, 3), ['IIIII.', 'IIIII.', 'IIIII.']);
    assert.deepEqual(set.pinnedDefaultExtent.text.slice(0, 3), ['aaaaaZ', 'bbbbbY', 'cccccX']);
    const reverse = caseOf(report, RARA).observed;
    assert.deepEqual(reverse.pinnedDefaultExtent.marks.slice(0, 3), ['.....I', '.....I', '......']);
    assert.deepEqual(reverse.rectangleExperiment.marks.slice(0, 3), ['......', '......', '......']);
    assert.equal(reverse.toggle.restored, true);
    const extent = caseOf(report, SACE).observed;
    assert.deepEqual(extent.stream0.marks, ['..IIIIII', 'IIIIIIII', 'IIIIIIII', 'IIIIII..']);
    assert.deepEqual(extent.rectangle.marks, ['..IIII..', '..IIII..', '..IIII..', '..IIII..']);
    assert.equal(extent.ignoredValue.keptRectangle, true);
    assert.match(extent.extentObservation, /not readable/);
});

test('the pinned responses are byte-exact shapes of the pinned code', () => {
    const report = build(syntheticAttrRectEvidence(runId));
    const set = caseOf(report, CCARA).pinned;
    assert.deepEqual(Object.keys(JSON.parse(set.response)), ['before', 'setup', 'after']);
    assert.equal(JSON.parse(set.response).setup.x, 0);
    assert.deepEqual(set.assertions, [], 'an inconclusive pinned result carries no assertions');
    assert.equal(JSON.parse(caseOf(report, SACE).pinned.response).text.split('\n').length, 10);
});

test('the pinned replay equals the live pinned shapes over the observed fixture screens', () => {
    for (const contract of attrRectContracts.filter(item => item.family !== 'extent')) {
        const screens = syntheticAttrRectScreens(contract.caseId);
        const context = attrRectRecordingContext(contract, screens);
        const rectangle = replayAttrRectPinned(contract, screens, contract.rectanglePinnedReads);
        assert.equal(rectangle.pass, true);
        assert.equal(rectangle.observation.outcome, 'supported');
        assert.equal(rectangle.assertions[0].expected, contract.pinnedExpected);
        assert.throws(() => context.getCell(0, 0), /read before its fixture/);
        assert.throws(() => context.feed('x'), /feed 0 differs from the contract/);
        context.feed(contract.pinnedFeeds[0]);
        assert.equal(context.getScrollback().screenLines, 10);
        assert.equal(context.cols, 60);
    }
});

test('a pinned supported verdict needs the rectangle to be the default; a contract can be stricter but never more permissive', () => {
    // An App whose default extent is the rectangle: the pinned probe supports it, the effect contract (the VT510 default is the stream) does not.
    for (const id of [CCARA, RARA]) {
        const evidence = evidenceWith(id, reads => { reads.edited = reads.rect; reads.witness = reads.rect; });
        const item = caseOf(build(evidence), id);
        assert.equal(item.pinned.observation.outcome, 'supported');
        assert.equal(item.effectContract.outcome, 'unsupported');
        assert.equal(item.effectContract.defaultExtent, 'rectangle');
        assert.equal(item.divergence.kind, 'contract-stricter-than-pinned');
        assert.equal(item.conclusion.outcome, 'unsupported', 'a finer effect verdict is the conclusion when the pinned verdict decided');
        assert.equal(item.conclusion.grading, 'pinned-termless-plus-contract-v1');
    }
});

test('a no-op App is a pinned unsupported and an effect unsupported with no divergence', () => {
    const noop = (id, grid) => evidenceWith(id, reads => { reads.edited = { grid, cursor: [0, 0] }; });
    const set = caseOf(build(noop(CCARA, pinnedC.grid)), CCARA);
    assert.equal(set.pinned.observation.outcome, 'unsupported');
    assert.equal(set.effectContract.outcome, 'unsupported');
    assert.equal(set.divergence, undefined);
    assert.equal(set.conclusion.outcome, 'unsupported');
    const reverse = caseOf(build(noop(RARA, pinnedR.grid)), RARA);
    assert.equal(reverse.pinned.observation.outcome, 'unsupported');
    assert.equal(reverse.divergence, undefined);
});

test('the reconcile rule never lets a decided effect verdict contradict a decided pinned verdict or invent a preserved one', () => {
    const [set, , extent] = attrRectContracts;
    const note = { note: set.notes.control };
    assert.equal(reconcileAttrRect(set, 'supported', { outcome: 'supported' }, {}), null);
    assert.equal(reconcileAttrRect(set, 'unsupported', { outcome: 'unsupported' }, {}), null);
    assert.equal(reconcileAttrRect(set, 'inconclusive', { outcome: 'inconclusive' }, {}), null);
    assert.equal(reconcileAttrRect(set, 'inconclusive', { outcome: 'supported', defaultExtent: 'stream' }, note).kind, 'pinned-fixture-assumes-rectangle-extent');
    // The preserved divergence needs the stream default and the pinned control note; anything else is a more permissive contract.
    assert.throws(() => reconcileAttrRect(set, 'inconclusive', { outcome: 'supported', defaultExtent: 'other' }, note), /more permissive/);
    assert.throws(() => reconcileAttrRect(set, 'inconclusive', { outcome: 'supported', defaultExtent: 'stream' }, { note: set.notes.seed }), /more permissive/);
    assert.throws(() => reconcileAttrRect(set, 'unsupported', { outcome: 'supported' }, {}), /more permissive/);
    assert.equal(reconcileAttrRect(set, 'supported', { outcome: 'unsupported' }, {}).kind, 'contract-stricter-than-pinned');
    assert.equal(reconcileAttrRect(set, 'supported', { outcome: 'inconclusive' }, {}).kind, 'contract-stricter-than-pinned');
    assert.equal(reconcileAttrRect(set, 'inconclusive', { outcome: 'unsupported' }, {}).kind, 'contract-measured-mismatch');
    assert.equal(reconcileAttrRect(set, 'unsupported', { outcome: 'inconclusive' }, {}).kind, 'contract-more-cautious-than-pinned');
    assert.equal(reconcileAttrRect(extent, 'inconclusive', { outcome: 'inconclusive' }, {}), null);
    assert.equal(reconcileAttrRect(extent, 'inconclusive', { outcome: 'unsupported' }, {}).kind, 'pinned-inconclusive-by-design');
    assert.throws(() => reconcileAttrRect(extent, 'supported', { outcome: 'supported' }, {}), /must be inconclusive by design/);
});

test('an unmeasured seed, cursor or geometry is the pinned inconclusive with the pinned note', () => {
    const noSeed = caseOf(build(evidenceWith(CCARA, reads => { reads.seed = { grid: new Map(), cursor: [2, 6] }; })), CCARA);
    assert.equal(noSeed.pinned.note, pinnedNotes[CCARA].seed);
    assert.equal(noSeed.effectContract.outcome, 'inconclusive');
    assert.equal(noSeed.divergence, undefined);
    assert.equal(noSeed.conclusion.grading, 'pinned-termless-plus-contract-v1');
    const noCursor = caseOf(build(evidenceWith(RARA, reads => { reads.cursor = { grid: pinnedR.grid, cursor: [2, 2] }; })), RARA);
    assert.equal(noCursor.pinned.note, pinnedNotes[RARA].cursor);
    assert.equal(noCursor.effectContract.outcome, 'inconclusive');
});

test('DECSACE: a terminal that ignores the selection is a recorded separate effect verdict, never a pinned one', () => {
    const report = build(evidenceWith(SACE, reads => { reads.rectangle = reads.stream0; }));
    const item = caseOf(report, SACE);
    assert.equal(item.pinned.observation.outcome, 'inconclusive');
    assert.equal(item.effectContract.outcome, 'unsupported');
    assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'unsupported', kind: 'pinned-inconclusive-by-design' });
    assert.equal(item.conclusion.outcome, 'inconclusive');
    assert.equal(item.conclusion.layers, undefined);
});

test('tampered pinned results, run logs and identities are refused', () => {
    const evidence = syntheticAttrRectEvidence(runId);
    const mutate = edit => { const copy = clone(evidence); edit(copy); return copy; };
    assert.throws(() => build(mutate(copy => { copy.run.cases[0].pinned.pass = true; })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[0].pinned.observation.outcome = 'supported'; })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[1].pinned.response = copy.run.cases[1].pinned.response.replace('"inverse":false', '"inverse":true'); })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[2].pinned.observation.outcome = 'supported'; })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[0].pinnedRectangle.observation.outcome = 'inconclusive'; })), /rectangle experiment/);
    assert.throws(() => build(mutate(copy => { delete copy.run.cases[0].pinnedRectangle; })), /Invalid editing.deccara rectangle-extent experiment record/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[2].pinnedRectangle = copy.run.cases[0].pinnedRectangle; })), /Invalid editing.decsace rectangle-extent experiment record/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[0].pinnedRectangleFed = 9; })), /rectangle-extent experiment record/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[0].feeds[4] = '\x1b[0*x'; })), /do not match/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[1].feeds[7] = '\x1b[1;1;3;5;7$r'; })), /do not match/);
    assert.throws(() => build(mutate(copy => { copy.run.cases.reverse(); })), /case order differs/);
    assert.throws(() => build(mutate(copy => { copy.run.probeIds = [...copy.run.probeIds].reverse(); })), /does not cover/);
    assert.throws(() => build(mutate(copy => { copy.adapter.id = 'other'; })), /Invalid Attribute-rectangle adapter identity/);
    assert.throws(() => build(mutate(copy => { copy.adapter.derivedFromAdapterId = 'ntilde-owned-scrollback-grading-v1'; })), /Invalid Attribute-rectangle adapter identity/);
    assert.throws(() => build(mutate(copy => { copy.run.adapterId = 'other'; })), /Invalid Attribute-rectangle adapter identity/);
    assert.throws(() => build(mutate(copy => { delete copy.run.cases[0].pinnedFed; })), /Invalid editing\.deccara pinned feed count/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[2].pinnedFed = 5; })), /Invalid editing\.decsace pinned feed count/);
    assert.throws(() => buildAttrRectGradingReport(evidence.frames, evidence.run, 'd'.repeat(32), evidence.adapter), /not bound to this run/);
});

test('mismatched identities: a wrong recorded barrier, cursor or read kind is refused', () => {
    const evidence = syntheticAttrRectEvidence(runId);
    const barrier = clone(evidence);
    barrier.run.cases[0].barriers[3] = '\x1b[?1;2c';
    assert.throws(() => build(barrier), /barrier differs from its read frame/);
    const cursor = clone(evidence);
    cursor.run.cases[0].reads[2].cursor = [0, 3];
    assert.throws(() => build(cursor), /cursor differs from its read frame/);
    const kind = clone(evidence);
    kind.run.cases[1].reads[0].kind = 'text';
    assert.throws(() => build(kind), /Wrong editing.decrara read kind/);
});

test('stale or edited observations (changed screen with an old digest, reordered or dropped frames) are refused', () => {
    const evidence = syntheticAttrRectEvidence(runId);
    const stale = clone(evidence);
    stale.frames[4].screen.lines[0] = 'zzzzzZ';
    assert.throws(() => build(stale), /Changed Attribute-rectangle snapshot evidence/);
    const reordered = clone(evidence);
    [reordered.frames[3], reordered.frames[4]] = [reordered.frames[4], reordered.frames[3]];
    assert.throws(() => build(reordered), /Changed Attribute-rectangle snapshot evidence/);
    const missing = clone(evidence);
    missing.frames.splice(5, 1);
    assert.throws(() => build(missing), /Incomplete Attribute-rectangle snapshot sequence/);
});

test('an edited stored attribute with a recomputed digest is caught by the pinned agreement, not the digest', () => {
    const copy = clone(syntheticAttrRectEvidence(runId));
    // Frame 2 is the initial read; frame 5 is the DECCARA edited read. Strip one inverse attribute from its first cell.
    const frame = copy.frames[2 + 3];
    assert.equal(frame.kind, 'atrc-read');
    const screen = frame.screen;
    screen.attributeRuns = screen.attributeRuns.map(run => ({ ...run, flags: run.flags & ~0x02 }));
    screen.cellDetails = screen.cellDetails.map(run => ({ ...run, flags: run.flags & ~0x02 }));
    frame.sha256 = snapshotHash(frame.screen);
    assert.throws(() => build(copy), /does not match its observed evidence|Contradictory|Invalid|incoherent/i);
});

test('absent or malformed buffer state and history are refused, not defaulted', () => {
    const evidence = syntheticAttrRectEvidence(runId);
    const noBuffer = clone(evidence);
    delete noBuffer.frames[4].screen.bufferState;
    noBuffer.frames[4].sha256 = snapshotHash(noBuffer.frames[4].screen);
    assert.throws(() => build(noBuffer));
    const noHistory = clone(evidence);
    delete noHistory.frames[4].screen.history;
    noHistory.frames[4].sha256 = snapshotHash(noHistory.frames[4].screen);
    assert.throws(() => build(noHistory));
});

test('incomplete restoration is refused', () => {
    const copy = clone(syntheticAttrRectEvidence(runId));
    const last = copy.frames.at(-1);
    last.screen.lines[0] = 'left over';
    last.sha256 = snapshotHash(last.screen);
    assert.throws(() => build(copy), /was not restored/);
});

test('DECSCNM on throughout is a stable but unusable run; a mid-run change voids it', () => {
    const everywhere = syntheticAttrRectEvidence(runId, 'f'.repeat(64), { ...paletteBinding(), reverseVideo: true });
    assert.deepEqual(build(everywhere).cases.map(item => item.effectContract.outcome), Array(3).fill('inconclusive'));
    const one = clone(syntheticAttrRectEvidence(runId));
    one.frames[4].screen.colorBinding.reverseVideo = true;
    one.frames[4].sha256 = snapshotHash(one.frames[4].screen);
    assert.throws(() => build(one), /palette or theme change/);
});

test('contract-version pinning and evidence validation entry', () => {
    const evidence = syntheticAttrRectEvidence(runId);
    assert.doesNotThrow(() => validateAttrRectEvidence(evidence.frames, evidence.run, evidence.runId, evidence.adapter));
    assert.throws(() => buildAttrRectGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter, { contractsVersion: 2 }), /Unknown attribute-rectangle contracts revision 2/);
});

test('replaying through a module that feeds a different number of pinned writes is refused', () => {
    const evidence = syntheticAttrRectEvidence(runId);
    const short = (id, context) => {
        const contract = attrRectContracts.find(item => item.caseId === id);
        const result = replayAttrRectPinned(contract, syntheticAttrRectScreens(id));
        for (const feed of contract.pinnedFeeds.slice(0, contract.pinnedFeeds.length - 1)) context.feed(feed);
        return { probeId: id, ...result };
    };
    assert.throws(() => buildAttrRectGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter, { replayModule: { ntildeRunAttrRectTermless: short } }),
        /fed a different number of pinned writes/);
    const disagreeing = (id, context) => {
        const contract = attrRectContracts.find(item => item.caseId === id);
        for (const feed of contract.pinnedFeeds) context.feed(feed);
        return { probeId: id, ...replayAttrRectPinned(contract, syntheticAttrRectScreens(id)), pass: true };
    };
    assert.throws(() => buildAttrRectGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter, { replayModule: { ntildeRunAttrRectTermless: disagreeing } }),
        /replay does not reproduce the live grading/);
});

test('applyOp refuses a rectangle over unoccupied cells so contract authoring cannot silently depend on them', () => {
    assert.throws(() => applyOp(new Map([['0,0', { char: 'a', attrs: [] }]]), { top: 1, left: 1, bottom: 1, right: 4, selectors: [7], reverse: false, rectangle: true }), /unoccupied/);
    assert.equal(applyOp(new Map([['0,0', { char: 'a', attrs: [] }]]), { top: 1, left: 1, bottom: 1, right: 4, selectors: [7], reverse: false, rectangle: false }).size, 1);
});
