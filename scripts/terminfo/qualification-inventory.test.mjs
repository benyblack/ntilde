import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import {
    assertInventory, benchmarkSuites, buildInventory, defaultQaRoot, effectVerdictOf, findingSuites, inventoryFiles, loadPublication, measuredLayersOf,
    nonInventoryPublicationTests, pinnedVerdictOf, readText, receiptBinding, renderSummary, serialize, stockBaselineFile, stockProvenance, testDigest, totalsOf,
} from './qualification-inventory.mjs';

const read = name => JSON.parse(readText(join(defaultQaRoot, name)));
const committed = () => read(inventoryFiles.json);
const baseline = () => read(stockBaselineFile);
const clone = value => structuredClone(value);
const check = inventory => assertInventory(inventory, { baseline: baseline() });
const row = (inventory, id) => inventory.benchmark.find(item => item.id === id);

test('the committed inventory is exactly what the merged contracts and retained publications derive', async () => {
    const stored = committed();
    const rederivations = new Map([...stored.suites, ...stored.findings].map(entry => [entry.suite, entry.rederivation]));
    // Reuses the recorded rederivation results (the publication tests themselves run in CI) but re-derives everything else
    // and re-binds each result to the current test file digest.
    const fresh = await buildInventory({ rederivation: suite => ({ ...rederivations.get(suite.suite), testSha256: testDigest(suite) }) });
    assert.equal(JSON.stringify(fresh), JSON.stringify(stored));
    assert.equal(readText(join(defaultQaRoot, inventoryFiles.markdown)), `${renderSummary(stored)}\n`);
    assert.equal(readText(join(defaultQaRoot, inventoryFiles.json)), serialize(stored));
    check(stored);
});

test('every suite was rederived and every counted claim sits on a passed rederivation', () => {
    const stored = committed();
    for (const entry of [...stored.suites, ...stored.findings]) {
        assert.equal(entry.rederivation.status, 'passed', entry.suite);
        assert.match(entry.rederivation.command, /^node --test scripts\/terminfo\/[a-z0-9-]+-publication\.test\.mjs$/);
        assert.match(entry.rederivation.testSha256, /^[0-9a-f]{64}$/);
    }
    assert.ok(stored.benchmark.every(item => item.claims.every(claim => claim.status === 'qualified')));
    const suiteTests = new Set([...benchmarkSuites, ...findingSuites].map(suite => suite.test));
    assert.equal(suiteTests.size, benchmarkSuites.length + findingSuites.length, 'one publication test per suite');
});

test('the totals are identities over the rows, not independent numbers', () => {
    const stored = committed(), { totals } = stored;
    assert.equal(totals.uniqueBenchmarkIds, stored.benchmark.length);
    assert.equal(totals.suiteClaims, stored.benchmark.reduce((count, item) => count + item.claims.length, 0));
    assert.equal(totals.suiteClaims, stored.suites.reduce((count, suite) => count + suite.caseCount, 0));
    assert.equal(totals.suiteClaims - totals.uniqueBenchmarkIds, totals.idsWithMultipleClaims.reduce((count, id) => count + row(stored, id).claims.length - 1, 0));
    assert.equal(totals.effectSupportedIds, totals.pinnedAndEffectSupportedIds + totals.effectSupportedPinnedInconclusiveIds.length);
    // Every qualified id has exactly one effect state; an unsupported id is neither supported nor missing.
    assert.equal(totals.uniqueBenchmarkIds, totals.effectSupportedIds + totals.effectUnsupportedIds + totals.effectOtherIds);
    assert.equal(totals.effectUnsupportedIds, stored.benchmark.filter(item => item.effect.verdict === 'unsupported').length);
    assert.equal(totals.effectUnsupportedIds - totals.effectUnsupportedOfStockInconclusive, totals.ungradedStockIdsWithEvidence.filter(id => row(stored, id).effect.verdict === 'unsupported').length);
    assert.equal(totals.pinnedSupportedIds, totals.pinnedAndEffectSupportedIds, 'a pinned-supported id is also effect-supported here');
    const inconclusive = totals.baseline.stockInconclusive;
    const remaining = totals.remainingAgainstStockInconclusive;
    assert.equal(remaining.effectUnqualified, inconclusive - totals.effectSupportedOfStockInconclusive);
    // `effectUnqualified` splits into three distinct states: measured-unsupported, measured-other and not measured at all.
    assert.equal(remaining.effectUnqualified, remaining.effectMeasuredUnsupported + remaining.effectMeasuredOther + remaining.effectNotMeasured);
    assert.equal(remaining.effectMeasuredUnsupported, totals.effectUnsupportedOfStockInconclusive);
    assert.equal(remaining.effectNotMeasured, inconclusive - stored.benchmark.filter(item => item.stockOutcome === 'inconclusive').length);
    assert.equal(totals.remainingAgainstStockInconclusive.notPinnedSupported, inconclusive - totals.pinnedSupportedOfStockInconclusive);
    assert.equal(totals.effectSupportedIds - totals.effectSupportedOfStockInconclusive, totals.ungradedStockIdsWithEvidence.length, 'only ungraded stock ids can be effect-supported without being stock-inconclusive');
    assert.equal(totals.baseline.allCases, totals.baseline.stockSupported + totals.baseline.stockUnsupported + inconclusive);
    assert.deepEqual(totalsOf(stored.benchmark, baseline()), totals);
});

test('multi-suite ids keep their separate claims and different ids keep different rows', () => {
    const stored = committed();
    const fg = row(stored, 'sgr.fg.standard');
    assert.deepEqual(fg.claims.map(claim => claim.suite), ['sgr-colors', 'sgr-rgb']);
    assert.deepEqual(fg.pinned.byClaim.map(entry => entry.verdict), ['inconclusive', 'supported']);
    assert.equal(fg.pinned.best, 'supported');
    assert.equal(fg.claims[0].divergence.recordedInReport, false, 'the older suite recorded no divergence; the inventory derives and labels it');
    assert.notDeepEqual(fg.claims[0].measuredLayers, fg.claims[1].measuredLayers);
    // Identical fixtures under different ids stay distinct rows.
    for (const id of ['scrollback.accumulate', 'scrollback.total-lines']) assert.ok(row(stored, id), id);
    assert.equal(new Set(stored.benchmark.map(item => item.id)).size, stored.benchmark.length);
});

test('the five effect-only ids keep a recorded pinned/effect divergence instead of one merged grade', () => {
    const stored = committed();
    for (const id of stored.totals.effectSupportedPinnedInconclusiveIds) {
        const item = row(stored, id);
        assert.equal(item.pinned.best, 'inconclusive', id);
        assert.equal(item.effect.verdict, 'supported', id);
        assert.ok(item.claims.every(claim => claim.divergence?.recordedInReport === true), id);
    }
    // Saved-state suite preserves the pinned verdict as the reported conclusion; the scrollback suite reports the combined one. Both are visible.
    assert.equal(row(stored, 'modes.altscreen-47').claims[0].reportedConclusion.outcome, 'inconclusive');
    assert.equal(row(stored, 'scrollback.set-region').claims[0].reportedConclusion.outcome, 'supported');
});

test('benchmark ids, Ntilde findings, controls and diagnostics are separated', () => {
    const stored = committed(), base = baseline();
    for (const item of stored.benchmark) {
        if (item.stockStatus) {
            assert.equal(item.stockStatus, 'ungraded-collector-error'); assert.equal(item.stockOutcome, null);
            assert.ok(stored.ungradedDiagnostics.some(diagnostic => diagnostic.id === item.id), item.id);
        } else assert.equal(base.outcomes[item.id], 'inconclusive', item.id);
        assert.doesNotMatch(item.id, /^(?:finding|ntilde)\./);
    }
    assert.deepEqual(stored.findings.map(finding => finding.suite), ['truecolor-literal-finding', 'sgr-underline-color-resolved']);
    const truecolor = stored.findings[0];
    assert.deepEqual(truecolor.outcomes, { before: { unsupported: 10 }, after: { supported: 10 } });
    assert.equal(truecolor.caseIds.length, 10);
    assert.deepEqual(stored.findings[1].outcomes, { supported: 3 });
    assert.equal(stored.findings[1].caseIds.length, 3);
    const findingIds = new Set(stored.findings.flatMap(finding => finding.caseIds));
    assert.ok(stored.benchmark.every(item => !findingIds.has(item.id)));
    assert.deepEqual(stored.ungradedDiagnostics.map(item => [item.id, item.graded, item.suiteComplete]), [['cursor.reverse-wrap', false, false]]);
    assert.equal(base.outcomes['cursor.reverse-wrap'] ?? null, null, 'the ungraded diagnostic is not a graded stock case');
    assert.deepEqual(stored.stockBaseline.stockUnsupported, ['extensions.osc5522-clipboard']);
});

test('only controlled pixels are measured, by the cursor and runtime-color reset suites; layers are classified from the retained evidence', () => {
    const stored = committed();
    assert.ok(stored.benchmark.every(item => item.claims.every(claim => !claim.measuredLayers.includes('pixels'))));
    const pixelClaims = stored.benchmark.flatMap(item => item.claims.filter(claim => claim.measuredLayers.includes('controlled-pixels')).map(claim => [item.id, claim.suite]));
    assert.deepEqual(pixelClaims, [['cursor.hide', 'cursor'], ['cursor.shape', 'cursor'], ['extensions.osc104-reset-palette', 'color-reset'], ['extensions.osc110-reset-fg', 'color-reset'],
        ['extensions.osc111-reset-bg', 'color-reset'], ['extensions.osc112-reset-cursor', 'color-reset']]);
    assert.deepEqual(measuredLayersOf('owned-stored-physical-cursor-and-cpr', ''), ['stored-state', 'cursor-position-report']);
    assert.deepEqual(measuredLayersOf('owned-stored-cursor-and-controlled-pixels', ''), ['stored-state', 'controlled-pixels']);
    assert.deepEqual(measuredLayersOf('owned-cells', 'graded from parent-journaled readScreen cells'), ['stored-state']);
    assert.deepEqual(measuredLayersOf('owned-cells', 'Named-color conclusions cover stored indices and markers, never theme-resolved RGB'), ['stored-state'], 'a scope that disclaims resolution does not add the layer');
    assert.deepEqual(measuredLayersOf('owned-resolved-colors', 'resolved RGB bound to one recorded palette/theme state'), ['app-resolved-color']);
    assert.deepEqual(measuredLayersOf('owned-stored-and-resolved-cells', 'App-resolved RGB bound to one recorded theme'), ['stored-state', 'app-resolved-color']);
    assert.deepEqual(measuredLayersOf('owned-stored-and-resolved-cells', 'resolved underline RGB derived parent-side from the stored color'), ['stored-state', 'parent-derived-resolution']);
    assert.deepEqual(measuredLayersOf('owned-stored-cells-and-actual-scrollback', ''), ['stored-state', 'actual-scrollback']);
    assert.throws(() => measuredLayersOf('mystery', ''), /Cannot classify/);
});

test('stock baseline: retained outcome map, explicit identity, stock counts kept separate and labeled fresh or carried', () => {
    const base = baseline(), stored = committed();
    const counts = {};
    for (const outcome of Object.values(base.outcomes)) counts[outcome] = (counts[outcome] ?? 0) + 1;
    assert.deepEqual(counts, base.counts);
    assert.equal(base.source.suiteComplete, false);
    assert.equal(base.source.identity, 'unverified');
    assert.ok(Object.keys(base.ungradedDiagnostics).every(id => !(id in base.outcomes)));
    for (const suite of stored.suites) {
        assert.ok(['collected-run-id-recorded', 'collected-raw-digest-recorded', 'carried-forward'].includes(suite.stock.basis), suite.suite);
        // Runs from the rectangle-checksum suite on (policy v33 and later) turn the off-by-default DECRQCRA report on in their initial
        // settings, so their stock run answers editing.decrqcra: exactly that one case moves from inconclusive to supported. Every earlier
        // collected suite matches the baseline.
        const gated = stored.suites.findIndex(item => item.suite === 'rect-checksum');
        // The notification suite's App answers the unchanged OSC 99 probe (query policy v3), so its stock run also grades extensions.osc99-kitty-notify supported.
        const notice = stored.suites.findIndex(item => item.suite === 'notify');
        const moved = (gated >= 0 && stored.suites.indexOf(suite) >= gated ? 1 : 0) + (notice >= 0 && stored.suites.indexOf(suite) >= notice ? 1 : 0);
        const expected = { ...base.counts, inconclusive: base.counts.inconclusive - moved, supported: base.counts.supported + moved };
        if (suite.stock.basis !== 'carried-forward') assert.deepEqual(suite.stock.counts, expected, suite.suite);
        assert.equal(suite.stock.suiteComplete ?? false, false);
    }
    const carried = stored.suites.filter(suite => suite.stock.basis === 'carried-forward');
    assert.deepEqual(carried.map(suite => suite.suite), ['char-edit', 'attr-rect', 'erase-chars-history', 'selective-erase']);
    for (const suite of carried) assert.match(suite.stock.note, /carried, not re-measured/, suite.suite);
    const runIds = stored.suites.map(suite => suite.stock.runId).filter(Boolean);
    assert.equal(new Set(runIds).size, runIds.length, 'two suites must not claim the same stock run as freshly collected');
    assert.deepEqual(stockProvenance({}), null);
    assert.equal(stockProvenance({ stockCollector: { rerun: false, counts: {} }, stockCollectorRunId: 'x' }).basis, 'carried-forward');
    assert.equal(stockProvenance({ stockCollector: { counts: {} } }).basis, 'unspecified');
});

test('every retained report, contract and publication layout loads and agrees on its case ids', () => {
    for (const suite of benchmarkSuites) {
        const { report } = loadPublication(defaultQaRoot, suite);
        assert.ok(report.cases.length > 0 && report.cases.every(item => typeof item.caseId === 'string'), suite.suite);
    }
});

test('pinned and effect verdicts are read separately and malformed retained results are refused', () => {
    assert.equal(pinnedVerdictOf({ pinned: { observation: { outcome: 'inconclusive' } } }), 'inconclusive');
    assert.equal(pinnedVerdictOf({ pinned: { pass: true } }), 'supported');
    assert.throws(() => pinnedVerdictOf({ caseId: 'x', pinned: { pass: false } }), /neither an observation nor a pass/);
    assert.throws(() => pinnedVerdictOf({ caseId: 'x', pinned: { observation: { outcome: 'passed' } } }), /unknown pinned outcome/);
    assert.equal(effectVerdictOf({ contractResult: { outcome: 'supported' } }), 'supported');
    assert.throws(() => effectVerdictOf({ caseId: 'x', contractResult: {} }), /no effect-contract verdict/);
    assert.deepEqual(receiptBinding(null, 's'), null);
    const binding = receiptBinding({ schemaVersion: 3, runId: 'r', suiteId: 'cells', file: 'c.json', sha256: 'h', own: { suiteId: 's', file: 'o.json', sha256: 'p', counts: { passed: 1 } } }, 's');
    assert.deepEqual(binding, { schemaVersion: 3, runId: 'r', cellReceipt: { file: 'c.json', sha256: 'h' }, suiteEvidence: { suiteId: 's', file: 'o.json', sha256: 'p', counts: { passed: 1 } } });
});

test('guard: a benchmark id counted twice, or one suite claiming an id twice, is refused', () => {
    const duplicateRow = committed();
    duplicateRow.benchmark.push(clone(duplicateRow.benchmark[0]));
    assert.throws(() => check(duplicateRow), /listed twice/);
    const duplicateClaim = committed();
    duplicateClaim.benchmark[0].claims.push(clone(duplicateClaim.benchmark[0].claims[0]));
    assert.throws(() => check(duplicateClaim), /duplicate claim/);
});

test('guard: a lost or added case is refused', () => {
    const lost = committed();
    lost.benchmark = lost.benchmark.filter(item => item.id !== 'scrollback.decstbm');
    assert.throws(() => check(lost), /scrollback: .*(?:claims|lost)/);
    const added = committed();
    added.suites[0].caseIds = [...added.suites[0].caseIds, 'text.invented'];
    assert.throws(() => check(added), /ascii-sgr: .*(?:contract ids differ|claims)/);
    const drift = committed();
    assert.throws(() => assertInventory(drift, { baseline: baseline(), reportCaseIds: new Map([['ascii-sgr', [...drift.suites[0].caseIds, 'text.extra']]]) }), /lost or added case/);
});

test('guard: pinned and effect verdicts cannot be collapsed and a missing divergence is refused', () => {
    const collapsed = committed();
    const claim = row(collapsed, 'scrollback.set-region').claims[0];
    claim.divergence = null;
    assert.throws(() => check(collapsed), /differ but no divergence is recorded/);
    const invented = committed();
    row(invented, 'text.basic').claims[0].divergence = { recordedInReport: false, kind: 'x', pinned: 'supported', effect: 'supported', reason: null };
    assert.throws(() => check(invented), /divergence is recorded for agreeing verdicts/);
    const tampered = committed();
    row(tampered, 'scrollback.set-region').pinned.best = 'supported';
    assert.throws(() => check(tampered), /roll-up differs/);
});

test('guard: unsupported evidence cannot be promoted into the benchmark totals', () => {
    const unrederived = committed();
    unrederived.suites.find(suite => suite.suite === 'scrollback').rederivation.status = 'failed';
    assert.throws(() => check(unrederived), /without a passed rederivation/);
    const foreign = committed();
    foreign.benchmark.push({ ...clone(foreign.benchmark[0]), id: 'finding.truecolor-literal.fg.0-55-218' });
    assert.throws(() => check(foreign), /not a case of the pinned stock baseline|Ntilde-specific id/);
    const diagnostic = committed();
    diagnostic.benchmark.push({ ...clone(diagnostic.benchmark[0]), id: 'cursor.reverse-wrap' });
    assert.throws(() => check(diagnostic), /listed twice|ungraded stock diagnostic/);
    const stockOutcome = committed();
    row(stockOutcome, 'cursor.reverse-wrap').stockOutcome = 'inconclusive';
    assert.throws(() => check(stockOutcome), /stock outcome differs|ungraded stock diagnostic/);
    const statusless = committed();
    delete row(statusless, 'cursor.reverse-wrap').stockStatus;
    assert.throws(() => check(statusless), /ungraded stock diagnostic has no stock outcome and must say so/);
    const stockSupported = committed();
    const base = baseline();
    const supportedId = Object.keys(base.outcomes).find(id => base.outcomes[id] === 'supported');
    stockSupported.benchmark.push({ ...clone(stockSupported.benchmark[0]), id: supportedId, stockOutcome: 'supported' });
    assert.throws(() => check(stockSupported), /only cover stock-inconclusive cases/);
    const graded = committed();
    graded.ungradedDiagnostics[0].graded = true;
    assert.throws(() => check(graded), /must stay ungraded/);
    const complete = committed();
    complete.ungradedDiagnostics[0].suiteComplete = true;
    assert.throws(() => check(complete), /suiteComplete=false/);
});

test('a failed suite lends no verdict to the totals of an id another suite still qualifies', () => {
    const stored = committed();
    const failed = clone(stored);
    failed.suites.find(suite => suite.suite === 'sgr-rgb').rederivation.status = 'failed';
    for (const item of failed.benchmark) for (const claim of item.claims) if (claim.suite === 'sgr-rgb') claim.status = 'declared-not-rederived';
    const totals = totalsOf(failed.benchmark, baseline());
    // sgr.fg/bg.standard stay qualified through sgr-colors, whose pinned probe was inconclusive; sgr-rgb's pinned-supported must not count.
    assert.equal(totals.pinnedSupportedIds, stored.totals.pinnedSupportedIds - 2);
    assert.equal(totals.uniqueBenchmarkIds, stored.totals.uniqueBenchmarkIds);
    assert.equal(totals.suiteClaims, stored.totals.suiteClaims - 2);
    assert.deepEqual(totals.effectSupportedPinnedInconclusiveIds.filter(id => id.endsWith('.standard')).sort(), ['sgr.bg.standard', 'sgr.fg.standard']);
    failed.totals = totals;
    check(failed);
});

test('guard: recorded totals must match the recomputed ones', () => {
    const inflated = committed();
    inflated.totals.effectSupportedIds += 1;
    assert.throws(() => check(inflated), /totals differ/);
    const pixels = committed();
    pixels.benchmark[0].claims[0].measuredLayers.push('pixels');
    assert.throws(() => check(pixels), /unknown measured layer pixels/);
});

test('extensions.reflow is admitted with an unsupported effect verdict kept distinct from its inconclusive pinned verdict', () => {
    const stored = committed(), item = row(stored, 'extensions.reflow');
    assert.ok(item, 'the reflow suite is registered, so its one benchmark id is in the inventory');
    assert.equal(item.stockOutcome, 'inconclusive');
    assert.equal(item.pinned.best, 'inconclusive');
    assert.equal(item.effect.verdict, 'unsupported');
    assert.deepEqual(item.claims.map(claim => [claim.suite, claim.status, claim.pinned.verdict, claim.effect.verdict]), [['ascii-reflow', 'qualified', 'inconclusive', 'unsupported']]);
    const [claim] = item.claims;
    assert.deepEqual(claim.divergence, { recordedInReport: true, kind: 'pinned-inconclusive-by-design', pinned: 'inconclusive', effect: 'unsupported', reason: claim.divergence.reason });
    assert.deepEqual(claim.measuredLayers, ['stored-state', 'actual-scrollback']);
    // The reported conclusion keeps the pinned verdict; the effect verdict is a separate field and never rewrites it.
    assert.equal(claim.reportedConclusion.outcome, 'inconclusive');
    assert.equal(claim.reportedConclusion.grading, 'pinned-termless-preserved-by-design');
    const suite = stored.suites.find(entry => entry.suite === 'ascii-reflow');
    assert.equal(suite.suiteId, 'ntilde-owned-ascii-reflow-v1');
    assert.equal(suite.caseCount, 1);
    assert.deepEqual(suite.caseIds, ['extensions.reflow']);
    assert.equal(suite.publication, '2026-11-12-ascii-reflow/');
    assert.equal(suite.rederivation.status, 'passed');
    assert.equal(suite.rederivation.command, 'node --test scripts/terminfo/ascii-reflow-publication.test.mjs');
    assert.equal(suite.run.ownedRunId, 'e8d8ebdb956ba201d2924f76dcda48c7');
    assert.equal(suite.run.scriptsRevision, 'd8679e112ce919fd050c5a74f40693edfce13b1c');
    assert.equal(suite.run.sourceSnapshotPolicy, 'readonly-owned-source-graph-v52');
    assert.equal(suite.receipt.schemaVersion, 58);
    assert.equal(suite.receipt.runId, suite.run.ownedRunId);
    assert.ok(suite.evidence.files >= 4 + 15, 'the run archive, manifest, report, summary and every retained superseded attempt are in the evidence digest');
    assert.equal(suite.stock.basis, 'collected-run-id-recorded');
    assert.deepEqual(suite.stock.counts, { inconclusive: 167, supported: 87, unsupported: 1 });
});

test('an unsupported effect result is counted as qualified evidence but never as supported, pinned-supported or a score change', () => {
    const stored = committed(), { totals } = stored;
    assert.deepEqual(totals.effectUnsupportedPinnedInconclusiveIds, ['extensions.reflow']);
    assert.equal(totals.effectUnsupportedIds, 1);
    assert.equal(totals.effectOtherIds, 0);
    assert.ok(!totals.effectSupportedPinnedInconclusiveIds.includes('extensions.reflow'));
    assert.ok(!totals.ungradedStockIdsWithEvidence.includes('extensions.reflow'));
    assert.equal(totals.effectSupportedIds, stored.benchmark.filter(item => item.effect.verdict === 'supported').length);
    assert.equal(totals.uniqueBenchmarkIds, totals.effectSupportedIds + 1);
    assert.equal(totals.suiteClaims, stored.suites.reduce((count, suite) => count + suite.caseCount, 0));
    // Supported-only measures are exactly what they were before reflow was admitted: the unsupported id adds to none of them.
    assert.equal(totals.effectSupportedIds, 108);
    assert.equal(totals.pinnedSupportedIds, 85);
    assert.equal(totals.pinnedAndEffectSupportedIds, 85);
    assert.equal(totals.effectSupportedPinnedInconclusiveIds.length, 23);
    assert.equal(totals.effectSupportedOfStockInconclusive, 107);
    assert.equal(totals.pinnedSupportedOfStockInconclusive, 84);
    // The reflow row lowers no stock grade and the baseline is untouched.
    assert.deepEqual(stored.stockBaseline.counts, baseline().counts);
    assert.equal(baseline().outcomes['extensions.reflow'], 'inconclusive');
    // The remaining counts keep "not supported" and "not measured" apart: reflow moves one id from not-measured to measured-unsupported
    // without changing the not-effect-supported or not-pinned-supported counts it was already part of.
    const remaining = totals.remainingAgainstStockInconclusive;
    assert.deepEqual(remaining, { effectUnqualified: 61, effectMeasuredUnsupported: 1, effectMeasuredOther: 0, effectNotMeasured: 60, notPinnedSupported: 84 });
});

test('totalsOf separates unsupported from supported and unmeasured for any rows, and pinned inconclusive stays pinned inconclusive', () => {
    const stored = committed(), base = baseline();
    const without = stored.benchmark.filter(item => item.id !== 'extensions.reflow');
    const before = totalsOf(without, base), after = totalsOf(stored.benchmark, base);
    assert.equal(after.uniqueBenchmarkIds, before.uniqueBenchmarkIds + 1);
    assert.equal(after.suiteClaims, before.suiteClaims + 1);
    for (const key of ['effectSupportedIds', 'pinnedSupportedIds', 'pinnedAndEffectSupportedIds', 'effectSupportedOfStockInconclusive', 'pinnedSupportedOfStockInconclusive']) assert.equal(after[key], before[key], key);
    assert.deepEqual(after.effectSupportedPinnedInconclusiveIds, before.effectSupportedPinnedInconclusiveIds);
    assert.equal(after.effectUnsupportedIds, before.effectUnsupportedIds + 1);
    assert.equal(after.remainingAgainstStockInconclusive.effectUnqualified, before.remainingAgainstStockInconclusive.effectUnqualified);
    assert.equal(after.remainingAgainstStockInconclusive.effectNotMeasured, before.remainingAgainstStockInconclusive.effectNotMeasured - 1);
    assert.equal(after.remainingAgainstStockInconclusive.effectMeasuredUnsupported, before.remainingAgainstStockInconclusive.effectMeasuredUnsupported + 1);
    // Flipping an existing supported id to a retained unsupported effect verdict moves it only out of the supported measures.
    const flipped = clone(stored.benchmark), target = flipped.find(item => item.id === 'text.basic');
    target.claims[0].effect.verdict = 'unsupported'; target.effect = { verdict: 'unsupported', byClaim: [{ suite: target.claims[0].suite, verdict: 'unsupported' }] };
    const moved = totalsOf(flipped, base);
    assert.equal(moved.effectSupportedIds, after.effectSupportedIds - 1);
    assert.equal(moved.effectUnsupportedIds, after.effectUnsupportedIds + 1);
    assert.equal(moved.uniqueBenchmarkIds, after.uniqueBenchmarkIds);
    assert.equal(moved.remainingAgainstStockInconclusive.effectUnqualified, after.remainingAgainstStockInconclusive.effectUnqualified + 1);
    assert.equal(moved.remainingAgainstStockInconclusive.effectNotMeasured, after.remainingAgainstStockInconclusive.effectNotMeasured, 'a measured id is never "not measured"');
    assert.ok(moved.effectSupportedPinnedInconclusiveIds.every(id => id !== 'text.basic'));
});

test('guard: an unsupported publication cannot be collapsed into its pinned verdict or promoted to supported', () => {
    const collapsed = committed();
    row(collapsed, 'extensions.reflow').claims[0].divergence = null;
    assert.throws(() => check(collapsed), /differ but no divergence is recorded/);
    const promoted = committed();
    const item = row(promoted, 'extensions.reflow');
    item.claims[0].effect.verdict = 'supported';
    assert.throws(() => check(promoted), /roll-up differs|divergence does not match|totals differ/);
    const rolled = committed();
    row(rolled, 'extensions.reflow').effect.verdict = 'supported';
    assert.throws(() => check(rolled), /roll-up differs/);
    const pinnedPromoted = committed();
    row(pinnedPromoted, 'extensions.reflow').pinned.best = 'supported';
    assert.throws(() => check(pinnedPromoted), /roll-up differs/);
    const unrederived = committed();
    unrederived.suites.find(suite => suite.suite === 'ascii-reflow').rederivation.status = 'failed';
    assert.throws(() => check(unrederived), /without a passed rederivation/);
    const dropped = committed();
    dropped.benchmark = dropped.benchmark.filter(entry => entry.id !== 'extensions.reflow');
    assert.throws(() => check(dropped), /ascii-reflow: .*(?:claims|lost)/);
    const inflated = committed();
    inflated.totals.effectUnsupportedIds = 0;
    assert.throws(() => check(inflated), /totals differ/);
});

test('an unsupported publication is re-derived from its retained evidence, not copied from a stored verdict', async () => {
    const suite = benchmarkSuites.find(entry => entry.suite === 'ascii-reflow');
    assert.ok(suite, 'registered');
    const { report, summary } = loadPublication(defaultQaRoot, suite);
    assert.deepEqual(report.cases.map(item => [item.caseId, pinnedVerdictOf(item), effectVerdictOf(item)]), [['extensions.reflow', 'inconclusive', 'unsupported']]);
    assert.equal(summary.ownedRunId, 'e8d8ebdb956ba201d2924f76dcda48c7');
    // A fresh derivation that trusts the recorded rederivation results reproduces the committed row byte for byte, and one whose
    // publication test did not pass refuses to count the claim at all.
    const stored = committed();
    const rederivations = new Map([...stored.suites, ...stored.findings].map(entry => [entry.suite, entry.rederivation]));
    const fresh = await buildInventory({ rederivation: entry => ({ ...rederivations.get(entry.suite), testSha256: testDigest(entry) }) });
    assert.equal(JSON.stringify(row(fresh, 'extensions.reflow')), JSON.stringify(row(stored, 'extensions.reflow')));
    const failing = await buildInventory({ rederivation: entry => ({ status: entry.suite === 'ascii-reflow' ? 'failed' : 'passed', command: 'x', testSha256: testDigest(entry) }) });
    assert.equal(row(failing, 'extensions.reflow').claims[0].status, 'declared-not-rederived');
    assert.equal(failing.totals.uniqueBenchmarkIds, stored.totals.uniqueBenchmarkIds - 1);
    assert.equal(failing.totals.effectUnsupportedIds, 0);
    assert.equal(failing.totals.remainingAgainstStockInconclusive.effectNotMeasured, stored.totals.remainingAgainstStockInconclusive.effectNotMeasured + 1);
});

test('every publication test is a registered suite or finding, or is explicitly classified as outside the inventory', () => {
    const registered = new Set([...benchmarkSuites, ...findingSuites].map(suite => suite.test));
    const present = readdirSync(new URL('.', import.meta.url)).filter(name => /^[a-z0-9-]*publication[a-z0-9-]*\.test\.mjs$/.test(name));
    const unclassified = present.filter(name => !registered.has(name) && !Object.hasOwn(nonInventoryPublicationTests, name));
    assert.deepEqual(unclassified, [], 'a new graded publication must be registered in benchmarkSuites (or classified with a reason), never silently left out');
    for (const name of registered) assert.ok(present.includes(name), `${name} is registered but missing`);
    for (const [name, reason] of Object.entries(nonInventoryPublicationTests)) {
        assert.ok(present.includes(name), `${name} is classified but missing`);
        assert.ok(!registered.has(name), `${name} cannot be both registered and excluded`);
        assert.ok(reason.length > 20, name);
    }
});

test('the generated summary defines unsupported and not-measured separately and lists the unsupported id without counting it as supported', () => {
    const text = renderSummary(committed());
    assert.match(text, /\| Effect contract unsupported \(measured, retained, not supported\) \| 1 \|/);
    assert.match(text, /Effect unsupported, pinned inconclusive \(by design\): `extensions\.reflow`\./);
    assert.match(text, /\| of which measured, effect unsupported \| 1 \|/);
    assert.match(text, /\| of which not measured \(no qualified effect evidence\) \| 60 \|/);
    assert.match(text, /is \*\*not\*\* counted as effect-supported, pinned-supported or as any stock, Core or common-harness improvement/);
    assert.doesNotMatch(text.split('Effect supported but pinned inconclusive:')[1].split('\n')[0], /extensions\.reflow/);
    assert.match(text, /\| `extensions\.reflow` \| inconclusive \| inconclusive \| unsupported \| stored-state \+ actual-scrollback \| ascii-reflow \| ascii-reflow: pinned-inconclusive-by-design \|/);
});

test('the generated summary states its baseline and keeps the diagnostic out of the counts', () => {
    const text = renderSummary(committed());
    assert.match(text, /Baseline: stock run `[0-9a-f]{32}`/);
    assert.match(text, /cursor\.reverse-wrap.*suiteComplete=false.*not a graded stock case/);
    assert.match(text, /Ntilde-specific findings \(not benchmark ids\)/);
    // Measured in the LF form the inventory itself hashes: a Windows checkout adds one CR per line and must not change the verdict.
    const lfLength = bytes => Buffer.from(bytes.toString('latin1').replaceAll('\r\n', '\n'), 'latin1').length;
    // 240 KiB: the modifyOtherKeys suite entry (receipt schema 56, its receipt and run bindings) joins the merged inventory.
    assert.ok(lfLength(Buffer.from(text)) < 40 * 1024 && lfLength(readFileSync(join(defaultQaRoot, inventoryFiles.json))) < 256 * 1024, 'the generated files stay reviewable');
});
