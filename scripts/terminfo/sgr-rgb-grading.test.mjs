import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildSgrRgbGradingReport, replaySgrRgbPinned } from './sgr-rgb-grading.mjs';
import { sgrRgbContracts, sgrRgbSuiteId, sgrRgbFeeds, sgrRgbCellAt } from './sgr-rgb-contracts.mjs';
import { sgrRgbAdapterId } from './sgr-rgb-grading-collector.mjs';
import { syntheticSgrRgbEvidence, syntheticSgrRgbScreen, syntheticColorBinding, rgb } from './fixtures/sgr-rgb-evidence.mjs';
import { sgrRgbRecordingContext } from './sgr-rgb-collector.mjs';
import { snapshotHash } from './agent-observer.mjs';

const report = () => {
    const evidence = syntheticSgrRgbEvidence();
    return { evidence, report: buildSgrRgbGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter) };
};

test('rebuilds a complete graded report with two supported RGB conclusions', () => {
    const { evidence, report: value } = report();
    assert.equal(value.suiteId, sgrRgbSuiteId);
    assert.equal(value.contractsVersion, 1);
    assert.equal(value.snapshots.length, 5);
    assert.deepEqual(value.cases.map(item => [item.caseId, item.conclusion.outcome, item.pinned.observation.outcome]),
        [['sgr.fg.standard', 'supported', 'supported'], ['sgr.bg.standard', 'supported', 'supported']]);
    for (const item of value.cases) {
        assert.equal(item.conclusion.evidence, 'owned-resolved-colors');
        assert.equal(item.pinned.observation.evidence, 'parser-state');
        assert.deepEqual(item.feeds, sgrRgbFeeds(sgrRgbContracts.find(contract => contract.caseId === item.caseId)));
        assert.ok(item.barriers.every(barrier => /^\x1b\[\?[\d;]+c$/.test(barrier)));
        assert.ok(item.conclusion.scope.includes('resolution stage'));
        assert.ok(item.conclusion.scope.includes('stored-index conclusions of the SGR-colors suite'));
        assert.equal(item.colorBinding.stage, 'theme-resolution');
        assert.equal(item.colorBinding.themeName, 'Synthetic Dark');
        assert.match(item.colorBinding.bindingSha256, /^[a-f\d]{64}$/);
        assert.equal(item.pinned.pass, true);
        assert.equal(item.pinned.assertions.length, 1);
        assert.equal(item.pinned.assertions[0].kind, 'positive');
        assert.equal(item.pinned.assertions[0].expected, `${item.caseId}: ${sgrRgbContracts.find(c => c.caseId === item.caseId).pinnedCriterion}`);
    }
    void evidence;
});

test('the pinned response embeds the RGB-carrying projection verbatim', () => {
    const { report: value } = report();
    const fg = value.cases[0];
    const parsed = JSON.parse(fg.pinned.response);
    assert.equal(parsed.baseline.fg, null);
    assert.deepEqual(parsed.first.fg, { r: 205, g: 0, b: 0 });
    assert.deepEqual(parsed.second.fg, { r: 0, g: 0, b: 238 });
    assert.equal(parsed.first.fgSource, 'palette');
    assert.equal(parsed.baseline.fgSource, 'default');
});

test('rejects wrong input variants, missing captures and reordered cases', () => {
    const base = report();
    const wrongBytes = structuredClone(base.evidence);
    wrongBytes.run.cases[0].feeds[1] = 'C\x1b[32mX\x1b[34mY';
    assert.throws(() => buildSgrRgbGradingReport(wrongBytes.frames, wrongBytes.run, wrongBytes.runId, wrongBytes.adapter),
        /do not match the pinned contract/);

    const missingRead = structuredClone(base.evidence);
    missingRead.frames.splice(3, 1);
    assert.throws(() => buildSgrRgbGradingReport(missingRead.frames, missingRead.run, missingRead.runId, missingRead.adapter),
        /Incomplete SGR-RGB snapshot sequence/);

    const missingCapture = structuredClone(base.evidence);
    missingCapture.run.cases[0].reads.pop();
    assert.throws(() => buildSgrRgbGradingReport(missingCapture.frames, missingCapture.run, missingCapture.runId, missingCapture.adapter),
        /Invalid sgr.fg.standard graded capture record/);

    const reordered = structuredClone(base.evidence);
    [reordered.run.cases[0], reordered.run.cases[1]] = [reordered.run.cases[1], reordered.run.cases[0]];
    assert.throws(() => buildSgrRgbGradingReport(reordered.frames, reordered.run, reordered.runId, reordered.adapter),
        /case order differs/);
});

test('rejects tampered observations, hashes and barriers', () => {
    const base = report();
    const tamperedScreen = structuredClone(base.evidence);
    tamperedScreen.frames[2].screen.resolvedColors[1].fg.argb = rgb(1, 2, 3);
    assert.throws(() => buildSgrRgbGradingReport(tamperedScreen.frames, tamperedScreen.run, tamperedScreen.runId, tamperedScreen.adapter),
        /Changed SGR-RGB snapshot evidence|Invalid resolved-color run/);

    const tamperedHash = structuredClone(base.evidence);
    tamperedHash.frames[2].sha256 = '0'.repeat(64);
    assert.throws(() => buildSgrRgbGradingReport(tamperedHash.frames, tamperedHash.run, tamperedHash.runId, tamperedHash.adapter),
        /Changed SGR-RGB snapshot evidence/);

    const missingBarrier = structuredClone(base.evidence);
    missingBarrier.frames[2].barrier = null;
    assert.throws(() => buildSgrRgbGradingReport(missingBarrier.frames, missingBarrier.run, missingBarrier.runId, missingBarrier.adapter),
        /Missing SGR-RGB ordering barrier evidence/);

    const barrierDrift = structuredClone(base.evidence);
    barrierDrift.frames[3].barrier = '\x1b[?62;4;23c';
    assert.throws(() => buildSgrRgbGradingReport(barrierDrift.frames, barrierDrift.run, barrierDrift.runId, barrierDrift.adapter),
        /barrier differs from its read frame/);

    const cursorDrift = structuredClone(base.evidence);
    cursorDrift.run.cases[1].reads[0].cursor = [4, 4];
    assert.throws(() => buildSgrRgbGradingReport(cursorDrift.frames, cursorDrift.run, cursorDrift.runId, cursorDrift.adapter),
        /cursor differs from its read frame/);
});

test('rejects mismatched run identities, adapter bindings and missing RGB data', () => {
    const base = report();
    const otherRun = structuredClone(base.evidence);
    assert.throws(() => buildSgrRgbGradingReport(otherRun.frames, otherRun.run, 'b'.repeat(32), otherRun.adapter),
        /not bound to this run/);

    const foreignAdapter = structuredClone(base.evidence);
    foreignAdapter.adapter.id = 'some-other-adapter-v9';
    assert.throws(() => buildSgrRgbGradingReport(foreignAdapter.frames, foreignAdapter.run, foreignAdapter.runId, foreignAdapter.adapter),
        /Invalid SGR-RGB adapter identity/);

    const wrongLayer = structuredClone(base.evidence);
    wrongLayer.adapter.derivedFromAdapterId = 'ntilde-owned-ascii-sgr-grading-v1';
    assert.throws(() => buildSgrRgbGradingReport(wrongLayer.frames, wrongLayer.run, wrongLayer.runId, wrongLayer.adapter),
        /Invalid SGR-RGB adapter identity/);

    const wrongProbeList = structuredClone(base.evidence);
    wrongProbeList.run.probeIds = ['sgr.reset'];
    assert.throws(() => buildSgrRgbGradingReport(wrongProbeList.frames, wrongProbeList.run, wrongProbeList.runId, wrongProbeList.adapter),
        /does not cover the contracted cases/);

    // Missing resolved RGB data: the projection is absent, never an empty grade.
    const missingRgb = structuredClone(base.evidence);
    delete missingRgb.frames[2].screen.resolvedColors;
    missingRgb.frames[2].sha256 = snapshotHash(missingRgb.frames[2].screen);
    assert.throws(() => buildSgrRgbGradingReport(missingRgb.frames, missingRgb.run, missingRgb.runId, missingRgb.adapter),
        /Invalid resolved-color budget|does not match its observed evidence|Missing resolved cell/);
});

test('a palette or theme change during observation voids the whole run', () => {
    const base = report();
    const changed = structuredClone(base.evidence);
    const later = syntheticColorBinding({ palette: syntheticColorBinding().ansiPaletteArgb.map((value, index) => index === 9 ? rgb(4, 4, 4) : value) });
    // Replace (not mutate): frames may share the binding object.
    changed.frames[3] = { ...changed.frames[3], screen: { ...changed.frames[3].screen, colorBinding: later },
        sha256: snapshotHash({ ...changed.frames[3].screen, colorBinding: later }) };
    assert.throws(() => buildSgrRgbGradingReport(changed.frames, changed.run, changed.runId, changed.adapter),
        /palette or theme change/);
});

test('incomplete restoration prevents any conclusion', () => {
    const base = report();
    const unrestored = structuredClone(base.evidence);
    const drifted = { ...unrestored.frames[4].screen, cursorRow: 2 };
    unrestored.frames[4] = { ...unrestored.frames[4], screen: drifted, sha256: snapshotHash(drifted) };
    assert.throws(() => buildSgrRgbGradingReport(unrestored.frames, unrestored.run, unrestored.runId, unrestored.adapter),
        /SGR-RGB fixture was not restored/);
});

test('fabricated pinned results are refused', () => {
    const base = report();
    for (const [name, mutate] of [
        ['fabricated assertion detail', pinned => { pinned.assertions[0].expected = 'made up criterion'; }],
        ['dropped assertions', pinned => { delete pinned.assertions; }],
        ['wrong pass flag', pinned => { pinned.pass = false; }],
        ['wrong response', pinned => { pinned.response = '{"fabricated":true}'; }],
        ['invented inconclusive verdict', pinned => {
            pinned.pass = false;
            pinned.note = 'made up';
            pinned.observation = { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'parser-state', note: 'made up' };
        }],
    ]) {
        const tampered = structuredClone(base.evidence);
        mutate(tampered.run.cases[0].pinned);
        assert.throws(() => buildSgrRgbGradingReport(tampered.frames, tampered.run, tampered.runId, tampered.adapter),
            /does not match its observed evidence|RGB grading disagree/, name);
    }
});

test('an aliased theme yields both pinned and contract inconclusive verdicts', () => {
    // Entries 1 and 4 alias: distinguishability fails by the pinned semantics,
    // which must stay inconclusive rather than unsupported, and the contract
    // grader must agree with the pinned verdict.
    const aliased = syntheticColorBinding({ palette: syntheticColorBinding().ansiPaletteArgb.map((value, index) => index === 4 ? rgb(205, 0, 0) : value) });
    const evidence = syntheticSgrRgbEvidence('a'.repeat(32), 'f'.repeat(64), aliased);
    const value = buildSgrRgbGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
    for (const item of value.cases) {
        assert.equal(item.conclusion.outcome, 'inconclusive');
        assert.equal(item.conclusion.note, 'Baseline, theme colors, or color readback were indistinguishable');
        assert.equal(item.pinned.observation.outcome, 'inconclusive');
        assert.equal(item.pinned.note, 'Baseline, theme colors, or color readback were indistinguishable');
    }
});

test('the replay module must reproduce the live pinned grading exactly', () => {
    const base = report();
    const faithful = { ntildeRunSgrRgbTermless: (probeId, ctx) => {
        const contract = sgrRgbContracts.find(item => item.caseId === probeId);
        const live = base.report.cases.find(item => item.caseId === probeId).pinned;
        contract.plan.filter(step => step.feed).slice(1).forEach(() => ctx.feed(''));
        return { probeId, ...live };
    } };
    const replayed = buildSgrRgbGradingReport(base.evidence.frames, base.evidence.run, base.evidence.runId, base.evidence.adapter, { replayModule: faithful });
    assert.equal(replayed.cases[0].conclusion.outcome, 'supported');

    const unfaithful = { ntildeRunSgrRgbTermless: () => ({ pass: true, response: 'x',
        observation: { outcome: 'supported', evidence: 'parser-state' }, assertions: [{ kind: 'positive', expected: 'x', observed: 'x' }] }) };
    assert.throws(() => buildSgrRgbGradingReport(base.evidence.frames, base.evidence.run, base.evidence.runId, base.evidence.adapter, { replayModule: unfaithful }),
        /replay does not reproduce the live grading/);
});

test('the recording context serves the pinned probe reads in plan order', () => {
    const contract = sgrRgbContracts.find(item => item.caseId === 'sgr.fg.standard');
    const screen = syntheticSgrRgbScreen('sgr.fg.standard');
    const ctx = sgrRgbRecordingContext(contract, [screen]);
    ctx.feed('C\x1b[31mX\x1b[34mY');
    assert.equal(ctx.getCell(0, 0).fg, null);
    assert.deepEqual(ctx.getCell(0, 1).fg, { r: 205, g: 0, b: 0 });
    assert.equal(ctx.getCell(0, 2).fgIndex, 4);

    assert.throws(() => sgrRgbRecordingContext(contract, [screen]).feed('C\x1b[32mX\x1b[34mY'), /feed 0 differs/);
    const misused = sgrRgbRecordingContext(contract, [screen]);
    misused.feed('C\x1b[31mX\x1b[34mY');
    assert.throws(() => misused.getText(), /must not read text/);
    assert.equal(sgrRgbCellAt(screen, 0, 1).fgSource, 'palette');
    const replayed = replaySgrRgbPinned(contract, [screen]);
    assert.equal(replayed.pass, true);
    assert.equal(replayed.observation.outcome, 'supported');
});
