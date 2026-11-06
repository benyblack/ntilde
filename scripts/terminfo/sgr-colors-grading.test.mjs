import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { buildSgrColorsGradingReport, replaySgrColorsPinned } from './sgr-colors-grading.mjs';
import { sgrColorsContracts, sgrColorsSuiteId, sgrColorsFeeds, sgrColorsCellAt } from './sgr-colors-contracts.mjs';
import { sgrColorsAdapterId } from './sgr-colors-grading-collector.mjs';
import { syntheticSgrColorsEvidence, syntheticSgrColorsScreen } from './fixtures/sgr-colors-evidence.mjs';
import { sgrColorsRecordingContext } from './sgr-colors-collector.mjs';
import { snapshotHash } from './agent-observer.mjs';

const report = () => {
    const evidence = syntheticSgrColorsEvidence();
    return { evidence, report: buildSgrColorsGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter) };
};

test('rebuilds a complete graded report with five supported conclusions', () => {
    const { evidence, report: value } = report();
    assert.equal(value.suiteId, sgrColorsSuiteId);
    assert.equal(value.contractsVersion, 2);
    assert.equal(value.snapshots.length, 8);
    assert.deepEqual(value.cases.map(item => [item.caseId, item.conclusion.outcome, item.pinned.observation.outcome]),
        [['sgr.selective-reset.bold', 'supported', 'supported'],
            ['sgr.selective-reset.italic', 'supported', 'supported'],
            ['sgr.reset', 'supported', 'supported'],
            ['sgr.fg.standard', 'supported', 'inconclusive'],
            ['sgr.bg.standard', 'supported', 'inconclusive']]);
    for (const item of value.cases) {
        assert.equal(item.conclusion.evidence, 'owned-cells');
        assert.equal(item.pinned.observation.evidence, 'parser-state');
        assert.deepEqual(item.feeds, sgrColorsFeeds(sgrColorsContracts.find(contract => contract.caseId === item.caseId)));
        item.barriers.every(barrier => /^\x1b\[\?[\d;]+c$/.test(barrier));
    }
    const colors = value.cases.filter(item => item.caseId.startsWith('sgr.fg.') || item.caseId.startsWith('sgr.bg.'));
    assert.ok(colors.every(item => item.conclusion.scope.includes('stays inconclusive')));
    assert.ok(colors.every(item => item.pinnedExpectation === 'inapplicable'));
    const resets = value.cases.filter(item => item.pinnedExpectation === 'measured');
    assert.ok(resets.every(item => item.conclusion.scope.includes('stored style flags')));
});

test('rejects wrong input variants, missing captures and reordered cases', () => {
    const base = report();
    const wrongBytes = structuredClone(base.evidence);
    wrongBytes.run.cases[3].feeds[1] = 'C\x1b[32mX\x1b[34mY';
    assert.throws(() => buildSgrColorsGradingReport(wrongBytes.frames, wrongBytes.run, wrongBytes.runId, wrongBytes.adapter),
        /do not match the pinned contract/);

    const missingRead = structuredClone(base.evidence);
    missingRead.frames.splice(6, 1);
    assert.throws(() => buildSgrColorsGradingReport(missingRead.frames, missingRead.run, missingRead.runId, missingRead.adapter),
        /Incomplete SGR-colors snapshot sequence/);

    const missingCapture = structuredClone(base.evidence);
    missingCapture.run.cases[0].reads.pop();
    assert.throws(() => buildSgrColorsGradingReport(missingCapture.frames, missingCapture.run, missingCapture.runId, missingCapture.adapter),
        /Invalid sgr.selective-reset.bold graded capture record/);

    const reordered = structuredClone(base.evidence);
    [reordered.run.cases[0], reordered.run.cases[1]] = [reordered.run.cases[1], reordered.run.cases[0]];
    assert.throws(() => buildSgrColorsGradingReport(reordered.frames, reordered.run, reordered.runId, reordered.adapter),
        /case order differs/);
});

test('rejects tampered observations, barriers and cursors', () => {
    const base = report();
    const tamperedScreen = structuredClone(base.evidence);
    // Keep the run structure canonical: X loses only its bold flag.
    tamperedScreen.frames[4].screen.attributeRuns[1] = { count: 1, flags: 768 | 0x04 | 0x08, fg: -1, bg: -1 };
    assert.throws(() => buildSgrColorsGradingReport(tamperedScreen.frames, tamperedScreen.run, tamperedScreen.runId, tamperedScreen.adapter),
        /does not match its observed evidence|Changed SGR-colors snapshot evidence/);

    const tamperedHash = structuredClone(base.evidence);
    tamperedHash.frames[4].sha256 = '0'.repeat(64);
    assert.throws(() => buildSgrColorsGradingReport(tamperedHash.frames, tamperedHash.run, tamperedHash.runId, tamperedHash.adapter),
        /Changed SGR-colors snapshot evidence/);

    const missingBarrier = structuredClone(base.evidence);
    missingBarrier.frames[2].barrier = null;
    assert.throws(() => buildSgrColorsGradingReport(missingBarrier.frames, missingBarrier.run, missingBarrier.runId, missingBarrier.adapter),
        /Missing SGR-colors ordering barrier evidence/);

    const badRunBarrier = structuredClone(base.evidence);
    badRunBarrier.run.cases[1].barriers[0] = '\x1b[6n';
    assert.throws(() => buildSgrColorsGradingReport(badRunBarrier.frames, badRunBarrier.run, badRunBarrier.runId, badRunBarrier.adapter),
        /Invalid sgr.selective-reset.italic graded capture record/);

    const barrierDrift = structuredClone(base.evidence);
    barrierDrift.frames[3].barrier = '\x1b[?62;4;23c';
    assert.throws(() => buildSgrColorsGradingReport(barrierDrift.frames, barrierDrift.run, barrierDrift.runId, barrierDrift.adapter),
        /barrier differs from its read frame/);

    const cursorDrift = structuredClone(base.evidence);
    cursorDrift.run.cases[2].reads[0].cursor = [4, 4];
    assert.throws(() => buildSgrColorsGradingReport(cursorDrift.frames, cursorDrift.run, cursorDrift.runId, cursorDrift.adapter),
        /cursor differs from its read frame/);
});

test('rejects mismatched run identities and adapter bindings', () => {
    const base = report();
    const otherRun = structuredClone(base.evidence);
    assert.throws(() => buildSgrColorsGradingReport(otherRun.frames, otherRun.run, 'b'.repeat(32), otherRun.adapter),
        /not bound to this run/);

    const foreignAdapter = structuredClone(base.evidence);
    foreignAdapter.adapter.id = 'some-other-adapter-v9';
    assert.throws(() => buildSgrColorsGradingReport(foreignAdapter.frames, foreignAdapter.run, foreignAdapter.runId, foreignAdapter.adapter),
        /Invalid SGR-colors adapter identity/);

    const wrongLayer = structuredClone(base.evidence);
    wrongLayer.adapter.derivedFromAdapterId = 'ntilde-owned-macos-v1';
    assert.throws(() => buildSgrColorsGradingReport(wrongLayer.frames, wrongLayer.run, wrongLayer.runId, wrongLayer.adapter),
        /Invalid SGR-colors adapter identity/);

    const wrongProbeList = structuredClone(base.evidence);
    wrongProbeList.run.probeIds = ['sgr.reset'];
    assert.throws(() => buildSgrColorsGradingReport(wrongProbeList.frames, wrongProbeList.run, wrongProbeList.runId, wrongProbeList.adapter),
        /does not cover the contracted cases/);
});

test('incomplete restoration prevents any conclusion', () => {
    const base = report();
    const unrestored = structuredClone(base.evidence);
    // Replace (not mutate): the start and finish frames may share a screen object.
    const drifted = { ...unrestored.frames[7].screen, cursorRow: 2 };
    unrestored.frames[7] = { ...unrestored.frames[7], screen: drifted, sha256: snapshotHash(drifted) };
    assert.throws(() => buildSgrColorsGradingReport(unrestored.frames, unrestored.run, unrestored.runId, unrestored.adapter),
        /SGR-colors fixture was not restored/);
});

test('fabricated pinned results are refused', () => {
    const base = report();
    for (const [name, caseIndex, mutate] of [
        ['fabricated assertion detail', 2, pinned => { pinned.assertions[0].expected = 'made up criterion'; }],
        ['dropped assertions', 2, pinned => { delete pinned.assertions; }],
        ['added note', 2, pinned => { pinned.note = 'made up note'; }],
        ['wrong pass flag', 2, pinned => { pinned.pass = false; }],
        ['wrong response', 2, pinned => { pinned.response = '{"fabricated":true}'; }],
        ['fabricated observation note on a measured support', 2, pinned => { pinned.observation.note = 'made up'; }],
        ['fabricated observation reason', 2, pinned => { pinned.observation.reason = 'insufficient-evidence'; }],
        ['invented pinned supported verdict on a color case', 3, pinned => {
            pinned.pass = true;
            pinned.note = undefined;
            pinned.observation = { outcome: 'supported', evidence: 'parser-state' };
        }],
    ]) {
        const tampered = structuredClone(base.evidence);
        mutate(tampered.run.cases[caseIndex].pinned);
        assert.throws(() => buildSgrColorsGradingReport(tampered.frames, tampered.run, tampered.runId, tampered.adapter),
            /does not match its observed evidence|not the expected inapplicable-inconclusive/, name);
    }
});

test('a measured mismatch stays unsupported only while its control holds', () => {
    const evidence = syntheticSgrColorsEvidence();
    // SGR 22 also cleared the preserved italic: control holds, target mismatch.
    const screen = structuredClone(syntheticSgrColorsScreen('sgr.selective-reset.bold'));
    // Y keeps a non-plain attribute so the runs stay canonical: italic was
    // cleared too, which is the measured mismatch under test.
    screen.attributeRuns[2] = { count: 1, flags: 768 | 0x08, fg: -1, bg: -1 };
    evidence.frames[2].screen = screen;
    evidence.frames[2].sha256 = snapshotHash(screen);
    const contract = sgrColorsContracts.find(item => item.caseId === 'sgr.selective-reset.bold');
    const pinned = { probeId: contract.caseId, ...replaySgrColorsPinned(contract, [screen]) };
    evidence.run.cases[0].pinned = pinned;
    const value = buildSgrColorsGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
    assert.equal(value.cases[0].conclusion.outcome, 'unsupported');

    // Initially-unset control failure: the setup never became measurable.
    const stale = structuredClone(syntheticSgrColorsScreen('sgr.selective-reset.bold'));
    stale.attributeRuns = [{ count: 360, flags: 768, fg: -1, bg: -1 }];
    evidence.frames[2].screen = stale;
    evidence.frames[2].sha256 = snapshotHash(stale);
    evidence.run.cases[0].pinned = { probeId: contract.caseId, ...replaySgrColorsPinned(contract, [stale]) };
    const inconclusive = buildSgrColorsGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
    assert.equal(inconclusive.cases[0].conclusion.outcome, 'inconclusive');
    assert.equal(inconclusive.cases[0].conclusion.note, 'Neutral baseline, styled setup, or reset readback was not measured');
});

test('the replay module must reproduce the live pinned grading exactly', () => {
    const base = report();
    const faithful = { ntildeRunSgrColorsTermless: (probeId, ctx) => {
        const contract = sgrColorsContracts.find(item => item.caseId === probeId);
        const live = base.report.cases.find(item => item.caseId === probeId).pinned;
        contract.plan.filter(step => step.feed).slice(1).forEach(() => ctx.feed(''));
        return { probeId, ...live };
    } };
    const replayed = buildSgrColorsGradingReport(base.evidence.frames, base.evidence.run, base.evidence.runId, base.evidence.adapter, { replayModule: faithful });
    assert.equal(replayed.cases[0].conclusion.outcome, 'supported');

    const unfaithful = { ntildeRunSgrColorsTermless: () => ({ pass: false, response: 'x',
        observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'parser-state', note: 'x' } }) };
    assert.throws(() => buildSgrColorsGradingReport(base.evidence.frames, base.evidence.run, base.evidence.runId, base.evidence.adapter, { replayModule: unfaithful }),
        /replay does not reproduce the live grading/);
});

test('a stricter contract mismatch is published as unsupported with the divergence recorded', () => {
    const evidence = syntheticSgrColorsEvidence();
    // SGR 22 cleared bold and dim but left a palette color on Y: the pinned
    // criterion still passes (it only reads style flags) while the contract
    // grader measures the color corruption.
    const screen = structuredClone(syntheticSgrColorsScreen('sgr.selective-reset.bold'));
    screen.attributeRuns[2] = { count: 1, flags: 768 | 0x04 | 0x4000, fg: 3, bg: -1 };
    evidence.frames[2].screen = screen;
    evidence.frames[2].sha256 = snapshotHash(screen);
    const contract = sgrColorsContracts.find(item => item.caseId === 'sgr.selective-reset.bold');
    evidence.run.cases[0].pinned = { probeId: contract.caseId, ...replaySgrColorsPinned(contract, [screen]) };
    const value = buildSgrColorsGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
    assert.equal(value.cases[0].conclusion.outcome, 'unsupported');
    assert.equal(value.cases[0].pinned.observation.outcome, 'supported');
    assert.deepEqual(value.cases[0].divergence, { pinnedOutcome: 'supported', contractOutcome: 'unsupported' });
});

test('the recording context serves the pinned probe reads in plan order', () => {
    const contract = sgrColorsContracts.find(item => item.caseId === 'sgr.reset');
    const screen = syntheticSgrColorsScreen('sgr.reset');
    const ctx = sgrColorsRecordingContext(contract, [screen]);
    ctx.feed('C\x1b[1;3;4mX\x1b[0mY');
    assert.equal(ctx.getCell(0, 0).char, 'C');
    assert.equal(ctx.getCell(0, 1).bold, true);
    assert.equal(ctx.getCell(0, 2).bold, false);

    assert.throws(() => sgrColorsRecordingContext(contract, [screen]).feed('C\x1b[1mX\x1b[0mY'), /feed 0 differs/);
    const misused = sgrColorsRecordingContext(contract, [screen]);
    misused.feed('C\x1b[1;3;4mX\x1b[0mY');
    assert.throws(() => misused.getText(), /must not read text/);
    assert.equal(sgrColorsCellAt(screen, 0, 1).underline, true);
});
