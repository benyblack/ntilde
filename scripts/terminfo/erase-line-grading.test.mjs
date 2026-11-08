import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildEraseLineGradingReport, replayEraseLinePinned, validateEraseLineEvidence } from './erase-line-grading.mjs';
import { eraseLineContracts, eraseLineSuiteId, eraseLineContractsVersion, pinnedFixtureNote, pinnedGeometryNote } from './erase-line-contracts.mjs';
import { eraseLineRecordingContext } from './erase-line-collector.mjs';
import { snapshotHash } from './agent-observer.mjs';
import { syntheticEraseLineEvidence, syntheticEraseLineReads, syntheticEraseLineScreens, rowOf, survivors,
    paletteBinding } from './fixtures/erase-line-evidence.mjs';

const RIGHT = 'erase.line.right', LEFT = 'erase.line.left', ALL = 'erase.line.all';
const runId = 'c'.repeat(32);
const evidenceWith = (caseId, edit, binding) => syntheticEraseLineEvidence(runId, 'f'.repeat(64), binding, id => {
    const reads = syntheticEraseLineReads(id);
    if (id === caseId && edit) edit(reads);
    return reads;
});
const build = evidence => buildEraseLineGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
/** A JSON round trip: structuredClone would keep the shared screen and binding objects shared between frames. */
const clone = value => JSON.parse(JSON.stringify(value));

test('a complete run rederives all three cases: pinned and contract agree and nothing diverges', () => {
    const report = build(syntheticEraseLineEvidence(runId));
    assert.equal(report.suiteId, eraseLineSuiteId);
    assert.equal(report.contractsVersion, eraseLineContractsVersion);
    assert.deepEqual(report.cases.map(item => [item.caseId, item.pinned.observation.outcome, item.conclusion.outcome]), [
        [RIGHT, 'supported', 'supported'], [LEFT, 'supported', 'supported'], [ALL, 'supported', 'supported']]);
    for (const item of report.cases) {
        assert.equal(item.divergence, undefined);
        assert.equal(item.conclusion.grading, 'pinned-termless-plus-contract-v1');
        assert.equal(item.pinned.assertions[0].expected, item.pinnedFeeds && eraseLineContracts.find(c => c.caseId === item.caseId).pinnedExpected);
    }
    assert.equal(report.snapshots.length, 3 + 3 * 10);
    assert.match(report.scope, /capture-only upstream and is not qualified/);
});

test('the pinned response carries the five cells and, for left/right, the setup cursor; the all case carries both rows', () => {
    const [right, left, all] = build(syntheticEraseLineEvidence(runId)).cases;
    assert.equal(right.pinned.response, JSON.stringify({ before: ['A', 'B', 'C', 'D', 'E'], cursorBefore: { x: 2, y: 0 }, after: ['A', 'B', ' ', ' ', ' '] }));
    assert.equal(left.pinned.response, JSON.stringify({ before: ['A', 'B', 'C', 'D', 'E'], cursorBefore: { x: 2, y: 0 }, after: [' ', ' ', ' ', 'D', 'E'] }));
    assert.equal(all.pinned.response, JSON.stringify({ before: ['A', 'B', 'C', 'D', 'E'], after: [' ', ' ', ' ', ' ', ' '],
        neighborBefore: ['K', 'E', 'E', 'P', '!'], neighborAfter: ['K', 'E', 'E', 'P', '!'] }));
});

test('a no-op erase grades unsupported by both the pinned probe and the contract', () => {
    const report = build(evidenceWith(RIGHT, reads => { reads.erased = reads.setup; }));
    assert.equal(report.cases[0].pinned.observation.outcome, 'unsupported');
    assert.equal(report.cases[0].conclusion.outcome, 'unsupported');
    assert.equal(report.cases[0].divergence, undefined);
});

test('a moved cursor is a recorded contract-stricter divergence from a supported pinned verdict', () => {
    const report = build(evidenceWith(LEFT, reads => { reads.erased = { ...reads.erased, cursor: [0, 5] }; }));
    const item = report.cases[1];
    assert.equal(item.pinned.observation.outcome, 'supported');
    assert.equal(item.conclusion.outcome, 'unsupported');
    assert.deepEqual(item.divergence, { pinnedOutcome: 'supported', contractOutcome: 'unsupported', kind: 'contract-stricter-than-pinned' });
});

test('damage the pinned probe cannot see (wide row, attributes) is also a recorded stricter divergence', () => {
    const wide = build(evidenceWith(ALL, reads => { reads.wide = { rows: [rowOf('ABOVE-ROW!'), rowOf('x'), rowOf('BELOW-ROW!')], cursor: [1, 2] }; }));
    assert.equal(wide.cases[2].divergence.kind, 'contract-stricter-than-pinned');
    const attrs = build(evidenceWith(RIGHT, reads => { reads.bce = { rows: [rowOf('AB   ')], cursor: [0, 2] }; }));
    assert.equal(attrs.cases[0].pinned.observation.outcome, 'supported');
    assert.equal(attrs.cases[0].conclusion.outcome, 'unsupported');
});

test('an invalid setup is inconclusive in both graders with the pinned note', () => {
    const report = build(evidenceWith(RIGHT, reads => { reads.setup = { ...reads.setup, cursor: [0, 3] }; }));
    assert.equal(report.cases[0].pinned.observation.outcome, 'inconclusive');
    assert.equal(report.cases[0].conclusion.outcome, 'inconclusive');
    assert.equal(report.cases[0].pinned.note, pinnedFixtureNote);
    const noNeighbor = build(evidenceWith(ALL, reads => { reads.setup = { rows: [rowOf('ABCDE')], cursor: [0, 2] }; }));
    assert.equal(noNeighbor.cases[2].pinned.observation.outcome, 'inconclusive');
    assert.equal(noNeighbor.cases[2].conclusion.outcome, 'inconclusive');
});

test('a pinned unsupported over an invalid wide-row control is recorded as the contract being more cautious', () => {
    const report = build(evidenceWith(LEFT, reads => {
        reads.erased = { rows: [rowOf(survivors('ABCDE', 0))], cursor: [0, 2] };
        reads.wideBefore = { rows: [rowOf('ABOVE-ROW!')], cursor: [1, 2] };
    }));
    const item = report.cases[1];
    assert.equal(item.pinned.observation.outcome, 'unsupported');
    assert.equal(item.conclusion.outcome, 'inconclusive');
    assert.deepEqual(item.divergence, { pinnedOutcome: 'unsupported', contractOutcome: 'inconclusive', kind: 'contract-more-cautious-than-pinned' });
});

test('the pinned replay equals the live pinned shapes over the observed fixture screens', () => {
    for (const contract of eraseLineContracts) {
        const screens = syntheticEraseLineScreens(contract.caseId);
        const replay = replayEraseLinePinned(contract, screens);
        assert.equal(replay.pass, true);
        assert.equal(replay.observation.outcome, 'supported');
        assert.equal(replay.assertions[0].expected, contract.pinnedExpected);
        const context = eraseLineRecordingContext(contract, screens);
        assert.equal(context.cols, 60);
        assert.equal(context.getScrollback().screenLines, 6);
        context.feed(contract.pinnedFeeds[0]);
        assert.equal(context.getCell(0, 2).char, 'C');
        assert.deepEqual([context.getCursor().x, context.getCursor().y], [2, 0]);
        context.feed(contract.pinnedFeeds[1]);
        assert.equal(context.fedCount, 2);
        assert.throws(() => context.feed('x'), /differs from the contract/);
        assert.throws(() => eraseLineRecordingContext(contract, screens).feed(contract.pinnedFeeds[1]), /differs from the contract/);
        assert.throws(() => eraseLineRecordingContext(contract, screens).getCell(0, 0), /before its fixture/);
        assert.throws(() => eraseLineRecordingContext(contract, screens).getCursor(), /before its fixture/);
        assert.throws(() => eraseLineRecordingContext(contract, screens).getText(), /must not read text/);
    }
});

test('the pinned geometry gate: too few rows or columns is the pinned inconclusive shape', () => {
    const contract = eraseLineContracts[2];
    const screens = syntheticEraseLineScreens(contract.caseId);
    const narrow = screens.map(screen => ({ ...screen, cols: 5 }));
    const replay = replayEraseLinePinned(contract, narrow);
    assert.equal(replay.note, pinnedGeometryNote);
    assert.equal(replay.response, JSON.stringify({ cols: 5, rows: 6 }));
    assert.equal(replay.observation.outcome, 'inconclusive');
});

test('tampered pinned results, run logs and identities are refused', () => {
    const evidence = syntheticEraseLineEvidence(runId);
    const mutate = edit => { const copy = clone(evidence); edit(copy); return copy; };
    assert.throws(() => build(mutate(copy => { copy.run.cases[0].pinned.pass = false; })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[1].pinned.observation.outcome = 'unsupported'; })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[2].pinned.response = copy.run.cases[2].pinned.response.replace('"P"', '"Q"'); })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[0].feeds[2] = 'x'; })), /do not match/);
    assert.throws(() => build(mutate(copy => { copy.run.cases.reverse(); })), /case order differs/);
    assert.throws(() => build(mutate(copy => { copy.run.probeIds = [...copy.run.probeIds].reverse(); })), /does not cover/);
    assert.throws(() => build(mutate(copy => { copy.adapter.id = 'other'; })), /Invalid Erase-in-line adapter identity/);
    assert.throws(() => build(mutate(copy => { copy.adapter.derivedFromAdapterId = 'ntilde-owned-sgr-underline-color-grading-v1'; })), /Invalid Erase-in-line adapter identity/);
    assert.throws(() => build(mutate(copy => { copy.run.adapterId = 'other'; })), /Invalid Erase-in-line adapter identity/);
    assert.throws(() => buildEraseLineGradingReport(evidence.frames, evidence.run, 'd'.repeat(32), evidence.adapter), /not bound to this run/);
});

test('mismatched identities: a wrong recorded barrier or cursor is refused', () => {
    const evidence = syntheticEraseLineEvidence(runId);
    const barrier = clone(evidence);
    barrier.run.cases[0].barriers[3] = '\x1b[?1;2c';
    assert.throws(() => build(barrier), /barrier differs from its read frame/);
    const cursor = clone(evidence);
    cursor.run.cases[0].reads[2].cursor = [0, 3];
    assert.throws(() => build(cursor), /cursor differs from its read frame/);
    const kind = clone(evidence);
    kind.run.cases[1].reads[0].kind = 'text';
    assert.throws(() => build(kind), /Wrong erase\.line\.left read kind/);
});

test('stale or edited observations (changed screen with an old digest) are refused', () => {
    const evidence = syntheticEraseLineEvidence(runId);
    const stale = clone(evidence);
    stale.frames[4].screen.lines[0] = 'ABCDX';
    assert.throws(() => build(stale), /Changed Erase-in-line snapshot evidence/);
    const reordered = clone(evidence);
    [reordered.frames[3], reordered.frames[4]] = [reordered.frames[4], reordered.frames[3]];
    assert.throws(() => build(reordered), /Changed Erase-in-line snapshot evidence/);
    const missing = clone(evidence);
    missing.frames.splice(5, 1);
    assert.throws(() => build(missing), /Incomplete Erase-in-line snapshot sequence/);
});

test('an edited screen with a recomputed digest is caught by the pinned agreement, not the digest', () => {
    const evidence = syntheticEraseLineEvidence(runId);
    const copy = clone(evidence);
    const frame = copy.frames.find(item => item.index === 5);
    frame.screen.lines[0] = 'AB';
    frame.screen.cursorCol = 4;
    frame.sha256 = snapshotHash(frame.screen);
    assert.throws(() => build(copy));
});

test('inconsistent flags and details: attribute runs that contradict the stored details are refused', () => {
    const evidence = syntheticEraseLineEvidence(runId);
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
    const evidence = syntheticEraseLineEvidence(runId);
    const copy = clone(evidence);
    const last = copy.frames.at(-1);
    last.screen.lines[0] = 'left over';
    last.sha256 = snapshotHash(last.screen);
    assert.throws(() => build(copy), /was not restored/);
});

test('DECSCNM on throughout is a stable but unusable run: every case is inconclusive, never graded', () => {
    const everywhere = syntheticEraseLineEvidence(runId, 'f'.repeat(64), { ...paletteBinding(), reverseVideo: true });
    assert.deepEqual(build(everywhere).cases.map(item => item.conclusion.outcome), ['inconclusive', 'inconclusive', 'inconclusive']);
    const one = syntheticEraseLineEvidence(runId);
    const copy = clone(one);
    copy.frames[4].screen.colorBinding.reverseVideo = true;
    copy.frames[4].sha256 = snapshotHash(copy.frames[4].screen);
    assert.throws(() => build(copy), /palette or theme change/);
});

test('a theme or palette change during the suite voids the run', () => {
    const evidence = syntheticEraseLineEvidence(runId);
    const theme = clone(evidence);
    theme.frames[4].screen.colorBinding.themeName = 'other';
    theme.frames[4].sha256 = snapshotHash(theme.frames[4].screen);
    assert.throws(() => build(theme), /palette or theme change/);
});

test('contract-version pinning and evidence validation entry', () => {
    const evidence = syntheticEraseLineEvidence(runId);
    assert.doesNotThrow(() => validateEraseLineEvidence(evidence.frames, evidence.run, evidence.runId, evidence.adapter));
    assert.throws(() => buildEraseLineGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter, { contractsVersion: 2 }), /Unknown erase-in-line contracts revision 2/);
});
