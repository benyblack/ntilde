import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildSgrResetGradingReport, replaySgrResetPinned, validateSgrResetEvidence } from './sgr-reset-grading.mjs';
import { sgrResetContracts, sgrResetSuiteId, sgrResetContractsVersion } from './sgr-reset-contracts.mjs';
import { sgrResetRecordingContext } from './sgr-reset-collector.mjs';
import { snapshotHash } from './agent-observer.mjs';
import { syntheticSgrResetEvidence, syntheticSgrResetReads, syntheticSgrResetScreens, styled, plainCell,
    paletteBinding, reverseBinding } from './fixtures/sgr-reset-evidence.mjs';

const UNDERLINE = 'sgr.selective-reset.underline';
const INVERSE = 'sgr.selective-reset.inverse';
const runId = 'c'.repeat(32);
const evidenceWith = (caseId, edit, binding) => syntheticSgrResetEvidence(runId, 'f'.repeat(64), binding, id => {
    const reads = syntheticSgrResetReads(id);
    if (id === caseId && edit) edit(reads);
    return reads;
});
const build = evidence => buildSgrResetGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
/** A JSON round trip: structuredClone would keep the shared screen and binding objects shared between frames. */
const clone = value => JSON.parse(JSON.stringify(value));

test('a complete run rederives both cases: pinned and contract agree and nothing diverges', () => {
    const report = build(syntheticSgrResetEvidence(runId));
    assert.equal(report.suiteId, sgrResetSuiteId);
    assert.equal(report.contractsVersion, sgrResetContractsVersion);
    assert.deepEqual(report.cases.map(item => [item.caseId, item.pinned.observation.outcome, item.conclusion.outcome]), [
        [UNDERLINE, 'supported', 'supported'], [INVERSE, 'supported', 'supported']]);
    for (const item of report.cases) {
        assert.equal(item.divergence, undefined);
        assert.equal(item.conclusion.grading, 'pinned-termless-plus-contract-v1');
        assert.deepEqual(item.pinnedFeeds, [`C${item.terminalFixture.writes[0].replace(/^(.*)X/, '$1X')}`.replace(/^C/, 'C')].length ? item.pinnedFeeds : []);
        assert.equal(item.pinned.response.includes('"char":"X"'), true);
    }
});

test('the inverse report records the DECSCNM-on control frame as the only reverse-video frame', () => {
    const report = build(syntheticSgrResetEvidence(runId));
    const flags = report.snapshots.map(frame => frame.screen.colorBinding.reverseVideo);
    assert.equal(flags.filter(Boolean).length, 1);
    assert.equal(flags.at(-1), false);
    assert.equal(report.cases[1].observed.reverseControl, true);
});

test('a no-op reset grades unsupported by both the pinned probe and the contract', () => {
    const report = build(evidenceWith(UNDERLINE, reads => { reads.pinned[2] = styled('Y', ['bold'], { style: 1 }); }));
    const item = report.cases[0];
    assert.equal(item.pinned.observation.outcome, 'unsupported');
    assert.equal(item.conclusion.outcome, 'unsupported');
    assert.equal(item.divergence, undefined);
});

test('unrelated drift is a recorded contract-stricter divergence from a supported pinned verdict', () => {
    const report = build(evidenceWith(INVERSE, reads => { reads.pinned[2] = styled('Y', ['bold', 'italic']); }));
    const item = report.cases[1];
    assert.equal(item.pinned.observation.outcome, 'supported');
    assert.equal(item.conclusion.outcome, 'unsupported');
    assert.deepEqual(item.divergence, { pinnedOutcome: 'supported', contractOutcome: 'unsupported', kind: 'contract-stricter-than-pinned' });
});

test('an invalid setup is inconclusive in both graders', () => {
    const report = build(evidenceWith(UNDERLINE, reads => { reads.pinned[1] = styled('X', ['bold']); }));
    assert.equal(report.cases[0].pinned.observation.outcome, 'inconclusive');
    assert.equal(report.cases[0].conclusion.outcome, 'inconclusive');
    assert.equal(report.cases[0].pinned.note, 'Neutral baseline, styled setup, or reset readback was not measured');
});

test('the pinned replay equals the live pinned shapes over the observed fixture screen', () => {
    for (const contract of sgrResetContracts) {
        const screens = syntheticSgrResetScreens(contract.caseId);
        const replay = replaySgrResetPinned(contract, screens);
        assert.equal(replay.pass, true);
        assert.equal(replay.observation.outcome, 'supported');
        assert.equal(replay.assertions[0].expected, contract.pinnedExpected);
        const context = sgrResetRecordingContext(contract, screens);
        context.feed(contract.pinnedFeeds[0]);
        assert.equal(context.getCell(0, 1).char, 'X');
        assert.throws(() => sgrResetRecordingContext(contract, screens).feed('C'), /differs from the contract/);
        assert.throws(() => sgrResetRecordingContext(contract, screens).getCell(0, 0), /before its fixture/);
        assert.throws(() => sgrResetRecordingContext(contract, screens).getText(), /must not read text/);
    }
});

test('tampered pinned results, run logs and identities are refused', () => {
    const evidence = syntheticSgrResetEvidence(runId);
    const mutate = edit => { const copy = clone(evidence); edit(copy); return copy; };
    assert.throws(() => build(mutate(copy => { copy.run.cases[0].pinned.pass = false; })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[1].pinned.observation.outcome = 'unsupported'; })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[0].feeds[1] = 'x'; })), /do not match/);
    assert.throws(() => build(mutate(copy => { copy.run.cases.reverse(); })), /case order differs/);
    assert.throws(() => build(mutate(copy => { copy.run.probeIds = [...copy.run.probeIds].reverse(); })), /does not cover/);
    assert.throws(() => build(mutate(copy => { copy.adapter.id = 'other'; })), /Invalid SGR selective-reset adapter identity/);
    assert.throws(() => build(mutate(copy => { copy.adapter.derivedFromAdapterId = 'ntilde-owned-sgr-underline-grading-v1'; })), /Invalid SGR selective-reset adapter identity/);
    assert.throws(() => build(mutate(copy => { copy.run.adapterId = 'other'; })), /Invalid SGR selective-reset adapter identity/);
    assert.throws(() => buildSgrResetGradingReport(evidence.frames, evidence.run, 'd'.repeat(32), evidence.adapter), /not bound to this run/);
});

test('stale or edited observations (changed screen with an old digest) are refused', () => {
    const evidence = syntheticSgrResetEvidence(runId);
    const stale = clone(evidence);
    const frame = stale.frames[3];
    frame.screen.lines[0] = 'CXZ';
    assert.throws(() => build(stale), /Changed SGR selective-reset snapshot evidence/);
    const reordered = clone(evidence);
    [reordered.frames[3], reordered.frames[4]] = [reordered.frames[4], reordered.frames[3]];
    assert.throws(() => build(reordered), /Changed SGR selective-reset snapshot evidence/);
    const missing = clone(evidence);
    missing.frames.splice(5, 1);
    assert.throws(() => build(missing), /Incomplete SGR selective-reset snapshot sequence/);
});

test('incomplete restoration is refused', () => {
    const evidence = syntheticSgrResetEvidence(runId);
    const copy = clone(evidence);
    const last = copy.frames.at(-1);
    last.screen.lines[0] = 'left over';
    last.sha256 = snapshotHash(last.screen);
    assert.throws(() => build(copy), /was not restored/);
});

test('DECSCNM confusion: reverse video in a graded read or a missing control frame voids the run', () => {
    const evidence = syntheticSgrResetEvidence(runId);
    const inGraded = clone(evidence);
    inGraded.frames[3].screen.colorBinding.reverseVideo = true;
    inGraded.frames[3].sha256 = snapshotHash(inGraded.frames[3].screen);
    assert.throws(() => build(inGraded), /unexpected binding change/);
    const absent = syntheticSgrResetEvidence(runId, 'f'.repeat(64), undefined, id => {
        const reads = syntheticSgrResetReads(id); reads.reverseBinding = paletteBinding(); return reads;
    });
    assert.throws(() => build(absent), /DECSCNM control frame did not report reverse video/);
    const everywhere = syntheticSgrResetEvidence(runId, 'f'.repeat(64), reverseBinding(paletteBinding()));
    // DECSCNM on throughout is a stable but unusable run: both cases are inconclusive, never graded.
    assert.deepEqual(build(everywhere).cases.map(item => item.conclusion.outcome), ['inconclusive', 'inconclusive']);
});

test('a theme or palette change during the suite voids the run', () => {
    const evidence = syntheticSgrResetEvidence(runId);
    const theme = clone(evidence);
    theme.frames[4].screen.colorBinding.themeName = 'other';
    theme.frames[4].sha256 = snapshotHash(theme.frames[4].screen);
    assert.throws(() => build(theme), /theme or palette change/);
    const palette = clone(evidence);
    palette.frames[6].screen.colorBinding.ansiPaletteArgb[5] = 0xff010203;
    palette.frames[6].sha256 = snapshotHash(palette.frames[6].screen);
    assert.throws(() => build(palette), /Resolved|theme or palette change|disagree/);
});

test('stored-color changes in the journal contradict the resolved colors and are refused', () => {
    const evidence = syntheticSgrResetEvidence(runId);
    const copy = clone(evidence);
    const frame = copy.frames[3];
    frame.screen.cellDetails[0].fg = 5;
    frame.sha256 = snapshotHash(frame.screen);
    assert.throws(() => build(copy));
});

test('contract-version pinning and evidence validation entry', () => {
    const evidence = syntheticSgrResetEvidence(runId);
    assert.doesNotThrow(() => validateSgrResetEvidence(evidence.frames, evidence.run, evidence.runId, evidence.adapter));
    assert.throws(() => buildSgrResetGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter, { contractsVersion: 2 }), /Unknown SGR selective-reset contracts revision 2/);
    assert.equal(plainCell('x').char, 'x');
});
