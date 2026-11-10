import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildColEditGradingReport, replayColEditPinned, validateColEditEvidence } from './col-edit-grading.mjs';
import { colEditContracts, colEditSuiteId, colEditContractsVersion, pinnedNotes } from './col-edit-contracts.mjs';
import { expectedFedCount } from './col-edit-collector.mjs';
import { snapshotHash } from './agent-observer.mjs';
import { syntheticColEditEvidence, syntheticColEditReads, syntheticColEditScreens, plainCell, paletteBinding } from './fixtures/col-edit-evidence.mjs';

const SL = 'editing.sl', SR = 'editing.sr', IC = 'editing.decic', DC = 'editing.decdc';
const runId = 'c'.repeat(32);
const evidenceWith = (caseId, edit, binding) => syntheticColEditEvidence(runId, 'f'.repeat(64), binding, id => {
    const reads = syntheticColEditReads(id);
    if (id === caseId && edit) edit(reads);
    return reads;
});
const build = evidence => buildColEditGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
const caseOf = (report, id) => report.cases.find(item => item.caseId === id);
/** A JSON round trip: structuredClone would keep the shared screen and binding objects shared between frames. */
const clone = value => JSON.parse(JSON.stringify(value));
const texts = (reads, role, rows, cursor) => { reads[role] = { ...reads[role], rows: rows.map(line => [...line].map(char => plainCell(char))), ...(cursor ? { cursor } : {}) }; };
/** The frame index of a case's role in the journal: two leading frames, then each case's reads in contract order. */
const frameOf = (caseId, role) => {
    let index = 2;
    for (const contract of colEditContracts) {
        if (contract.caseId === caseId) return index + contract.roles.indexOf(role);
        index += contract.roles.length;
    }
    throw new Error(caseId);
};

test('a complete run rederives all four cases: pinned and contract agree and nothing diverges', () => {
    const report = build(syntheticColEditEvidence(runId));
    assert.equal(report.suiteId, colEditSuiteId);
    assert.equal(report.contractsVersion, colEditContractsVersion);
    assert.deepEqual(report.cases.map(item => [item.caseId, item.pinned.observation.outcome, item.conclusion.outcome]), [
        [SL, 'supported', 'supported'], [SR, 'supported', 'supported'], [IC, 'supported', 'supported'], [DC, 'supported', 'supported']]);
    for (const item of report.cases) {
        assert.equal(item.divergence, undefined);
        assert.equal(item.conclusion.grading, 'pinned-termless-plus-contract-v1');
        assert.equal(item.pinned.assertions[0].expected, colEditContracts.find(c => c.caseId === item.caseId).pinnedExpected);
    }
    // Two leading frames, the reads, one finishing frame: within the shared observer's 64-capture budget.
    assert.equal(report.snapshots.length, 3 + 13 + 13 + 14 + 14);
    assert.ok(report.snapshots.length <= 64);
    assert.match(report.scope, /pixel path never carries a verdict and is not qualified/);
    assert.match(report.scope, /left\/right margins \(DECLRMM\) are not implemented or claimed/);
    assert.match(report.scope, /pending-wrap state after an edit at the last column is observed, never graded/);
});

test('the pinned response carries the before cells, the setup cursor and the after cells each probe reads, as arrays of characters', () => {
    const report = build(syntheticColEditEvidence(runId));
    const [sl, sr, ic, dc] = report.cases.map(item => JSON.parse(item.pinned.response));
    assert.deepEqual(sl.before[0], [...'ABCDEFGHIJK']);
    assert.deepEqual(sl.after[1], [...'LMNOPQRST']);
    assert.deepEqual(sr.after[0], [...'  ABCDEFG']);
    assert.deepEqual(ic.after[2], [...'QR  STUV']);
    assert.deepEqual(dc.before[2], [...'QRSTUVWXab']);
    assert.deepEqual(dc.after[1], [...'IJMNOPQR']);
    for (const state of [sl, sr]) assert.deepEqual(state.setup, { x: 0, y: 0, visible: true });
    for (const state of [ic, dc]) assert.deepEqual(state.setup, { x: 2, y: 1, visible: true });
});

test('a no-op edit grades unsupported by both the pinned probe and the contract', () => {
    for (const id of [SL, SR, IC, DC]) {
        const item = caseOf(build(evidenceWith(id, reads => { reads.edited = { ...reads.setup }; })), id);
        assert.deepEqual([item.pinned.observation.outcome, item.conclusion.outcome], ['unsupported', 'unsupported'], id);
        assert.equal(item.divergence, undefined);
        assert.match(item.conclusion.note, /no-op/);
    }
});

test('an edit of only the cursor row (ICH/DCH semantics) is unsupported and named, with the pinned probe agreeing where it can see it', () => {
    const only = { [SL]: ['CDEFGHIJK', 'JKLMNOPQRST'], [SR]: ['  ABCDEFGHI', 'JKLMNOPQR'] };
    for (const id of [SL, SR]) {
        const item = caseOf(build(evidenceWith(id, reads => texts(reads, 'edited', only[id]))), id);
        assert.deepEqual([item.pinned.observation.outcome, item.conclusion.outcome], ['unsupported', 'unsupported'], id);
        assert.match(item.conclusion.note, /Only the cursor row changed/);
    }
    const ic = caseOf(build(evidenceWith(IC, reads => texts(reads, 'edited', ['ABCDEFGH', 'IJ  KLMNOP', 'QRSTUVWX'], [1, 2]))), IC);
    assert.deepEqual([ic.pinned.observation.outcome, ic.conclusion.outcome], ['unsupported', 'unsupported']);
    assert.match(ic.conclusion.note, /Only the cursor row changed/);
});

test('a drifted cursor, a wrong region, a smeared or misplaced column, lost protection and a lost attribute are contract-stricter divergences', () => {
    const drift = caseOf(build(evidenceWith(SL, reads => { reads.edited = { ...reads.edited, cursor: [0, 5] }; })), SL);
    assert.deepEqual([drift.pinned.observation.outcome, drift.conclusion.outcome, drift.divergence.kind], ['supported', 'unsupported', 'contract-stricter-than-pinned']);
    assert.match(drift.conclusion.note, /cursor drifted/);
    // The region control: rows outside the margins moved too.
    const region = caseOf(build(evidenceWith(IC, reads => { reads.region = { ...syntheticColEditReads(IC).modes, state: { margins: [2, 4] }, cursor: [6, 5] }; })), IC);
    assert.equal(region.divergence.kind, 'contract-stricter-than-pinned');
    assert.match(region.conclusion.note, /region control differs/);
    // The counts control off by one at the right edge (a column that should have been pushed off survived).
    const edge = caseOf(build(evidenceWith(SR, reads => {
        const rows = reads.counts.rows.map(line => line.map(cell => cell.char).join(''));
        rows[0] = rows[0].padEnd(60).slice(0, 59) + 'Z';
        texts(reads, 'counts', rows);
    })), SR);
    assert.match(edge.conclusion.note, /counts control differs/);
    // Protection dropped from the moved ROW cells of the attribute and protection control.
    const unguarded = caseOf(build(evidenceWith(DC, reads => { reads.attr = { ...reads.attr, guarded: reads.attr.guarded.filter(([row]) => row !== 3) }; })), DC);
    assert.equal(unguarded.divergence.kind, 'contract-stricter-than-pinned');
    assert.match(unguarded.conclusion.note, /attributes or protection/);
    // An introduced blank that kept the pen's italic attribute.
    const italic = caseOf(build(evidenceWith(SL, reads => {
        const rows = reads.attr.rows.map(line => [...line]);
        rows[5] = [...rows[5]];
        rows[5][59] = { ...rows[5][59], attrs: ['italic'] };
        reads.attr = { ...reads.attr, rows };
    })), SL);
    assert.match(italic.conclusion.note, /introduced blank carried a visual attribute/);
});

test('an invalid setup is inconclusive in both graders with the pinned note', () => {
    for (const contract of colEditContracts) {
        const notes = pinnedNotes[contract.mode];
        const seed = caseOf(build(evidenceWith(contract.caseId, reads => texts(reads, 'before', ['zzzzzzzzzz']))), contract.caseId);
        assert.deepEqual([seed.pinned.observation.outcome, seed.conclusion.outcome, seed.pinned.note], ['inconclusive', 'inconclusive', notes.seed]);
        const cursor = caseOf(build(evidenceWith(contract.caseId, reads => { reads.setup = { ...reads.setup, cursor: [2, 4] }; })), contract.caseId);
        assert.deepEqual([cursor.pinned.observation.outcome, cursor.pinned.note, cursor.conclusion.outcome], ['inconclusive', notes.cursor, 'inconclusive']);
    }
});

test('an unmeasured control seed or pen reference leaves the contract inconclusive beside a supported pinned verdict', () => {
    const block = caseOf(build(evidenceWith(SR, reads => texts(reads, 'blockBefore', ['ABCDEFGHIJ'], [0, 10]))), SR);
    assert.deepEqual([block.pinned.observation.outcome, block.conclusion.outcome, block.divergence.kind], ['supported', 'inconclusive', 'contract-stricter-than-pinned']);
    assert.match(block.conclusion.note, /block control seed was not measured/);
    const seed = caseOf(build(evidenceWith(IC, reads => { reads.attrBefore = { ...reads.attrBefore, guarded: [] }; })), IC);
    assert.match(seed.conclusion.note, /attribute and protection control seed was not measured/);
    const pen = caseOf(build(evidenceWith(DC, reads => {
        const rows = reads.attr.rows.map(line => [...line]);
        rows[11] = [...rows[11]];
        rows[11][2] = plainCell('P');
        reads.attr = { ...reads.attr, rows };
    })), DC);
    assert.match(pen.conclusion.note, /reference cell printed under the control pen was not measured/);
});

test('the pending-wrap reading is observed, never graded', () => {
    const cleared = caseOf(build(evidenceWith(IC, reads => {
        const rows = reads.pendingWrap.rows.map(line => [...line]);
        rows[7] = [];
        rows[6] = [...rows[6]];
        rows[6][59] = plainCell('w');
        reads.pendingWrap = { ...reads.pendingWrap, rows, cursor: [6, 59] };
    })), IC);
    assert.equal(cleared.conclusion.outcome, 'supported');
    assert.match(cleared.observed.pendingWrap.reading, /^cleared/);
    assert.match(caseOf(build(syntheticColEditEvidence(runId)), IC).observed.pendingWrap.reading, /^kept/);
});

test('the pinned replay equals the live pinned shapes over the observed fixture screens', () => {
    for (const contract of colEditContracts) {
        const replay = replayColEditPinned(contract, syntheticColEditScreens(contract.caseId));
        assert.deepEqual(Object.keys(replay), ['pass', 'response', 'observation', 'assertions']);
        assert.equal(replay.pass, true);
        assert.deepEqual(replay.observation, { outcome: 'supported', evidence: 'parser-state' });
        assert.deepEqual(replay.assertions, [{ kind: 'positive', expected: contract.pinnedExpected, observed: replay.response }]);
        const unsupported = replayColEditPinned(contract, syntheticColEditScreens(contract.caseId, undefined, id => {
            const reads = syntheticColEditReads(id); reads.edited = { ...reads.setup }; return reads; }));
        assert.deepEqual(unsupported.assertions.map(item => item.kind), ['negative']);
        assert.deepEqual(unsupported.observation, { outcome: 'unsupported', evidence: 'parser-state' });
    }
});

test('the pinned geometry gate: too few columns or screen lines is the pinned inconclusive shape', () => {
    for (const contract of colEditContracts) {
        const atCursor = contract.mode === 'insert' || contract.mode === 'delete';
        const screens = syntheticColEditScreens(contract.caseId);
        const narrow = atCursor ? 7 : 8, short = atCursor ? 2 : 1;
        const replay = replayColEditPinned(contract, screens.map(screen => ({ ...screen, cols: narrow })));
        assert.equal(replay.note, pinnedNotes[contract.mode].geometry);
        assert.equal(replay.response, JSON.stringify({ cols: narrow, rows: 13 }));
        assert.equal(replay.observation.outcome, 'inconclusive');
        assert.equal(replay.assertions, undefined);
        assert.equal(replayColEditPinned(contract, screens.map(screen => ({ ...screen, rows: short }))).observation.outcome, 'inconclusive');
    }
});

test('tampered pinned results, run logs and identities are refused', () => {
    const evidence = syntheticColEditEvidence(runId);
    const mutate = edit => { const copy = clone(evidence); edit(copy); return copy; };
    assert.throws(() => build(mutate(copy => { copy.run.cases[0].pinned.pass = false; })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[1].pinned.observation.outcome = 'unsupported'; })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[2].pinned.response = copy.run.cases[2].pinned.response.replace('"A"', '"Z"'); })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[3].feeds[2] = 'x'; })), /do not match/);
    assert.throws(() => build(mutate(copy => { copy.run.cases.reverse(); })), /case order differs/);
    assert.throws(() => build(mutate(copy => { copy.run.probeIds = [...copy.run.probeIds].reverse(); })), /does not cover/);
    assert.throws(() => build(mutate(copy => { copy.adapter.id = 'other'; })), /Invalid Column-editing adapter identity/);
    assert.throws(() => build(mutate(copy => { copy.adapter.derivedFromAdapterId = 'ntilde-owned-selective-erase-grading-v1'; })), /Invalid Column-editing adapter identity/);
    assert.throws(() => build(mutate(copy => { copy.run.adapterId = 'other'; })), /Invalid Column-editing adapter identity/);
    assert.throws(() => buildColEditGradingReport(evidence.frames, evidence.run, 'd'.repeat(32), evidence.adapter), /not bound to this run/);
});

test('wrong fixture bytes are refused: a pinned feed substituted for a convenient equivalent', () => {
    const evidence = syntheticColEditEvidence(runId);
    for (const [index, sequence] of [[0, '\x1b[2@'], [1, '\x1b[2A'], [2, '\x1b[2;1\'}'], [3, '\x1b[2P']]) {
        const copy = clone(evidence);
        const feeds = copy.run.cases[index].feeds;
        feeds[feeds.indexOf(colEditContracts[index].sequence)] = sequence;
        assert.throws(() => build(copy), /do not match/);
    }
});

test('mismatched identities: a wrong recorded barrier, cursor or read kind is refused', () => {
    const evidence = syntheticColEditEvidence(runId);
    const barrier = clone(evidence);
    barrier.run.cases[0].barriers[4] = '\x1b[?1;2c';
    assert.throws(() => build(barrier), /barrier differs from its read frame/);
    const cursor = clone(evidence);
    cursor.run.cases[0].reads[2].cursor = [0, 3];
    assert.throws(() => build(cursor), /cursor differs from its read frame/);
    const kind = clone(evidence);
    kind.run.cases[1].reads[0].kind = 'text';
    assert.throws(() => build(kind), /Wrong editing\.sr read kind/);
});

test('stale or edited observations (changed screen with an old digest) are refused', () => {
    const evidence = syntheticColEditEvidence(runId);
    const stale = clone(evidence);
    stale.frames[frameOf(SL, 'before')].screen.lines[0] = 'ABCDEFGHIJX';
    assert.throws(() => build(stale), /Changed Column-editing snapshot evidence/);
    const reordered = clone(evidence);
    [reordered.frames[3], reordered.frames[4]] = [reordered.frames[4], reordered.frames[3]];
    assert.throws(() => build(reordered), /Changed Column-editing snapshot evidence/);
    const missing = clone(evidence);
    missing.frames.splice(5, 1);
    assert.throws(() => build(missing), /Incomplete Column-editing snapshot sequence/);
});

test('an edited screen with a recomputed digest is caught by the pinned agreement, not the digest', () => {
    const copy = clone(syntheticColEditEvidence(runId));
    const frame = copy.frames[frameOf(IC, 'edited')];
    frame.screen.lines[1] = 'IJ  KLMQ';
    frame.sha256 = snapshotHash(frame.screen);
    assert.throws(() => build(copy), /does not match its observed evidence/);
});

test('a stored margin, origin or insert flag rewritten with a recomputed digest changes the conclusion, never the digest check', () => {
    const copy = clone(syntheticColEditEvidence(runId));
    const frame = copy.frames[frameOf(DC, 'region')];
    frame.screen.bufferState.scrollTop = 0;
    frame.screen.bufferState.scrollBottom = 12;
    frame.sha256 = snapshotHash(frame.screen);
    assert.equal(caseOf(build(copy), DC).conclusion.outcome, 'inconclusive');
    const insert = clone(syntheticColEditEvidence(runId));
    const modes = insert.frames[frameOf(SL, 'modes')];
    modes.screen.bufferState.insertMode = false;
    modes.sha256 = snapshotHash(modes.screen);
    assert.match(caseOf(build(insert), SL).conclusion.note, /differ from the contracted setup in the modes read/);
});

test('a missing per-cell protection state is refused', () => {
    const copy = clone(syntheticColEditEvidence(runId));
    for (const run of copy.frames[4].screen.cellDetails) delete run.protected;
    copy.frames[4].sha256 = snapshotHash(copy.frames[4].screen);
    assert.throws(() => build(copy));
});

test('inconsistent flags and details: attribute runs that contradict the stored details are refused', () => {
    const evidence = syntheticColEditEvidence(runId);
    const copy = clone(evidence);
    copy.frames[4].screen.attributeRuns[0].flags = 0x0301;
    copy.frames[4].sha256 = snapshotHash(copy.frames[4].screen);
    assert.throws(() => build(copy));
});

test('incomplete restoration is refused', () => {
    const copy = clone(syntheticColEditEvidence(runId));
    const last = copy.frames.at(-1);
    last.screen.lines[0] = 'left over';
    last.sha256 = snapshotHash(last.screen);
    assert.throws(() => build(copy), /was not restored/);
    const margins = clone(syntheticColEditEvidence(runId));
    const finish = margins.frames.at(-1);
    finish.screen.bufferState.scrollTop = 2;
    finish.sha256 = snapshotHash(finish.screen);
    assert.throws(() => build(margins), /was not restored/);
});

test('DECSCNM on throughout is a stable but unusable run: every case is inconclusive, never graded', () => {
    const everywhere = syntheticColEditEvidence(runId, 'f'.repeat(64), { ...paletteBinding(), reverseVideo: true });
    assert.deepEqual(build(everywhere).cases.map(item => item.conclusion.outcome), ['inconclusive', 'inconclusive', 'inconclusive', 'inconclusive']);
    const theme = clone(syntheticColEditEvidence(runId));
    theme.frames[4].screen.colorBinding.themeName = 'other';
    theme.frames[4].sha256 = snapshotHash(theme.frames[4].screen);
    assert.throws(() => build(theme), /palette or theme change/);
});

test('contract-version pinning and evidence validation entry', () => {
    const evidence = syntheticColEditEvidence(runId);
    assert.doesNotThrow(() => validateColEditEvidence(evidence.frames, evidence.run, evidence.runId, evidence.adapter));
    assert.throws(() => buildColEditGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter, { contractsVersion: 2 }), /Unknown column-edit contracts revision 2/);
});

test('the pinned replay may stop early only where the pinned probe does: geometry, unread seed and wrong setup cursor', () => {
    for (const contract of colEditContracts) {
        const notes = pinnedNotes[contract.mode];
        const screens = syntheticColEditScreens(contract.caseId);
        assert.equal(expectedFedCount(contract, replayColEditPinned(contract, screens)), 3);
        const seed = replayColEditPinned(contract, syntheticColEditScreens(contract.caseId, undefined, id => {
            const reads = syntheticColEditReads(id); texts(reads, 'before', ['zzzzzzzzzz']); return reads; }));
        assert.equal(seed.note, notes.seed);
        assert.equal(expectedFedCount(contract, seed), 1);
        const cursor = replayColEditPinned(contract, syntheticColEditScreens(contract.caseId, undefined, id => {
            const reads = syntheticColEditReads(id); reads.setup = { ...reads.setup, cursor: [2, 4] }; return reads; }));
        assert.equal(expectedFedCount(contract, cursor), 2);
        assert.equal(expectedFedCount(contract, replayColEditPinned(contract, screens.map(screen => ({ ...screen, cols: 7 })))), 0);
    }
});
