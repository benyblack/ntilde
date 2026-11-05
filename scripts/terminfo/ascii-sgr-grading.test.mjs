import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildAsciiSgrGradingReport } from './ascii-sgr-grading.mjs';
import { asciiSgrContracts, asciiSgrSuiteId, contractFeeds, gradeRecordedCase } from './ascii-sgr-contracts.mjs';
import { syntheticGradingEvidence, syntheticCaseScreens } from './fixtures/ascii-sgr-evidence.mjs';
import { recordingContext } from './ascii-sgr-collector.mjs';
import { snapshotHash } from './agent-observer.mjs';

const report = () => {
    const evidence = syntheticGradingEvidence();
    return { evidence, report: buildAsciiSgrGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter) };
};

/** Drive a recording context exactly like a pinned termless probe would. */
function faithfulReplay(probeId, ctx) {
    const contract = asciiSgrContracts.find(item => item.caseId === probeId);
    const reads = contract.plan.filter(step => step.read);
    const groups = [];
    for (const [index, bytes] of contractFeeds(contract).slice(1).entries()) {
        ctx.feed(bytes);
        groups.push(reads[index].read === 'text' ? ctx.getText() : [ctx.getCell(0, 0), ctx.getCell(0, 1)]);
    }
    const response = probeId === 'text.basic'
        ? JSON.stringify({ before: groups[0], after: groups[1] })
        : probeId === 'text.overwrite'
            ? JSON.stringify({ before: groups[0], first: groups[1][0], second: groups[1][1] })
            : JSON.stringify(groups[0][0]);
    const expected = probeId.startsWith('sgr.')
        ? `${probeId}: attribute applied to the rendered X cell`
        : contract.pinnedCriterion;
    return { probeId, pass: true, response,
        observation: { outcome: 'supported', evidence: 'parser-state' },
        assertions: [{ kind: 'positive', expected, observed: response }] };
}

test('rebuilds a complete graded report with four supported conclusions', () => {
    const { evidence, report: value } = report();
    assert.equal(value.suiteId, asciiSgrSuiteId);
    assert.equal(value.contractsVersion, 1);
    assert.equal(value.initialSnapshot, 0);
    assert.equal(value.restoredSnapshot, 8);
    assert.deepEqual(value.cases.map(item => [item.caseId, item.conclusion.outcome]),
        [['text.basic', 'supported'], ['text.overwrite', 'supported'], ['sgr.bold', 'supported'], ['sgr.italic', 'supported']]);
    for (const item of value.cases) {
        assert.equal(item.conclusion.evidence, 'owned-cells');
        assert.equal(item.pinned.observation.evidence, 'parser-state');
        assert.equal(item.pinned.observation.outcome, item.conclusion.outcome);
        assert.deepEqual(item.feeds, contractFeeds(asciiSgrContracts.find(contract => contract.caseId === item.caseId)));
        item.barriers.every(barrier => /^\x1b\[\?[\d;]+c$/.test(barrier));
    }
    const sgr = value.cases.filter(item => item.caseId.startsWith('sgr.'));
    assert.ok(sgr.every(item => item.conclusion.scope.includes('stored')));
    assert.ok(sgr.every(item => !item.conclusion.scope.includes('glyph appearance is claimed')));
    assert.equal(value.snapshots.length, 9);
});

test('rejects wrong input variants, missing captures and reordered cases', () => {
    const base = report();
    const wrongBytes = structuredClone(base.evidence);
    wrongBytes.run.cases[2].feeds[1] = '\x1b[2mX';
    assert.throws(() => buildAsciiSgrGradingReport(wrongBytes.frames, wrongBytes.run, wrongBytes.runId, wrongBytes.adapter),
        /do not match the pinned contract/);

    const missingRead = structuredClone(base.evidence);
    missingRead.frames.splice(6, 1);
    assert.throws(() => buildAsciiSgrGradingReport(missingRead.frames, missingRead.run, missingRead.runId, missingRead.adapter),
        /Incomplete graded snapshot sequence/);

    const missingCapture = structuredClone(base.evidence);
    missingCapture.run.cases[0].reads.pop();
    assert.throws(() => buildAsciiSgrGradingReport(missingCapture.frames, missingCapture.run, missingCapture.runId, missingCapture.adapter),
        /Invalid text.basic graded capture record/);

    const reordered = structuredClone(base.evidence);
    [reordered.run.cases[2], reordered.run.cases[3]] = [reordered.run.cases[3], reordered.run.cases[2]];
    assert.throws(() => buildAsciiSgrGradingReport(reordered.frames, reordered.run, reordered.runId, reordered.adapter),
        /Graded case order differs/);
});

test('rejects tampered observations and barrier evidence', () => {
    const base = report();
    const tamperedScreen = structuredClone(base.evidence);
    tamperedScreen.frames[3].screen.lines[0] = 'Hello!';
    assert.throws(() => buildAsciiSgrGradingReport(tamperedScreen.frames, tamperedScreen.run, tamperedScreen.runId, tamperedScreen.adapter),
        /Changed graded snapshot evidence/);

    const tamperedHash = structuredClone(base.evidence);
    tamperedHash.frames[3].sha256 = '0'.repeat(64);
    assert.throws(() => buildAsciiSgrGradingReport(tamperedHash.frames, tamperedHash.run, tamperedHash.runId, tamperedHash.adapter),
        /Changed graded snapshot evidence/);

    const missingBarrier = structuredClone(base.evidence);
    missingBarrier.frames[2].barrier = null;
    assert.throws(() => buildAsciiSgrGradingReport(missingBarrier.frames, missingBarrier.run, missingBarrier.runId, missingBarrier.adapter),
        /Missing graded ordering barrier evidence/);

    const badRunBarrier = structuredClone(base.evidence);
    badRunBarrier.run.cases[1].barriers[0] = '\x1b[6n';
    assert.throws(() => buildAsciiSgrGradingReport(badRunBarrier.frames, badRunBarrier.run, badRunBarrier.runId, badRunBarrier.adapter),
        /Invalid text.overwrite graded capture record/);
});

test('rejects mismatched run identities and adapter bindings', () => {
    const base = report();
    const otherRun = structuredClone(base.evidence);
    assert.throws(() => buildAsciiSgrGradingReport(otherRun.frames, otherRun.run, 'b'.repeat(32), otherRun.adapter),
        /not bound to this run/);

    const foreignAdapter = structuredClone(base.evidence);
    foreignAdapter.adapter.id = 'some-other-adapter-v9';
    assert.throws(() => buildAsciiSgrGradingReport(foreignAdapter.frames, foreignAdapter.run, foreignAdapter.runId, foreignAdapter.adapter),
        /Invalid grading adapter identity/);

    const foreignRunAdapter = structuredClone(base.evidence);
    foreignRunAdapter.run.adapterId = 'some-other-adapter-v9';
    assert.throws(() => buildAsciiSgrGradingReport(foreignRunAdapter.frames, foreignRunAdapter.run, foreignRunAdapter.runId, foreignRunAdapter.adapter),
        /Invalid grading adapter identity/);

    const wrongProbeList = structuredClone(base.evidence);
    wrongProbeList.run.probeIds = ['text.basic', 'text.overwrite', 'sgr.bold'];
    assert.throws(() => buildAsciiSgrGradingReport(wrongProbeList.frames, wrongProbeList.run, wrongProbeList.runId, wrongProbeList.adapter),
        /does not cover the contracted cases/);
});

test('incomplete restoration and changed geometry prevent any conclusion', () => {
    const base = report();
    const unrestored = structuredClone(base.evidence);
    unrestored.frames[8].screen.cursorRow = 2;
    unrestored.frames[8].sha256 = snapshotHash(unrestored.frames[8].screen);
    assert.throws(() => buildAsciiSgrGradingReport(unrestored.frames, unrestored.run, unrestored.runId, unrestored.adapter),
        /Graded fixture was not restored/);

    const resized = structuredClone(base.evidence);
    resized.frames[4].screen.rows = 7;
    // Geometry tampering is refused by the state validator before comparison.
    assert.throws(() => buildAsciiSgrGradingReport(resized.frames, resized.run, resized.runId, resized.adapter),
        /Invalid or unsupported observer geometry\/state/);
});

test('a measured mismatch stays unsupported only while its control holds', () => {
    const evidence = syntheticGradingEvidence();
    // Bold flag removed: control (rendered X) still valid, target mismatch.
    const noBold = structuredClone(syntheticCaseScreens('sgr.bold')[0]);
    noBold.attributeRuns = [{ count: 360, flags: 0, fg: -1, bg: -1 }];
    const noBoldCell = { char: 'X', flags: 0, bold: false, italic: false };
    evidence.frames[6].screen = noBold;
    evidence.frames[6].sha256 = snapshotHash(noBold);
    evidence.run.cases[2].pinned = { probeId: 'sgr.bold', pass: false,
        response: JSON.stringify(noBoldCell),
        observation: { outcome: 'unsupported', evidence: 'parser-state' },
        assertions: [{ kind: 'negative', expected: 'sgr.bold: attribute applied to the rendered X cell',
            observed: JSON.stringify(noBoldCell) }] };
    const value = buildAsciiSgrGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
    assert.equal(value.cases[2].conclusion.outcome, 'unsupported');

    // Missing rendered X: no control, so the outcome must not be conclusive.
    const noX = structuredClone(noBold);
    noX.lines = ['Y', '', '', '', '', ''];
    const noXCell = { char: 'Y', flags: 0, bold: false, italic: false };
    evidence.frames[6].screen = noX;
    evidence.frames[6].sha256 = snapshotHash(noX);
    evidence.run.cases[2].pinned = { probeId: 'sgr.bold', pass: false, response: JSON.stringify(noXCell),
        note: 'No rendered X cell to evaluate',
        observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'parser-state', note: 'No rendered X cell to evaluate' } };
    const inconclusive = buildAsciiSgrGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
    assert.equal(inconclusive.cases[2].conclusion.outcome, 'inconclusive');
    assert.equal(inconclusive.cases[2].conclusion.note, 'No rendered X cell to evaluate');
});

test('divergence between the pinned grading and the contract grader is refused', () => {
    const base = report();
    const diverged = structuredClone(base.evidence);
    diverged.run.cases[0].pinned.observation.outcome = 'unsupported';
    assert.throws(() => buildAsciiSgrGradingReport(diverged.frames, diverged.run, diverged.runId, diverged.adapter),
        /Pinned and contract text.basic grading disagree|does not match its observed evidence/);

    const missingPinned = structuredClone(base.evidence);
    delete missingPinned.run.cases[1].pinned;
    assert.throws(() => buildAsciiSgrGradingReport(missingPinned.frames, missingPinned.run, missingPinned.runId, missingPinned.adapter),
        /Missing pinned text.overwrite grading result/);
});

test('fabricated pinned assertions, notes and responses are refused', () => {
    const base = report();
    for (const [name, mutate] of [
        ['fabricated assertion detail', pinned => { pinned.assertions[0].expected = 'made up criterion'; }],
        ['dropped assertion', pinned => { delete pinned.assertions; }],
        ['added note', pinned => { pinned.note = 'made up note'; }],
        ['wrong pass flag', pinned => { pinned.pass = false; }],
        ['wrong response', pinned => { pinned.response = '{"fabricated":true}'; }],
        ['fabricated observation note on a supported result', pinned => { pinned.observation.note = 'made up note'; }],
        ['fabricated observation reason on a supported result', pinned => { pinned.observation.reason = 'insufficient-evidence'; }],
        ['dropped observation evidence', pinned => { delete pinned.observation.evidence; }],
    ]) {
        const tampered = structuredClone(base.evidence);
        mutate(tampered.run.cases[2].pinned);
        assert.throws(() => buildAsciiSgrGradingReport(tampered.frames, tampered.run, tampered.runId, tampered.adapter),
            /does not match its observed evidence|Missing pinned/, name);
    }
});

test('the replay module must reproduce the live pinned grading exactly', () => {
    const base = report();
    const replayed = buildAsciiSgrGradingReport(base.evidence.frames, base.evidence.run, base.evidence.runId, base.evidence.adapter,
        { ntildeRunTermless: faithfulReplay });
    assert.equal(replayed.cases[0].conclusion.outcome, 'supported');
    assert.deepEqual(replayed.cases.map(item => item.conclusion.outcome), ['supported', 'supported', 'supported', 'supported']);

    const unfaithful = { ntildeRunTermless: () => ({ observation: { outcome: 'inconclusive', evidence: 'parser-state' } }) };
    assert.throws(() => buildAsciiSgrGradingReport(base.evidence.frames, base.evidence.run, base.evidence.runId, base.evidence.adapter, unfaithful),
        /replay does not reproduce the live grading/);
});

test('the recording context serves the pinned probe reads in plan order', () => {
    const contract = asciiSgrContracts.find(item => item.caseId === 'text.overwrite');
    const screens = syntheticCaseScreens('text.overwrite');
    const ctx = recordingContext(contract, screens);
    ctx.feed('AB');
    assert.equal(ctx.getCell(0, 0).char, 'A');
    assert.equal(ctx.getCell(0, 1).char, 'B');
    ctx.feed('\x1b[1GC');
    assert.equal(ctx.getCell(0, 0).char, 'C');
    assert.equal(ctx.getCell(0, 1).char, 'B');

    assert.throws(() => recordingContext(contract, screens).feed('AX'), /feed 0 differs/);
    const misordered = recordingContext(contract, screens);
    misordered.feed('AB');
    assert.throws(() => misordered.getText(), /read order differs/);
});
