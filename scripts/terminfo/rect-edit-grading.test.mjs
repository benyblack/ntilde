import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildRectEditGradingReport, replayRectEditPinned, validateRectEditEvidence } from './rect-edit-grading.mjs';
import { rectEditContracts, rectEditSuiteId, rectEditContractsVersion, pinnedNotes } from './rect-edit-contracts.mjs';
import { expectedFedCount } from './rect-edit-collector.mjs';
import { snapshotHash } from './agent-observer.mjs';
import { syntheticRectEditEvidence, syntheticRectEditReads, syntheticRectEditScreens, rowsOfText, rowOf, paletteBinding } from './fixtures/rect-edit-evidence.mjs';

const FRA = 'editing.decfra', ERA = 'editing.decera', CRA = 'editing.deccra';
const runId = 'c'.repeat(32);
const evidenceWith = (caseId, edit, binding) => syntheticRectEditEvidence(runId, 'f'.repeat(64), binding, id => {
    const reads = syntheticRectEditReads(id);
    if (id === caseId && edit) edit(reads);
    return reads;
});
const build = evidence => buildRectEditGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
/** A JSON round trip: structuredClone would keep the shared screen and binding objects shared between frames. */
const clone = value => JSON.parse(JSON.stringify(value));
const texts = (reads, role, rows, cursor) => { reads[role] = { ...reads[role], rows: rowsOfText(rows), ...(cursor ? { cursor } : {}) }; };
/** The frame index of a case's role in the journal: two leading frames, then each case's reads in contract order. */
const frameOf = (caseId, role) => {
    let index = 2;
    for (const contract of rectEditContracts) {
        if (contract.caseId === caseId) return index + contract.roles.indexOf(role);
        index += contract.roles.length;
    }
    throw new Error(caseId);
};

test('a complete run rederives all three cases: pinned and contract agree and nothing diverges', () => {
    const report = build(syntheticRectEditEvidence(runId));
    assert.equal(report.suiteId, rectEditSuiteId);
    assert.equal(report.contractsVersion, rectEditContractsVersion);
    assert.deepEqual(report.cases.map(item => [item.caseId, item.pinned.observation.outcome, item.conclusion.outcome]), [
        [FRA, 'supported', 'supported'], [ERA, 'supported', 'supported'], [CRA, 'supported', 'supported']]);
    for (const item of report.cases) {
        assert.equal(item.divergence, undefined);
        assert.equal(item.conclusion.grading, 'pinned-termless-plus-contract-v1');
        assert.equal(item.pinned.assertions[0].expected, rectEditContracts.find(c => c.caseId === item.caseId).pinnedExpected);
    }
    assert.equal(report.snapshots.length, 3 + 17 + 16 + 20);
    assert.match(report.scope, /pixel path never carries a verdict and is not qualified/);
    assert.match(report.scope, /no Unicode, wide-cell, scaled-text, image-plane, protected-cell, DECSERA/);
});

test('the pinned response carries the before cells, the setup cursor and the after rows each probe reads, in its own shape', () => {
    const report = build(syntheticRectEditEvidence(runId));
    const [fra, era, cra] = report.cases.map(item => JSON.parse(item.pinned.response));
    assert.deepEqual(fra.before, ['aaaaaZ', 'bbbbbY', 'cccccW']);
    assert.deepEqual(fra.after, ['XXXXXZ', 'XXXXXY', 'XXXXXW']);
    assert.deepEqual(era.before[0], ['a', 'a', 'a', 'a', 'a', 'Z']);
    assert.deepEqual(era.after[2], [' ', ' ', ' ', ' ', ' ', 'X']);
    assert.equal(cra.before.length, 6);
    assert.equal(cra.after[4], '.........ABCDE');
    for (const state of [fra, era, cra]) assert.deepEqual(state.setup, { x: 0, y: 0, visible: true });
});

test('a no-op edit grades unsupported by both the pinned probe and the contract', () => {
    for (const id of [FRA, ERA, CRA]) {
        const report = build(evidenceWith(id, reads => { reads.edited = { ...reads.setup }; }));
        const item = report.cases.find(entry => entry.caseId === id);
        assert.deepEqual([item.pinned.observation.outcome, item.conclusion.outcome], ['unsupported', 'unsupported'], id);
        assert.equal(item.divergence, undefined);
        assert.match(item.conclusion.note, /no-op/);
    }
});

test('a drifted cursor, a smeared overlap and a lost attribute are recorded contract-stricter divergences from a supported pinned verdict', () => {
    const drift = build(evidenceWith(FRA, reads => { reads.edited = { ...reads.edited, cursor: [0, 5] }; })).cases[0];
    assert.equal(drift.pinned.observation.outcome, 'supported');
    assert.equal(drift.conclusion.outcome, 'unsupported');
    assert.equal(drift.divergence.kind, 'contract-stricter-than-pinned');
    const smear = build(evidenceWith(CRA, reads => { texts(reads, 'right', ['ABABABABIJ', 'KLMNOPQRST', 'UVWXYZabcd', 'efghijklmn', 'opqrstuvwx', 'yz01234567', '89!@#$%^&*']); })).cases[2];
    assert.equal(smear.pinned.observation.outcome, 'supported');
    assert.equal(smear.divergence.kind, 'contract-stricter-than-pinned');
    assert.match(smear.conclusion.note, /right control differs/);
    const outside = build(evidenceWith(ERA, reads => { texts(reads, 'box', ['ABCDEFGHIJ', 'KL    QRST', 'UV    abcd', 'ef    klmn', 'opqrstuvwx', 'yz01234567', '89!@#$%^&*'.replace('*', '+')]); })).cases[1];
    assert.equal(outside.divergence.kind, 'contract-stricter-than-pinned');
});

test('an invalid setup is inconclusive in both graders with the pinned note', () => {
    for (const contract of rectEditContracts) {
        const notes = pinnedNotes[contract.mode];
        const seed = build(evidenceWith(contract.caseId, reads => texts(reads, 'before', ['zzzzzz']))).cases.find(item => item.caseId === contract.caseId);
        assert.deepEqual([seed.pinned.observation.outcome, seed.conclusion.outcome, seed.pinned.note], ['inconclusive', 'inconclusive', notes.seed]);
        const cursor = build(evidenceWith(contract.caseId, reads => { reads.setup = { ...reads.setup, cursor: [1, 3] }; })).cases.find(item => item.caseId === contract.caseId);
        assert.deepEqual([cursor.pinned.observation.outcome, cursor.pinned.note], ['inconclusive', notes.cursor]);
        assert.equal(cursor.conclusion.outcome, 'inconclusive');
    }
});

test('a pinned unsupported over an invalid control is recorded as the contract being more cautious', () => {
    const report = build(evidenceWith(FRA, reads => { reads.attr = { ...reads.attr, rows: [...reads.attr.rows.slice(0, 11), [{ ...reads.attr.rows[11][0], char: 'Q' }]] }; }));
    const item = report.cases[0];
    assert.equal(item.pinned.observation.outcome, 'supported');
    assert.equal(item.conclusion.outcome, 'inconclusive');
    assert.equal(item.divergence.kind, 'contract-stricter-than-pinned');
});

test('the pinned replay equals the live pinned shapes over the observed fixture screens', () => {
    for (const contract of rectEditContracts) {
        const replay = replayRectEditPinned(contract, syntheticRectEditScreens(contract.caseId));
        assert.deepEqual(Object.keys(replay), ['pass', 'response', 'observation', 'assertions']);
        assert.equal(replay.pass, true);
        assert.deepEqual(replay.observation, { outcome: 'supported', evidence: 'parser-state' });
        assert.deepEqual(replay.assertions, [{ kind: 'positive', expected: contract.pinnedExpected, observed: replay.response }]);
        const unsupported = replayRectEditPinned(contract, syntheticRectEditScreens(contract.caseId, undefined, id => {
            const reads = syntheticRectEditReads(id); reads.edited = { ...reads.setup }; return reads; }));
        assert.deepEqual(unsupported.assertions.map(item => item.kind), ['negative']);
        assert.deepEqual(unsupported.observation, { outcome: 'unsupported', evidence: 'parser-state' });
    }
});

test('the pinned geometry gate: too few columns or screen lines is the pinned inconclusive shape', () => {
    for (const contract of rectEditContracts) {
        const screens = syntheticRectEditScreens(contract.caseId);
        const narrow = contract.mode === 'copy' ? 13 : 5, short = contract.mode === 'copy' ? 5 : 2;
        const replay = replayRectEditPinned(contract, screens.map(screen => ({ ...screen, cols: narrow })));
        assert.equal(replay.note, pinnedNotes[contract.mode].geometry);
        assert.equal(replay.response, JSON.stringify({ cols: narrow, rows: 12 }));
        assert.equal(replay.observation.outcome, 'inconclusive');
        assert.equal(replay.assertions, undefined);
        assert.equal(replayRectEditPinned(contract, screens.map(screen => ({ ...screen, rows: short }))).observation.outcome, 'inconclusive');
    }
});

test('tampered pinned results, run logs and identities are refused', () => {
    const evidence = syntheticRectEditEvidence(runId);
    const mutate = edit => { const copy = clone(evidence); edit(copy); return copy; };
    assert.throws(() => build(mutate(copy => { copy.run.cases[0].pinned.pass = false; })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[1].pinned.observation.outcome = 'unsupported'; })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[2].pinned.response = copy.run.cases[2].pinned.response.replace('ABCDE', 'ABCDX'); })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[0].feeds[2] = 'x'; })), /do not match/);
    assert.throws(() => build(mutate(copy => { copy.run.cases.reverse(); })), /case order differs/);
    assert.throws(() => build(mutate(copy => { copy.run.probeIds = [...copy.run.probeIds].reverse(); })), /does not cover/);
    assert.throws(() => build(mutate(copy => { copy.adapter.id = 'other'; })), /Invalid Rectangle-editing adapter identity/);
    assert.throws(() => build(mutate(copy => { copy.adapter.derivedFromAdapterId = 'ntilde-owned-saved-reset-grading-v1'; })), /Invalid Rectangle-editing adapter identity/);
    assert.throws(() => build(mutate(copy => { copy.run.adapterId = 'other'; })), /Invalid Rectangle-editing adapter identity/);
    assert.throws(() => buildRectEditGradingReport(evidence.frames, evidence.run, 'd'.repeat(32), evidence.adapter), /not bound to this run/);
});

test('wrong fixture bytes are refused: a pinned feed substituted for a convenient equivalent', () => {
    const evidence = syntheticRectEditEvidence(runId);
    for (const [index, sequence] of [[0, '\x1b[88;1;1;3;5$X'], [1, '\x1b[1;1;3;4$z'], [2, '\x1b[1;1;2;5;1;5;10;1$v']]) {
        const copy = clone(evidence);
        const feeds = copy.run.cases[index].feeds;
        feeds[feeds.indexOf(rectEditContracts[index].sequence)] = sequence;
        assert.throws(() => build(copy), /do not match/);
    }
});

test('mismatched identities: a wrong recorded barrier, cursor or read kind is refused', () => {
    const evidence = syntheticRectEditEvidence(runId);
    const barrier = clone(evidence);
    barrier.run.cases[0].barriers[4] = '\x1b[?1;2c';
    assert.throws(() => build(barrier), /barrier differs from its read frame/);
    const cursor = clone(evidence);
    cursor.run.cases[0].reads[2].cursor = [0, 3];
    assert.throws(() => build(cursor), /cursor differs from its read frame/);
    const kind = clone(evidence);
    kind.run.cases[1].reads[0].kind = 'text';
    assert.throws(() => build(kind), /Wrong editing\.decera read kind/);
});

test('stale or edited observations (changed screen with an old digest) are refused', () => {
    const evidence = syntheticRectEditEvidence(runId);
    const stale = clone(evidence);
    stale.frames[frameOf(FRA, 'before')].screen.lines[0] = 'aaaaX';
    assert.throws(() => build(stale), /Changed Rectangle-editing snapshot evidence/);
    const reordered = clone(evidence);
    [reordered.frames[3], reordered.frames[4]] = [reordered.frames[4], reordered.frames[3]];
    assert.throws(() => build(reordered), /Changed Rectangle-editing snapshot evidence/);
    const missing = clone(evidence);
    missing.frames.splice(5, 1);
    assert.throws(() => build(missing), /Incomplete Rectangle-editing snapshot sequence/);
});

test('an edited screen with a recomputed digest is caught by the pinned agreement, not the digest', () => {
    const copy = clone(syntheticRectEditEvidence(runId));
    const frame = copy.frames[frameOf(FRA, 'edited')];
    frame.screen.lines[1] = 'XXXXXQ';
    frame.sha256 = snapshotHash(frame.screen);
    assert.throws(() => build(copy), /does not match its observed evidence/);
});

test('a stored margin, origin or insert flag rewritten with a recomputed digest changes the conclusion, never the digest check', () => {
    const copy = clone(syntheticRectEditEvidence(runId));
    const frame = copy.frames[frameOf(FRA, 'margins')];
    frame.screen.bufferState.scrollTop = 0;
    frame.screen.bufferState.scrollBottom = 11;
    frame.sha256 = snapshotHash(frame.screen);
    assert.equal(build(copy).cases[0].conclusion.outcome, 'inconclusive');
    const insert = clone(syntheticRectEditEvidence(runId));
    const edited = insert.frames[frameOf(CRA, 'edited')];
    edited.screen.bufferState.insertMode = true;
    edited.sha256 = snapshotHash(edited.screen);
    assert.equal(build(insert).cases[2].conclusion.outcome, 'inconclusive');
});

test('a missing or malformed stored buffer state is refused', () => {
    const copy = clone(syntheticRectEditEvidence(runId));
    delete copy.frames[4].screen.bufferState;
    copy.frames[4].sha256 = snapshotHash(copy.frames[4].screen);
    assert.throws(() => build(copy));
    const range = clone(syntheticRectEditEvidence(runId));
    range.frames[4].screen.bufferState.scrollBottom = 99;
    range.frames[4].sha256 = snapshotHash(range.frames[4].screen);
    assert.throws(() => build(range), /Missing or invalid observed editing state/);
});

test('inconsistent flags and details: attribute runs that contradict the stored details are refused', () => {
    const evidence = syntheticRectEditEvidence(runId);
    const copy = clone(evidence);
    const frame = copy.frames[4];
    frame.screen.attributeRuns[0].flags = 0x0301;
    frame.sha256 = snapshotHash(frame.screen);
    assert.throws(() => build(copy));
    const colors = clone(evidence);
    colors.frames[4].screen.cellDetails[0].fg = 5;
    colors.frames[4].sha256 = snapshotHash(colors.frames[4].screen);
    assert.throws(() => build(colors));
});

test('incomplete restoration is refused', () => {
    const copy = clone(syntheticRectEditEvidence(runId));
    const last = copy.frames.at(-1);
    last.screen.lines[0] = 'left over';
    last.sha256 = snapshotHash(last.screen);
    assert.throws(() => build(copy), /was not restored/);
    const margins = clone(syntheticRectEditEvidence(runId));
    const finish = margins.frames.at(-1);
    finish.screen.bufferState.scrollTop = 2;
    finish.sha256 = snapshotHash(finish.screen);
    assert.throws(() => build(margins), /was not restored/);
});

test('DECSCNM on throughout is a stable but unusable run: every case is inconclusive, never graded', () => {
    const everywhere = syntheticRectEditEvidence(runId, 'f'.repeat(64), { ...paletteBinding(), reverseVideo: true });
    assert.deepEqual(build(everywhere).cases.map(item => item.conclusion.outcome), ['inconclusive', 'inconclusive', 'inconclusive']);
    const copy = clone(syntheticRectEditEvidence(runId));
    copy.frames[4].screen.colorBinding.reverseVideo = true;
    copy.frames[4].sha256 = snapshotHash(copy.frames[4].screen);
    assert.throws(() => build(copy), /palette or theme change/);
});

test('a theme or palette change during the suite voids the run', () => {
    const theme = clone(syntheticRectEditEvidence(runId));
    theme.frames[4].screen.colorBinding.themeName = 'other';
    theme.frames[4].sha256 = snapshotHash(theme.frames[4].screen);
    assert.throws(() => build(theme), /palette or theme change/);
});

test('contract-version pinning and evidence validation entry', () => {
    const evidence = syntheticRectEditEvidence(runId);
    assert.doesNotThrow(() => validateRectEditEvidence(evidence.frames, evidence.run, evidence.runId, evidence.adapter));
    assert.throws(() => buildRectEditGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter, { contractsVersion: 2 }), /Unknown rectangle-edit contracts revision 2/);
});

test('rowOf keeps spaces as plain blank cells so fixture rows are exactly their text', () => {
    assert.deepEqual(rowOf('A B').map(cell => cell.char), ['A', ' ', 'B']);
});

test('the pinned replay may stop early only where the pinned probe does: geometry, unread seed and wrong setup cursor', () => {
    for (const contract of rectEditContracts) {
        const notes = pinnedNotes[contract.mode];
        const screens = syntheticRectEditScreens(contract.caseId);
        assert.equal(expectedFedCount(contract, replayRectEditPinned(contract, screens)), 3);
        const seed = replayRectEditPinned(contract, syntheticRectEditScreens(contract.caseId, undefined, id => {
            const reads = syntheticRectEditReads(id); texts(reads, 'before', ['zzzzzz']); return reads; }));
        assert.equal(seed.note, notes.seed);
        assert.equal(expectedFedCount(contract, seed), 1);
        const cursor = replayRectEditPinned(contract, syntheticRectEditScreens(contract.caseId, undefined, id => {
            const reads = syntheticRectEditReads(id); reads.setup = { ...reads.setup, cursor: [1, 3] }; return reads; }));
        assert.equal(expectedFedCount(contract, cursor), 2);
        assert.equal(expectedFedCount(contract, replayRectEditPinned(contract, screens.map(screen => ({ ...screen, cols: 5 })))), 0);
        // A flank (or source) control failing is inconclusive after all three feeds.
        const control = replayRectEditPinned(contract, syntheticRectEditScreens(contract.caseId, undefined, id => {
            const reads = syntheticRectEditReads(id);
            texts(reads, 'edited', contract.mode === 'copy' ? ['ZZZZZ.........'] : ['XXXXXQ', 'XXXXXY', 'XXXXXW']); return reads; }));
        assert.equal(control.note, notes.controls);
        assert.equal(expectedFedCount(contract, control), 3);
    }
});
