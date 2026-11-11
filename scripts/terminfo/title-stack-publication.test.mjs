// Frozen real-App publication. Synthetic fixtures do not replace this measurement; every digest below is checked against the committed evidence.
// The publication is split into reviewable files; the test reassembles the exact journal order and re-derives the graded report and the pinned-probe
// replay from those retained bytes.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { benchmarkSuites } from './qualification-inventory.mjs';
import { colorBindingHash } from './resolved-colors.mjs';
import { replayTitleStackTerminal, replayTitleStackTermless, titleStackCaseIds, titleStackFixture, titleStackPinned, titleStackSuiteId } from './title-stack-contracts.mjs';
import { titleStackAdapterId } from './title-stack-grading-collector.mjs';
import { buildTitleStackGradingReport, titleStackCounts } from './title-stack-grading.mjs';

const dir = new URL('../../docs/qa/terminfo/2026-11-11-title-stack/', import.meta.url);
const read = name => JSON.parse(readFileSync(new URL(name, dir)));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const bare = text => text.replace(/^[0-9a-f]{8}-/, '');
const [push, pop] = titleStackCaseIds;
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
    const cases = titleStackCaseIds.map(id => read(`case-${id}.json`));
    return { summary, run, common, frames, report: { ...common.reportHeader, cases } };
})();
const derive = loaded => buildTitleStackGradingReport(loaded.frames, loaded.run, loaded.summary.context.runId, loaded.report.adapter, { contractsVersion: loaded.report.contractsVersion });
const strip = ({ snapshots, ...rest }) => rest;
const table = (report, id, name) => report.cases.find(item => item.caseId === id).observed.experiments.find(item => item.id === `${id}/${name}`).reads
    .map(entry => `${entry.label}=${bare(entry.title)}/${bare(entry.icon)}`);

test('the published evidence re-derives exactly from its retained paired journal and run log, and every digest is the one the receipt bound', () => {
    const loaded = load(), { report, summary, run, frames } = loaded, derived = derive(loaded);
    assert.equal(JSON.stringify(report), JSON.stringify(strip(derived)));
    assert.equal(report.suiteId, titleStackSuiteId);
    assert.equal(report.adapter.id, titleStackAdapterId);
    assert.equal(report.adapter.derivedFromAdapterId, 'ntilde-owned-left-right-margins-grading-v1');
    assert.equal(frames.length, 61);
    assert.deepEqual(frames.map(frame => frame.index), frames.map((_, index) => index));
    assert.equal(report.restoredSnapshot, 60);
    const { receipt } = summary;
    assert.equal(receipt.titleStackEvidence.sha256, hash(JSON.stringify(derived, null, 2) + '\n'));
    assert.equal(receipt.titleStackEvidence.runSha256, hash(JSON.stringify(run) + '\n'));
    assert.equal(summary.retained.titleStackJournalSha256, hash(frames.map(frame => JSON.stringify(frame)).join('\n') + '\n'));
    assert.equal(summary.retained.titleStackObservationsSha256, receipt.titleStackEvidence.sha256);
    assert.equal(summary.retained.titleStackRunSha256, receipt.titleStackEvidence.runSha256);
    assert.equal(summary.retained.titleStackAdapterSha256, receipt.titleStackEvidence.adapterBundleSha256);
    assert.equal(receipt.titleStackEvidence.adapterBundleSha256, summary.context.titleStackBundleSha256);
    assert.equal(receipt.titleStackEvidence.adapterBundleSha256, report.adapter.bundleSha256);
});

test('the pinned termless verdicts are supported, the terminal path is inconclusive by design, and the two separately identified effect contracts are supported', () => {
    const { report } = load();
    assert.deepEqual(report.cases.map(item => item.caseId), [...titleStackCaseIds]);
    for (const item of report.cases) {
        assert.equal(item.pinned.pass, true);
        assert.equal(item.pinned.observation.outcome, 'supported');
        assert.equal(item.pinned.observation.evidence, 'parser-state');
        assert.equal(item.pinned.terminal.observation.outcome, 'inconclusive');
        assert.equal(item.pinned.terminal.observation.evidence, 'consumed');
        assert.equal(item.conclusion.outcome, 'supported');
        assert.equal(item.conclusion.grading, 'pinned-termless-replayed-over-owned-observations');
        assert.deepEqual(item.contractResult, { outcome: 'supported' });
        assert.match(item.effectContract.attribution, /not the pinned probe/);
        assert.ok(item.effectContract.validity.every(entry => entry.held) && item.effectContract.required.every(entry => entry.held));
        assert.deepEqual(item.divergences.map(entry => entry.kind), ['pinned-terminal-inconclusive-by-design']);
        assert.equal(item.effectContract.boundaries.outcome, 'supported');
    }
    assert.deepEqual(report.cases.map(item => item.effectContract.id), ['ntilde-xtwinops-title-push-effect-v1', 'ntilde-xtwinops-title-pop-effect-v1']);
    assert.equal(report.cases[0].effectContract.limits.outcome, 'supported');
    assert.equal(report.cases[1].effectContract.limits, undefined);
    assert.deepEqual(titleStackCounts(report), { 'supported/supported': 2 });
});

test('the pinned code ran over the owned observations: pushed-title, new-title, pushed-title on both cases, and the terminal path replayed after its own writes', () => {
    const { report, frames, run } = load();
    for (const item of report.cases) {
        const stock = item.experiments.find(entry => entry.name === 'stock');
        const titles = Object.fromEntries(stock.reads.filter(entry => entry.label.startsWith('pinned-')).map(entry => [entry.label, frames[entry.snapshot].before.metadata.oscTitle]));
        assert.deepEqual(titles, { 'pinned-original': 'pushed-title', 'pinned-changed': 'new-title', 'pinned-restored': 'pushed-title' });
        const recorded = run.pinned.find(entry => entry.caseId === item.caseId);
        assert.deepEqual(recorded.termless.feeds, [...titleStackPinned[item.caseId].termlessFeeds]);
        assert.deepEqual(recorded.terminal.writes, [...titleStackPinned[item.caseId].terminalWrites]);
        assert.deepEqual(recorded.termless.result, replayTitleStackTermless(item.caseId, titles));
        assert.deepEqual(recorded.terminal.result, replayTitleStackTerminal(item.caseId, true));
        assert.equal(recorded.termless.result.response, '{"original":"pushed-title","changed":"new-title","restored":"pushed-title"}');
        // The raw icon name stayed the experiment's own baseline throughout: the pinned titles never touched it.
        for (const entry of stock.reads) assert.match(entry.evaluation.observed.icon, /-s[23]-i0$/);
    }
    assert.deepEqual(report.cases[0].pinned.terminal.note, 'Cursor answered after title push; title stack was not measured');
    assert.deepEqual(report.cases[1].pinned.terminal.note, 'Cursor answered after title pop; restored title was not measured');
});

test('observed stack behavior: a push saves without replacing, pops restore LIFO, selectors restore only their half and consume the entry, an empty pop is silent', () => {
    const { report } = load();
    assert.deepEqual(table(report, push, 'effect'), ['base=e2-t0/e2-i0', 'pushed=e2-t0/e2-i0', 'changed=e2-t1/e2-i1', 'popped=e2-t0/e2-i0']);
    assert.deepEqual(table(report, push, 'selectors'), ['base=q2-t0/q2-i0', 'changed-1=q2-t1/q2-i1', 'title-restored=q2-t0/q2-i1', 'icon-restored=q2-t3/q2-i1']);
    assert.deepEqual(table(report, push, 'nested'), ['base=n2-t0/n2-i0', 'changed-2=n2-t2/n2-i2', 'popped-1=n2-t1/n2-i1', 'popped-2=n2-t0/n2-i0']);
    assert.deepEqual(table(report, pop, 'effect'), ['base=e3-t0/e3-i0', 'changed=e3-t1/e3-i1', 'restored=e3-t0/e3-i0']);
    assert.deepEqual(table(report, pop, 'empty'), ['base=z3-t0/z3-i0', 'changed=z3-t1/z3-i1', 'empty-pop=z3-t1/z3-i1']);
    assert.deepEqual(table(report, pop, 'selectors'), ['base=q3-t0/q3-i0', 'changed-1=q3-t1/q3-i1', 'icon-popped=q3-t1/q3-i0', 'title-after-consumed=q3-t1/q3-i0', 'title-popped=q3-t1/q3-i3', 'icon-after-consumed=q3-t1/q3-i3']);
    assert.deepEqual(table(report, pop, 'lifo'), ['base=l3-t0/l3-i0', 'changed-2=l3-t2/l3-i2', 'popped-1=l3-t1/l3-i1', 'popped-2=l3-t0/l3-i0', 'popped-3=l3-t0/l3-i0']);
    assert.deepEqual(table(report, pop, 'mixed'), ['base=m3-t0/m3-i0', 'changed-2=m3-t2/m3-i2', 'popped-1=m3-t0/m3-i1', 'popped-2=m3-t0/m3-i1'], 'the one-sided copy rule, not an assumed model');
});

test('the documented depth of ten: twelve pushes keep the newest ten, so pops return the 12th, then the 3rd, then nothing', () => {
    const { report } = load();
    assert.deepEqual(table(report, push, 'overflow'), ['base=o2-t0/o2-i0', 'changed=o2-t12/o2-i12', 'pop-1=o2-t11/o2-i11', 'pop-10=o2-t2/o2-i2', 'pop-11=o2-t2/o2-i2']);
    assert.deepEqual(report.cases[0].effectContract.limits.experiments, ['device.xtwinops-22/overflow']);
});

test('boundaries: the stack survives alternate screens and DECSTR; RIS clears it, does not retitle, clears the cells and raises the history generation', () => {
    const { report, frames } = load();
    assert.deepEqual(table(report, push, 'alternate-screen'), ['base=a2-t0/a2-i0', 'changed-1=a2-t1/a2-i1', 'popped-1=a2-t0/a2-i0', 'changed-2=a2-t2/a2-i2', 'popped-2=a2-t0/a2-i0']);
    assert.deepEqual(table(report, push, 'decstr'), ['base=d2-t0/d2-i0', 'changed=d2-t1/d2-i1', 'popped=d2-t0/d2-i0']);
    assert.deepEqual(table(report, pop, 'ris'), ['base=r3-t0/r3-i0', 'after-ris=r3-t1/r3-i1', 'after-pop=r3-t1/r3-i1']);
    const ris = report.cases[1].experiments.find(entry => entry.name === 'ris');
    const first = frames[0].before.metadata.buffer.historyGeneration;
    assert.deepEqual(ris.reads.map(entry => entry.evaluation.observed.historyGeneration), [first, first + 1, first + 1]);
    for (const entry of ris.reads.slice(1)) {
        assert.equal(entry.evaluation.scope, 'cleared');
        assert.deepEqual(entry.evaluation.observed.cursor, [0, 0]);
        assert.ok(frames[entry.snapshot].before.screen.lines.every(line => line === ''), 'RIS cleared every cell');
    }
});

test('the raw title, the raw icon name and the actual tab text were read separately on every read; the display title equals the nonblank raw title and the tab text contains it', () => {
    const { report, frames } = load();
    for (const item of report.cases) for (const experiment of item.experiments) for (const entry of experiment.reads) {
        const metadata = frames[entry.snapshot].before.metadata;
        assert.equal(typeof metadata.oscTitle, 'string'); assert.equal(typeof metadata.buffer.requestedIconName, 'string');
        assert.equal(metadata.displayTitle, metadata.oscTitle);
        assert.ok(metadata.presentation.tabText.includes(metadata.oscTitle), `${experiment.id} ${entry.label}`);
        assert.equal(entry.evaluation.passed, true, `${experiment.id} ${entry.label}`);
        assert.equal(metadata.buffer.links.length, 0);
    }
});

test('restoration is exactly what the observation API can show: the prepared title and icon, the initial cells and the original palette, never the earlier residue', () => {
    const { frames, summary } = load(), first = frames[0].before, bound = frames[1].before, last = frames.at(-1).before, fixture = titleStackFixture(summary.context.runId);
    assert.deepEqual(last.screen, first.screen, 'the initial screen, cursor, history and color binding are restored');
    assert.deepEqual(first.screen.colorBinding.paletteOverrideIndices, []);
    assert.deepEqual(bound.screen.colorBinding.paletteOverrideIndices, [7]);
    assert.notEqual(colorBindingHash(bound.screen.colorBinding), colorBindingHash(first.screen.colorBinding));
    assert.equal(last.metadata.oscTitle, fixture.title, 'the prepared title, not the pre-existing one, is what is restored');
    assert.equal(last.metadata.buffer.requestedIconName, fixture.title, 'the prepared icon name, after a final pop on an emptied stack');
    assert.notEqual(first.metadata.oscTitle, fixture.title, 'the title found at the start was an earlier suite\'s residue and is not claimed restored');
    assert.notEqual(first.metadata.buffer.requestedIconName, fixture.title);
    assert.equal(last.metadata.workingDirectory, fixture.cwd);
    assert.equal(summary.receipt.fixtureRemoved, true);
});

test('the receipt binds the run, adapter and counts, and every predecessor and finding reconfirm in the same run', async () => {
    const { summary, report } = load(), { receipt } = summary;
    assert.equal(receipt.schemaVersion, 52);
    assert.equal(receipt.titleStackEvidence.runId, summary.context.runId);
    assert.equal(receipt.titleStackEvidence.suiteId, titleStackSuiteId);
    assert.deepEqual(receipt.titleStackEvidence.counts, titleStackCounts(report));
    assert.equal(summary.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v46');
    const predecessors = benchmarkSuites.slice(0, benchmarkSuites.findIndex(item => item.suite === 'title-stack'));
    const expected = [];
    for (const suite of predecessors) expected.push(...(await import(new URL(suite.module, import.meta.url)))[suite.exports[2]]);
    const unique = new Set(Object.values(summary.sameRun).flatMap(suite => suite.cases.map(item => item.caseId)));
    assert.deepEqual([...unique].sort(), [...new Set(expected)].sort());
    assert.equal(Object.keys(summary.sameRun).length, predecessors.length);
    for (const [name, suite] of Object.entries(summary.sameRun)) {
        if (suite.cases.some(item => item.effectContract)) assert.ok(suite.cases.every(item => item.effectContract === 'supported'), name);
        else assert.ok(suite.cases.every(item => item.outcome === 'supported'), name);
    }
    for (const id of titleStackCaseIds) assert.ok(!unique.has(id));
    assert.deepEqual(Object.keys(summary.coreSuites), ['coreGapsEvidence', 'textCursorEvidence', 'coreEraseEvidence', 'tabStopsEvidence', 'coreWrapEvidence', 'sgrRasterEvidence', 'reverseWideEvidence']);
    for (const [key, entry] of Object.entries(summary.coreSuites)) assert.deepEqual(entry, { suiteId: receipt[key].suiteId, file: receipt[key].file, sha256: receipt[key].sha256 }, key);
    assert.deepEqual(summary.queryCounts, { matched: 33, mismatched: 7 }, 'the query suite reconfirms with the same frozen mismatches');
    assert.equal(summary.truecolorFinding.cases.length, 10);
    assert.equal(summary.underlineResolved.cases.length, 3);
});

test('the stock run (re-run in the same owned run) keeps both ids inconclusive on the terminal path and no stock grade is rewritten or raised', () => {
    const { summary } = load();
    assert.deepEqual(summary.stockCollector.counts, { inconclusive: 167, supported: 87, unsupported: 1 });
    assert.equal(summary.stockCollector.suiteComplete, false);
    for (const id of titleStackCaseIds) {
        assert.equal(summary.stockCollector.probes[id].outcome, 'inconclusive', id);
        assert.equal(summary.stockCollector.probes[id].evidence, 'consumed', id);
    }
    assert.match(summary.stockCollector.probes[push].note, /title stack was not measured/);
    assert.match(summary.stockCollector.probes[pop].note, /restored title was not measured/);
});

test('revisions, attempts (every superseded one keeps its actual reason) and file sizes', () => {
    const { summary } = load();
    assert.match(summary.appRevision, /^[0-9a-f]{40}$/);
    assert.match(summary.scriptsRevision, /^[0-9a-f]{40}$/);
    assert.deepEqual(summary.attempts.map(item => item.receiptCompleted), [false, true, true, true, true, true, true]);
    assert.equal(summary.attempts.at(-1).runId, summary.context.runId);
    assert.equal(summary.attempts.at(-1).scriptsRevision, summary.scriptsRevision);
    assert.match(summary.attempts[0].outcome, /history generation/);
    assert.match(summary.attempts[1].outcome, /uncommitted/);
    assert.match(summary.attempts[2].outcome, /Codex review \(P2\)/);
    assert.match(summary.attempts[3].outcome, /SonarCloud/);
    assert.match(summary.attempts[4].outcome, /OSC 7\/8 layer \(PR 953\)/);
    assert.match(summary.attempts[5].outcome, /CodeRabbit/);
    for (const number of [1, 2, 3, 4, 5, 6]) assert.ok(existsSync(new URL(`superseded/attempt-${number}-superseded.zip`, dir)));
    for (const name of readdirSync(dir)) {
        if (name === 'superseded') continue;
        assert.ok(readFileSync(new URL(name, dir), 'utf8').split('\n').length <= (name === 'summary.json' ? 2000 : 1500), name);
    }
});
