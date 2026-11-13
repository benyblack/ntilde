import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildSelectionColorsGradingReport, replaySelectionColorQuery, replaySelectionColorResetTermless, reconcileSelectionColors,
    renderSamples, validateSelectionColorsEvidence, selectionColorsCounts } from './selection-colors-grading.mjs';
import { selectionColorsContracts, selectionColorsFixture, selectionColorsCaseIds, selectionColorsSuiteId } from './selection-colors-contracts.mjs';
import { selectionColorsAdapterId } from './selection-colors-grading-collector.mjs';
import { syntheticSelectionColorsEvidence, syntheticSelectionColorsReports, cloneSelectionColorsEvidence } from './fixtures/selection-colors-evidence.mjs';
import { screenSnapshot, snapshotHash } from './agent-observer.mjs';

test('the set-case pinned replay reproduces the pinned oscReplyResult over one reply, key order included', () => {
    const contract = selectionColorsContracts[0];
    const complete = replaySelectionColorQuery(contract, [{ raw: '\x1b]17;rgb:aaaa/bbbb/cccc\x1b\\' }]);
    assert.equal(complete.pass, true);
    assert.deepEqual(complete.observation, { outcome: 'supported', evidence: 'query' });
    assert.deepEqual(complete.assertions, [{ kind: 'positive', expected: 'Complete OSC 17 color reply', observed: '\x1b]17;rgb:aaaa/bbbb/cccc\x1b\\' }]);
    assert.deepEqual(Object.keys(complete), ['pass', 'response', 'observation', 'assertions']);
    const absent = replaySelectionColorQuery(contract, [null]);
    assert.equal(absent.pass, false);
    assert.deepEqual(absent.observation, { outcome: 'inconclusive', reason: 'no-response', evidence: 'query' });
    assert.equal(JSON.stringify(Object.keys(absent)), '["pass","response","observation"]');
    const prefixed = replaySelectionColorQuery(contract, [{ raw: '\x1b]17;' }]);
    assert.equal(prefixed.observation.reason, 'invalid-reply', 'a prefix without a complete reply is invalid, never absence');
});

test('the reset-case pinned replay is the verbatim notTestedResult over the journaled cell (0,0)', () => {
    const contract = selectionColorsContracts[2];
    const replay = replaySelectionColorResetTermless(contract, { lines: ['X'] });
    assert.deepEqual(replay, {
        pass: false,
        response: '{"cell":{"char":"X"}}',
        note: 'highlight background color',
        notTested: { reason: 'no-semantic-observable', noObservable: 'highlight background color' },
    });
    assert.deepEqual(JSON.stringify(Object.keys(replay)), '["pass","response","note","notTested"]');
    const mirror = replaySelectionColorResetTermless(selectionColorsContracts[3], { lines: [''] });
    assert.equal(mirror.note, 'highlight foreground color');
});

test('the reconciliation records every verdict difference without collapsing any, and never allows a more permissive contract', () => {
    const setCase = selectionColorsContracts[0], resetCase = selectionColorsContracts[2];
    assert.equal(reconcileSelectionColors(setCase, 'supported', { outcome: 'supported' }), null);
    assert.equal(reconcileSelectionColors(resetCase, 'inconclusive', { outcome: 'inconclusive' }), null);
    assert.deepEqual(reconcileSelectionColors(setCase, 'inconclusive', { outcome: 'supported' }),
        { pinnedOutcome: 'inconclusive', contractOutcome: 'supported', kind: 'pinned-query-only-unset-default' });
    assert.deepEqual(reconcileSelectionColors(resetCase, 'inconclusive', { outcome: 'supported' }),
        { pinnedOutcome: 'inconclusive', contractOutcome: 'supported', kind: 'pinned-inconclusive-by-design' });
    assert.deepEqual(reconcileSelectionColors(setCase, 'inconclusive', { outcome: 'unsupported' }),
        { pinnedOutcome: 'inconclusive', contractOutcome: 'unsupported', kind: 'contract-measured-mismatch' });
    assert.throws(() => reconcileSelectionColors(setCase, 'unsupported', { outcome: 'supported' }), /more permissive/);
    assert.throws(() => reconcileSelectionColors(setCase, 'supported', { outcome: 'unsupported' }), /disagree/);
});

test('the render samples read what the synthetic render drew: fill, ink, control and background', () => {
    const evidence = syntheticSelectionColorsEvidence('a'.repeat(32));
    const baselineImage = evidence.images.get(evidence.frames[0].pixel.file);
    const overrideFrame = evidence.frames[4]; // case 0 set-query override-a (selection background = aa/bb/cc)
    const samples = renderSamples(evidence.images.get(overrideFrame.pixel.file), overrideFrame.pixel.renderDetails, baselineImage);
    assert.equal(samples.background, 0xff000000);
    assert.equal(samples.controlUnchanged, true);
    assert.equal(samples.selectionBlank.solid, 0xffaabbcc, 'the selected blank row is the override fill');
    assert.equal(samples.selectionBlank.matchesBaseline, false);
    assert.equal(samples.selectedInk.dominantMatchesBaseline, true, 'the ink itself is unchanged by a background override');
    assert.equal(samples.selectedInk.firstValueCoverage > 10, false, 'the ink does not carry the override value');
    const inkFrame = evidence.frames[12]; // case 1 set-query override-a (selection foreground = aa/bb/cc)
    const inkSamples = renderSamples(evidence.images.get(inkFrame.pixel.file), inkFrame.pixel.renderDetails, baselineImage);
    assert.equal(inkSamples.selectedInk.firstValueCoverage > 10, true, 'the selected ink carries the first override value');
    assert.equal(inkSamples.selectedInk.secondValueCoverage, 0, 'and not the second one');
});

test('the report builder rederives the synthetic run, records the pinned verdicts beside the effect verdicts and counts the conclusions', () => {
    const evidence = syntheticSelectionColorsEvidence('a'.repeat(32));
    const report = syntheticSelectionColorsReports(evidence);
    assert.equal(report.suiteId, selectionColorsSuiteId);
    assert.deepEqual(report.cases.map(item => item.caseId), [...selectionColorsCaseIds]);
    assert.deepEqual(report.cases.map(item => item.conclusion.outcome), ['supported', 'supported', 'supported', 'supported']);
    assert.deepEqual(report.cases.map(item => item.divergence?.kind ?? null),
        ['pinned-query-only-unset-default', 'pinned-query-only-unset-default', 'pinned-inconclusive-by-design', 'pinned-inconclusive-by-design']);
    assert.deepEqual(report.cases.map(item => item.pinned.observation?.outcome ?? item.pinned.notTested?.reason),
        ['inconclusive', 'inconclusive', 'no-semantic-observable', 'no-semantic-observable']);
    assert.deepEqual(report.cases.map(item => item.pinnedTerminal.outcome), ['inconclusive', 'inconclusive', 'inconclusive', 'inconclusive']);
    assert.deepEqual(report.cases.map(item => item.pinnedTerminal.byDesign), [false, false, true, true]);
    assert.deepEqual(selectionColorsCounts(report), { supported: 4 });
    assert.equal(report.cases[0].pinnedCriterion.includes('feedCaptures "\\u001b]17;?\\u0007"') || report.cases[0].pinnedCriterion.includes('\\u001b]17'), true);
});

test('the evidence validation refuses every tampering with a specific failure', () => {
    const evidence = syntheticSelectionColorsEvidence('a'.repeat(32), 'f'.repeat(64), {});
    const captureOf = frame => ({ details: frame.pixel.renderDetails, image: evidence.images.get(frame.pixel.file) });
    const valid = () => validateSelectionColorsEvidence(evidence.frames, evidence.run, evidence.runId, evidence.adapter, captureOf);
    assert.doesNotThrow(valid);

    const changedJournal = cloneSelectionColorsEvidence(evidence);
    for (const frame of [changedJournal.frames.at(-1)]) {
        frame.screen.lines[3] = '    tampered';
        frame.sha256 = snapshotHash(frame.screen);
        frame.afterScreen = structuredClone(frame.screen);
        frame.afterSha256 = frame.sha256;
    }
    let refused = null;
    try { validateSelectionColorsEvidence(changedJournal.frames, changedJournal.run, changedJournal.runId, changedJournal.adapter, captureOf); }
    catch (error) { refused = String(error.message); }
    assert.ok(refused !== null, 'a tampered finish frame is refused');
    assert.ok(/not restored|snapshot evidence/.test(refused), `the refusal names the restoration: ${refused}`);
    // A mid-journal text tamper is refused where the graded checks run: the observations no longer rederive from the journal.
    const midJournal = cloneSelectionColorsEvidence(evidence);
    midJournal.frames[3].screen.lines[3] = '    tampered';
    let midRefused = null;
    try { syntheticSelectionColorsReports(midJournal); }
    catch (error) { midRefused = String(error.message); }
    assert.ok(midRefused !== null, 'a doctored mid-journal screen is refused somewhere in the rederivation');

    const wrongAdapter = cloneSelectionColorsEvidence(evidence);
    wrongAdapter.adapter = { ...wrongAdapter.adapter, id: 'someone-else' };
    assert.throws(() => validateSelectionColorsEvidence(wrongAdapter.frames, wrongAdapter.run, wrongAdapter.runId, wrongAdapter.adapter, captureOf), /adapter identity/);

    const wrongOrigin = cloneSelectionColorsEvidence(evidence);
    wrongOrigin.adapter = { ...wrongOrigin.adapter, derivedFromAdapterId: 'ntilde-owned-osc133-grading-v1' };
    assert.throws(() => validateSelectionColorsEvidence(wrongOrigin.frames, wrongOrigin.run, wrongOrigin.runId, wrongOrigin.adapter, captureOf), /adapter identity/);

    const wrongCases = cloneSelectionColorsEvidence(evidence);
    wrongCases.run.cases = wrongCases.run.cases.slice(1);
    assert.throws(() => validateSelectionColorsEvidence(wrongCases.frames, wrongCases.run, wrongCases.runId, wrongCases.adapter, captureOf), /contracted cases/);

    const wrongNonce = cloneSelectionColorsEvidence(evidence);
    wrongNonce.frames[1].screen.lines[0] = 'NTILDE_CR_' + 'a'.repeat(32);
    wrongNonce.frames[1].sha256 = snapshotHash(wrongNonce.frames[1].screen);
    wrongNonce.frames[1].afterScreen = structuredClone(wrongNonce.frames[1].screen);
    wrongNonce.frames[1].afterSha256 = wrongNonce.frames[1].sha256;
    assert.throws(() => validateSelectionColorsEvidence(wrongNonce.frames, wrongNonce.run, wrongNonce.runId, wrongNonce.adapter, captureOf), /bound to this run/);

    const replyFrame = cloneSelectionColorsEvidence(evidence);
    replyFrame.frames[0].queries = [{ spec: { code: 17 }, barrier: '\x1b[?62;4;22c', reply: null }];
    assert.throws(() => validateSelectionColorsEvidence(replyFrame.frames, replyFrame.run, replyFrame.runId, replyFrame.adapter, captureOf), /reply frame/);

    const wrongBinding = cloneSelectionColorsEvidence(evidence);
    wrongBinding.frames[3].pixel.scale = 2;
    assert.throws(() => validateSelectionColorsEvidence(wrongBinding.frames, wrongBinding.run, wrongBinding.runId, wrongBinding.adapter, captureOf), /capture binding/);

    const drifted = cloneSelectionColorsEvidence(evidence);
    // The fixture's start, bind and finish frames share the pristine host screen (and its binding reference); the drift re-hashes every
    // frame it reaches, and the theme-identity check (name, reverse video, minimum contrast — constant by contract) refuses the drift.
    drifted.frames[0].screen.colorBinding.minimumContrast = 2;
    for (const frame of drifted.frames) {
        if (frame.sha256 !== snapshotHash(frame.screen)) {
            frame.sha256 = snapshotHash(frame.screen);
            frame.afterScreen = structuredClone(frame.screen);
            frame.afterSha256 = frame.sha256;
        }
    }
    assert.throws(() => validateSelectionColorsEvidence(drifted.frames, drifted.run, drifted.runId, drifted.adapter, captureOf), /theme identity change/);

    assert.throws(() => buildSelectionColorsGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter, { replayModule: null }),
        /needs its retained captures/);
});

test('a changed run log or a doctored observation file is refused by the full rederivation', () => {
    const evidence = syntheticSelectionColorsEvidence('a'.repeat(32), 'f'.repeat(64), {});
    const pristine = syntheticSelectionColorsReports(evidence);
    assert.deepEqual(pristine.cases.map(item => item.conclusion.outcome), ['supported', 'supported', 'supported', 'supported']);
    const tampered = cloneSelectionColorsEvidence(evidence);
    tampered.run.cases[0].reads[2].queries[0].reply = null;
    assert.throws(() => syntheticSelectionColorsReports(tampered), /replies differ from their read frame/, 'the rederivation refuses a reply swapped after the fact');
});
