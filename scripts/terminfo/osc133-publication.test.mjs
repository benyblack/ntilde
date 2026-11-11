// Frozen real-App publication. Synthetic fixtures do not replace this measurement; every digest below is checked against the committed evidence.
// The publication is split into reviewable files; the test reassembles the exact journal order and re-derives the graded report and the pinned-probe
// replay from those retained bytes.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { benchmarkSuites } from './qualification-inventory.mjs';
import { colorBindingHash } from './resolved-colors.mjs';
import { osc133CaseIds, osc133Fixture, osc133SuiteId, replayOsc133Pinned } from './osc133-contracts.mjs';
import { osc133AdapterId } from './osc133-grading-collector.mjs';
import { buildOsc133GradingReport, osc133Counts } from './osc133-grading.mjs';

const dir = new URL('../../docs/qa/terminfo/2026-11-11-osc133/', import.meta.url);
const read = name => JSON.parse(readFileSync(new URL(name, dir)));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
let cached;
const load = () => cached ??= (() => {
    const summary = read('summary.json'), run = read('run-log.json'), common = read('journal-common.json');
    const frames = [common.frames.start, common.frames.bind];
    for (const experiment of run.experiments) {
        const journal = read(`journal-${experiment.caseId.replace('/', '-')}.json`);
        assert.equal(journal.caseId, experiment.caseId);
        assert.equal(journal.firstSnapshot, frames.length);
        assert.equal(journal.frames.length, experiment.reads.length);
        frames.push(...journal.frames);
    }
    frames.push(common.frames.finish);
    const cases = osc133CaseIds.map(id => read(`case-${id}.json`));
    return { summary, run, common, frames, report: { ...common.reportHeader, cases } };
})();
const derive = loaded => buildOsc133GradingReport(loaded.frames, loaded.run, loaded.summary.context.runId, loaded.report.adapter, { contractsVersion: loaded.report.contractsVersion });
const strip = ({ snapshots, ...rest }) => rest;

test('the published evidence re-derives exactly from its retained paired journal and run log, and every digest is the one the receipt bound', () => {
    const loaded = load(), { report, summary, run, frames } = loaded, derived = derive(loaded);
    assert.equal(JSON.stringify(report), JSON.stringify(strip(derived)));
    assert.equal(report.suiteId, osc133SuiteId);
    assert.equal(report.adapter.id, osc133AdapterId);
    assert.equal(report.adapter.derivedFromAdapterId, 'ntilde-owned-left-right-margins-grading-v1');
    assert.equal(frames.length, 49);
    assert.deepEqual(frames.map(frame => frame.index), frames.map((_, index) => index));
    assert.equal(report.restoredSnapshot, 48);
    const { receipt } = summary;
    assert.equal(receipt.osc133Evidence.sha256, hash(JSON.stringify(derived, null, 2) + '\n'));
    assert.equal(receipt.osc133Evidence.runSha256, hash(JSON.stringify(run) + '\n'));
    assert.equal(summary.retained.osc133JournalSha256, hash(frames.map(frame => JSON.stringify(frame)).join('\n') + '\n'));
    assert.equal(summary.retained.osc133ObservationsSha256, receipt.osc133Evidence.sha256);
    assert.equal(summary.retained.osc133RunSha256, receipt.osc133Evidence.runSha256);
    assert.equal(summary.retained.osc133AdapterSha256, receipt.osc133Evidence.adapterBundleSha256);
    assert.equal(receipt.osc133Evidence.adapterBundleSha256, summary.context.osc133BundleSha256);
    assert.equal(receipt.osc133Evidence.adapterBundleSha256, report.adapter.bundleSha256);
});

test('the pinned verdicts are preserved inconclusive by design and the separately identified effect contracts are supported', () => {
    const { report } = load();
    assert.deepEqual(report.cases.map(item => item.caseId), [...osc133CaseIds]);
    for (const item of report.cases) {
        assert.equal(item.pinned.pass, false);
        assert.equal(item.pinned.observation.outcome, 'inconclusive');
        assert.equal(item.pinned.observation.evidence, 'consumed');
        assert.equal(item.pinned.terminal.observation.outcome, 'inconclusive');
        assert.equal(item.conclusion.outcome, 'inconclusive');
        assert.equal(item.conclusion.grading, 'pinned-consumption-preserved-by-design');
        assert.deepEqual([item.effectContract.id, item.effectContract.outcome], ['ntilde-osc133-shell-lifecycle-effect-v1', 'supported']);
        assert.match(item.effectContract.attribution, /not the pinned probe/);
        assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'supported', kind: 'pinned-inconclusive-by-design' });
        assert.deepEqual(item.contractResult, { outcome: 'supported' });
        assert.ok(item.effectContract.validity.every(entry => entry.held) && item.effectContract.required.every(entry => entry.held));
        assert.equal(item.effectContract.targetShown, true);
    }
    assert.deepEqual(osc133Counts(report), { 'inconclusive/supported': 4 });
});

test('the pinned code ran over the stock-context reads: marker consumed, X in cell (0,0), cursor in column 2, on both paths', () => {
    const { report, frames, run } = load();
    for (const item of report.cases) {
        const stock = item.experiments.find(entry => entry.name === 'stock'), screen = frames[stock.reads[0].snapshot].before.screen;
        assert.equal(screen.lines[0], 'X');
        assert.deepEqual([screen.cursorRow, screen.cursorCol], [0, 1]);
        const replay = replayOsc133Pinned(item.caseId, screen), recorded = run.pinned.find(entry => entry.caseId === item.caseId);
        assert.deepEqual(recorded.termless.result, replay.termless);
        assert.deepEqual(recorded.terminal.result, replay.terminal);
        assert.match(recorded.termless.result.note, /Marker was consumed; shell prompt integration was not observed/);
    }
});

test('observed transitions: A leaves a running command for the prompt, B anchors the input boundary, C names the payload command, D drops both anchors', () => {
    const [a, b, c, d] = load().report.cases.map(item => item.observed);
    assert.deepEqual(a.transition, { from: 'running', to: 'prompt' });
    assert.deepEqual(a.commandText, { before: 'echo a-run', after: null });
    assert.equal(a.sharedObservableWithD, true, 'D also returns a running session to its prompt; disclosed, never required');
    assert.deepEqual(a.gatingMarkers, { b: 'running', c: 'running' });
    assert.deepEqual(a.anchorsAfterAWithoutD, { accepting: false, commandStart: [2, 8], outputStart: [3, 0] }, 'implementation policy: A does not clear the anchors of an unterminated command');
    assert.deepEqual(b.pinnedFixture, { accepting: true, commandStart: [0, 0] });
    assert.deepEqual(b.boundary, { anchor: [3, 8], promptText: 'ntilde$ ', inputText: 'echo b-input', commandExposed: null });
    assert.deepEqual(b.secondCycleAnchor, [6, 8]);
    assert.deepEqual(c.commandIdentity, { typedText: 'echo c-typed', payload: 'echo c-payload', exposed: 'echo c-payload' });
    assert.deepEqual(c.outputAnchor, { atAccept: [3, 0], afterOutput: [3, 0], firstOutputRow: 'c-output' });
    assert.deepEqual(c.bareCPinnedFixture, { command: null, outputStartPolicy: [1, 0] });
    assert.deepEqual(c.payloadShapes, { attribute: null, plainText: 'echo c-plain' });
    assert.deepEqual(c.secondCycle, { command: 'echo c-two', outputStart: [7, 0] });
    assert.deepEqual(d.transition, { from: 'running', to: 'prompt' });
    assert.deepEqual(d.anchorsAfter, { accepting: false, commandStart: null, outputStart: null });
    for (const item of [a, b, c, d]) assert.deepEqual(item.variants.stTerminator, { targetShown: true, certainFieldsMatch: true });
});

test('exit status: the D;7 payload is retained as an observation only; the session status carries no command exit code', () => {
    const { report, frames } = load(), d = report.cases[3];
    assert.deepEqual({ ...d.observed.exitPayload, note: undefined }, { sent: 7, sessionStatusExitCode: null, interpreted: false, note: undefined });
    const post = d.experiments.find(entry => entry.name === 'exit-payload').reads.find(entry => entry.label === 'post');
    assert.equal(frames[post.snapshot].before.status.exitCode ?? null, null);
    assert.equal(frames[post.snapshot].before.rawStatus.exitCode ?? null, null);
});

test('every read matched the oracle: controls held first, gates did not produce another case\'s transition, and cycles never reused anchors or command text', () => {
    const { report } = load();
    assert.deepEqual(report.cases.map(item => item.experiments.length), [6, 8, 10, 8]);
    for (const item of report.cases) {
        for (const experiment of item.experiments) for (const read of experiment.reads) {
            assert.equal(read.evaluation.passed, true, `${experiment.id} ${read.label}`);
            assert.equal(read.evaluation.observed.confidence, 'precise');
            assert.ok(['awaitingInput', 'idle', 'running'].includes(read.evaluation.observed.statusRaw));
        }
        assert.deepEqual(item.experiments.filter(entry => entry.role === 'gate').length, item.caseId.endsWith('-a') ? 2 : 3);
    }
    // Implementation-policy predictions (fields the contract leaves unspecified) are recorded; none disagreed with this App.
    for (const item of report.cases) for (const experiment of item.experiments) for (const read of experiment.reads) {
        assert.ok(Object.values(read.evaluation.policy).every(entry => entry.match), `${experiment.id} ${read.label}`);
    }
});

test('restoration is exactly what the observation API can show: prepared baseline, initial cells and original palette, with no residual lifecycle state', () => {
    const { frames, summary } = load(), first = frames[0].before, bound = frames[1].before, last = frames.at(-1).before, fixture = osc133Fixture(summary.context.runId);
    assert.deepEqual(last.screen, first.screen, 'the initial screen, cursor, history and color binding are restored');
    assert.deepEqual(first.screen.colorBinding.paletteOverrideIndices, []);
    assert.deepEqual(bound.screen.colorBinding.paletteOverrideIndices, [7]);
    assert.notEqual(colorBindingHash(bound.screen.colorBinding), colorBindingHash(first.screen.colorBinding));
    assert.equal(last.metadata.oscTitle, fixture.title, 'the prepared title, not the unobservable original, is what is restored');
    assert.equal(last.metadata.workingDirectory, fixture.cwd);
    assert.deepEqual([last.metadata.buffer.acceptingCommandInput, last.metadata.buffer.commandStart, last.metadata.buffer.outputStart], [false, null, null]);
    assert.deepEqual([last.status.status, last.status.confidence, last.status.currentCommand], ['awaitingInput', 'precise', null]);
    assert.equal(summary.receipt.fixtureRemoved, true);
});

test('the receipt binds the run, adapter and counts, and every predecessor and finding reconfirm in the same run', async () => {
    const { summary, report } = load(), { receipt } = summary;
    assert.equal(receipt.schemaVersion, 44);
    assert.equal(receipt.osc133Evidence.runId, summary.context.runId);
    assert.equal(receipt.osc133Evidence.suiteId, osc133SuiteId);
    assert.deepEqual(receipt.osc133Evidence.counts, osc133Counts(report));
    assert.equal(summary.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v38');
    const predecessors = benchmarkSuites.slice(0, benchmarkSuites.findIndex(item => item.suite === 'osc133'));
    const expected = [];
    for (const suite of predecessors) expected.push(...(await import(new URL(suite.module, import.meta.url)))[suite.exports[2]]);
    const unique = new Set(Object.values(summary.sameRun).flatMap(suite => suite.cases.map(item => item.caseId)));
    assert.deepEqual([...unique].sort(), [...new Set(expected)].sort());
    assert.equal(Object.keys(summary.sameRun).length, predecessors.length);
    for (const [name, suite] of Object.entries(summary.sameRun)) {
        if (suite.cases.some(item => item.effectContract)) assert.ok(suite.cases.every(item => item.effectContract === 'supported'), name);
        else assert.ok(suite.cases.every(item => item.outcome === 'supported'), name);
    }
    for (const id of osc133CaseIds) assert.ok(!unique.has(id));
    assert.deepEqual(summary.queryCounts, { matched: 33, mismatched: 7 }, 'the query suite reconfirms with the same frozen mismatches');
    assert.equal(summary.truecolorFinding.cases.length, 10);
    assert.equal(summary.underlineResolved.cases.length, 3);
});

test('the stock run (re-run in the same owned run) keeps all four ids inconclusive and no stock grade is rewritten or raised', () => {
    const { summary } = load();
    assert.deepEqual(summary.stockCollector.counts, { inconclusive: 167, supported: 87, unsupported: 1 });
    assert.equal(summary.stockCollector.suiteComplete, false);
    for (const id of osc133CaseIds) {
        assert.equal(summary.stockCollector.probes[id].outcome, 'inconclusive', id);
        assert.equal(summary.stockCollector.probes[id].evidence, 'consumed', id);
    }
});

test('revisions, attempts (the superseded one keeps its actual reason) and file sizes', () => {
    const { summary } = load();
    assert.match(summary.appRevision, /^[0-9a-f]{40}$/);
    assert.match(summary.scriptsRevision, /^[0-9a-f]{40}$/);
    assert.deepEqual(summary.attempts.map(item => item.receiptCompleted), [true, true, true]);
    assert.equal(summary.attempts.at(-1).runId, summary.context.runId);
    assert.equal(summary.attempts.at(-1).scriptsRevision, summary.scriptsRevision);
    assert.match(summary.attempts[0].outcome, /superseded/);
    assert.match(summary.attempts[0].outcome, /uncommitted/);
    assert.match(summary.attempts[1].outcome, /Codex P2/);
    assert.ok(existsSync(new URL('superseded/attempt-1-superseded.zip', dir)));
    assert.ok(existsSync(new URL('superseded/attempt-2-superseded.zip', dir)));
    for (const name of readdirSync(dir)) {
        if (name === 'superseded') continue;
        assert.ok(readFileSync(new URL(name, dir), 'utf8').split('\n').length <= (name === 'summary.json' ? 2000 : 1500), name);
    }
});
