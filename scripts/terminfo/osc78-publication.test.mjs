// Frozen real-App publication. Synthetic fixtures do not replace this measurement; every digest below is checked against the committed evidence.
// The publication is split into reviewable files; the test reassembles the exact journal order and re-derives the graded report and the pinned-probe
// replay from those retained bytes.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { benchmarkSuites } from './qualification-inventory.mjs';
import { colorBindingHash } from './resolved-colors.mjs';
import { osc78CaseIds, osc78Fixture, osc78SuiteId, osc7EffectContractId, osc7Forms, osc8EffectContractId, osc8Forms, osc8PinnedUri, replayOsc78Pinned } from './osc78-contracts.mjs';
import { osc78AdapterId } from './osc78-grading-collector.mjs';
import { buildOsc78GradingReport, osc78Counts } from './osc78-grading.mjs';

const dir = new URL('../../docs/qa/terminfo/2026-11-11-osc7-osc8/', import.meta.url);
const read = name => JSON.parse(readFileSync(new URL(name, dir)));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
let cached;
const load = () => cached ??= (() => {
    const summary = read('summary.json'), run = read('run-log.json'), common = read('journal-common.json');
    const frames = [common.frames.start, common.frames.bind];
    for (const experiment of run.experiments) {
        const journal = read(`journal-${experiment.caseId.replaceAll('/', '-')}.json`);
        assert.equal(journal.caseId, experiment.caseId);
        assert.equal(journal.firstSnapshot, frames.length);
        assert.equal(journal.frames.length, experiment.reads.length);
        frames.push(...journal.frames);
    }
    frames.push(common.frames.finish);
    const cases = osc78CaseIds.map(id => read(`case-${id}.json`));
    return { summary, run, common, frames, report: { ...common.reportHeader, cases } };
})();
const derive = loaded => buildOsc78GradingReport(loaded.frames, loaded.run, loaded.summary.context.runId, loaded.report.adapter, { contractsVersion: loaded.report.contractsVersion });
const strip = ({ snapshots, ...rest }) => rest;

test('the published evidence re-derives exactly from its retained paired journal and run log, and every digest is the one the receipt bound', () => {
    const loaded = load(), { report, summary, run, frames } = loaded, derived = derive(loaded);
    assert.equal(JSON.stringify(report), JSON.stringify(strip(derived)));
    assert.equal(report.suiteId, osc78SuiteId);
    assert.equal(report.adapter.id, osc78AdapterId);
    assert.equal(report.adapter.derivedFromAdapterId, 'ntilde-owned-left-right-margins-grading-v1');
    assert.equal(frames.length, 26);
    assert.deepEqual(frames.map(frame => frame.index), frames.map((_, index) => index));
    assert.equal(report.restoredSnapshot, 25);
    const { receipt } = summary;
    assert.equal(receipt.osc78Evidence.sha256, hash(JSON.stringify(derived, null, 2) + '\n'));
    assert.equal(receipt.osc78Evidence.runSha256, hash(JSON.stringify(run) + '\n'));
    assert.equal(summary.retained.osc78JournalSha256, hash(frames.map(frame => JSON.stringify(frame)).join('\n') + '\n'));
    assert.equal(summary.retained.osc78ObservationsSha256, receipt.osc78Evidence.sha256);
    assert.equal(summary.retained.osc78RunSha256, receipt.osc78Evidence.runSha256);
    assert.equal(summary.retained.osc78AdapterSha256, receipt.osc78Evidence.adapterBundleSha256);
    assert.equal(receipt.osc78Evidence.adapterBundleSha256, report.adapter.bundleSha256);
    assert.equal(receipt.osc78Evidence.runId, summary.context.runId);
    assert.equal(receipt.osc78Evidence.suiteId, osc78SuiteId);
    assert.deepEqual(receipt.osc78Evidence.counts, osc78Counts(report));
});

test('extensions.osc7-cwd: the pinned verdict is preserved inconclusive by design (no fixture bytes exist) and the separately identified effect contract is supported', () => {
    const [osc7] = load().report.cases;
    assert.equal(osc7.caseId, 'extensions.osc7-cwd');
    assert.deepEqual(osc7.pinnedFixture, { fixtureBytes: false, termlessFeeds: [], terminalWrites: [] });
    assert.deepEqual([osc7.pinned.termless.observation.outcome, osc7.pinned.termless.observation.evidence, osc7.pinned.terminal.observation.outcome, osc7.pinned.terminal.observation.evidence],
        ['inconclusive', 'legacy', 'inconclusive', 'none']);
    assert.match(osc7.pinned.termless.note, /Declared OSC 7 capability does not establish working-directory metadata delivery/);
    assert.match(osc7.pinned.terminal.note, /No working-directory metadata readback for OSC 7/);
    assert.deepEqual([osc7.conclusion.outcome, osc7.conclusion.grading], ['inconclusive', 'pinned-declaration-only-preserved-by-design']);
    assert.deepEqual([osc7.effectContract.id, osc7.effectContract.outcome], [osc7EffectContractId, 'supported']);
    assert.match(osc7.effectContract.attribution, /not the pinned probe/);
    assert.deepEqual(osc7.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'supported', kind: 'pinned-inconclusive-by-design' });
    assert.deepEqual(osc7.contractResult, { outcome: 'supported' });
    assert.ok(osc7.effectContract.validity.every(entry => entry.held) && osc7.effectContract.required.every(entry => entry.held));
    assert.deepEqual(osc7.effectContract.required.map(entry => entry.id), ['first-update', 'fresh-second-update', 'restoration-to-baseline', 'encoded-space']);
});

test('extensions.osc8: the pinned termless code decided over the stored cells and link runs, its terminal path is inconclusive by design, and the effect contract is supported', () => {
    const osc8 = load().report.cases[1];
    assert.equal(osc8.caseId, 'extensions.osc8');
    assert.deepEqual(osc8.pinnedFixture.terminalWrites.join(''), osc8.pinnedFixture.termlessFeeds[0]);
    assert.equal(osc8.pinnedFixture.termlessFeeds[0], `\x1b[1;1H\x1b[2KA\x1b]8;;${osc8PinnedUri}\x07LINK\x1b]8;;\x07Z`);
    assert.deepEqual([osc8.pinned.termless.pass, osc8.pinned.termless.observation.outcome, osc8.pinned.termless.observation.evidence], [true, 'supported', 'parser-state']);
    assert.deepEqual(JSON.parse(osc8.pinned.termless.response).chars, [...'ALINKZ']);
    assert.deepEqual(JSON.parse(osc8.pinned.termless.response).links.map(link => link.uri), [null, osc8PinnedUri, osc8PinnedUri, osc8PinnedUri, osc8PinnedUri, null]);
    assert.deepEqual([osc8.pinned.terminal.observation.outcome, osc8.pinned.terminal.observation.evidence, osc8.pinned.terminal.response], ['inconclusive', 'consumed', '1;7']);
    assert.deepEqual([osc8.conclusion.outcome, osc8.conclusion.grading], ['supported', 'pinned-termless-replay-over-stored-cells']);
    assert.deepEqual([osc8.effectContract.id, osc8.effectContract.outcome], [osc8EffectContractId, 'supported']);
    assert.deepEqual(osc8.divergences, [{ termlessOutcome: 'supported', terminalOutcome: 'inconclusive', kind: 'terminal-path-inconclusive-by-design' }]);
    assert.equal(osc8.divergence, undefined, 'the pinned termless verdict and the effect contract agree');
    assert.ok(osc8.effectContract.validity.every(entry => entry.held) && osc8.effectContract.required.every(entry => entry.held));
    assert.deepEqual(osc8.effectContract.required.map(entry => entry.id), ['stock-pinned-fixture', 'pinned-fixture-effect', 'closed-link-later-text', 'id-parameter', 'two-links-not-merged', 'no-stale-links']);
    assert.deepEqual(osc78Counts(load().report), { 'inconclusive/supported': 1, 'supported/supported': 1 });
});

test('the pinned code ran over the stock-context reads: the replay of the retained stock read equals both live results on both paths', () => {
    const { report, frames, run } = load();
    for (const item of report.cases) {
        const stock = item.experiments.find(entry => entry.name === 'stock'), frame = frames[stock.reads[0].snapshot];
        const replay = replayOsc78Pinned(item.caseId, frame.before.screen, frame.before.metadata.buffer.links), recorded = run.pinned.find(entry => entry.caseId === item.caseId);
        assert.deepEqual(recorded.termless.result, replay.termless);
        assert.deepEqual(recorded.terminal.result, replay.terminal);
    }
    const stock = frames[report.cases[1].experiments.find(entry => entry.name === 'stock').reads[0].snapshot].before;
    assert.equal(stock.screen.lines[0], 'ALINKZ');
    assert.deepEqual([stock.screen.cursorRow, stock.screen.cursorCol], [0, 6]);
    assert.deepEqual(stock.metadata.buffer.links.map(({ identity, ...rest }) => rest), [{ row: 0, col: 1, length: 4, uri: osc8PinnedUri, id: null }]);
});

test('observed OSC 7 facts: the transmitted URIs, the decoded cwd metadata, fresh updates, restoration and no host component', () => {
    const facts = load().report.cases[0].observed, runId = load().summary.context.runId, forms = osc7Forms(runId), fixture = osc78Fixture(runId);
    for (const form of Object.values(forms)) assert.deepEqual(facts.transmitted[form.name], { uri: form.uri, expectedPath: form.path });
    assert.deepEqual(facts.observedCwd, { baseline: fixture.cwd, afterOrdinaryText: fixture.cwd, first: forms.first.path, second: forms.second.path, restored: fixture.cwd,
        encodedSpace: forms.space.path, foreignAuthority: forms.authority.path, stTerminatedFirst: forms.first.path, afterTitleChange: fixture.cwd });
    assert.equal(forms.space.uri.includes('%20'), true);
    assert.equal(facts.observedCwd.encodedSpace.includes(' '), true);
    assert.equal(facts.observedCwd.encodedSpace.includes('%20'), false);
    assert.equal(facts.freshness.baselineFirstSecondDistinct, true);
    assert.equal(facts.hostComponent.exposed, false);
    assert.deepEqual(facts.titleAfterUpdates, { first: fixture.title, second: fixture.title });
    assert.deepEqual(facts.notObserved, ['child process working directory', 'existence of the named path', 'remote-host validity']);
});

test('observed OSC 8 facts: the exact cell range, the witness, the id parameter, two distinct links, no stale metadata and snapshot-local identities', () => {
    const facts = load().report.cases[1].observed, links = osc8Forms(load().summary.context.runId);
    assert.deepEqual(facts.pinnedFixture.runs, [{ row: 0, col: 1, length: 4, uri: osc8PinnedUri, id: null }]);
    assert.deepEqual([facts.pinnedFixture.rowText, facts.pinnedFixture.cursor], ['ALINKZ', [0, 6]]);
    assert.deepEqual([facts.pinnedFixture.closedLinkWitness, facts.pinnedFixture.textBeforeLink], [{ char: 'Z', linked: false }, { char: 'A', linked: false }]);
    assert.deepEqual([facts.idParameter.sent, facts.idParameter.observed, facts.idParameter.uriObserved], [links.idValue, [links.idValue], [links.idUri]]);
    assert.deepEqual(facts.twoLinks.runs.map(run => [run.col, run.length, run.uri]), [[1, 3, links.oneUri], [4, 3, links.twoUri]]);
    assert.equal(facts.twoLinks.distinctIdentities, 2);
    assert.deepEqual(facts.laterText, { row: 4, text: 'later', runsUnchanged: true });
    assert.deepEqual(facts.stale.afterPlainOverwrite, []);
    assert.deepEqual(facts.stale.afterSecondFixture.map(run => run.uri), [links.secondFixtureUri]);
    assert.deepEqual(facts.sameAnchor.identities, [0, 0], 'recorded only: the same id and URI share one snapshot-local identity');
    assert.deepEqual(facts.stTerminator.runs, facts.pinnedFixture.runs, 'recorded only');
    assert.equal(facts.snapshotLocalIdentity.equalsOscId, false);
    assert.deepEqual(facts.notObserved, ['click handling', 'browser launch', 'URI safety policy', 'rendered underline appearance']);
});

test('every read matched the oracle: controls held first and each recorded-only variant is also reported', () => {
    const { report } = load();
    assert.deepEqual(report.cases.map(item => item.experiments.length), [6, 8]);
    for (const item of report.cases) for (const experiment of item.experiments) for (const read of experiment.reads) {
        assert.equal(read.evaluation.passed, true, `${experiment.id} ${read.label}`);
        assert.equal(read.evaluation.observed.confidence, 'precise');
        assert.deepEqual(Object.values(read.evaluation.required).map(entry => entry.match), [true, true, true]);
    }
    assert.deepEqual(report.cases.map(item => item.experiments.filter(entry => entry.role === 'gate').length), [1, 1]);
    assert.deepEqual(report.cases.map(item => item.experiments.filter(entry => entry.role === 'observed').length), [2, 2]);
});

test('restoration is exactly what the observation API can show: the prepared baseline, initial cells and original palette; as-found and original metadata are not claimed', () => {
    const { frames, summary, report } = load(), first = frames[0].before, bound = frames[1].before, last = frames.at(-1).before, fixture = osc78Fixture(summary.context.runId);
    assert.deepEqual(last.screen, first.screen, 'the initial screen, cursor, history and color binding are restored');
    assert.deepEqual(first.screen.colorBinding.paletteOverrideIndices, []);
    assert.deepEqual(bound.screen.colorBinding.paletteOverrideIndices, [7]);
    assert.notEqual(colorBindingHash(bound.screen.colorBinding), colorBindingHash(first.screen.colorBinding));
    assert.equal(last.metadata.oscTitle, fixture.title, 'the prepared title, not the as-found or original one, is what is restored');
    assert.equal(last.metadata.workingDirectory, fixture.cwd);
    assert.deepEqual(last.metadata.buffer.links, []);
    assert.deepEqual([last.metadata.buffer.acceptingCommandInput, last.metadata.buffer.commandStart, last.metadata.buffer.outputStart], [false, null, null]);
    assert.deepEqual([last.status.status, last.status.confidence, last.status.currentCommand], ['awaitingInput', 'precise', null]);
    assert.equal(summary.receipt.fixtureRemoved, true);
    assert.equal(report.restoration.preparedBaselineRestored, true);
    assert.equal(report.restoration.originalMetadataRestored, false);
    assert.deepEqual([report.restoration.initial.oscTitle, report.restoration.initial.workingDirectory], [first.metadata.oscTitle, first.metadata.workingDirectory]);
    assert.notEqual(first.metadata.workingDirectory, fixture.cwd, 'the as-found cwd (left by an earlier suite) is not the prepared one');
});

test('the receipt binds the run, adapter and counts, and every predecessor reconfirms in the same run', () => {
    const { summary } = load(), { receipt } = summary;
    assert.equal(receipt.schemaVersion, 51);
    assert.equal(summary.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v45');
    assert.equal(summary.context.appVersion, '0.13.0');
    const predecessors = benchmarkSuites.slice(0, benchmarkSuites.findIndex(item => item.suite === 'osc78'));
    assert.ok(predecessors.length >= 29);
    for (const key of ['gradingEvidence', 'lrMarginsEvidence', 'colorResetEvidence', 'routedInputEvidence', 'osc133Evidence', 'coreGapsEvidence', 'textCursorEvidence', 'coreEraseEvidence',
        'tabStopsEvidence', 'coreWrapEvidence', 'sgrRasterEvidence', 'reverseWideEvidence', 'metadataEvidence', 'queryEvidence', 'staticImageEvidence', 'inputEvidence']) {
        assert.ok(summary.sameRun[key] !== undefined, key);
        assert.deepEqual(receipt[key].sha256, summary.sameRun[key].sha256, key);
    }
    assert.equal(summary.sameRun.osc133Evidence.suiteId, 'ntilde-owned-osc133-graded-v1');
    assert.deepEqual(summary.sameRun.osc133Evidence.counts, { 'inconclusive/supported': 4 });
    assert.equal(summary.sameRun.osc78Evidence, undefined);
    for (const id of osc78CaseIds) assert.ok(!predecessors.some(suite => suite.module === 'osc78-contracts.mjs'), id);
});

test('the stock run (re-run in the same owned run) keeps both ids inconclusive and no stock grade is rewritten or raised', () => {
    const { summary } = load();
    assert.deepEqual(summary.stockCollector.counts, { inconclusive: 167, supported: 87, unsupported: 1 });
    assert.equal(summary.stockCollector.suiteComplete, false);
    assert.equal(summary.stockCollector.probes['extensions.osc7-cwd'].outcome, 'inconclusive');
    assert.equal(summary.stockCollector.probes['extensions.osc7-cwd'].evidence, 'none');
    assert.equal(summary.stockCollector.probes['extensions.osc8'].outcome, 'inconclusive');
    assert.equal(summary.stockCollector.probes['extensions.osc8'].evidence, 'consumed');
    assert.equal(summary.stockCollector.probeHash, '16a690f34c0e');
});

test('revisions, attempts (each keeps its actual reason) and file sizes', () => {
    const { summary } = load();
    assert.match(summary.appRevision, /^[0-9a-f]{40}$/);
    assert.match(summary.scriptsRevision, /^[0-9a-f]{40}$/);
    assert.equal(summary.attempts.at(-1).runId, summary.context.runId);
    assert.equal(summary.attempts.at(-1).scriptsRevision, summary.scriptsRevision);
    assert.ok(summary.attempts.slice(0, -1).every(item => /superseded|aborted/.test(item.outcome)));
    for (const item of summary.attempts.slice(0, -1)) assert.ok(existsSync(new URL(item.archive, dir)), item.archive);
    for (const name of readdirSync(dir)) {
        if (name === 'superseded') continue;
        assert.ok(readFileSync(new URL(name, dir), 'utf8').split('\n').length <= (name === 'summary.json' ? 2000 : 1500), name);
    }
});
