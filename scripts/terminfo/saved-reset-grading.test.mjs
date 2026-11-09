import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildSavedResetGradingReport, replaySavedResetPinned, reconcileSavedReset, validateSavedResetEvidence } from './saved-reset-grading.mjs';
import { savedResetContracts, savedResetSuiteId, savedResetContractsVersion, pinnedSeedNote, pinnedModeNote, pinnedStyledNote } from './saved-reset-contracts.mjs';
import { savedResetRecordingContext } from './saved-reset-collector.mjs';
import { snapshotHash } from './agent-observer.mjs';
import { syntheticSavedResetEvidence, syntheticSavedResetReads, syntheticSavedResetScreens, put, paletteBinding } from './fixtures/saved-reset-evidence.mjs';

const EXIT = 'modes.alt-screen.exit', M47 = 'modes.altscreen-47', M1047 = 'modes.altscreen-1047', SGR = 'reset.sgr', DECALN = 'reset.decaln';
const runId = 'c'.repeat(32);
const evidenceWith = (caseId, edit, binding) => syntheticSavedResetEvidence(runId, 'f'.repeat(64), binding, id => {
    const reads = syntheticSavedResetReads(id);
    if (id === caseId && edit) edit(reads);
    return reads;
});
const build = evidence => buildSavedResetGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
/** A JSON round trip: structuredClone would keep the shared screen and binding objects shared between frames. */
const clone = value => JSON.parse(JSON.stringify(value));
const caseOf = (report, id) => report.cases.find(item => item.caseId === id);
const primary = (cursor = [4, 6]) => ({ cells: [...put(0, 0, 'PRIMARY-A'), ...put(2, 0, 'PRIMARY-B')], cursor });

test('a complete run rederives all five cases with the pinned verdicts and the effect verdicts kept apart', () => {
    const report = build(syntheticSavedResetEvidence(runId));
    assert.equal(report.suiteId, savedResetSuiteId);
    assert.equal(report.contractsVersion, savedResetContractsVersion);
    assert.deepEqual(report.cases.map(item => [item.caseId, item.pinned.observation.outcome, item.effectContract.outcome, item.conclusion.outcome]), [
        [EXIT, 'supported', 'supported', 'supported'],
        [M47, 'inconclusive', 'supported', 'inconclusive'],
        [M1047, 'inconclusive', 'supported', 'inconclusive'],
        [SGR, 'supported', 'supported', 'supported'],
        [DECALN, 'supported', 'supported', 'supported']]);
    assert.equal(report.snapshots.length, 3 + 13 + 9 + 12 + 5 + 6);
    assert.match(report.scope, /inconclusive by design/);
    assert.match(report.scope, /inactive bank is never inspected directly/);
});

test('the inconclusive-by-design pinned verdicts are preserved, with the effect contract separately identified', () => {
    const report = build(syntheticSavedResetEvidence(runId));
    for (const id of [M47, M1047]) {
        const item = caseOf(report, id);
        assert.equal(item.pinned.pass, false);
        assert.equal(item.pinned.note, pinnedModeNote);
        assert.equal(item.pinned.response, '{"entered":true,"exited":true}');
        assert.ok(!item.pinned.assertions?.length, 'an inconclusive pinned result carries no assertions');
        assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'supported', kind: 'pinned-inconclusive-by-design' });
        assert.equal(item.conclusion.grading, 'pinned-termless-preserved-by-design');
        assert.equal(item.effectContract.id, 'ntilde.saved-reset.effect/v1');
        assert.match(item.effectContract.attribution, /not the pinned probe/);
        assert.equal(item.conclusion.layers, undefined, 'the effect layers are never attributed to the pinned conclusion');
        assert.ok(item.effectContract.layers.length >= 1);
    }
    for (const id of [EXIT, SGR, DECALN]) {
        const item = caseOf(report, id);
        assert.equal(item.divergence, undefined);
        assert.equal(item.conclusion.grading, 'pinned-termless-plus-contract-v1');
        assert.match(item.effectContract.attribution, /finer grader/);
    }
});

test('the pinned responses are byte-exact shapes of the pinned code', () => {
    const report = build(syntheticSavedResetEvidence(runId));
    const exit = caseOf(report, EXIT);
    assert.equal(exit.pinned.response, JSON.stringify({ before: { x: 6, y: 4, visible: true }, seedA: 'PRIMARY-A', seedB: 'PRIMARY-B', mark: 'ALT-MARK',
        restoredA: 'PRIMARY-A', restoredB: 'PRIMARY-B', leaked: false, after: { x: 6, y: 4, visible: true } }));
    assert.equal(exit.pinned.assertions[0].expected, 'CSI ? 1049 l restores the primary buffer and the saved cursor');
    assert.equal(exit.pinned.note, null);
    assert.equal(caseOf(report, DECALN).pinned.assertions[0].expected, 'DECALN fills the measured cell (0,0) with E');
    assert.match(caseOf(report, DECALN).pinned.response, /^\{"cell":\{"char":"E"/);
    assert.equal(caseOf(report, SGR).pinned.assertions[0].expected, 'SGR 0 clears measured bold, italic, and inverse styling on Y');
    assert.match(caseOf(report, SGR).pinned.response, /^\{"before":\{"char":"X".*"bold":true.*"after":\{"char":"Y"/);
});

test('exit: the pinned verdict is supported or inconclusive, never unsupported; a failed roundtrip is a recorded measured mismatch', () => {
    const report = build(evidenceWith(EXIT, reads => { reads.exited = { cells: [], cursor: [4, 6] }; }));
    const item = caseOf(report, EXIT);
    assert.equal(item.pinned.observation.outcome, 'inconclusive');
    assert.match(item.pinned.note, /1049 roundtrip failed: PRIMARY-A not restored; PRIMARY-B not restored; cannot attribute failure to enter or exit/);
    assert.equal(item.effectContract.outcome, 'unsupported');
    assert.equal(item.conclusion.outcome, 'unsupported');
    assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'unsupported', kind: 'contract-measured-mismatch' });
    const cursor = caseOf(build(evidenceWith(EXIT, reads => { reads.exited = primary([0, 0]); })), EXIT);
    assert.match(cursor.pinned.note, /saved cursor not restored/);
    assert.equal(cursor.conclusion.outcome, 'unsupported');
    const leaked = caseOf(build(evidenceWith(EXIT, reads => { reads.exited = { cells: [...put(0, 0, 'ALT-MARK'), ...put(2, 0, 'PRIMARY-B')], cursor: [4, 6] }; })), EXIT);
    assert.match(leaked.pinned.note, /ALT-MARK leaked into the restored position/);
});

test('exit: damage the pinned fixture cannot see (stale cursor, lost attributes, bank sharing) is a recorded stricter divergence', () => {
    const stale = caseOf(build(evidenceWith(EXIT, reads => { reads.staleExited = primary([1, 1]); })), EXIT);
    assert.equal(stale.pinned.observation.outcome, 'supported');
    assert.deepEqual(stale.divergence, { pinnedOutcome: 'supported', contractOutcome: 'unsupported', kind: 'contract-stricter-than-pinned' });
    const shared = caseOf(build(evidenceWith(EXIT, reads => { reads.reentry = { ...primary(), alt: true }; })), EXIT);
    assert.equal(shared.divergence.kind, 'contract-stricter-than-pinned');
});

test('exit: unmeasured seed, parked cursor and entry are the pinned inconclusives with the pinned notes', () => {
    const seed = caseOf(build(evidenceWith(EXIT, reads => { reads.seed = { cells: put(0, 0, 'PRIMARY-X'), cursor: [4, 6] }; })), EXIT);
    assert.equal(seed.pinned.note, pinnedSeedNote);
    assert.equal(seed.pinned.response, JSON.stringify({ rows: 10, cols: 60, seedA: 'PRIMARY-X', seedB: ' '.repeat(9) }));
    assert.equal(seed.conclusion.outcome, 'inconclusive');
    const parked = caseOf(build(evidenceWith(EXIT, reads => { reads.seed = primary([4, 7]); })), EXIT);
    assert.equal(parked.pinned.note, 'Parked pre-cursor was not measured at row 4/col 6');
    assert.equal(parked.pinned.response, JSON.stringify({ before: { x: 7, y: 4, visible: true }, expectedCursor: { x: 6, y: 4 } }));
    const entry = caseOf(build(evidenceWith(EXIT, reads => { reads.entered = { cells: put(0, 0, 'PRIMARY-A'), cursor: [0, 0], alt: true }; })), EXIT);
    assert.equal(entry.pinned.note, 'Entry did not clear the seeded primary spans; exit cannot be attributed');
    assert.equal(entry.conclusion.outcome, 'inconclusive');
    const marker = caseOf(build(evidenceWith(EXIT, reads => { reads.marked = { cells: put(0, 0, 'ALT-MARX'), cursor: [0, 8], alt: true }; })), EXIT);
    assert.equal(marker.pinned.note, 'Alternate marker was not measured');
});

test('?47 and ?1047: the pinned verdict stays inconclusive whatever the terminal does, and a wrong terminal is a separate effect verdict', () => {
    for (const id of [M47, M1047]) {
        const noop = caseOf(build(evidenceWith(id, reads => { reads.active = { cells: [], cursor: [0, 0] }; })), id);
        assert.equal(noop.pinned.observation.outcome, 'inconclusive');
        assert.equal(noop.conclusion.outcome, 'inconclusive');
        assert.equal(noop.effectContract.outcome, 'unsupported');
        assert.deepEqual(noop.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'unsupported', kind: 'pinned-inconclusive-by-design' });
        // Mode metadata that never switched changes only the pinned response, not the pinned verdict.
        const never = caseOf(build(evidenceWith(id, reads => { reads.pinnedEntered = { cells: [], cursor: [0, 0] }; })), id);
        assert.equal(never.pinned.response, '{"entered":false,"exited":true}');
        assert.equal(never.pinned.observation.outcome, 'inconclusive');
        const invalid = caseOf(build(evidenceWith(id, reads => { reads.seed = { cells: [], cursor: [2, 4] }; })), id);
        assert.equal(invalid.effectContract.outcome, 'inconclusive');
        assert.equal(invalid.divergence, undefined);
    }
});

test('reset.sgr: pinned inconclusive and unsupported shapes, and stricter-than-pinned stuck fields', () => {
    const pair = (x, y) => ({ cells: [...put(0, 0, 'X', x), ...put(0, 1, 'Y', y)], cursor: [0, 2] });
    const styled = { attrs: ['bold', 'italic', 'inverse'] };
    const stuck = caseOf(build(evidenceWith(SGR, reads => { reads.pinned = pair(styled, { attrs: ['bold'] }); })), SGR);
    assert.equal(stuck.pinned.observation.outcome, 'unsupported');
    assert.equal(stuck.conclusion.outcome, 'unsupported');
    assert.equal(stuck.divergence, undefined);
    const colorOnly = caseOf(build(evidenceWith(SGR, reads => { reads.pinned = pair(styled, { fg: { kind: 'palette', index: 1 } }); })), SGR);
    assert.equal(colorOnly.pinned.observation.outcome, 'supported');
    assert.equal(colorOnly.divergence.kind, 'contract-stricter-than-pinned');
    const unstyled = caseOf(build(evidenceWith(SGR, reads => { reads.pinned = pair({ attrs: ['bold'] }, {}); })), SGR);
    assert.equal(unstyled.pinned.note, pinnedStyledNote);
    assert.equal(unstyled.conclusion.outcome, 'inconclusive');
});

test('reset.decaln: a stored E at 0,0 is supported by the pinned probe; a partial grid is a stricter divergence', () => {
    const partial = caseOf(build(evidenceWith(DECALN, reads => { reads.pinned = { cells: put(0, 0, 'E'), cursor: [0, 0] }; })), DECALN);
    assert.equal(partial.pinned.observation.outcome, 'supported');
    assert.equal(partial.conclusion.outcome, 'unsupported');
    assert.deepEqual(partial.divergence, { pinnedOutcome: 'supported', contractOutcome: 'unsupported', kind: 'contract-stricter-than-pinned' });
    assert.deepEqual(partial.conclusion.layers.map(layer => layer.name), ['pinned-cell-0-0', 'full-grid-refinement']);
    const none = caseOf(build(evidenceWith(DECALN, reads => { reads.pinned = { cells: [], cursor: [0, 0] }; })), DECALN);
    assert.equal(none.pinned.observation.outcome, 'unsupported');
    assert.equal(none.divergence, undefined);
});

test('the reconcile rule never lets a decided effect verdict contradict a decided pinned verdict', () => {
    const strict = savedResetContracts[0], design = savedResetContracts[1];
    assert.equal(reconcileSavedReset(strict, 'supported', { outcome: 'supported' }), null);
    assert.throws(() => reconcileSavedReset(strict, 'inconclusive', { outcome: 'supported' }), /more permissive than the pinned probe/);
    assert.throws(() => reconcileSavedReset(strict, 'unsupported', { outcome: 'supported' }), /more permissive than the pinned probe/);
    assert.throws(() => reconcileSavedReset(design, 'supported', { outcome: 'supported' }), /must be inconclusive by design/);
    assert.throws(() => reconcileSavedReset(design, 'unsupported', { outcome: 'unsupported' }), /must be inconclusive by design/);
    assert.equal(reconcileSavedReset(design, 'inconclusive', { outcome: 'inconclusive' }), null);
});

test('the pinned replay equals the live pinned shapes over the observed fixture screens', () => {
    for (const contract of savedResetContracts) {
        const screens = syntheticSavedResetScreens(contract.caseId);
        const replay = replaySavedResetPinned(contract, screens);
        assert.equal(replay.observation.outcome, contract.pinnedAlwaysInconclusive ? 'inconclusive' : 'supported', contract.caseId);
        const context = savedResetRecordingContext(contract, screens);
        assert.equal(context.cols, 60);
        assert.equal(context.getScrollback().screenLines, 10);
        assert.throws(() => context.getText(), /must not read text/);
        assert.throws(() => context.feed('x'), /differs from the contract/);
        for (const feed of contract.pinnedFeeds) context.feed(feed);
        assert.equal(context.fedCount, contract.pinnedFeeds.length);
        assert.throws(() => context.feed(contract.pinnedFeeds[0]), /differs from the contract/);
        assert.throws(() => context.getMode('bracketedPaste'), /unexpected mode|has no observed screen/);
    }
    const exit = savedResetRecordingContext(savedResetContracts[0], syntheticSavedResetScreens(EXIT));
    for (const feed of savedResetContracts[0].pinnedFeeds.slice(0, 3)) exit.feed(feed);
    assert.deepEqual(exit.getCursor(), { x: 6, y: 4, visible: true });
    assert.equal(exit.getCell(0, 0).char, 'P');
    const partway = savedResetRecordingContext(savedResetContracts[0], syntheticSavedResetScreens(EXIT));
    partway.feed(savedResetContracts[0].pinnedFeeds[0]);
    assert.throws(() => partway.getCell(0, 0), /after feed 1 has no observed screen/);
    const swap = savedResetRecordingContext(savedResetContracts[1], syntheticSavedResetScreens(M47));
    assert.equal(swap.getCell(0, 0).char, ' ', 'before any feed the reset screen is served');
    swap.feed('\x1b[?47h');
    assert.equal(swap.getMode('altScreen'), true);
    swap.feed('\x1b[?47l');
    assert.equal(swap.getMode('altScreen'), false);
});

test('the pinned geometry gate and early returns are the pinned inconclusive shapes', () => {
    const contract = savedResetContracts[0];
    const screens = syntheticSavedResetScreens(EXIT).map(screen => screen);
    // Fewer than five rows cannot be a valid screen snapshot, so exercise the pinned gate through a shortened copy.
    const short = screens.map(screen => ({ ...screen, rows: 4 }));
    const replay = replaySavedResetPinned(contract, short);
    assert.equal(replay.note, '1049 fixture needs at least 5 visible rows and 9 columns');
    assert.equal(replay.response, JSON.stringify({ rows: 4, cols: 60, needed: 9 }));
    assert.equal(replay.observation.outcome, 'inconclusive');
});

test('tampered pinned results, run logs and identities are refused', () => {
    const evidence = syntheticSavedResetEvidence(runId);
    const mutate = edit => { const copy = clone(evidence); edit(copy); return copy; };
    assert.throws(() => build(mutate(copy => { copy.run.cases[0].pinned.pass = false; })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[1].pinned.observation.outcome = 'supported'; })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[1].pinned.response = '{"entered":false,"exited":true}'; })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[3].pinned.response = copy.run.cases[3].pinned.response.replace('"bold":true', '"bold":false'); })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[0].feeds[3] = '\x1b[?1049l'; })), /do not match/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[2].feeds[1] = '\x1b[?47h'; })), /do not match/);
    assert.throws(() => build(mutate(copy => { copy.run.cases.reverse(); })), /case order differs/);
    assert.throws(() => build(mutate(copy => { copy.run.probeIds = [...copy.run.probeIds].reverse(); })), /does not cover/);
    assert.throws(() => build(mutate(copy => { copy.adapter.id = 'other'; })), /Invalid Saved-state\/reset adapter identity/);
    assert.throws(() => build(mutate(copy => { copy.adapter.derivedFromAdapterId = 'ntilde-owned-sgr-closure-grading-v1'; })), /Invalid Saved-state\/reset adapter identity/);
    assert.throws(() => build(mutate(copy => { copy.run.adapterId = 'other'; })), /Invalid Saved-state\/reset adapter identity/);
    assert.throws(() => build(mutate(copy => { delete copy.run.cases[0].pinnedFed; })), /Invalid modes\.alt-screen\.exit pinned feed count/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[3].pinnedFed = 5; })), /Invalid reset\.sgr pinned feed count/);
    assert.throws(() => buildSavedResetGradingReport(evidence.frames, evidence.run, 'd'.repeat(32), evidence.adapter), /not bound to this run/);
});

test('mismatched identities: a wrong recorded barrier, cursor or read kind is refused', () => {
    const evidence = syntheticSavedResetEvidence(runId);
    const barrier = clone(evidence);
    barrier.run.cases[0].barriers[3] = '\x1b[?1;2c';
    assert.throws(() => build(barrier), /barrier differs from its read frame/);
    const cursor = clone(evidence);
    cursor.run.cases[0].reads[2].cursor = [0, 3];
    assert.throws(() => build(cursor), /cursor differs from its read frame/);
    const kind = clone(evidence);
    kind.run.cases[1].reads[0].kind = 'text';
    assert.throws(() => build(kind), /Wrong modes\.altscreen-47 read kind/);
});

test('stale or edited observations (changed screen with an old digest, reordered or dropped frames) are refused', () => {
    const evidence = syntheticSavedResetEvidence(runId);
    const stale = clone(evidence);
    stale.frames[4].screen.lines[0] = 'PRIMARY-X';
    assert.throws(() => build(stale), /Changed Saved-state\/reset snapshot evidence/);
    const reordered = clone(evidence);
    [reordered.frames[3], reordered.frames[4]] = [reordered.frames[4], reordered.frames[3]];
    assert.throws(() => build(reordered), /Changed Saved-state\/reset snapshot evidence/);
    const missing = clone(evidence);
    missing.frames.splice(5, 1);
    assert.throws(() => build(missing), /Incomplete Saved-state\/reset snapshot sequence/);
});

test('absent or malformed buffer state and history are refused, not defaulted', () => {
    const evidence = syntheticSavedResetEvidence(runId);
    const noBuffer = clone(evidence);
    delete noBuffer.frames[4].screen.bufferState;
    noBuffer.frames[4].sha256 = snapshotHash(noBuffer.frames[4].screen);
    assert.throws(() => build(noBuffer));
    const noHistory = clone(evidence);
    delete noHistory.frames[4].screen.history;
    noHistory.frames[4].sha256 = snapshotHash(noHistory.frames[4].screen);
    assert.throws(() => build(noHistory));
    const flag = clone(evidence);
    flag.frames[4].screen.bufferState.alternateScreen = 'yes';
    flag.frames[4].sha256 = snapshotHash(flag.frames[4].screen);
    assert.throws(() => build(flag));
});

test('an edited alternate-screen flag with a recomputed digest is caught by the pinned agreement, not the digest', () => {
    const evidence = syntheticSavedResetEvidence(runId);
    const copy = clone(evidence);
    // Frame 2+1 is the exit case's entered read; flipping the flag back to main changes the pinned replay.
    const frame = copy.frames[2 + 2];
    assert.equal(frame.screen.bufferState.alternateScreen, true);
    frame.screen.lines[0] = 'PRIMARY-A';
    frame.sha256 = snapshotHash(frame.screen);
    assert.throws(() => build(copy), /does not match its observed evidence/);
});

test('inconsistent flags and details: attribute runs that contradict the stored details are refused', () => {
    const evidence = syntheticSavedResetEvidence(runId);
    const copy = clone(evidence);
    copy.frames[4].screen.attributeRuns[0].flags = 0x0301;
    copy.frames[4].sha256 = snapshotHash(copy.frames[4].screen);
    assert.throws(() => build(copy));
    const colors = clone(evidence);
    colors.frames[4].screen.cellDetails[0].fg = 5;
    colors.frames[4].sha256 = snapshotHash(colors.frames[4].screen);
    assert.throws(() => build(colors));
});

test('incomplete restoration is refused', () => {
    const copy = clone(syntheticSavedResetEvidence(runId));
    const last = copy.frames.at(-1);
    last.screen.lines[0] = 'left over';
    last.sha256 = snapshotHash(last.screen);
    assert.throws(() => build(copy), /was not restored/);
    const alt = clone(syntheticSavedResetEvidence(runId));
    alt.frames.at(-1).screen.bufferState.alternateScreen = true;
    alt.frames.at(-1).sha256 = snapshotHash(alt.frames.at(-1).screen);
    assert.throws(() => build(alt), /was not restored/);
});

test('DECSCNM on throughout is a stable but unusable run; a mid-run change voids it', () => {
    const everywhere = syntheticSavedResetEvidence(runId, 'f'.repeat(64), { ...paletteBinding(), reverseVideo: true });
    assert.deepEqual(build(everywhere).cases.map(item => item.effectContract.outcome), Array(5).fill('inconclusive'));
    const one = clone(syntheticSavedResetEvidence(runId));
    one.frames[4].screen.colorBinding.reverseVideo = true;
    one.frames[4].sha256 = snapshotHash(one.frames[4].screen);
    assert.throws(() => build(one), /palette or theme change/);
});

test('contract-version pinning and evidence validation entry', () => {
    const evidence = syntheticSavedResetEvidence(runId);
    assert.doesNotThrow(() => validateSavedResetEvidence(evidence.frames, evidence.run, evidence.runId, evidence.adapter));
    assert.throws(() => buildSavedResetGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter, { contractsVersion: 2 }), /Unknown saved-state contracts revision 2/);
});

test('replaying through a module that feeds a different number of pinned writes is refused', () => {
    const evidence = syntheticSavedResetEvidence(runId);
    const real = (id, context) => {
        const contract = savedResetContracts.find(item => item.caseId === id);
        const result = replaySavedResetPinned(contract, syntheticSavedResetScreens(id));
        for (const feed of contract.pinnedFeeds.slice(0, -1)) context.feed(feed);
        return { probeId: id, ...result };
    };
    assert.throws(() => buildSavedResetGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter, { replayModule: { ntildeRunSavedResetTermless: real } }),
        /fed a different number of pinned writes/);
    const exact = (id, context) => {
        const contract = savedResetContracts.find(item => item.caseId === id);
        const result = replaySavedResetPinned(contract, syntheticSavedResetScreens(id));
        for (const feed of contract.pinnedFeeds) context.feed(feed);
        return { probeId: id, ...result };
    };
    assert.doesNotThrow(() => buildSavedResetGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter, { replayModule: { ntildeRunSavedResetTermless: exact } }));
});
