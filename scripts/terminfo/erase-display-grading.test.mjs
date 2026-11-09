import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildEraseDisplayGradingReport, replayEraseDisplayPinned, validateEraseDisplayEvidence } from './erase-display-grading.mjs';
import { eraseDisplayContracts, eraseDisplaySuiteId, eraseDisplayContractsVersion, pinnedFixtureNote, pinnedGeometryNote } from './erase-display-contracts.mjs';
import { eraseDisplayRecordingContext } from './erase-display-collector.mjs';
import { snapshotHash } from './agent-observer.mjs';
import { syntheticEraseDisplayEvidence, syntheticEraseDisplayReads, syntheticEraseDisplayScreens, survivors, textCells,
    paletteBinding } from './fixtures/erase-display-evidence.mjs';

const BELOW = 'erase.screen.below', ABOVE = 'erase.screen.above', ALL = 'erase.screen.all';
const runId = 'c'.repeat(32);
const seed = () => [...textCells(0, 'AAAAA'), ...textCells(20, 'BBBBB'), ...textCells(39, 'CCCCC')];
const evidenceWith = (caseId, edit, binding) => syntheticEraseDisplayEvidence(runId, 'f'.repeat(64), binding, id => {
    const reads = syntheticEraseDisplayReads(id);
    if (id === caseId && edit) edit(reads);
    return reads;
});
const build = evidence => buildEraseDisplayGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
/** A JSON round trip: structuredClone would keep the shared screen and binding objects shared between frames. */
const clone = value => JSON.parse(JSON.stringify(value));

test('a complete run rederives all three cases: pinned and contract agree and nothing diverges', () => {
    const report = build(syntheticEraseDisplayEvidence(runId));
    assert.equal(report.suiteId, eraseDisplaySuiteId);
    assert.equal(report.contractsVersion, eraseDisplayContractsVersion);
    assert.deepEqual(report.cases.map(item => [item.caseId, item.pinned.observation.outcome, item.conclusion.outcome]), [
        [BELOW, 'supported', 'supported'], [ABOVE, 'supported', 'supported'], [ALL, 'supported', 'supported']]);
    for (const item of report.cases) {
        assert.equal(item.divergence, undefined);
        assert.equal(item.conclusion.grading, 'pinned-termless-plus-contract-v1');
        assert.equal(item.pinned.assertions[0].expected, eraseDisplayContracts.find(c => c.caseId === item.caseId).pinnedExpected);
    }
    assert.equal(report.snapshots.length, 3 + 3 * 15);
    assert.match(report.scope, /capture-only upstream and is not qualified/);
    assert.match(report.scope, /CSI 3J is not graded/);
});

test('the pinned response carries the three rows, the cursor and the scrollback counts before and after', () => {
    const [below, above, all] = build(syntheticEraseDisplayEvidence(runId)).cases;
    const counts = { screenLines: 40, totalLines: 40, viewportY: 0 };
    const response = (before, after) => JSON.stringify({ rows: [0, 20, 39], before, cursorBefore: { x: 2, y: 20 }, scrollbackBefore: counts, after,
        cursorAfter: { x: 2, y: 20 }, scrollbackAfter: counts });
    const seeded = [['A', 'A', 'A', 'A', 'A'], ['B', 'B', 'B', 'B', 'B'], ['C', 'C', 'C', 'C', 'C']];
    assert.equal(below.pinned.response, response(seeded, [['A', 'A', 'A', 'A', 'A'], ['B', 'B', ' ', ' ', ' '], [' ', ' ', ' ', ' ', ' ']]));
    assert.equal(above.pinned.response, response(seeded, [[' ', ' ', ' ', ' ', ' '], [' ', ' ', ' ', 'B', 'B'], ['C', 'C', 'C', 'C', 'C']]));
    assert.equal(all.pinned.response, response(seeded, [[' ', ' ', ' ', ' ', ' '], [' ', ' ', ' ', ' ', ' '], [' ', ' ', ' ', ' ', ' ']]));
});

test('a no-op erase grades unsupported by both the pinned probe and the contract', () => {
    const report = build(evidenceWith(BELOW, reads => { reads.erased = reads.setup; }));
    assert.equal(report.cases[0].pinned.observation.outcome, 'unsupported');
    assert.equal(report.cases[0].conclusion.outcome, 'unsupported');
    assert.equal(report.cases[0].divergence, undefined);
});

test('a moved or homed cursor is a recorded pinned-unsupported agreement (the pinned probe does read the cursor after)', () => {
    const report = build(evidenceWith(ABOVE, reads => { reads.erased = { ...reads.erased, cursor: [0, 0] }; }));
    const item = report.cases[1];
    assert.equal(item.pinned.observation.outcome, 'unsupported');
    assert.equal(item.conclusion.outcome, 'unsupported');
    assert.equal(item.divergence, undefined);
});

test('damage the pinned probe cannot see (fill rows, boundary, attributes, modes, history) is a recorded stricter divergence', () => {
    const fill = build(evidenceWith(ALL, reads => { reads.wide = { cells: [[3, 0, { ...reads.setup.cells[0][2], char: 'x' }]], cursor: [20, 4] }; }));
    assert.equal(fill.cases[2].pinned.observation.outcome, 'supported');
    assert.equal(fill.cases[2].divergence.kind, 'contract-stricter-than-pinned');
    const attrs = build(evidenceWith(BELOW, reads => { reads.bce = { cells: [], cursor: [20, 2] }; }));
    assert.equal(attrs.cases[0].pinned.observation.outcome, 'supported');
    assert.equal(attrs.cases[0].conclusion.outcome, 'unsupported');
    const history = build(evidenceWith(ALL, reads => { reads.history = { ...reads.history, history: ['HIST-A'] }; }));
    assert.equal(history.cases[2].pinned.observation.outcome, 'supported');
    assert.equal(history.cases[2].conclusion.outcome, 'unsupported');
    assert.equal(history.cases[2].divergence.kind, 'contract-stricter-than-pinned');
    const mode = build(evidenceWith(BELOW, reads => { reads.erased = { ...reads.erased, buffer: { originMode: true } }; }));
    assert.equal(mode.cases[0].pinned.observation.outcome, 'supported');
    assert.equal(mode.cases[0].conclusion.outcome, 'unsupported');
});

test('an invalid setup is inconclusive in both graders with the pinned note', () => {
    const report = build(evidenceWith(BELOW, reads => { reads.setup = { ...reads.setup, cursor: [20, 3] }; }));
    assert.equal(report.cases[0].pinned.observation.outcome, 'inconclusive');
    assert.equal(report.cases[0].conclusion.outcome, 'inconclusive');
    assert.equal(report.cases[0].pinned.observation.note, pinnedFixtureNote);
    const missing = build(evidenceWith(ALL, reads => { reads.setup = { cells: [...textCells(0, 'AAAAA'), ...textCells(20, 'BBBBB')], cursor: [20, 2] }; }));
    assert.equal(missing.cases[2].pinned.observation.outcome, 'inconclusive');
    assert.equal(missing.cases[2].conclusion.outcome, 'inconclusive');
});

test('a pinned unsupported over an invalid fill control is recorded as the contract being more cautious', () => {
    const report = build(evidenceWith(ABOVE, reads => {
        reads.erased = { cells: survivors(seed(), 0, [20, 2]), cursor: [20, 2] };
        reads.wideBefore = { cells: [], cursor: [20, 4] };
    }));
    const item = report.cases[1];
    assert.equal(item.pinned.observation.outcome, 'unsupported');
    assert.equal(item.conclusion.outcome, 'inconclusive');
    assert.deepEqual(item.divergence, { pinnedOutcome: 'unsupported', contractOutcome: 'inconclusive', kind: 'contract-more-cautious-than-pinned' });
});

test('the pinned replay equals the live pinned shapes over the observed fixture screens', () => {
    for (const contract of eraseDisplayContracts) {
        const screens = syntheticEraseDisplayScreens(contract.caseId);
        const replay = replayEraseDisplayPinned(contract, screens);
        assert.equal(replay.pass, true);
        assert.equal(replay.observation.outcome, 'supported');
        assert.equal(replay.assertions[0].expected, contract.pinnedExpected);
        const context = eraseDisplayRecordingContext(contract, screens);
        assert.equal(context.cols, 140);
        // The initial scrollback read precedes every feed and is served from the initial blank screen.
        assert.deepEqual(context.getScrollback(), { screenLines: 40, totalLines: 40, viewportY: 0 });
        context.feed(contract.pinnedFeeds[0]);
        assert.equal(context.getCell(20, 2).char, 'B');
        assert.deepEqual([context.getCursor().x, context.getCursor().y], [2, 20]);
        context.feed(contract.pinnedFeeds[1]);
        assert.equal(context.fedCount, 2);
        assert.throws(() => context.feed('x'), /differs from the contract/);
        assert.throws(() => eraseDisplayRecordingContext(contract, screens).feed(contract.pinnedFeeds[1]), /differs from the contract/);
        assert.throws(() => eraseDisplayRecordingContext(contract, screens).getCell(0, 0), /before its fixture/);
        assert.throws(() => eraseDisplayRecordingContext(contract, screens).getCursor(), /before its fixture/);
        assert.throws(() => eraseDisplayRecordingContext(contract, screens).getText(), /must not read text/);
    }
});

test('the pinned scrollback total follows the observed saved lines, not a constant', () => {
    const contract = eraseDisplayContracts[0];
    const screens = syntheticEraseDisplayScreens(BELOW, paletteBinding(), id => {
        const reads = syntheticEraseDisplayReads(id);
        reads.setup = { ...reads.setup, history: ['x', 'y', 'z'], main: true };
        return reads;
    });
    const context = eraseDisplayRecordingContext(contract, screens);
    context.feed(contract.pinnedFeeds[0]);
    assert.deepEqual(context.getScrollback(), { screenLines: 40, totalLines: 43, viewportY: 0 });
});

test('the pinned geometry gate: too few columns is the pinned inconclusive shape with the note only inside the observation', () => {
    const contract = eraseDisplayContracts[2];
    const screens = syntheticEraseDisplayScreens(contract.caseId);
    const narrow = screens.map(screen => ({ ...screen, cols: 5 }));
    const replay = replayEraseDisplayPinned(contract, narrow);
    assert.equal(replay.note, undefined);
    assert.equal(replay.observation.note, pinnedGeometryNote);
    assert.equal(replay.response, JSON.stringify({ initialScrollback: { screenLines: 40, totalLines: 40, viewportY: 0 } }));
    assert.equal(replay.observation.outcome, 'inconclusive');
});

test('tampered pinned results, run logs and identities are refused', () => {
    const evidence = syntheticEraseDisplayEvidence(runId);
    const mutate = edit => { const copy = clone(evidence); edit(copy); return copy; };
    assert.throws(() => build(mutate(copy => { copy.run.cases[0].pinned.pass = false; })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[1].pinned.observation.outcome = 'unsupported'; })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[2].pinned.response = copy.run.cases[2].pinned.response.replace('"C"', '"D"'); })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[0].feeds[2] = 'x'; })), /do not match/);
    assert.throws(() => build(mutate(copy => { copy.run.cases.reverse(); })), /case order differs/);
    assert.throws(() => build(mutate(copy => { copy.run.probeIds = [...copy.run.probeIds].reverse(); })), /does not cover/);
    assert.throws(() => build(mutate(copy => { copy.adapter.id = 'other'; })), /Invalid Erase-in-display adapter identity/);
    assert.throws(() => build(mutate(copy => { copy.adapter.derivedFromAdapterId = 'ntilde-owned-erase-line-grading-v1'; })), /Invalid Erase-in-display adapter identity/);
    assert.throws(() => build(mutate(copy => { copy.run.adapterId = 'other'; })), /Invalid Erase-in-display adapter identity/);
    assert.throws(() => buildEraseDisplayGradingReport(evidence.frames, evidence.run, 'd'.repeat(32), evidence.adapter), /not bound to this run/);
});

test('mismatched identities: a wrong recorded barrier, cursor or read kind is refused', () => {
    const evidence = syntheticEraseDisplayEvidence(runId);
    const barrier = clone(evidence);
    barrier.run.cases[0].barriers[3] = '\x1b[?1;2c';
    assert.throws(() => build(barrier), /barrier differs from its read frame/);
    const cursor = clone(evidence);
    cursor.run.cases[0].reads[2].cursor = [0, 3];
    assert.throws(() => build(cursor), /cursor differs from its read frame/);
    const kind = clone(evidence);
    kind.run.cases[1].reads[0].kind = 'text';
    assert.throws(() => build(kind), /Wrong erase\.screen\.above read kind/);
});

test('stale or edited observations (changed screen with an old digest) are refused', () => {
    const evidence = syntheticEraseDisplayEvidence(runId);
    const stale = clone(evidence);
    stale.frames[4].screen.lines[0] = 'AAAAX';
    assert.throws(() => build(stale), /Changed Erase-in-display snapshot evidence/);
    const reordered = clone(evidence);
    [reordered.frames[3], reordered.frames[4]] = [reordered.frames[4], reordered.frames[3]];
    assert.throws(() => build(reordered), /Changed Erase-in-display snapshot evidence/);
    const missing = clone(evidence);
    missing.frames.splice(5, 1);
    assert.throws(() => build(missing), /Incomplete Erase-in-display snapshot sequence/);
});

test('an edited screen with a recomputed digest is caught by the pinned agreement, not the digest', () => {
    const copy = clone(syntheticEraseDisplayEvidence(runId));
    const frame = copy.frames.find(item => item.index === 5);
    frame.screen.lines[20] = 'BB';
    frame.screen.cursorCol = 4;
    frame.sha256 = snapshotHash(frame.screen);
    assert.throws(() => build(copy));
});

test('inconsistent flags and details: attribute runs that contradict the stored details are refused', () => {
    const evidence = syntheticEraseDisplayEvidence(runId);
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

test('a blank cell whose attribute run contradicts its stored details is refused, not skipped', () => {
    const evidence = syntheticEraseDisplayEvidence(runId);
    const copy = clone(evidence);
    const frame = copy.frames[4];
    // Split the long default run so exactly one blank cell (10,10) claims bold in the attribute runs while its stored details stay default.
    const runs = [];
    let position = 0;
    for (const run of frame.screen.attributeRuns) {
        const end = position + run.count;
        if (position <= 10 * 140 + 10 && 10 * 140 + 10 < end) {
            const before = 10 * 140 + 10 - position, after = end - (10 * 140 + 11);
            if (before > 0) runs.push({ ...run, count: before });
            runs.push({ ...run, count: 1, flags: run.flags | 0x01 });
            if (after > 0) runs.push({ ...run, count: after });
        } else runs.push(run);
        position = end;
    }
    frame.screen.attributeRuns = runs;
    frame.sha256 = snapshotHash(frame.screen);
    assert.throws(() => build(copy), /Contradictory stored cell projections at 10,10/);
});

test('screens without buffer state or with an incomplete history are refused', () => {
    const evidence = syntheticEraseDisplayEvidence(runId);
    const noState = clone(evidence);
    delete noState.frames[4].screen.bufferState;
    noState.frames[4].sha256 = snapshotHash(noState.frames[4].screen);
    assert.throws(() => build(noState));
    const history = clone(evidence);
    history.frames[4].screen.history.lines = ['x'];
    history.frames[4].sha256 = snapshotHash(history.frames[4].screen);
    assert.throws(() => build(history));
});

test('incomplete restoration is refused', () => {
    const copy = clone(syntheticEraseDisplayEvidence(runId));
    const last = copy.frames.at(-1);
    last.screen.lines[0] = 'left over';
    last.sha256 = snapshotHash(last.screen);
    assert.throws(() => build(copy), /was not restored/);
    // Saved lines left behind on the main screen are incomplete restoration too: the start and finish frames must be identical.
    const history = clone(syntheticEraseDisplayEvidence(runId));
    const finish = history.frames.at(-1);
    finish.screen.bufferState.scrollbackLines = 4;
    finish.screen.history = { startLine: 0, totalLines: 4, lines: ['HIST-A', 'HIST-B', 'HIST-C', ''] };
    finish.sha256 = snapshotHash(finish.screen);
    assert.throws(() => build(history), /was not restored/);
});

test('DECSCNM on throughout is a stable but unusable run: every case is inconclusive, never graded', () => {
    const everywhere = syntheticEraseDisplayEvidence(runId, 'f'.repeat(64), { ...paletteBinding(), reverseVideo: true });
    assert.deepEqual(build(everywhere).cases.map(item => item.conclusion.outcome), ['inconclusive', 'inconclusive', 'inconclusive']);
    const one = clone(syntheticEraseDisplayEvidence(runId));
    one.frames[4].screen.colorBinding.reverseVideo = true;
    one.frames[4].sha256 = snapshotHash(one.frames[4].screen);
    assert.throws(() => build(one), /palette or theme change/);
});

test('a theme or palette change during the suite voids the run', () => {
    const theme = clone(syntheticEraseDisplayEvidence(runId));
    theme.frames[4].screen.colorBinding.themeName = 'other';
    theme.frames[4].sha256 = snapshotHash(theme.frames[4].screen);
    assert.throws(() => build(theme), /palette or theme change/);
});

test('contract-version pinning and evidence validation entry', () => {
    const evidence = syntheticEraseDisplayEvidence(runId);
    assert.doesNotThrow(() => validateEraseDisplayEvidence(evidence.frames, evidence.run, evidence.runId, evidence.adapter));
    assert.throws(() => buildEraseDisplayGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter, { contractsVersion: 2 }), /Unknown erase-in-display contracts revision 2/);
});
