import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildEraseAttrsRegionGradingReport, replayEraseAttrsRegionPinned, validateEraseAttrsRegionEvidence } from './erase-attrs-region-grading.mjs';
import { eraseAttrsRegionContracts, eraseAttrsRegionSuiteId, eraseAttrsRegionContractsVersion, pinnedElSetupNote, pinnedElUnavailableNote,
    pinnedEdSetupNote, pinnedEdPreservedNote, pinnedEdGeometryNote } from './erase-attrs-region-contracts.mjs';
import { eraseAttrsRegionRecordingContext } from './erase-attrs-region-collector.mjs';
import { snapshotHash } from './agent-observer.mjs';
import { syntheticEraseAttrsRegionEvidence, syntheticEraseAttrsRegionReads, syntheticEraseAttrsRegionScreens, blankRow, textCells, paletteBinding,
    colorCell } from './fixtures/erase-attrs-region-evidence.mjs';

const EL = 'erase.el-with-attrs', ED = 'erase.ed-scroll-region';
const runId = 'c'.repeat(32);
const green = { bg: { kind: 'palette', index: 2 } };
const margins = { scrollTop: 2, scrollBottom: 9 };
const evidenceWith = (caseId, edit, binding) => syntheticEraseAttrsRegionEvidence(runId, 'f'.repeat(64), binding, id => {
    const reads = syntheticEraseAttrsRegionReads(id);
    if (id === caseId && edit) edit(reads);
    return reads;
});
const build = evidence => buildEraseAttrsRegionGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
/** A JSON round trip: structuredClone would keep the shared screen and binding objects shared between frames. */
const clone = value => JSON.parse(JSON.stringify(value));
const [elContract, edContract] = eraseAttrsRegionContracts;
/** The journal index of one read of one case. */
const frameOf = (caseId, role) => 2 + eraseAttrsRegionContracts.slice(0, eraseAttrsRegionContracts.findIndex(item => item.caseId === caseId))
    .reduce((sum, item) => sum + item.roles.length, 0) + eraseAttrsRegionContracts.find(item => item.caseId === caseId).roles.indexOf(role);

test('a complete run rederives both cases: pinned and contract agree and nothing diverges', () => {
    const report = build(syntheticEraseAttrsRegionEvidence(runId));
    assert.equal(report.suiteId, eraseAttrsRegionSuiteId);
    assert.equal(report.contractsVersion, eraseAttrsRegionContractsVersion);
    assert.deepEqual(report.cases.map(item => [item.caseId, item.pinned.observation.outcome, item.conclusion.outcome]), [[EL, 'supported', 'supported'], [ED, 'supported', 'supported']]);
    for (const item of report.cases) {
        assert.equal(item.divergence, undefined);
        assert.equal(item.conclusion.grading, 'pinned-termless-plus-contract-v1');
        assert.equal(item.pinned.assertions[0].expected, eraseAttrsRegionContracts.find(contract => contract.caseId === item.caseId).pinnedExpected);
    }
    assert.equal(report.snapshots.length, 3 + 11 + 7);
    assert.match(report.scope, /cursor-query paths never carry a verdict/);
    assert.match(report.scope, /no claim that ED is or is not clipped/);
    assert.match(report.scope, /rendered appearance is not claimed/);
});

test('the pinned responses carry exactly what the pinned probes read', () => {
    const [el, ed] = build(syntheticEraseAttrsRegionEvidence(runId)).cases;
    const before = JSON.parse(el.pinned.response).before, after = JSON.parse(el.pinned.response).after;
    assert.deepEqual(Object.keys(JSON.parse(el.pinned.response)), ['before', 'after'], 'one cell is read twice');
    assert.equal(before.char, 'X');
    assert.deepEqual(before.bg, { r: 0, g: 205, b: 0 });
    assert.equal(after.char, ' ');
    assert.deepEqual(after.bg, before.bg);
    const cursor = { x: 0, y: 2, visible: true };
    assert.equal(ed.pinned.response, JSON.stringify({ before: { control: ['K', 'E', 'E', 'P', '!'], target: ['E', 'R', 'A', 'S', 'E'], cursor },
        after: { control: ['K', 'E', 'E', 'P', '!'], target: [' ', ' ', ' ', ' ', ' '] } }));
});

test('a no-op or wrong-background erase is a recorded pinned-and-contract agreement', () => {
    const noop = build(evidenceWith(EL, reads => { reads.erased = reads.setup; })).cases[0];
    assert.equal(noop.pinned.observation.outcome, 'unsupported');
    assert.equal(noop.conclusion.outcome, 'unsupported');
    assert.equal(noop.divergence, undefined);
    const wrong = build(evidenceWith(EL, reads => { reads.erased = { cells: blankRow(0, { bg: { kind: 'palette', index: 4 } }), cursor: [0, 0] }; })).cases[0];
    assert.equal(wrong.pinned.observation.outcome, 'unsupported');
    assert.equal(wrong.conclusion.outcome, 'unsupported');
});

test('clearing to the default background: the pinned probe cannot measure it (inconclusive) and the contract observes the mismatch (unsupported)', () => {
    const item = build(evidenceWith(EL, reads => { reads.erased = { cells: [], cursor: [0, 0] }; })).cases[0];
    assert.equal(item.pinned.observation.outcome, 'inconclusive');
    assert.equal(item.pinned.observation.note, pinnedElUnavailableNote);
    assert.equal(item.conclusion.outcome, 'unsupported');
    assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'unsupported', kind: 'contract-measured-mismatch' });
});

test('palette versus literal storage is the same resolved color for both graders and is recorded as an observation only', () => {
    const item = build(evidenceWith(EL, reads => { reads.erased = { cells: blankRow(0, { bg: { kind: 'literal', rgb: { r: 0, g: 205, b: 0 } } }), cursor: [0, 0] }; })).cases[0];
    assert.equal(item.pinned.observation.outcome, 'supported');
    assert.equal(item.conclusion.outcome, 'supported');
    assert.equal(item.divergence, undefined);
    assert.equal(item.observed.background.storedRepresentationMatches, false);
    assert.equal(item.observed.background.resolvedMatchesBefore, true);
});

test('damage the pinned probes cannot see is a recorded stricter divergence', () => {
    const fill = build(evidenceWith(EL, reads => { reads.fill = { cells: reads.erased.cells, cursor: [0, 0] }; })).cases[0];
    assert.equal(fill.pinned.observation.outcome, 'supported');
    assert.equal(fill.conclusion.outcome, 'unsupported');
    assert.equal(fill.divergence.kind, 'contract-stricter-than-pinned');
    const row = build(evidenceWith(EL, reads => { reads.erased = { cells: [...blankRow(0, green, 0, 100), ...textCells(0, 'X', green).map(([r, , spec]) => [r, 100, spec])], cursor: [0, 0] }; })).cases[0];
    assert.equal(row.pinned.observation.outcome, 'supported', 'the pinned probe only reads cell (0,0)');
    assert.equal(row.conclusion.outcome, 'unsupported');
    const stray = build(evidenceWith(ED, reads => { reads.erased = { cells: [...reads.erased.cells, [30, 7, colorCell('Z')]], cursor: [2, 0], buffer: margins }; })).cases[1];
    assert.equal(stray.pinned.observation.outcome, 'supported', 'the pinned probe never reads past column 4 or row 2');
    assert.equal(stray.conclusion.outcome, 'unsupported');
    const cursor = build(evidenceWith(ED, reads => { reads.erased = { cells: reads.erased.cells, cursor: [9, 9], buffer: margins }; })).cases[1];
    assert.equal(cursor.pinned.observation.outcome, 'supported', 'the pinned probe never reads the cursor after the erase');
    assert.equal(cursor.divergence.kind, 'contract-stricter-than-pinned');
    const mode = build(evidenceWith(ED, reads => { reads.erased = { ...reads.erased, buffer: { scrollTop: 0, scrollBottom: 39 } }; })).cases[1];
    assert.equal(mode.pinned.observation.outcome, 'supported', 'the pinned probe never reads the margins');
    assert.equal(mode.conclusion.outcome, 'unsupported');
});

test('ED: a retained target and an erased preceding row follow the pinned verdict shapes', () => {
    const retained = build(evidenceWith(ED, reads => { reads.erased = reads.setup; })).cases[1];
    assert.equal(retained.pinned.observation.outcome, 'unsupported');
    assert.equal(retained.conclusion.outcome, 'unsupported');
    const preceding = build(evidenceWith(ED, reads => { reads.erased = { cells: [], cursor: [2, 0], buffer: margins }; })).cases[1];
    assert.equal(preceding.pinned.observation.outcome, 'inconclusive');
    assert.equal(preceding.pinned.observation.note, pinnedEdPreservedNote);
    assert.equal(preceding.pinned.assertions, null);
    assert.equal(preceding.conclusion.outcome, 'unsupported');
    assert.equal(preceding.divergence.kind, 'contract-measured-mismatch');
});

test('an invalid setup is inconclusive in both graders with the pinned note', () => {
    const el = build(evidenceWith(EL, reads => { reads.setup = { cells: textCells(0, 'XXXXX'), cursor: [0, 0] }; })).cases[0];
    assert.equal(el.pinned.observation.outcome, 'inconclusive');
    assert.equal(el.pinned.observation.note, pinnedElSetupNote);
    assert.equal(el.conclusion.outcome, 'inconclusive');
    const ed = build(evidenceWith(ED, reads => { reads.setup = { ...reads.setup, cursor: [2, 1] }; })).cases[1];
    assert.equal(ed.pinned.observation.outcome, 'inconclusive');
    assert.equal(ed.pinned.observation.note, pinnedEdSetupNote);
    assert.equal(ed.conclusion.outcome, 'inconclusive');
});

test('a pinned supported over an incomplete restoration is recorded as the contract being stricter, never supported', () => {
    const pen = build(evidenceWith(EL, reads => { reads.restored = { cells: [[0, 0, colorCell('W', green)]], cursor: [0, 1] }; })).cases[0];
    assert.equal(pen.pinned.observation.outcome, 'supported');
    assert.equal(pen.conclusion.outcome, 'inconclusive');
    assert.deepEqual(pen.divergence, { pinnedOutcome: 'supported', contractOutcome: 'inconclusive', kind: 'contract-stricter-than-pinned' });
    const margin = build(evidenceWith(ED, reads => { reads.restored = { ...reads.restored, buffer: margins }; })).cases[1];
    assert.equal(margin.pinned.observation.outcome, 'supported');
    assert.equal(margin.conclusion.outcome, 'inconclusive');
    assert.equal(margin.divergence.kind, 'contract-stricter-than-pinned');
});

test('the pinned replay equals the live pinned shapes over the observed fixture screens, and the geometry gate is the pinned shape', () => {
    for (const contract of eraseAttrsRegionContracts) {
        const screens = syntheticEraseAttrsRegionScreens(contract.caseId);
        const replay = replayEraseAttrsRegionPinned(contract, screens);
        assert.equal(replay.pass, true);
        assert.equal(replay.assertions[0].expected, contract.pinnedExpected);
        const context = eraseAttrsRegionRecordingContext(contract, screens);
        assert.equal(context.cols, 140);
        assert.deepEqual(context.getScrollback(), { screenLines: 40, totalLines: 40, viewportY: 0 });
        assert.throws(() => context.getCell(0, 0), /before its fixture/);
        assert.throws(() => context.getCursor(), /before its fixture/);
        context.feed(contract.pinnedFeeds[0]);
        assert.equal(context.getCell(0, 0).char, contract.caseId === EL ? 'X' : 'K');
        context.feed(contract.pinnedFeeds[1]);
        assert.equal(context.getCell(0, 0).char, contract.caseId === EL ? ' ' : 'K');
        context.feed(contract.pinnedFeeds[2]);
        assert.equal(context.fedCount, 3);
        assert.ok(context.complete);
        assert.throws(() => context.feed('x'), /differs from the contract/);
        const early = eraseAttrsRegionRecordingContext(contract, screens);
        early.feed(contract.pinnedFeeds[0]);
        assert.equal(early.complete, false);
        early.feed(contract.pinnedFeeds[2]);
        assert.equal(early.complete, true, 'an early return still runs the pinned finally feed');
        assert.throws(() => early.feed(contract.pinnedFeeds[1]), /differs from the contract/);
        assert.throws(() => eraseAttrsRegionRecordingContext(contract, screens).feed(contract.pinnedFeeds[1]), /differs from the contract/);
        assert.throws(() => eraseAttrsRegionRecordingContext(contract, screens).getText(), /must not read text/);
    }
    const narrow = syntheticEraseAttrsRegionScreens(ED).map(screen => ({ ...screen, cols: 9 }));
    const gate = replayEraseAttrsRegionPinned(edContract, narrow);
    assert.equal(gate.note, pinnedEdGeometryNote, 'parserStateResult carries the note at both levels');
    assert.equal(gate.observation.note, pinnedEdGeometryNote);
    assert.equal(gate.response, JSON.stringify({ rows: 40, cols: 9 }));
    assert.equal(gate.observation.outcome, 'inconclusive');
    assert.throws(() => replayEraseAttrsRegionPinned({ ...elContract, caseId: 'erase.line.right' }, syntheticEraseAttrsRegionScreens(EL)), /Unknown erase-attributes case/);
});

test('tampered pinned results, run logs and identities are refused', () => {
    const evidence = syntheticEraseAttrsRegionEvidence(runId);
    const mutate = edit => { const copy = clone(evidence); edit(copy); return copy; };
    assert.throws(() => build(mutate(copy => { copy.run.cases[0].pinned.pass = false; })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[1].pinned.observation.outcome = 'unsupported'; })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[0].pinned.response = copy.run.cases[0].pinned.response.replace('"X"', '"Y"'); })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[0].feeds[1] = 'x'; })), /do not match/);
    assert.throws(() => build(mutate(copy => { copy.run.cases.reverse(); })), /case order differs/);
    assert.throws(() => build(mutate(copy => { copy.run.probeIds = [...copy.run.probeIds].reverse(); })), /does not cover/);
    assert.throws(() => build(mutate(copy => { copy.adapter.id = 'other'; })), /Invalid Erase attributes and margins adapter identity/);
    assert.throws(() => build(mutate(copy => { copy.adapter.derivedFromAdapterId = 'ntilde-owned-scrollback-grading-v1'; })), /Invalid Erase attributes and margins adapter identity/);
    assert.throws(() => build(mutate(copy => { copy.run.adapterId = 'other'; })), /Invalid Erase attributes and margins adapter identity/);
    assert.throws(() => buildEraseAttrsRegionGradingReport(evidence.frames, evidence.run, 'd'.repeat(32), evidence.adapter), /not bound to this run/);
});

test('mismatched identities: a wrong recorded barrier, cursor or read kind is refused', () => {
    const evidence = syntheticEraseAttrsRegionEvidence(runId);
    const barrier = clone(evidence);
    barrier.run.cases[0].barriers[3] = '\x1b[?1;2c';
    assert.throws(() => build(barrier), /barrier differs from its read frame/);
    const cursor = clone(evidence);
    cursor.run.cases[1].reads[2].cursor = [0, 3];
    assert.throws(() => build(cursor), /cursor differs from its read frame/);
    const kind = clone(evidence);
    kind.run.cases[1].reads[0].kind = 'text';
    assert.throws(() => build(kind), /Wrong erase\.ed-scroll-region read kind/);
});

test('stale or edited observations (changed screen with an old digest) are refused', () => {
    const evidence = syntheticEraseAttrsRegionEvidence(runId);
    const stale = clone(evidence);
    stale.frames[frameOf(EL, 'setup')].screen.lines[0] = 'XXXXY';
    assert.throws(() => build(stale), /Changed Erase attributes and margins snapshot evidence/);
    const reordered = clone(evidence);
    [reordered.frames[3], reordered.frames[4]] = [reordered.frames[4], reordered.frames[3]];
    assert.throws(() => build(reordered), /Changed Erase attributes and margins snapshot evidence/);
    const missing = clone(evidence);
    missing.frames.splice(5, 1);
    assert.throws(() => build(missing), /Incomplete Erase attributes and margins snapshot sequence/);
});

test('an edited screen with a recomputed digest is caught by the pinned agreement, not the digest', () => {
    const copy = clone(syntheticEraseAttrsRegionEvidence(runId));
    const frame = copy.frames[frameOf(ED, 'setup')];
    frame.screen.lines[2] = 'ERASX';
    frame.sha256 = snapshotHash(frame.screen);
    assert.throws(() => build(copy));
});

test('stale palette bindings and inconsistent flags or details are refused', () => {
    const evidence = syntheticEraseAttrsRegionEvidence(runId);
    const stale = clone(evidence);
    const frame = stale.frames[frameOf(EL, 'setup')];
    frame.screen.colorBinding.ansiPaletteArgb[2] = 0xff123456;
    frame.sha256 = snapshotHash(frame.screen);
    assert.throws(() => build(stale), /resolved|palette or theme change/i);
    const flags = clone(evidence);
    const flagged = flags.frames[frameOf(EL, 'setup')];
    flagged.screen.attributeRuns[0].flags = 0x0301;
    flagged.sha256 = snapshotHash(flagged.screen);
    assert.throws(() => build(flags));
    const details = clone(evidence);
    const detailed = details.frames[frameOf(EL, 'setup')];
    detailed.screen.cellDetails[0].bg = 5;
    detailed.sha256 = snapshotHash(detailed.screen);
    assert.throws(() => build(details));
});

test('screens without buffer state or with an incomplete history are refused', () => {
    const evidence = syntheticEraseAttrsRegionEvidence(runId);
    const noState = clone(evidence);
    delete noState.frames[frameOf(ED, 'setup')].screen.bufferState;
    noState.frames[frameOf(ED, 'setup')].sha256 = snapshotHash(noState.frames[frameOf(ED, 'setup')].screen);
    assert.throws(() => build(noState));
    const history = clone(evidence);
    history.frames[frameOf(ED, 'setup')].screen.history.lines = ['x'];
    history.frames[frameOf(ED, 'setup')].sha256 = snapshotHash(history.frames[frameOf(ED, 'setup')].screen);
    assert.throws(() => build(history));
});

test('incomplete restoration of the disposable screen is refused', () => {
    const copy = clone(syntheticEraseAttrsRegionEvidence(runId));
    const last = copy.frames.at(-1);
    last.screen.lines[0] = 'left over';
    last.sha256 = snapshotHash(last.screen);
    assert.throws(() => build(copy), /was not restored/);
    const margin = clone(syntheticEraseAttrsRegionEvidence(runId));
    const finish = margin.frames.at(-1);
    finish.screen.bufferState.scrollTop = 2;
    finish.sha256 = snapshotHash(finish.screen);
    assert.throws(() => build(margin), /was not restored/);
});

test('DECSCNM on throughout is a stable but unusable run; a palette change mid-run voids it', () => {
    const everywhere = syntheticEraseAttrsRegionEvidence(runId, 'f'.repeat(64), { ...paletteBinding(), reverseVideo: true });
    assert.deepEqual(build(everywhere).cases.map(item => item.conclusion.outcome), ['inconclusive', 'inconclusive']);
    const theme = clone(syntheticEraseAttrsRegionEvidence(runId));
    theme.frames[4].screen.colorBinding.themeName = 'other';
    theme.frames[4].sha256 = snapshotHash(theme.frames[4].screen);
    assert.throws(() => build(theme), /palette or theme change/);
});

test('contract-version pinning and evidence validation entry', () => {
    const evidence = syntheticEraseAttrsRegionEvidence(runId);
    assert.doesNotThrow(() => validateEraseAttrsRegionEvidence(evidence.frames, evidence.run, evidence.runId, evidence.adapter));
    assert.throws(() => buildEraseAttrsRegionGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter, { contractsVersion: 2 }), /Unknown erase-attributes contracts revision 2/);
});
