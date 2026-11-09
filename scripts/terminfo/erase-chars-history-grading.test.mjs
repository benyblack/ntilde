import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildEraseCharsHistoryGradingReport, replayEraseCharsHistoryPinned, validateEraseCharsHistoryEvidence } from './erase-chars-history-grading.mjs';
import { eraseCharsHistoryCaseIds, eraseCharsHistoryContractsVersion, eraseCharsHistorySuiteId, pinnedEraseCharsHistoryNotes } from './erase-chars-history-contracts.mjs';
import { eraseCharsHistoryRecordingContext } from './erase-chars-history-collector.mjs';
import { snapshotHash } from './agent-observer.mjs';
import { syntheticEraseCharsHistoryEvidence, syntheticEraseCharsHistoryContracts, syntheticEraseCharsHistoryScreens, paletteBinding, echRows,
    echCols, erasedRow, shiftedRow } from './fixtures/erase-chars-history-evidence.mjs';

const ECH = 'erase.character', ED3 = 'erase.screen.scrollback';
const runId = 'c'.repeat(32);
const [echContract, ed3Contract] = syntheticEraseCharsHistoryContracts();
const evidenceWith = (caseId, role, change) => syntheticEraseCharsHistoryEvidence(runId, 'f'.repeat(64), undefined,
    (id, name, state) => { if (id === caseId && name === role) change(state); return state; });
const build = evidence => buildEraseCharsHistoryGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
const byId = (report, id) => report.cases.find(item => item.caseId === id);
/** A JSON round trip: structuredClone would keep the shared screen and binding objects shared between frames. */
const clone = value => JSON.parse(JSON.stringify(value));
const frameOf = (evidence, caseId, role) => {
    const contracts = syntheticEraseCharsHistoryContracts();
    const position = contracts.findIndex(contract => contract.caseId === caseId);
    return evidence.frames[2 + contracts.slice(0, position).reduce((sum, item) => sum + item.roles.length, 0)
        + contracts[position].roles.findIndex(entry => entry.role === role)];
};
const refreeze = frame => { frame.sha256 = snapshotHash(frame.screen); };

test('a complete run rederives both cases: pinned and contract agree and nothing diverges', () => {
    const report = build(syntheticEraseCharsHistoryEvidence(runId));
    assert.equal(report.suiteId, eraseCharsHistorySuiteId);
    assert.equal(report.contractsVersion, eraseCharsHistoryContractsVersion);
    assert.deepEqual(report.cases.map(item => [item.caseId, item.pinned.observation.outcome, item.conclusion.outcome]),
        [[ECH, 'supported', 'supported'], [ED3, 'supported', 'supported']]);
    for (const item of report.cases) {
        assert.equal(item.divergence, undefined);
        assert.equal(item.conclusion.grading, 'pinned-termless-plus-contract-v1');
        assert.equal(item.pinned.assertions[0].expected, [echContract, ed3Contract].find(contract => contract.caseId === item.caseId).pinnedExpected);
        assert.equal(item.conclusion.evidence, item.caseId === ECH ? 'owned-stored-cells' : 'owned-stored-cells-and-actual-scrollback');
    }
    assert.equal(report.snapshots.length, 3 + 15 + 10);
    assert.match(report.scope, /no wide-cell or combining-character erasure/);
    assert.match(report.scope, /History counts are physical rows/);
    assert.match(report.scope, /pixel\/query paths never carry a verdict and are not qualified/);
    assert.deepEqual(eraseCharsHistoryCaseIds, report.cases.map(item => item.caseId));
});

test('the pinned responses carry the exact cells, cursor and measured totals of each pinned read', () => {
    const report = build(syntheticEraseCharsHistoryEvidence(runId));
    assert.equal(byId(report, ECH).pinned.response, JSON.stringify({ before: [...'ABCDE'], cursorBefore: { x: 0, y: 0 }, after: [' ', ' ', ' ', 'D', 'E'] }));
    assert.equal(byId(report, ED3).pinned.response, JSON.stringify({ before: { screenLines: echRows, totalLines: echRows + 3, viewportY: 0 },
        after: { screenLines: echRows, totalLines: echRows, viewportY: 0 } }));
});

test('a no-op ECH and an ED 3 that saved nothing to erase grade as the pinned code decides', () => {
    const noop = build(evidenceWith(ECH, 'erased', state => { state.lines[0] = 'ABCDE'; }));
    assert.equal(byId(noop, ECH).pinned.observation.outcome, 'unsupported');
    assert.equal(byId(noop, ECH).conclusion.outcome, 'unsupported');
    assert.equal(byId(noop, ECH).divergence, undefined);
    const kept = build(evidenceWith(ED3, 'after', state => { state.history = ['', '', '']; }));
    assert.equal(byId(kept, ED3).pinned.observation.outcome, 'unsupported');
    assert.equal(byId(kept, ED3).conclusion.outcome, 'unsupported');
    // The pinned probe cannot erase what was never saved: both graders say inconclusive.
    const empty = build(evidenceWith(ED3, 'before', state => { state.history = []; }));
    assert.equal(byId(empty, ED3).pinned.observation.outcome, 'inconclusive');
    assert.equal(byId(empty, ED3).pinned.note, pinnedEraseCharsHistoryNotes.historyNone);
    assert.equal(byId(empty, ED3).conclusion.outcome, 'inconclusive');
});

test('a wrong erased range or a shifted suffix is unsupported in both graders', () => {
    for (const text of [erasedRow('ABCDE', 0, 2), erasedRow('ABCDE', 0, 4), shiftedRow('ABCDE', 0, 3)]) {
        const report = build(evidenceWith(ECH, 'erased', state => { state.lines[0] = text; }));
        assert.equal(byId(report, ECH).pinned.observation.outcome, 'unsupported', text);
        assert.equal(byId(report, ECH).conclusion.outcome, 'unsupported', text);
    }
});

test('damage the pinned probes cannot see is a recorded stricter divergence from a supported pinned verdict', () => {
    // A moved cursor after the erase (the pinned probe never reads it).
    const cursor = build(evidenceWith(ECH, 'erased', state => { state.cursor = [0, 3]; }));
    assert.equal(byId(cursor, ECH).pinned.observation.outcome, 'supported');
    assert.deepEqual(byId(cursor, ECH).divergence, { pinnedOutcome: 'supported', contractOutcome: 'unsupported', kind: 'contract-stricter-than-pinned' });
    // Text past the pinned window, an erased wide row, a stale attribute.
    for (const [role, change] of [['erased', state => { state.lines[0] = '   DEFG'; }], ['wide', state => { state.lines[1] = shiftedRow('ABCDEFGHIJ', 2, 3); }],
        ['attr', state => { state.cells['0,3'] = { ...state.cells['0,3'], attrs: ['bold'] }; }]]) {
        const report = build(evidenceWith(ECH, role, change));
        assert.equal(byId(report, ECH).pinned.observation.outcome, 'supported', role);
        assert.equal(byId(report, ECH).conclusion.outcome, 'unsupported', role);
        assert.equal(byId(report, ECH).divergence.kind, 'contract-stricter-than-pinned', role);
    }
    // ED 3 that also clears the viewport: the pinned fixture saves blank rows only, so it cannot see the clear.
    const cleared = build(evidenceWith(ED3, 'erase3', state => { state.lines = state.lines.map(() => ''); }));
    assert.equal(byId(cleared, ED3).pinned.observation.outcome, 'supported');
    assert.equal(byId(cleared, ED3).conclusion.outcome, 'unsupported');
    assert.equal(byId(cleared, ED3).divergence.kind, 'contract-stricter-than-pinned');
    assert.match(byId(cleared, ED3).conclusion.note, /the viewport was cleared along with the saved lines/);
    // Retained history in the marked control after a pinned-supported fixture.
    const kept = build(evidenceWith(ED3, 'erase3', state => { state.history = ['HIST-0']; }));
    assert.equal(byId(kept, ED3).divergence.kind, 'contract-stricter-than-pinned');
    // Alternate screen during the pinned read: the pinned totals do not see it either.
    const alt = build(evidenceWith(ED3, 'after', state => { state.alternate = true; }));
    assert.equal(byId(alt, ED3).pinned.observation.outcome, 'supported');
    assert.equal(byId(alt, ED3).conclusion.outcome, 'unsupported');
});

test('an invalid pinned setup is inconclusive in both graders with the pinned note', () => {
    const cursor = build(evidenceWith(ECH, 'seed', state => { state.cursor = [0, 2]; }));
    assert.equal(byId(cursor, ECH).pinned.observation.outcome, 'inconclusive');
    assert.equal(byId(cursor, ECH).pinned.note, pinnedEraseCharsHistoryNotes.fixture);
    assert.equal(byId(cursor, ECH).conclusion.outcome, 'inconclusive');
    const seed = build(evidenceWith(ECH, 'seed', state => { state.lines[0] = 'ABCXE'; }));
    assert.equal(byId(seed, ECH).pinned.note, pinnedEraseCharsHistoryNotes.fixture);
    assert.equal(byId(seed, ECH).conclusion.outcome, 'inconclusive');
});

test('a pinned unsupported over an invalid unrelated control is recorded as the contract being more cautious', () => {
    const report = build(evidenceWith(ECH, 'erased', state => { state.lines[0] = 'ABCDE'; }));
    assert.equal(byId(report, ECH).conclusion.outcome, 'unsupported');
    const damaged = syntheticEraseCharsHistoryEvidence(runId, 'f'.repeat(64), undefined, (id, role, state) => {
        if (id === ECH && role === 'erased') state.lines[0] = erasedRow('ABCDE', 0, 2);
        if (id === ECH && role === 'wideBefore') state.lines[1] = 'junk';
        return state;
    });
    const item = byId(build(damaged), ECH);
    assert.equal(item.pinned.observation.outcome, 'unsupported');
    assert.equal(item.conclusion.outcome, 'inconclusive');
    assert.deepEqual(item.divergence, { pinnedOutcome: 'unsupported', contractOutcome: 'inconclusive', kind: 'contract-more-cautious-than-pinned' });
});

test('observed spellings never change a grade, and are recorded against ECMA-48', () => {
    const base = build(syntheticEraseCharsHistoryEvidence(runId));
    for (const name of ['omitted', 'explicit0', 'explicit1', 'explicit5', 'wideOversized']) assert.equal(byId(base, ECH).observed.roles[name].matchesModel, true, name);
    const odd = build(evidenceWith(ECH, 'explicit1', state => { state.lines[0] = 'ABCDE'; }));
    assert.equal(byId(odd, ECH).conclusion.outcome, 'supported');
    assert.equal(byId(odd, ECH).observed.roles.explicit1.matchesModel, false);
    assert.equal(byId(odd, ECH).observed.roles.explicit1.differs, 'viewport');
    assert.deepEqual(byId(odd, ED3).conclusion, byId(base, ED3).conclusion);
});

test('the attribute observation separates retained cells, erased cells and the background', () => {
    const report = build(syntheticEraseCharsHistoryEvidence(runId));
    assert.deepEqual(byId(report, ECH).observed.attributes.retainedKeepAttributes, true);
    assert.equal(byId(report, ECH).observed.attributes.erasedNoStaleAttributes, true);
    assert.equal(byId(report, ECH).observed.attributes.createdBlankBackground, 'erased cells carry the current background');
    const defaults = build(evidenceWith(ECH, 'attr', state => { for (const col of [2, 3, 4]) state.cells[`0,${col}`] = { ...state.cells[`0,${col}`], bg: { kind: 'default' } }; }));
    assert.equal(byId(defaults, ECH).conclusion.outcome, 'supported');
    assert.equal(byId(defaults, ECH).observed.attributes.createdBlankBackground, 'erased cells keep the default background');
});

test('the pinned replay equals the live pinned shapes over the observed fixture screens', () => {
    for (const contract of [echContract, ed3Contract]) {
        const screens = syntheticEraseCharsHistoryScreens(contract);
        const replay = replayEraseCharsHistoryPinned(contract, screens);
        assert.equal(replay.pass, true);
        assert.equal(replay.observation.outcome, 'supported');
        assert.equal(replay.assertions[0].expected, contract.pinnedExpected);
        const context = eraseCharsHistoryRecordingContext(contract, screens);
        assert.equal(context.cols, echCols);
        assert.equal(context.getScrollback().screenLines, echRows);
        assert.equal(context.getScrollback().totalLines, echRows);
        context.feed(contract.pinnedFeeds[0]);
        if (contract.caseId === ECH) { assert.equal(context.getCell(0, 0).char, 'A'); assert.deepEqual(context.getCursor(), { x: 0, y: 0, visible: true }); }
        else assert.equal(context.getScrollback().totalLines, echRows + 3);
        context.feed(contract.pinnedFeeds[1]);
        assert.equal(context.fedCount, 2);
        assert.throws(() => context.feed('x'), /differs from the contract/);
        assert.throws(() => eraseCharsHistoryRecordingContext(contract, screens).feed(contract.pinnedFeeds[1]), /differs from the contract/);
        assert.throws(() => eraseCharsHistoryRecordingContext(contract, screens).getText(), /must not read text/);
    }
    // A read at a feed count with no journaled state is refused rather than invented.
    const gappy = eraseCharsHistoryRecordingContext({ ...echContract, pinnedReadAt: { 0: 0 } }, syntheticEraseCharsHistoryScreens(echContract));
    gappy.feed(echContract.pinnedFeeds[0]);
    assert.throws(() => gappy.getCursor(), /read at feed 1 without an observed state/);
});

test('the pinned geometry gates: too few columns or an unusable height is the pinned inconclusive shape', () => {
    const narrow = syntheticEraseCharsHistoryScreens(echContract).map(screen => ({ ...screen, cols: 5 }));
    const replay = replayEraseCharsHistoryPinned(echContract, narrow);
    assert.equal(replay.note, pinnedEraseCharsHistoryNotes.geometry);
    assert.equal(replay.response, JSON.stringify({ cols: 5, rows: echRows }));
    assert.equal(replay.observation.outcome, 'inconclusive');
    const flat = syntheticEraseCharsHistoryScreens(ed3Contract).map(screen => ({ ...screen, rows: 1100 }));
    assert.equal(replayEraseCharsHistoryPinned(ed3Contract, flat).note, pinnedEraseCharsHistoryNotes.historyGeometry);
    const readback = syntheticEraseCharsHistoryScreens(ed3Contract);
    readback[2] = { ...readback[2], rows: echRows + 1 };
    assert.equal(replayEraseCharsHistoryPinned(ed3Contract, readback).note, pinnedEraseCharsHistoryNotes.historyReadback);
});

test('tampered pinned results, run logs and identities are refused', () => {
    const evidence = syntheticEraseCharsHistoryEvidence(runId);
    const mutate = edit => { const copy = clone(evidence); edit(copy); return copy; };
    assert.throws(() => build(mutate(copy => { copy.run.cases[0].pinned.pass = false; })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[1].pinned.observation.outcome = 'unsupported'; })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[1].pinned.response = copy.run.cases[1].pinned.response.replace(`"totalLines":${echRows}`, '"totalLines":0'); })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[0].feeds[2] = 'x'; })), /do not match/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[0].feeds[2] = '\x1b[1X'; })), /do not match/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[1].feeds[2] = '\r\n'.repeat(echRows + 1); })), /do not match/);
    assert.throws(() => build(mutate(copy => { copy.run.cases.reverse(); })), /case order differs/);
    assert.throws(() => build(mutate(copy => { copy.run.probeIds = [...copy.run.probeIds].reverse(); })), /does not cover/);
    assert.throws(() => build(mutate(copy => { copy.adapter.id = 'other'; })), /Invalid Erase-character\/saved-lines adapter identity/);
    assert.throws(() => build(mutate(copy => { copy.adapter.derivedFromAdapterId = 'ntilde-owned-scrollback-grading-v1'; })), /Invalid Erase-character\/saved-lines adapter identity/);
    assert.throws(() => build(mutate(copy => { copy.run.adapterId = 'other'; })), /Invalid Erase-character\/saved-lines adapter identity/);
    assert.throws(() => buildEraseCharsHistoryGradingReport(evidence.frames, evidence.run, 'd'.repeat(32), evidence.adapter), /not bound to this run/);
});

test('mismatched identities: a wrong recorded barrier, cursor or read kind is refused', () => {
    const evidence = syntheticEraseCharsHistoryEvidence(runId);
    const barrier = clone(evidence);
    barrier.run.cases[0].barriers[2] = '\x1b[?1;2c';
    assert.throws(() => build(barrier), /barrier differs from its read frame/);
    const cursor = clone(evidence);
    cursor.run.cases[1].reads[2].cursor = [0, 3];
    assert.throws(() => build(cursor), /cursor differs from its read frame/);
    const kind = clone(evidence);
    kind.run.cases[1].reads[0].kind = 'cells';
    assert.throws(() => build(kind), /Wrong erase.screen.scrollback read kind/);
});

test('stale or edited observations (changed screen with an old digest) are refused', () => {
    const evidence = syntheticEraseCharsHistoryEvidence(runId);
    const stale = clone(evidence);
    frameOf(stale, ECH, 'erased').screen.lines[0] = 'ABCDX';
    assert.throws(() => build(stale), /Changed Erase-character\/saved-lines snapshot evidence/);
    const reordered = clone(evidence);
    [reordered.frames[3], reordered.frames[4]] = [reordered.frames[4], reordered.frames[3]];
    assert.throws(() => build(reordered), /Changed Erase-character\/saved-lines snapshot evidence/);
    const missing = clone(evidence);
    missing.frames.splice(5, 1);
    assert.throws(() => build(missing), /Incomplete Erase-character\/saved-lines snapshot sequence/);
});

test('missing history contents or counts are refused, however the digest is recomputed', () => {
    const lost = clone(syntheticEraseCharsHistoryEvidence(runId));
    const frame = frameOf(lost, ED3, 'seed');
    frame.screen.history.lines.pop();
    refreeze(frame);
    assert.throws(() => build(lost), /history|Invalid/i);
    const count = clone(syntheticEraseCharsHistoryEvidence(runId));
    const counted = frameOf(count, ED3, 'before');
    counted.screen.bufferState.scrollbackLines = 7;
    refreeze(counted);
    assert.throws(() => build(count), /history|Invalid/i);
    const absent = clone(syntheticEraseCharsHistoryEvidence(runId));
    const bare = frameOf(absent, ED3, 'seed');
    delete bare.screen.history;
    refreeze(bare);
    assert.throws(() => build(absent));
});

test('an edited screen with a recomputed digest is caught by the pinned agreement, not the digest', () => {
    const copy = clone(syntheticEraseCharsHistoryEvidence(runId));
    const frame = frameOf(copy, ECH, 'erased');
    frame.screen.lines[0] = 'DE';
    refreeze(frame);
    assert.throws(() => build(copy), /does not match its observed evidence/);
    const history = clone(syntheticEraseCharsHistoryEvidence(runId));
    const after = frameOf(history, ED3, 'after');
    after.screen.history = { startLine: 0, totalLines: 3, lines: ['', '', ''] };
    after.screen.bufferState.scrollbackLines = 3;
    refreeze(after);
    assert.throws(() => build(history), /does not match its observed evidence/);
});

test('unrelated-state drift: margins, modes, buffer and cursor visibility changes between reads are graded, not ignored', () => {
    for (const [role, change] of [['seed', state => { state.top = 4; }], ['initial', state => { state.alternate = true; }], ['erase3', state => { state.details = { autoWrap: false }; }]]) {
        const report = build(evidenceWith(ED3, role, change));
        assert.notEqual(byId(report, ED3).conclusion.outcome, 'supported', role);
    }
    for (const role of ['initial', 'wideBefore']) {
        const report = build(evidenceWith(ECH, role, state => { state.details = { originMode: true }; }));
        assert.equal(byId(report, ECH).conclusion.outcome, 'inconclusive', role);
    }
});

test('inconsistent flags and details: attribute runs that contradict the stored details are refused', () => {
    const copy = clone(syntheticEraseCharsHistoryEvidence(runId));
    const frame = frameOf(copy, ECH, 'seed');
    frame.screen.attributeRuns[0].flags = 0x0301;
    refreeze(frame);
    assert.throws(() => build(copy));
    const colors = clone(syntheticEraseCharsHistoryEvidence(runId));
    const details = frameOf(colors, ECH, 'attr');
    details.screen.cellDetails[0].fg = 5;
    refreeze(details);
    assert.throws(() => build(colors));
});

test('incomplete restoration is refused', () => {
    const copy = clone(syntheticEraseCharsHistoryEvidence(runId));
    const last = copy.frames.at(-1);
    last.screen.lines[0] = 'left over';
    refreeze(last);
    assert.throws(() => build(copy), /was not restored/);
    const history = clone(syntheticEraseCharsHistoryEvidence(runId));
    const finish = history.frames.at(-1);
    finish.screen.history = { startLine: 0, totalLines: 1, lines: ['stale'] };
    finish.screen.bufferState.scrollbackLines = 1;
    refreeze(finish);
    assert.throws(() => build(history), /was not restored/);
});

test('DECSCNM on throughout is a stable but unusable run: every case is inconclusive, never graded', () => {
    const everywhere = syntheticEraseCharsHistoryEvidence(runId, 'f'.repeat(64), { ...paletteBinding(), reverseVideo: true });
    assert.deepEqual(build(everywhere).cases.map(item => item.conclusion.outcome), ['inconclusive', 'inconclusive']);
    const copy = clone(syntheticEraseCharsHistoryEvidence(runId));
    const frame = frameOf(copy, ECH, 'seed');
    frame.screen.colorBinding.reverseVideo = true;
    refreeze(frame);
    assert.throws(() => build(copy), /palette or theme change/);
});

test('a theme or palette change during the suite voids the run', () => {
    const copy = clone(syntheticEraseCharsHistoryEvidence(runId));
    const frame = frameOf(copy, ED3, 'regrow');
    frame.screen.colorBinding.themeName = 'other';
    refreeze(frame);
    assert.throws(() => build(copy), /palette or theme change/);
});

test('contract-version pinning and evidence validation entry', () => {
    const evidence = syntheticEraseCharsHistoryEvidence(runId);
    assert.doesNotThrow(() => validateEraseCharsHistoryEvidence(evidence.frames, evidence.run, evidence.runId, evidence.adapter));
    assert.throws(() => buildEraseCharsHistoryGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter, { contractsVersion: 2 }),
        /Unknown erase-character\/saved-lines contracts revision 2/);
});
