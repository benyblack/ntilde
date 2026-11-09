import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import {
    assertInventory, benchmarkSuites, buildInventory, defaultQaRoot, effectVerdictOf, findingSuites, inventoryFiles, loadPublication, measuredLayersOf,
    pinnedVerdictOf, readText, receiptBinding, renderSummary, serialize, stockBaselineFile, stockProvenance, testDigest, totalsOf,
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
        assert.match(entry.rederivation.command, /^node --test scripts\/terminfo\/[a-z-]+-publication\.test\.mjs$/);
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
    assert.equal(totals.pinnedSupportedIds, totals.pinnedAndEffectSupportedIds, 'a pinned-supported id is also effect-supported here');
    const inconclusive = totals.baseline.stockInconclusive;
    assert.equal(totals.remainingAgainstStockInconclusive.effectUnqualified, inconclusive - totals.effectSupportedOfStockInconclusive);
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

test('only controlled pixels are measured, by the cursor suite alone; layers are classified from the retained evidence', () => {
    const stored = committed();
    assert.ok(stored.benchmark.every(item => item.claims.every(claim => !claim.measuredLayers.includes('pixels'))));
    const pixelClaims = stored.benchmark.flatMap(item => item.claims.filter(claim => claim.measuredLayers.includes('controlled-pixels')).map(claim => [item.id, claim.suite]));
    assert.deepEqual(pixelClaims, [['cursor.hide', 'cursor'], ['cursor.shape', 'cursor']]);
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
        if (suite.stock.basis !== 'carried-forward') assert.deepEqual(suite.stock.counts, base.counts, suite.suite);
        assert.equal(suite.stock.suiteComplete ?? false, false);
    }
    const carried = stored.suites.filter(suite => suite.stock.basis === 'carried-forward');
    assert.deepEqual(carried.map(suite => suite.suite), ['char-edit', 'attr-rect']);
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

test('the generated summary states its baseline and keeps the diagnostic out of the counts', () => {
    const text = renderSummary(committed());
    assert.match(text, /Baseline: stock run `[0-9a-f]{32}`/);
    assert.match(text, /cursor\.reverse-wrap.*suiteComplete=false.*not a graded stock case/);
    assert.match(text, /Ntilde-specific findings \(not benchmark ids\)/);
    assert.ok(Buffer.byteLength(text) < 40 * 1024 && readFileSync(join(defaultQaRoot, inventoryFiles.json)).length < 160 * 1024, 'the generated files stay reviewable');
});
