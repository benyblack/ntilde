import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildLineEditGradingReport, replayLineEditPinned, validateLineEditEvidence } from './line-edit-grading.mjs';
import { lineEditContracts, lineEditSuiteId, lineEditContractsVersion, pinnedNotes } from './line-edit-contracts.mjs';
import { lineEditRecordingContext, expectedFedCount } from './line-edit-collector.mjs';
import { snapshotHash } from './agent-observer.mjs';
import { syntheticLineEditEvidence, syntheticLineEditReads, syntheticLineEditScreens, rowsOfText, rowOf,
    paletteBinding } from './fixtures/line-edit-evidence.mjs';

const IL = 'editing.insert-lines', DL = 'editing.delete-lines';
const runId = 'c'.repeat(32);
const evidenceWith = (caseId, edit, binding) => syntheticLineEditEvidence(runId, 'f'.repeat(64), binding, id => {
    const reads = syntheticLineEditReads(id);
    if (id === caseId && edit) edit(reads);
    return reads;
});
const build = evidence => buildLineEditGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
/** A JSON round trip: structuredClone would keep the shared screen and binding objects shared between frames. */
const clone = value => JSON.parse(JSON.stringify(value));
const texts = (reads, role, rows, cursor) => { reads[role] = { ...reads[role], rows: rowsOfText(rows), ...(cursor ? { cursor } : {}) }; };

test('a complete run rederives both cases: pinned and contract agree and nothing diverges', () => {
    const report = build(syntheticLineEditEvidence(runId));
    assert.equal(report.suiteId, lineEditSuiteId);
    assert.equal(report.contractsVersion, lineEditContractsVersion);
    assert.deepEqual(report.cases.map(item => [item.caseId, item.pinned.observation.outcome, item.conclusion.outcome]), [
        [IL, 'supported', 'supported'], [DL, 'supported', 'supported']]);
    for (const item of report.cases) {
        assert.equal(item.divergence, undefined);
        assert.equal(item.conclusion.grading, 'pinned-termless-plus-contract-v1');
        assert.equal(item.pinned.assertions[0].expected, lineEditContracts.find(c => c.caseId === item.caseId).pinnedExpected);
    }
    assert.equal(report.snapshots.length, 3 + 2 * 20);
    assert.match(report.scope, /pixel path never carries a verdict and is not qualified/);
});

test('the pinned response carries the before cells, the setup cursor and the after rows each probe reads', () => {
    const [il, dl] = build(syntheticLineEditEvidence(runId)).cases;
    const cells = text => [...text];
    const before = [cells('AAAAA'), cells('BBBBB'), cells('CCCCC'), cells('DDDDD')];
    const setup = { x: 0, y: 1, visible: true };
    assert.equal(il.pinned.response, JSON.stringify({ before, setup, after: [cells('AAAAA'), cells('     '), cells('BBBBB'), cells('CCCCC')] }));
    assert.equal(dl.pinned.response, JSON.stringify({ before, setup, after: [cells('AAAAA'), cells('CCCCC'), cells('DDDDD')] }));
});

test('a no-op edit grades unsupported by both the pinned probe and the contract', () => {
    for (const [index, id] of [IL, DL].entries()) {
        const item = build(evidenceWith(id, reads => { reads.edited = reads.setup; })).cases[index];
        assert.equal(item.pinned.observation.outcome, 'unsupported');
        assert.equal(item.conclusion.outcome, 'unsupported');
        assert.equal(item.divergence, undefined);
    }
});

test('reversed movement grades unsupported by both graders', () => {
    const il = build(evidenceWith(IL, reads => texts(reads, 'edited', ['AAAAA', 'CCCCC', 'DDDDD']))).cases[0];
    assert.deepEqual([il.pinned.observation.outcome, il.conclusion.outcome, il.divergence], ['unsupported', 'unsupported', undefined]);
    const dl = build(evidenceWith(DL, reads => texts(reads, 'edited', ['AAAAA', '', 'BBBBB', 'CCCCC', 'DDDDD']))).cases[1];
    assert.deepEqual([dl.pinned.observation.outcome, dl.conclusion.outcome, dl.divergence], ['unsupported', 'unsupported', undefined]);
});

test('a drifted cursor is a recorded contract-stricter divergence from a supported pinned verdict', () => {
    for (const [index, id] of [IL, DL].entries()) {
        const item = build(evidenceWith(id, reads => { reads.edited = { ...reads.edited, cursor: [4, 5] }; })).cases[index];
        assert.equal(item.pinned.observation.outcome, 'supported');
        assert.equal(item.conclusion.outcome, 'unsupported');
        assert.deepEqual(item.divergence, { pinnedOutcome: 'supported', contractOutcome: 'unsupported', kind: 'contract-stricter-than-pinned' });
    }
});

test('damage the pinned probe cannot see (the discarded row, the bottom blank row, regions, attributes) is a recorded stricter divergence', () => {
    // The pinned IL reads rows 1-4 only: the row the insert pushes to row 5 is invisible to it.
    const lost = build(evidenceWith(IL, reads => texts(reads, 'edited', ['AAAAA', '', 'BBBBB', 'CCCCC']))).cases[0];
    assert.equal(lost.pinned.observation.outcome, 'supported');
    assert.equal(lost.divergence.kind, 'contract-stricter-than-pinned');
    // The pinned DL reads rows 1-3 only: a stray fourth row is invisible to it.
    const stray = build(evidenceWith(DL, reads => texts(reads, 'edited', ['AAAAA', 'CCCCC', 'DDDDD', 'DDDDD']))).cases[1];
    assert.equal(stray.pinned.observation.outcome, 'supported');
    assert.equal(stray.divergence.kind, 'contract-stricter-than-pinned');
    const region = build(evidenceWith(IL, reads => texts(reads, 'region', ['AAAAAAAAAA', 'BBBBBBBBBB', 'CCCCCCCCCC', '', 'DDDDDDDDDD', 'EEEEEEEEEE', 'FFFFFFFFFF', 'GGGGGGGGGG']))).cases[0];
    assert.equal(region.pinned.observation.outcome, 'supported');
    assert.equal(region.conclusion.outcome, 'unsupported');
    const attrs = build(evidenceWith(DL, reads => { reads.attr = { ...reads.attr, rows: reads.attr.rows.map((row, index) => (index === 1 ? rowOf('RED-ON-BLU') : row)) }; })).cases[1];
    assert.equal(attrs.pinned.observation.outcome, 'supported');
    assert.equal(attrs.conclusion.outcome, 'unsupported');
});

test('an invalid setup is inconclusive in both graders with the pinned note', () => {
    for (const [index, id] of [IL, DL].entries()) {
        const notes = pinnedNotes[id === IL ? 'insert' : 'delete'];
        const cursor = build(evidenceWith(id, reads => { reads.setup = { ...reads.setup, cursor: [1, 3] }; })).cases[index];
        assert.equal(cursor.pinned.observation.outcome, 'inconclusive');
        assert.equal(cursor.conclusion.outcome, 'inconclusive');
        assert.equal(cursor.pinned.note, notes.cursor);
        const seed = build(evidenceWith(id, reads => texts(reads, 'before', ['AAAAA', 'BBBBB', 'CCCCC', 'DDDDX']))).cases[index];
        assert.equal(seed.pinned.note, notes.seed);
        assert.equal(seed.conclusion.outcome, 'inconclusive');
        // The first-row control changing during the pinned edit is inconclusive in the pinned probe, and the contract grades unsupported.
        const control = build(evidenceWith(id, reads => texts(reads, 'edited', ['ZZZZZ', 'CCCCC', 'DDDDD']))).cases[index];
        assert.equal(control.pinned.note, notes.controls);
        assert.deepEqual([control.pinned.observation.outcome, control.conclusion.outcome, control.divergence.kind], ['inconclusive', 'unsupported', 'contract-measured-mismatch']);
    }
});

test('a pinned unsupported over an invalid control is recorded as the contract being more cautious', () => {
    const report = build(evidenceWith(IL, reads => {
        texts(reads, 'edited', ['AAAAA', 'CCCCC', 'DDDDD']);
        reads.regionBefore = { ...reads.regionBefore, state: {} };
    }));
    const item = report.cases[0];
    assert.equal(item.pinned.observation.outcome, 'unsupported');
    assert.equal(item.conclusion.outcome, 'inconclusive');
    assert.deepEqual(item.divergence, { pinnedOutcome: 'unsupported', contractOutcome: 'inconclusive', kind: 'contract-more-cautious-than-pinned' });
});

test('the pinned replay equals the live pinned shapes over the observed fixture screens', () => {
    for (const contract of lineEditContracts) {
        const screens = syntheticLineEditScreens(contract.caseId);
        const replay = replayLineEditPinned(contract, screens);
        assert.equal(replay.pass, true);
        assert.equal(replay.observation.outcome, 'supported');
        assert.equal(replay.assertions[0].expected, contract.pinnedExpected);
        const context = lineEditRecordingContext(contract, screens);
        assert.equal(context.cols, 60);
        assert.equal(context.getScrollback().screenLines, 12);
        for (let index = 0; index < 4; index++) context.feed(contract.pinnedFeeds[index]);
        assert.equal(context.getCell(2, 0).char, 'C');
        context.feed(contract.pinnedFeeds[4]);
        assert.deepEqual([context.getCursor().x, context.getCursor().y], [0, 1]);
        context.feed(contract.pinnedFeeds[5]);
        assert.equal(context.fedCount, 6);
        assert.equal(context.getCell(1, 0).char, contract.mode === 'insert' ? ' ' : 'C');
        assert.throws(() => context.feed('x'), /differs from the contract/);
        assert.throws(() => lineEditRecordingContext(contract, screens).feed(contract.pinnedFeeds[1]), /differs from the contract/);
        assert.throws(() => lineEditRecordingContext(contract, screens).getCell(0, 0), /before its fixture/);
        assert.throws(() => lineEditRecordingContext(contract, screens).getCursor(), /before its fixture/);
        assert.throws(() => lineEditRecordingContext(contract, screens).getText(), /must not read text/);
    }
});

test('the pinned geometry gate: too few columns or screen lines is the pinned inconclusive shape', () => {
    for (const contract of lineEditContracts) {
        const screens = syntheticLineEditScreens(contract.caseId);
        const replay = replayLineEditPinned(contract, screens.map(screen => ({ ...screen, cols: 5 })));
        assert.equal(replay.note, pinnedNotes[contract.mode].geometry);
        assert.equal(replay.response, JSON.stringify({ cols: 5, screenLines: 12 }));
        assert.equal(replay.observation.outcome, 'inconclusive');
        assert.equal(replay.assertions, undefined);
        assert.equal(replayLineEditPinned(contract, screens.map(screen => ({ ...screen, rows: 3 }))).observation.outcome, 'inconclusive');
    }
});

test('tampered pinned results, run logs and identities are refused', () => {
    const evidence = syntheticLineEditEvidence(runId);
    const mutate = edit => { const copy = clone(evidence); edit(copy); return copy; };
    assert.throws(() => build(mutate(copy => { copy.run.cases[0].pinned.pass = false; })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[1].pinned.observation.outcome = 'unsupported'; })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[1].pinned.response = copy.run.cases[1].pinned.response.replace('"C"', '"Q"'); })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[0].feeds[2] = 'x'; })), /do not match/);
    assert.throws(() => build(mutate(copy => { copy.run.cases.reverse(); })), /case order differs/);
    assert.throws(() => build(mutate(copy => { copy.run.probeIds = [...copy.run.probeIds].reverse(); })), /does not cover/);
    assert.throws(() => build(mutate(copy => { copy.adapter.id = 'other'; })), /Invalid Line-editing adapter identity/);
    assert.throws(() => build(mutate(copy => { copy.adapter.derivedFromAdapterId = 'ntilde-owned-erase-line-grading-v1'; })), /Invalid Line-editing adapter identity/);
    assert.throws(() => build(mutate(copy => { copy.run.adapterId = 'other'; })), /Invalid Line-editing adapter identity/);
    assert.throws(() => buildLineEditGradingReport(evidence.frames, evidence.run, 'd'.repeat(32), evidence.adapter), /not bound to this run/);
});

test('wrong fixture bytes are refused: a pinned feed substituted for a convenient equivalent', () => {
    const evidence = syntheticLineEditEvidence(runId);
    for (const [index, sequence] of [[0, '\x1b[L'], [1, '\x1b[1;1M']]) {
        const copy = clone(evidence);
        const feeds = copy.run.cases[index].feeds;
        const at = feeds.lastIndexOf(feeds.find(feed => feed === lineEditContracts[index].sequence));
        feeds[at] = sequence;
        assert.throws(() => build(copy), /do not match/);
    }
});

test('mismatched identities: a wrong recorded barrier, cursor or read kind is refused', () => {
    const evidence = syntheticLineEditEvidence(runId);
    const barrier = clone(evidence);
    barrier.run.cases[0].barriers[4] = '\x1b[?1;2c';
    assert.throws(() => build(barrier), /barrier differs from its read frame/);
    const cursor = clone(evidence);
    cursor.run.cases[0].reads[2].cursor = [0, 3];
    assert.throws(() => build(cursor), /cursor differs from its read frame/);
    const kind = clone(evidence);
    kind.run.cases[1].reads[0].kind = 'text';
    assert.throws(() => build(kind), /Wrong editing\.delete-lines read kind/);
});

test('stale or edited observations (changed screen with an old digest) are refused', () => {
    const evidence = syntheticLineEditEvidence(runId);
    const stale = clone(evidence);
    stale.frames[4].screen.lines[0] = 'AAAAX';
    assert.throws(() => build(stale), /Changed Line-editing snapshot evidence/);
    const reordered = clone(evidence);
    [reordered.frames[3], reordered.frames[4]] = [reordered.frames[4], reordered.frames[3]];
    assert.throws(() => build(reordered), /Changed Line-editing snapshot evidence/);
    const missing = clone(evidence);
    missing.frames.splice(5, 1);
    assert.throws(() => build(missing), /Incomplete Line-editing snapshot sequence/);
});

test('an edited screen with a recomputed digest is caught by the pinned agreement, not the digest', () => {
    const copy = clone(syntheticLineEditEvidence(runId));
    const frame = copy.frames.find(item => item.index === 5);
    frame.screen.lines[2] = 'BBBBX';
    frame.sha256 = snapshotHash(frame.screen);
    assert.throws(() => build(copy), /does not match its observed evidence/);
});

test('a stored margin, origin or insert flag rewritten with a recomputed digest changes the conclusion, never the digest check', () => {
    const copy = clone(syntheticLineEditEvidence(runId));
    // Frame 13 is the insert-lines regionBefore read: pretending the region margins never took effect makes the setup unusable.
    const frame = copy.frames[13];
    frame.screen.bufferState.scrollTop = 0;
    frame.screen.bufferState.scrollBottom = 11;
    frame.sha256 = snapshotHash(frame.screen);
    assert.equal(build(copy).cases[0].conclusion.outcome, 'inconclusive');
    const insert = clone(syntheticLineEditEvidence(runId));
    insert.frames[5].screen.bufferState.insertMode = true;
    insert.frames[5].sha256 = snapshotHash(insert.frames[5].screen);
    assert.equal(build(insert).cases[0].conclusion.outcome, 'inconclusive');
});

test('a missing or malformed stored buffer state is refused', () => {
    const copy = clone(syntheticLineEditEvidence(runId));
    delete copy.frames[4].screen.bufferState;
    copy.frames[4].sha256 = snapshotHash(copy.frames[4].screen);
    assert.throws(() => build(copy));
    const range = clone(syntheticLineEditEvidence(runId));
    range.frames[4].screen.bufferState.scrollBottom = 99;
    range.frames[4].sha256 = snapshotHash(range.frames[4].screen);
    assert.throws(() => build(range), /Missing or invalid observed editing state/);
});

test('inconsistent flags and details: attribute runs that contradict the stored details are refused', () => {
    const evidence = syntheticLineEditEvidence(runId);
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
    const copy = clone(syntheticLineEditEvidence(runId));
    const last = copy.frames.at(-1);
    last.screen.lines[0] = 'left over';
    last.sha256 = snapshotHash(last.screen);
    assert.throws(() => build(copy), /was not restored/);
    const margins = clone(syntheticLineEditEvidence(runId));
    const finish = margins.frames.at(-1);
    finish.screen.bufferState.scrollTop = 2;
    finish.sha256 = snapshotHash(finish.screen);
    assert.throws(() => build(margins), /was not restored/);
});

test('DECSCNM on throughout is a stable but unusable run: every case is inconclusive, never graded', () => {
    const everywhere = syntheticLineEditEvidence(runId, 'f'.repeat(64), { ...paletteBinding(), reverseVideo: true });
    assert.deepEqual(build(everywhere).cases.map(item => item.conclusion.outcome), ['inconclusive', 'inconclusive']);
    const copy = clone(syntheticLineEditEvidence(runId));
    copy.frames[4].screen.colorBinding.reverseVideo = true;
    copy.frames[4].sha256 = snapshotHash(copy.frames[4].screen);
    assert.throws(() => build(copy), /palette or theme change/);
});

test('a theme or palette change during the suite voids the run', () => {
    const theme = clone(syntheticLineEditEvidence(runId));
    theme.frames[4].screen.colorBinding.themeName = 'other';
    theme.frames[4].sha256 = snapshotHash(theme.frames[4].screen);
    assert.throws(() => build(theme), /palette or theme change/);
});

test('contract-version pinning and evidence validation entry', () => {
    const evidence = syntheticLineEditEvidence(runId);
    assert.doesNotThrow(() => validateLineEditEvidence(evidence.frames, evidence.run, evidence.runId, evidence.adapter));
    assert.throws(() => buildLineEditGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter, { contractsVersion: 2 }), /Unknown line-edit contracts revision 2/);
});

test('rowOf keeps spaces as plain blank cells so fixture rows are exactly their text', () => {
    assert.deepEqual(rowOf('A B').map(cell => cell.char), ['A', ' ', 'B']);
});

test('the pinned replay may stop early only where the pinned probe does: geometry, unread seed and wrong setup cursor', () => {
    for (const contract of lineEditContracts) {
        const notes = pinnedNotes[contract.mode];
        const screens = syntheticLineEditScreens(contract.caseId);
        const full = replayLineEditPinned(contract, screens);
        assert.equal(expectedFedCount(contract, full), 6);
        const seed = replayLineEditPinned(contract, syntheticLineEditScreens(contract.caseId, undefined, id => {
            const reads = syntheticLineEditReads(id); texts(reads, 'before', ['AAAAA', 'BBBBB', 'CCCCC', 'DDDDX']); return reads; }));
        assert.equal(seed.note, notes.seed);
        assert.equal(expectedFedCount(contract, seed), 4);
        const cursor = replayLineEditPinned(contract, syntheticLineEditScreens(contract.caseId, undefined, id => {
            const reads = syntheticLineEditReads(id); reads.setup = { ...reads.setup, cursor: [1, 3] }; return reads; }));
        assert.equal(expectedFedCount(contract, cursor), 5);
        assert.equal(expectedFedCount(contract, replayLineEditPinned(contract, screens.map(screen => ({ ...screen, cols: 5 })))), 0);
        // The first-row control failing is inconclusive after all six feeds.
        const control = replayLineEditPinned(contract, syntheticLineEditScreens(contract.caseId, undefined, id => {
            const reads = syntheticLineEditReads(id); texts(reads, 'edited', ['ZZZZZ', 'CCCCC', 'DDDDD']); return reads; }));
        assert.equal(control.note, notes.controls);
        assert.equal(expectedFedCount(contract, control), 6);
    }
});
