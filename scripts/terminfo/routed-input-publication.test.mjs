// Frozen real-App publication. Synthetic fixtures do not replace this measurement; every digest below is checked against the
// committed evidence. The publication is split into reviewable files; the test reassembles the exact journal order and re-derives
// the graded report, the owned input report and the pinned-probe replay from those retained bytes.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { buildInputReport, defaultInputModes, inputModeReplyHex } from './input-state.mjs';
import { inputAttemptCount, inputFrameCount, inputPlanV2 } from './input-plan.mjs';
import { makeRoutedInputCases } from './routed-input-cases.mjs';
import { oracleDisagreements, replayRoutedInputPinned, routedInputCaseIds, routedInputEffectContracts, routedInputGradedSuiteId } from './routed-input-contracts.mjs';
import { routedInputAdapterId } from './routed-input-grading-collector.mjs';
import { buildRoutedInputGradingReport, routedInputCounts } from './routed-input-grading.mjs';
import { benchmarkSuites } from './qualification-inventory.mjs';

const dir = new URL('../../docs/qa/terminfo/2026-11-11-routed-input/', import.meta.url);
const read = name => JSON.parse(readFileSync(new URL(name, dir)));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
let cached;
const load = () => cached ??= (() => {
    const summary = read('summary.json'), run = read('run-log.json'), common = read('journal-common.json');
    const plan = makeRoutedInputCases(common.frames.start.screen.rows, common.frames.start.screen.cols);
    const frames = [common.frames.start, common.frames.bind], actions = [];
    plan.forEach((item, index) => {
        const slug = item.id.replace(/^input\./, ''), file = readdirSync(dir).find(name => name.startsWith(`input-${String(index).padStart(2, '0')}-`));
        assert.equal(file, `input-${String(index).padStart(2, '0')}-${slug}.json`);
        const journal = read(file);
        assert.deepEqual([journal.index, journal.caseId], [index, item.id]);
        frames.push(journal.control, journal.read); actions.push(journal.action);
    });
    frames.push(common.frames.finish);
    const cases = routedInputCaseIds.map(id => read(`case-${id}.json`));
    return { summary, run, common, plan, input: { frames, actions, seal: common.seal }, report: { ...common.reportHeader, cases } };
})();
const derive = loaded => buildRoutedInputGradingReport(loaded.input, loaded.run, loaded.summary.context.runId, loaded.report.adapter,
    { contractsVersion: loaded.report.contractsVersion, inputReportSha256: loaded.common.inputReportSha256 });

test('the published evidence re-derives exactly from its retained input journal and run log', () => {
    const loaded = load(), { report, input, summary, common } = loaded;
    assert.equal(JSON.stringify(report), JSON.stringify(derive(loaded)));
    assert.equal(report.suiteId, routedInputGradedSuiteId);
    assert.equal(report.adapter.id, routedInputAdapterId);
    assert.equal(report.adapter.derivedFromAdapterId, 'ntilde-owned-left-right-margins-grading-v1');
    assert.equal(input.frames.length, inputFrameCount(60));
    assert.equal(input.actions.length, 60);
    // The owned input report is re-derived too, and its bytes are the ones the receipt bound.
    const inputReport = buildInputReport(input.frames, input.actions, input.seal, summary.context.runId, inputPlanV2);
    const bytes = JSON.stringify(inputReport, null, 2) + '\n';
    assert.equal(hash(bytes), summary.receipt.inputEvidence.sha256);
    assert.equal(hash(bytes), common.inputReportSha256);
    assert.equal(hash(bytes), report.inputReportSha256);
    assert.deepEqual(summary.receipt.inputEvidence.counts, { passed: 60 });
    assert.deepEqual(common.inputReportHeader, { schemaVersion: 2, suiteId: inputPlanV2.suiteId, runId: summary.context.runId, scope: inputReport.scope,
        initialSnapshot: 0, restoredSnapshot: 122, observationCount: 60 });
    assert.deepEqual(Object.values(summary.inputSuite.results).filter(result => result !== 'passed'), []);
    assert.deepEqual(Object.keys(summary.inputSuite.results), loaded.plan.map(item => item.id));
});

test('the pinned verdicts are preserved inconclusive by design and the separately identified effect contracts are supported', () => {
    const { report } = load();
    assert.deepEqual(report.cases.map(item => item.caseId), ['input.x10-mouse', 'input.button-event-mouse', 'modes.application-keypad']);
    for (const item of report.cases) {
        assert.equal(item.pinned.pass, false);
        assert.equal(item.conclusion.outcome, 'inconclusive');
        assert.equal(item.conclusion.grading, 'pinned-termless-preserved-by-design');
        assert.deepEqual([item.effectContract.id, item.effectContract.outcome], [routedInputEffectContracts[item.caseId], 'supported']);
        assert.match(item.effectContract.attribution, /not the pinned probe/);
        assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'supported', kind: 'pinned-inconclusive-by-design' });
        assert.deepEqual(item.contractResult, { outcome: 'supported' });
    }
    const [x10, button, keypad] = report.cases;
    assert.equal(x10.pinned.response, '{"currentMode":false}');
    assert.equal(button.pinned.response, '{"currentMode":false}');
    assert.equal(x10.pinned.observation.evidence, 'legacy');
    assert.deepEqual(x10.pinnedFeeds, []);
    assert.deepEqual(keypad.pinnedFeeds, ['\x1b=', '\x1b>']);
    assert.equal(keypad.pinned.response, '{"on":true,"off":true}', 'the child reported DECKPAM set after ESC = and reset after ESC >');
    assert.equal(keypad.pinned.observation, null);
    assert.deepEqual(keypad.pinned.notTested, { reason: 'no-semantic-observable', noObservable: 'keypad input' });
    assert.deepEqual(keypad.served, { on: true, off: false, sources: ['input.keypad.application.0', 'input.keypad.restore.deckpnm.enter'] });
});

test('every experiment matched its literal, the independent oracle and the child bytes, with controls proved before any mismatch could count', () => {
    const { report, plan } = load();
    assert.deepEqual(oracleDisagreements(plan), []);
    assert.deepEqual(report.cases.map(item => item.experiments.length), [21, 11, 30]);
    for (const item of report.cases) {
        for (const experiment of item.experiments) {
            assert.equal(experiment.matched, true, experiment.id);
            assert.equal(experiment.actualHex, experiment.expectedHex, experiment.id);
            assert.equal(experiment.oracleHex, experiment.expectedHex, experiment.id);
            assert.deepEqual([experiment.appFacts.eventDispatched, experiment.appFacts.boundary], [true, 'avalonia-routed']);
        }
        assert.ok(item.experiments.some(experiment => experiment.role === 'control'), item.caseId);
        assert.ok(item.experiments.filter(experiment => experiment.role === 'required').length >= 6, item.caseId);
    }
    assert.deepEqual(report.cases[1].experiments.filter(experiment => experiment.role === 'label').map(experiment => experiment.id),
        ['input.mouse.button.press.sgr', 'input.mouse.button.move.sgr', 'input.mouse.button.release.sgr']);
});

test('observed event-to-child behavior: X10 reports modifier-free presses with single-octet high bytes and suppresses everything else', () => {
    const [x10] = load().report.cases, byId = id => x10.experiments.find(experiment => experiment.id === 'input.mouse.' + id);
    for (const id of ['x10.left.nomod.raw', 'x10.left.shift.raw', 'x10.left.ctrl.raw', 'x10.left.alt.raw', 'x10.left.meta.raw', 'x10.left.allmods.raw', 'x10.left.raw', 'x10.left.raw.afterSgrReset']) {
        assert.equal(byId(id).actualHex, '1b5b4d208023', id);
    }
    assert.equal(byId('x10.left.raw').event.modifiers, 7);
    assert.equal(byId('x10.left.allmods.raw').event.modifiers, 15);
    assert.equal(byId('x10.middle.raw').actualHex, '1b5b4d218023');
    assert.equal(byId('x10.right.raw').actualHex, '1b5b4d228023');
    assert.equal(byId('x10.left.sgr').actualHex, Buffer.from('\x1b[<0;96;3M').toString('hex'));
    for (const id of ['x10.release.suppressed', 'x10.move.suppressed', 'x10.hover.suppressed', 'x10.wheel.suppressed', 'x10.wheel.down.suppressed']) {
        assert.equal(byId(id).actualHex, '', id);
        assert.equal(byId(id).appFacts.eventDispatched, true, 'the event was dispatched; silence alone is not the evidence');
    }
    // The same events under the other protocols are reported, so a suppression is not a lost delivery.
    assert.equal(byId('normal.press.mods7.raw.control').actualHex, '1b5b4d3c8023');
    assert.equal(byId('normal.release.raw.control').actualHex, '1b5b4d238023');
    assert.equal(byId('normal.wheel.up.raw.control').actualHex, '1b5b4d608023');
    assert.equal(byId('button.move.left.raw').actualHex, '1b5b4d408023');
    assert.ok(x10.experiments.every(experiment => !experiment.actualHex.includes('c280')), 'no UTF-8 expansion of the 0x80 coordinate byte');
});

test('observed event-to-child behavior: button-event tracking reports press, held motion and release, and suppresses hover behind a completed barrier', () => {
    const { report, input } = load(), [, button] = report.cases, byId = id => button.experiments.find(experiment => experiment.id === 'input.mouse.' + id);
    assert.equal(byId('button.press.left.raw').actualHex, '1b5b4d208023');
    assert.equal(byId('button.press.right.raw').actualHex, '1b5b4d228023');
    assert.equal(byId('button.move.left.raw').actualHex, '1b5b4d408023');
    assert.equal(byId('button.move.middle.shiftalt.raw').actualHex, '1b5b4d4d8123');
    assert.equal(byId('button.release.left.raw').actualHex, '1b5b4d238023');
    assert.equal(byId('button.hover.suppressed').actualHex, '');
    assert.equal(byId('any.hover.raw.control').actualHex, Buffer.from('\x1b[MC~#', 'latin1').toString('hex'), 'the same hover under 1003 is reported');
    assert.equal(byId('normal.move.suppressed').actualHex, '', 'held motion under 1000 is suppressed');
    const hover = input.frames[byId('button.hover.suppressed').snapshot].capture;
    assert.equal(hover.hex, ''); assert.equal(hover.nonces.length, 2);
    assert.equal(hover.barrier, hover.nonces.map(n => `\x1b[?${n};0$y`).join(''), 'the ordering reply completed after the dispatch');
    assert.deepEqual(byId('button.press.sgr').modes, { applicationKeypad: false, mouseTracking: 1002, mouseSgr: true, kittyKeyboardFlags: 0 });
});

test('observed event-to-child behavior: application keypad selection, the 17-key table, duplicate suppression, text and restoration', () => {
    const { report } = load(), [, , keypad] = report.cases, byId = id => keypad.experiments.find(experiment => experiment.id === 'input.keypad.' + id);
    const table = { 0: 'p', 1: 'q', 2: 'r', 3: 's', 4: 't', 5: 'u', 6: 'v', 7: 'w', 8: 'x', 9: 'y', decimal: 'n', separator: 'l', add: 'k', subtract: 'm', multiply: 'j', divide: 'o', enter: 'M' };
    for (const [key, final] of Object.entries(table)) assert.equal(byId('application.' + key).actualHex, Buffer.from('\x1bO' + final).toString('hex'), key);
    assert.equal(byId('application.0').setup, '\x1b=', 'exactly the pinned first feed');
    assert.equal(byId('restore.deckpnm.enter').setup, '\x1b>', 'exactly the pinned second feed');
    for (const id of ['normal.keyDown', 'normal.keyUp', 'application.7.keyUp', 'application.enter.keyUp']) assert.equal(byId(id).actualHex, '', id);
    assert.equal(byId('normal.text').actualHex, '37');
    assert.equal(byId('normal.enter').actualHex, '0d');
    assert.equal(byId('normal.7.withText').actualHex, '37', 'numeric mode delivers the digit once');
    for (const id of ['application.7.withText', 'application.add.withText']) {
        assert.equal(byId(id).actualHex, byId(id.replace('.withText', '')).actualHex, 'the backend text for a handled key adds no byte');
        assert.deepEqual([byId(id).appFacts.handled, byId(id).appFacts.textHandled, byId(id).appFacts.inputSubmitted], [true, true, true], id);
    }
    assert.equal(byId('normal.7.withText').appFacts.handled, false);
    assert.equal(byId('application.7.text.standalone').actualHex, '37', 'a standalone text commit is ordinary text');
    assert.equal(byId('restore.deckpnm.enter').actualHex, '0d', 'ESC > restored numeric Enter');
    assert.equal(byId('decnkm.set.enter').actualHex, '1b4f4d');
    assert.equal(byId('decnkm.reset.enter').actualHex, '0d');
    assert.deepEqual(byId('decnkm.set.enter').modes, { applicationKeypad: true, mouseTracking: 0, mouseSgr: false, kittyKeyboardFlags: 0 });
});

test('modes were restored, the capture reader sealed healthy, and the pinned code agrees with its replay over the child replies', () => {
    const { input, run, report, summary } = load();
    assert.equal(input.seal.failed, false); assert.equal(input.seal.disposed, true); assert.equal(input.seal.attempts, inputAttemptCount(60));
    assert.equal(input.frames.at(-1).capture.hex, inputModeReplyHex(defaultInputModes));
    assert.deepEqual(input.frames.at(-1).screen, input.frames[0].screen, 'the original terminal state was restored');
    for (const [index, item] of run.cases.entries()) {
        const { probeId, ...live } = item.pinned;
        assert.equal(probeId, item.caseId);
        assert.deepEqual(live, replayRoutedInputPinned(item.caseId, item.served));
        assert.deepEqual(report.cases[index].served, item.served);
    }
    assert.equal(summary.context.nodeVersion.startsWith('v24.'), true);
});

test('the receipt binds the run, adapters and counts, and every predecessor and finding reconfirm in the same run', async () => {
    const { summary, run, report } = load(), { receipt } = summary;
    assert.equal(receipt.schemaVersion, 42);
    assert.equal(receipt.routedInputEvidence.runId, summary.context.runId);
    assert.deepEqual(routedInputCounts(report), { 'inconclusive/supported': 3 });
    assert.deepEqual(receipt.routedInputEvidence.counts, routedInputCounts(report));
    assert.equal(receipt.routedInputEvidence.suiteId, routedInputGradedSuiteId);
    assert.equal(receipt.routedInputEvidence.adapterBundleSha256, summary.context.routedInputBundleSha256);
    assert.equal(receipt.routedInputEvidence.adapterBundleSha256, report.adapter.bundleSha256);
    assert.equal(receipt.routedInputEvidence.sha256, hash(JSON.stringify(report, null, 2) + '\n'));
    assert.equal(receipt.routedInputEvidence.runSha256, hash(JSON.stringify(run) + '\n'));
    assert.equal(summary.retained.routedInputObservationsSha256, receipt.routedInputEvidence.sha256);
    assert.equal(summary.retained.routedInputRunSha256, receipt.routedInputEvidence.runSha256);
    assert.equal(summary.retained.routedInputAdapterSha256, receipt.routedInputEvidence.adapterBundleSha256);
    assert.equal(summary.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v36');
    assert.equal(summary.context.inputSuiteId, inputPlanV2.suiteId);
    assert.equal(receipt.fixtureRemoved, true);
    const predecessors = benchmarkSuites.slice(0, benchmarkSuites.findIndex(item => item.suite === 'routed-input'));
    const expected = [];
    for (const suite of predecessors) expected.push(...(await import(new URL(suite.module, import.meta.url)))[suite.exports[2]]);
    const unique = new Set(Object.values(summary.sameRun).flatMap(suite => suite.cases.map(item => item.caseId)));
    assert.deepEqual([...unique].sort(), [...new Set(expected)].sort());
    assert.equal(Object.keys(summary.sameRun).length, predecessors.length);
    for (const [name, suite] of Object.entries(summary.sameRun)) {
        if (suite.cases.some(item => item.effectContract)) assert.ok(suite.cases.every(item => item.effectContract === 'supported'), name);
        else assert.ok(suite.cases.every(item => item.outcome === 'supported'), name);
    }
    for (const id of routedInputCaseIds) assert.ok(!unique.has(id));
    assert.deepEqual(summary.queryCounts, { matched: 33, mismatched: 7 }, 'the query suite reconfirms with the same frozen mismatches');
    assert.equal(summary.truecolorFinding.cases.length, 10);
    assert.equal(summary.underlineResolved.cases.length, 3);
});

test('the stock run keeps all three benchmark ids inconclusive (the pinned paths never decide) and no stock grade is rewritten', () => {
    const { summary } = load();
    assert.deepEqual(summary.stockCollector.counts, { inconclusive: 167, supported: 87, unsupported: 1 });
    assert.equal(summary.stockCollector.suiteComplete, false);
    for (const id of routedInputCaseIds) assert.equal(summary.stockCollector.probes[id].outcome, 'inconclusive', id);
    assert.equal(summary.stockCollector.probes['modes.application-keypad'].evidence, 'none');
});

test('revisions, attempts (the superseded ones keep their actual reasons) and file sizes', () => {
    const { summary } = load();
    assert.match(summary.appRevision, /^[0-9a-f]{40}$/);
    assert.match(summary.scriptsRevision, /^[0-9a-f]{40}$/);
    assert.deepEqual(summary.attempts.map(item => item.receiptCompleted), [true, false, true, true, true, true]);
    assert.equal(summary.attempts.at(-1).runId, summary.context.runId);
    assert.equal(summary.attempts.at(-1).scriptsRevision, summary.scriptsRevision);
    assert.match(summary.attempts[0].outcome, /once per cell change/);
    assert.match(summary.attempts[0].outcome, /clears the one-shot text correlation/);
    assert.match(summary.attempts[1].outcome, /Unexpected input during cell collection/);
    assert.match(summary.attempts[3].outcome, /Core-gap batch \(PR 932\) merged first/);
    assert.match(summary.attempts[4].outcome, /typed input-unavailable fallback/);
    const first = read('superseded/attempt-1-routed-input-observations.json');
    assert.equal(first.runId, summary.attempts[0].runId);
    assert.deepEqual(first.cases.map(item => item.effectContract.outcome), ['supported', 'unsupported', 'unsupported']);
    assert.deepEqual(first.cases[1].effectContract.failedRequired, ['input.mouse.button.move.middle.shiftalt.raw']);
    assert.deepEqual(first.cases[2].effectContract.failedRequired, ['input.keypad.application.7.text.duplicate']);
    assert.equal(read('superseded/attempt-2-failure.json').error, 'Unexpected input during cell collection.');
    for (const name of readdirSync(dir)) {
        if (name === 'superseded') continue;
        assert.ok(readFileSync(new URL(name, dir), 'utf8').split('\n').length <= (name === 'summary.json' ? 2000 : 1500), name);
    }
    assert.ok(existsSync(new URL('superseded/', dir)));
});
