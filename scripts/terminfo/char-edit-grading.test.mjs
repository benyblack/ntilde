import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildCharEditGradingReport, replayCharEditPinned, validateCharEditEvidence } from './char-edit-grading.mjs';
import { charEditContracts, charEditSuiteId, charEditContractsVersion, pinnedNotes } from './char-edit-contracts.mjs';
import { charEditRecordingContext } from './char-edit-collector.mjs';
import { snapshotHash } from './agent-observer.mjs';
import { syntheticCharEditEvidence, syntheticCharEditReads, syntheticCharEditScreens, rowOf, shifted, repeated, colorCell,
    paletteBinding } from './fixtures/char-edit-evidence.mjs';

const ICH = 'editing.insert-chars', DCH = 'editing.delete-chars', REP = 'editing.repeat-char';
const runId = 'c'.repeat(32);
const evidenceWith = (caseId, edit, binding) => syntheticCharEditEvidence(runId, 'f'.repeat(64), binding, id => {
    const reads = syntheticCharEditReads(id);
    if (id === caseId && edit) edit(reads);
    return reads;
});
const build = evidence => buildCharEditGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
const byId = (report, id) => report.cases.find(item => item.caseId === id);
/** A JSON round trip: structuredClone would keep the shared screen and binding objects shared between frames. */
const clone = value => JSON.parse(JSON.stringify(value));
const frameOf = (evidence, caseId, role) => {
    const position = charEditContracts.findIndex(contract => contract.caseId === caseId);
    const contract = charEditContracts[position];
    return evidence.frames[2 + charEditContracts.slice(0, position).reduce((sum, item) => sum + item.roles.length, 0) + contract.roles.indexOf(role)];
};
const refreeze = frame => { frame.sha256 = snapshotHash(frame.screen); };

test('a complete run rederives all three cases: pinned and contract agree and nothing diverges', () => {
    const report = build(syntheticCharEditEvidence(runId));
    assert.equal(report.suiteId, charEditSuiteId);
    assert.equal(report.contractsVersion, charEditContractsVersion);
    assert.deepEqual(report.cases.map(item => [item.caseId, item.pinned.observation.outcome, item.conclusion.outcome]), [
        [ICH, 'supported', 'supported'], [DCH, 'supported', 'supported'], [REP, 'supported', 'supported']]);
    for (const item of report.cases) {
        assert.equal(item.divergence, undefined);
        assert.equal(item.conclusion.grading, 'pinned-termless-plus-contract-v1');
        assert.equal(item.pinned.assertions[0].expected, charEditContracts.find(c => c.caseId === item.caseId).pinnedExpected);
    }
    assert.equal(report.snapshots.length, 3 + 3 * 15);
    assert.match(report.scope, /never measure cells and are not qualified/);
    assert.match(report.scope, /no Unicode, wide-cell or combining-character editing/);
});

test('the pinned responses carry the exact cells and cursors of each pinned read', () => {
    const report = build(syntheticCharEditEvidence(runId));
    const visible = true;
    assert.equal(byId(report, ICH).pinned.response, JSON.stringify({ before: [...'ABCDEZQH'], setup: { x: 2, y: 0, visible },
        after: ['A', 'B', ' ', 'C', 'D', 'E', 'Z', 'Q'] }));
    assert.equal(byId(report, DCH).pinned.response, JSON.stringify({ before: [...'ABCDEZQH'], setup: { x: 2, y: 0, visible },
        after: ['A', 'B', 'D', 'E', 'Z', 'Q', 'H'] }));
    assert.equal(byId(report, REP).pinned.response, JSON.stringify({ seed: [...'AX   Z'], targetPosition: { x: 1, y: 0, visible },
        before: [...'AX   Z'], setup: { x: 2, y: 0, visible }, after: [...'AXXXXZ'] }));
});

test('a no-op edit grades unsupported by both the pinned probe and the contract', () => {
    const report = build(evidenceWith(ICH, reads => { reads.edited = reads.cursor; }));
    assert.equal(byId(report, ICH).pinned.observation.outcome, 'unsupported');
    assert.equal(byId(report, ICH).conclusion.outcome, 'unsupported');
    assert.equal(byId(report, ICH).divergence, undefined);
    const rep = build(evidenceWith(REP, reads => { reads.repeated = { rows: [rowOf('AX   Z')], cursor: [0, 2] }; }));
    assert.equal(byId(rep, REP).pinned.observation.outcome, 'unsupported');
    assert.equal(byId(rep, REP).conclusion.outcome, 'unsupported');
});

test('wrong shift direction and count are unsupported in both graders', () => {
    const reversed = build(evidenceWith(ICH, reads => { reads.edited = { rows: [rowOf(shifted('dch', 'ABCDEZQH', 2, 1))], cursor: [0, 2] }; }));
    assert.equal(byId(reversed, ICH).pinned.observation.outcome, 'unsupported');
    assert.equal(byId(reversed, ICH).conclusion.outcome, 'unsupported');
    const count = build(evidenceWith(DCH, reads => { reads.edited = { rows: [rowOf(shifted('dch', 'ABCDEZQH', 2, 2))], cursor: [0, 2] }; }));
    assert.equal(byId(count, DCH).pinned.observation.outcome, 'unsupported');
    const rep = build(evidenceWith(REP, reads => { reads.repeated = { rows: [rowOf(repeated(2))], cursor: [0, 4] }; }));
    assert.equal(byId(rep, REP).pinned.observation.outcome, 'unsupported');
});

test('a moved cursor and text past the pinned window are recorded contract-stricter divergences from a supported pinned verdict', () => {
    const cursor = build(evidenceWith(DCH, reads => { reads.edited = { ...reads.edited, cursor: [0, 5] }; }));
    assert.deepEqual(byId(cursor, DCH).divergence, { pinnedOutcome: 'supported', contractOutcome: 'unsupported', kind: 'contract-stricter-than-pinned' });
    const tail = build(evidenceWith(ICH, reads => { reads.edited = { rows: [rowOf('AB CDEZQHJ')], cursor: [0, 2] }; }));
    assert.equal(byId(tail, ICH).pinned.observation.outcome, 'supported');
    assert.equal(byId(tail, ICH).conclusion.outcome, 'unsupported');
    assert.equal(byId(tail, ICH).divergence.kind, 'contract-stricter-than-pinned');
    const repCursor = build(evidenceWith(REP, reads => { reads.repeated = { ...reads.repeated, cursor: [0, 3] }; }));
    assert.equal(byId(repCursor, REP).divergence.kind, 'contract-stricter-than-pinned');
});

test('damage the pinned probes cannot see (wide row, right edge, attributes, accidental rows) is a recorded stricter divergence', () => {
    const wide = build(evidenceWith(ICH, reads => { reads.wide = { rows: [rowOf('ABOVE-ROW!'), rowOf('x'), rowOf('BELOW-ROW!')], cursor: [1, 2] }; }));
    assert.equal(byId(wide, ICH).divergence.kind, 'contract-stricter-than-pinned');
    const edge = build(evidenceWith(DCH, reads => { reads.edge = { rows: [rowOf('stray')], cursor: [0, 57] }; }));
    assert.equal(byId(edge, DCH).conclusion.outcome, 'unsupported');
    const attrs = build(evidenceWith(ICH, reads => { reads.attr = { rows: [rowOf('AB CDEZQH')], cursor: [0, 2] }; }));
    assert.equal(byId(attrs, ICH).pinned.observation.outcome, 'supported');
    assert.equal(byId(attrs, ICH).conclusion.outcome, 'unsupported');
    const stray = build(evidenceWith(REP, reads => { reads.repeated = { rows: [rowOf('AXXXXZ'), null, rowOf('stray')], cursor: [0, 5] }; }));
    assert.equal(byId(stray, REP).pinned.observation.outcome, 'supported');
    assert.equal(byId(stray, REP).conclusion.outcome, 'unsupported');
});

test('an invalid setup is inconclusive in both graders with the pinned note', () => {
    const cursor = build(evidenceWith(ICH, reads => { reads.cursor = { ...reads.cursor, cursor: [0, 3] }; }));
    assert.equal(byId(cursor, ICH).pinned.observation.outcome, 'inconclusive');
    assert.equal(byId(cursor, ICH).conclusion.outcome, 'inconclusive');
    assert.equal(byId(cursor, ICH).pinned.note, pinnedNotes[ICH].cursor);
    const seed = build(evidenceWith(DCH, reads => { reads.seed = { rows: [rowOf('ABCDEZQX')], cursor: [0, 8] }; }));
    assert.equal(byId(seed, DCH).pinned.note, pinnedNotes[DCH].seed);
    assert.equal(byId(seed, DCH).conclusion.outcome, 'inconclusive');
    const setup = build(evidenceWith(REP, reads => { reads.before = { rows: [rowOf('AX   Z')], cursor: [0, 4] }; }));
    assert.equal(byId(setup, REP).pinned.note, pinnedNotes[REP].setup);
    assert.equal(byId(setup, REP).conclusion.outcome, 'inconclusive');
});

test('a changed prefix or flank control is pinned-inconclusive and a contract-measured mismatch', () => {
    const prefix = build(evidenceWith(ICH, reads => { reads.edited = { rows: [rowOf('XB CDEZQH')], cursor: [0, 2] }; }));
    assert.equal(byId(prefix, ICH).pinned.observation.outcome, 'inconclusive');
    assert.equal(byId(prefix, ICH).pinned.note, pinnedNotes[ICH].control);
    assert.equal(byId(prefix, ICH).conclusion.outcome, 'unsupported');
    assert.deepEqual(byId(prefix, ICH).divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'unsupported', kind: 'contract-measured-mismatch' });
    const flank = build(evidenceWith(REP, reads => { reads.repeated = { rows: [rowOf('AXXXXY')], cursor: [0, 5] }; }));
    assert.equal(byId(flank, REP).pinned.note, pinnedNotes[REP].control);
    assert.equal(byId(flank, REP).divergence.kind, 'contract-measured-mismatch');
});

test('a pinned unsupported over an invalid wide-row control is recorded as the contract being more cautious', () => {
    const report = build(evidenceWith(DCH, reads => {
        reads.edited = { rows: [rowOf(shifted('ich', 'ABCDEZQH', 2, 1))], cursor: [0, 2] };
        reads.wideBefore = { rows: [rowOf('ABOVE-ROW!')], cursor: [1, 2] };
    }));
    const item = byId(report, DCH);
    assert.equal(item.pinned.observation.outcome, 'unsupported');
    assert.equal(item.conclusion.outcome, 'inconclusive');
    assert.deepEqual(item.divergence, { pinnedOutcome: 'unsupported', contractOutcome: 'inconclusive', kind: 'contract-more-cautious-than-pinned' });
});

test('REP after a barrier and the other spellings are recorded observations that never change a grade', () => {
    const noRepeat = build(syntheticCharEditEvidence(runId));
    assert.equal(byId(noRepeat, REP).observed.splitControl.repeats, false);
    assert.equal(byId(noRepeat, REP).observed.spellingControls.omitted.matchesEcma48, true);
    const odd = build(evidenceWith(REP, reads => { reads.omitted = { rows: [rowOf('AX   Z')], cursor: [0, 2] }; reads.split = { rows: [rowOf(repeated(3))], cursor: [0, 5] }; }));
    assert.equal(byId(odd, REP).conclusion.outcome, 'supported');
    assert.equal(byId(odd, REP).observed.spellingControls.omitted.matchesEcma48, false);
    assert.equal(byId(odd, REP).observed.splitControl.repeats, true);
    const sibling = build(evidenceWith(ICH, reads => { reads.explicit2 = { rows: [rowOf('ABCDEZQH')], cursor: [0, 2] }; }));
    assert.equal(byId(sibling, ICH).conclusion.outcome, 'supported');
    assert.equal(byId(sibling, ICH).observed.spellingControls.explicit2.matchesEcma48, false);
    assert.deepEqual(byId(sibling, DCH).conclusion, byId(build(syntheticCharEditEvidence(runId)), DCH).conclusion);
});

test('the attribute observation separates moved cells, created blanks and repeated cells', () => {
    const report = build(syntheticCharEditEvidence(runId));
    assert.equal(byId(report, ICH).observed.attributes.movedKeepAttributes, true);
    assert.equal(byId(report, ICH).observed.attributes.createdBlankBackground, 'created cells keep the default background');
    assert.equal(byId(report, REP).observed.attributes.repeatedCells, 'repeated cells carry the current pen attributes');
    const painted = build(evidenceWith(DCH, reads => {
        const row = reads.attr.rows[0];
        // DCH exposes its blank at the last column (59 of 60): paint exactly that cell.
        const gap = Array.from({ length: 59 - row.length }, () => colorCell(' '));
        reads.attr = { rows: [[...row, ...gap, colorCell(' ', { bg: { kind: 'palette', index: 4 } })]], cursor: reads.attr.cursor };
    }));
    assert.equal(byId(painted, DCH).conclusion.outcome, 'supported');
    assert.equal(byId(painted, DCH).observed.attributes.createdBlankBackground, 'created cells carry the current background');
});

test('the pinned replay equals the live pinned shapes over the observed fixture screens', () => {
    for (const contract of charEditContracts) {
        const screens = syntheticCharEditScreens(contract.caseId);
        const replay = replayCharEditPinned(contract, screens);
        assert.equal(replay.pass, true);
        assert.equal(replay.observation.outcome, 'supported');
        assert.equal(replay.assertions[0].expected, contract.pinnedExpected);
        const context = charEditRecordingContext(contract, screens);
        assert.equal(context.cols, 60);
        assert.equal(context.getScrollback().screenLines, 6);
        context.feed(contract.pinnedFeeds[0]);
        assert.equal(context.getCell(0, 0).char, 'A');
        for (const feed of contract.pinnedFeeds.slice(1)) context.feed(feed);
        assert.equal(context.fedCount, contract.pinnedFeeds.length);
        assert.throws(() => context.feed('x'), /differs from the contract/);
        assert.throws(() => charEditRecordingContext(contract, screens).feed(contract.pinnedFeeds[1]), /differs from the contract/);
        assert.throws(() => charEditRecordingContext(contract, screens).getCell(0, 0), /before its fixture/);
        assert.throws(() => charEditRecordingContext(contract, screens).getCursor(), /before its fixture/);
        assert.throws(() => charEditRecordingContext(contract, screens).getText(), /must not read text/);
    }
});

test('the pinned geometry gates: too few columns is the pinned inconclusive shape', () => {
    for (const [contract, cols, note] of [[charEditContracts[0], 7, pinnedNotes[ICH].geometry], [charEditContracts[1], 7, pinnedNotes[DCH].geometry],
        [charEditContracts[2], 5, pinnedNotes[REP].geometry]]) {
        const screens = syntheticCharEditScreens(contract.caseId).map(screen => ({ ...screen, cols }));
        const replay = replayCharEditPinned(contract, screens);
        assert.equal(replay.note, note);
        assert.equal(replay.response, JSON.stringify({ cols }));
        assert.equal(replay.observation.outcome, 'inconclusive');
    }
});

test('tampered pinned results, run logs and identities are refused', () => {
    const evidence = syntheticCharEditEvidence(runId);
    const mutate = edit => { const copy = clone(evidence); edit(copy); return copy; };
    assert.throws(() => build(mutate(copy => { copy.run.cases[0].pinned.pass = false; })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[1].pinned.observation.outcome = 'unsupported'; })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[2].pinned.response = copy.run.cases[2].pinned.response.replace('"Z"]}', '"Q"]}'); })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[0].feeds[2] = 'x'; })), /do not match/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[2].feeds[2] = 'X\x1b[3b'; })), /do not match/);
    assert.throws(() => build(mutate(copy => { copy.run.cases.reverse(); })), /case order differs/);
    assert.throws(() => build(mutate(copy => { copy.run.probeIds = [...copy.run.probeIds].reverse(); })), /does not cover/);
    assert.throws(() => build(mutate(copy => { copy.adapter.id = 'other'; })), /Invalid Character-editing adapter identity/);
    assert.throws(() => build(mutate(copy => { copy.adapter.derivedFromAdapterId = 'ntilde-owned-sgr-closure-grading-v1'; })), /Invalid Character-editing adapter identity/);
    assert.throws(() => build(mutate(copy => { copy.run.adapterId = 'other'; })), /Invalid Character-editing adapter identity/);
    assert.throws(() => buildCharEditGradingReport(evidence.frames, evidence.run, 'd'.repeat(32), evidence.adapter), /not bound to this run/);
});

test('mismatched identities: a wrong recorded barrier, cursor or read kind is refused', () => {
    const evidence = syntheticCharEditEvidence(runId);
    const barrier = clone(evidence);
    barrier.run.cases[0].barriers[3] = '\x1b[?1;2c';
    assert.throws(() => build(barrier), /barrier differs from its read frame/);
    const cursor = clone(evidence);
    cursor.run.cases[0].reads[2].cursor = [0, 3];
    assert.throws(() => build(cursor), /cursor differs from its read frame/);
    const kind = clone(evidence);
    kind.run.cases[1].reads[0].kind = 'text';
    assert.throws(() => build(kind), /Wrong editing\.delete-chars read kind/);
});

test('stale or edited observations (changed screen with an old digest) are refused', () => {
    const evidence = syntheticCharEditEvidence(runId);
    const stale = clone(evidence);
    frameOf(stale, ICH, 'edited').screen.lines[0] = 'ABCDX';
    assert.throws(() => build(stale), /Changed Character-editing snapshot evidence/);
    const reordered = clone(evidence);
    [reordered.frames[3], reordered.frames[4]] = [reordered.frames[4], reordered.frames[3]];
    assert.throws(() => build(reordered), /Changed Character-editing snapshot evidence/);
    const missing = clone(evidence);
    missing.frames.splice(5, 1);
    assert.throws(() => build(missing), /Incomplete Character-editing snapshot sequence/);
});

test('an edited screen with a recomputed digest is caught by the pinned agreement, not the digest', () => {
    const copy = clone(syntheticCharEditEvidence(runId));
    const frame = frameOf(copy, REP, 'repeated');
    frame.screen.lines[0] = 'AXXZ';
    frame.screen.cursorCol = 4;
    refreeze(frame);
    assert.throws(() => build(copy));
});

test('inconsistent flags and details: attribute runs that contradict the stored details are refused', () => {
    const copy = clone(syntheticCharEditEvidence(runId));
    const frame = frameOf(copy, ICH, 'attrBefore');
    frame.screen.attributeRuns[0].flags = 0x0301;
    refreeze(frame);
    assert.throws(() => build(copy));
    const colors = clone(syntheticCharEditEvidence(runId));
    const details = frameOf(colors, DCH, 'attr');
    details.screen.cellDetails[0].fg = 5;
    refreeze(details);
    assert.throws(() => build(colors));
});

test('incomplete restoration is refused', () => {
    const copy = clone(syntheticCharEditEvidence(runId));
    const last = copy.frames.at(-1);
    last.screen.lines[0] = 'left over';
    refreeze(last);
    assert.throws(() => build(copy), /was not restored/);
});

test('DECSCNM on throughout is a stable but unusable run: every case is inconclusive, never graded', () => {
    const everywhere = syntheticCharEditEvidence(runId, 'f'.repeat(64), { ...paletteBinding(), reverseVideo: true });
    assert.deepEqual(build(everywhere).cases.map(item => item.conclusion.outcome), ['inconclusive', 'inconclusive', 'inconclusive']);
    const copy = clone(syntheticCharEditEvidence(runId));
    const frame = frameOf(copy, ICH, 'cursor');
    frame.screen.colorBinding.reverseVideo = true;
    refreeze(frame);
    assert.throws(() => build(copy), /palette or theme change/);
});

test('a theme or palette change during the suite voids the run', () => {
    const copy = clone(syntheticCharEditEvidence(runId));
    const frame = frameOf(copy, REP, 'wide');
    frame.screen.colorBinding.themeName = 'other';
    refreeze(frame);
    assert.throws(() => build(copy), /palette or theme change/);
});

test('contract-version pinning and evidence validation entry', () => {
    const evidence = syntheticCharEditEvidence(runId);
    assert.doesNotThrow(() => validateCharEditEvidence(evidence.frames, evidence.run, evidence.runId, evidence.adapter));
    assert.throws(() => buildCharEditGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter, { contractsVersion: 2 }), /Unknown character-editing contracts revision 2/);
});
