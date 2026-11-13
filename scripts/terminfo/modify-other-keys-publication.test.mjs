// Frozen real-App publication. Synthetic fixtures do not replace this measurement; every digest below is checked against the
// committed evidence. The test reassembles the exact 118-event journal order from the split files (one mode-control frame and one
// read frame per declared event, bracketed by the start/bind and restored finish frames) and re-derives the layered graded report
// from those retained bytes, without the collector.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readdirSync, readFileSync } from 'node:fs';
import { benchmarkSuites } from './qualification-inventory.mjs';
import { inputPlanForSuiteId } from './input-plan.mjs';
import { modifyOtherKeysContractsVersion, modifyOtherKeysGradedSuiteId } from './modify-other-keys-contracts.mjs';
import { modifyOtherKeysAdapterId } from './modify-other-keys-grading-collector.mjs';
import { buildModifyOtherKeysGradingReport } from './modify-other-keys-grading.mjs';

const dir = new URL('../../docs/qa/terminfo/2026-11-12-modify-other-keys/', import.meta.url);
const dirPath = new URL('.', dir).pathname;
const read = name => JSON.parse(readFileSync(new URL(name, dir)));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const strip = ({ snapshots, ...rest }) => rest;
let cached;
const load = () => cached ??= (() => {
    const summary = read('summary.json'), common = read('journal-common.json');
    const runBytes = readFileSync(new URL('run-log.json', dir));
    const run = JSON.parse(runBytes);
    const [item] = run.cases;
    const plan = inputPlanForSuiteId(summary.context.inputSuiteId);
    const frames = [common.frames.start, common.frames.bind];
    const actions = [];
    const inputs = readdirSync(dirPath).filter(name => /^input-\d{3}-.*\.json$/.test(name)).sort();
    assert.equal(inputs.length, plan.make(40, 140).length, 'one split file per declared event of the v3 plan');
    for (const [index, name] of inputs.entries()) {
        const journal = read(name);
        assert.equal(journal.index, index, 'the split files carry the journal order in their names');
        frames.push(journal.control, journal.read);
        actions.push(journal.action);
    }
    frames.push(common.frames.finish);
    const [caseItem] = run.cases;
    const caseFile = read(`case-${caseItem.caseId}.json`);
    assert.equal(caseItem.caseId, caseFile.caseId);
    return { summary, run, runBytes, common, case: caseFile, frames, actions, plan,
        report: { ...common.reportHeader, cases: [caseFile] } };
})();
const derive = loaded => buildModifyOtherKeysGradingReport(
    { frames: loaded.frames, actions: loaded.actions, seal: loaded.common.seal }, loaded.run,
    loaded.summary.context.runId, loaded.report.adapter,
    { replayModule: null, inputPlan: loaded.plan, inputReportSha256: loaded.common.inputReportSha256 });

test('the published evidence re-derives exactly from its retained journal and run log, and every digest is the one the receipt bound', () => {
    const loaded = load(), { report, summary, run, runBytes, frames, actions, common } = loaded, derived = derive(loaded);
    assert.equal(JSON.stringify(report), JSON.stringify(strip(derived)), 'the published case and header are the graded report');
    assert.equal(report.suiteId, modifyOtherKeysGradedSuiteId);
    assert.equal(report.contractsVersion, modifyOtherKeysContractsVersion);
    assert.equal(report.adapter.id, modifyOtherKeysAdapterId);
    assert.equal(report.adapter.derivedFromAdapterId, 'ntilde-owned-left-right-margins-grading-v1');
    assert.deepEqual(frames.map(frame => frame.index), frames.map((_, index) => index), 'the journal order is contiguous');
    assert.equal(report.restoredSnapshot, frames.length - 1);
    const { receipt } = summary;
    assert.equal(receipt.modifyOtherKeysEvidence.sha256, hash(JSON.stringify(derived, null, 2) + '\n'));
    assert.equal(receipt.modifyOtherKeysEvidence.runSha256, hash(runBytes));
    assert.equal(summary.retained.modifyOtherKeysObservationsSha256, receipt.modifyOtherKeysEvidence.sha256);
    assert.equal(summary.retained.modifyOtherKeysRunSha256, receipt.modifyOtherKeysEvidence.runSha256);
    assert.equal(summary.retained.modifyOtherKeysInputJournalSha256,
        hash(frames.map(frame => JSON.stringify(frame)).join('\n') + '\n'));
    assert.equal(summary.retained.modifyOtherKeysActionsSha256,
        hash(actions.map(action => JSON.stringify(action)).join('\n') + '\n'));
    assert.equal(summary.retained.modifyOtherKeysInputObservationsSha256, common.inputReportSha256);
    assert.equal(receipt.modifyOtherKeysEvidence.adapterBundleSha256, summary.context.modifyOtherKeysBundleSha256);
    assert.equal(receipt.modifyOtherKeysEvidence.adapterBundleSha256, report.adapter.bundleSha256);
    assert.equal(summary.ownedRunId, summary.context.runId);
    const entry = benchmarkSuites.find(item => item.suite === 'modify-other-keys');
    assert.equal(entry.module, 'modify-other-keys-contracts.mjs');
    assert.equal(entry.dir, '2026-11-12-modify-other-keys');
});

test('the pinned verdict stays inconclusive by design (the child refused the modifyOtherKeys name over XTGETTCAP), the effect contract is supported beside it, and the divergence is recorded', () => {
    const { case: item, summary } = load();
    assert.match(item.pinnedCriterion, /input\.modify-other-keys/);
    assert.equal(item.pinned.pass, false);
    assert.equal(item.pinned.observation.outcome, 'inconclusive');
    assert.equal(item.pinned.observation.reason, 'insufficient-evidence');
    assert.equal(item.pinned.response, '{"declaredModifyOtherKeys":false}');
    assert.equal(item.served.declaredModifyOtherKeys, false);
    assert.equal(item.served.source, 'child-xtgettcap-modifyOtherKeys');
    assert.equal(item.conclusion.outcome, 'inconclusive');
    assert.equal(item.conclusion.grading, 'pinned-termless-preserved-by-design');
    assert.equal(item.effectContract.id, 'ntilde-modify-other-keys-effect-v1');
    assert.equal(item.effectContract.outcome, 'supported');
    assert.match(item.effectContract.attribution, /not the pinned probe/);
    assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'supported', kind: 'pinned-inconclusive-by-design' });
    const experiments = Object.values(item.experiments);
    assert.equal(experiments.length, 58);
    assert.deepEqual(experiments.reduce((accumulator, experiment) => { accumulator[experiment.role] = (accumulator[experiment.role] ?? 0) + 1; return accumulator; }, {}),
        { required: 46, control: 9, label: 3 });
    assert.equal(experiments.filter(experiment => experiment.matched === true).length, 58, 'every experiment matched its independent oracle');
    // Level 1 keeps the X11 control alias: the generated Ctrl+[ key-down reached the child as the bare 0x1B, byte for byte.
    const alias = experiments.find(experiment => experiment.id === 'input.mok.l1.ctrl.leftbracket.alias');
    assert.equal(alias.expectedHex, '1b');
    assert.equal(alias.actualHex, '1b');
    assert.equal(alias.oracleHex, '1b');
    assert.equal(alias.appFacts.boundary, 'avalonia-routed');
    assert.equal(alias.appFacts.eventDispatched, true);
    // The declaration label ran with no bytes and the levels restored to 0 afterwards.
    const declaration = experiments.find(experiment => experiment.id === 'input.mok.declaration.xtgettcap');
    assert.equal(declaration.expectedHex, '');
    assert.equal(declaration.actualHex, '');
    assert.equal(summary.inputSuite.counts.passed, 118);
    assert.deepEqual(summary.queryCounts, { matched: 33, mismatched: 7 });
    assert.deepEqual(summary.stockCollector.counts, { inconclusive: 167, supported: 87, unsupported: 1 });
    assert.deepEqual(summary.stockCollector.probes['input.modify-other-keys'],
        { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'none',
            note: 'No generated input event and encoded report were validated' });
    assert.deepEqual(summary.stockCollector.probes['input.modify-other-keys-3'],
        { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'none',
            note: 'No generated input event and encoded report were validated' });
    assert.equal(summary.receipt.fixtureRemoved, true);
    const routed = summary.sameRun.routedInput ?? Object.entries(summary.sameRun).find(([key]) => /routed/i.test(key))?.[1];
    assert.equal(routed.cases.filter(caseItem => caseItem.effectContract === 'supported').length, 3, 'the routed-input predecessors reconfirm');
    assert.ok(summary.attempts.length >= 2 && summary.attempts.at(-1).receiptCompleted === true);
});

test('the finish frame proves the restoration: the last capture answers every mode query at its default and resets modifyOtherKeys to 0', () => {
    const { common } = load();
    const finish = common.frames.finish.capture.hex;
    const expected = Buffer.from('\x1b[?66;2$y\x1b[?9;2$y\x1b[?1000;2$y\x1b[?1002;2$y\x1b[?1003;2$y\x1b[?1006;2$y\x1b[?0u\x1b[>4;0m', 'latin1').toString('hex');
    assert.equal(finish, expected, 'the finish capture is the exact default-modes reply with modifyOtherKeys reset to 0');
});
