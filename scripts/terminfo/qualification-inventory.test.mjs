import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import {
    assertInventory, benchmarkSuites, buildInventory, defaultQaRoot, effectVerdictOf, evidenceDigest, findingSuites, inventoryFiles, loadPublication, measuredLayersOf,
    nonInventoryPublicationTests, pinnedVerdictOf, readText, receiptBinding, renderSummary, resolveCarriers, serialize, stockBaselineFile, stockProvenance, testDigest, totalsOf,
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
    assert.equal(totals.effectSupportedIds - totals.effectSupportedOfStockInconclusive,
        [...totals.ungradedStockIdsWithEvidence, ...totals.reassessedStockUnsupportedIds].filter(id => row(stored, id).effect.verdict === 'supported').length);
    assert.deepEqual(totals.reassessedStockUnsupportedIds, ['extensions.osc5522-clipboard']);
    assert.equal(row(stored, 'extensions.osc5522-clipboard').stockOutcome, 'unsupported');
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

test('supported effect claims keep a recorded divergence from the inconclusive pinned verdict', () => {
    const stored = committed();
    for (const id of stored.totals.effectSupportedPinnedInconclusiveIds) {
        const item = row(stored, id);
        assert.equal(item.pinned.best, 'inconclusive', id);
        assert.equal(item.effect.verdict, 'supported', id);
        const supportedClaims = item.claims.filter(claim => claim.effect.verdict === 'supported');
        assert.ok(supportedClaims.length > 0 && supportedClaims.every(claim => claim.divergence?.recordedInReport === true), id);
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
        } else {
            const expected = item.id === 'extensions.osc5522-clipboard' ? 'unsupported' : 'inconclusive';
            assert.equal(base.outcomes[item.id], expected, item.id);
        }
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

test('only controlled pixels are measured, by the cursor, runtime-color reset, selection-color, static-image and Kitty animation suites; layers are classified from the retained evidence', () => {
    const stored = committed();
    assert.ok(stored.benchmark.every(item => item.claims.every(claim => !claim.measuredLayers.includes('pixels'))));
    const pixelClaims = stored.benchmark.flatMap(item => item.claims.filter(claim => claim.measuredLayers.includes('controlled-pixels')).map(claim => [item.id, claim.suite]));
    assert.deepEqual(pixelClaims, [['cursor.hide', 'cursor'], ['cursor.shape', 'cursor'], ['extensions.iterm2-images', 'static-images'], ['extensions.kitty-graphics.animation', 'kitty-animation'], ['extensions.kitty-graphics.unicode-placeholders', 'kitty-placeholders'], ['extensions.osc104-reset-palette', 'color-reset'], ['extensions.osc110-reset-fg', 'color-reset'],
        ['extensions.osc111-reset-bg', 'color-reset'], ['extensions.osc112-reset-cursor', 'color-reset'],
        ['extensions.osc117-reset-highlight-bg', 'selection-colors'], ['extensions.osc119-reset-highlight-fg', 'selection-colors'],
        ['extensions.osc17-highlight-bg', 'selection-colors'], ['extensions.osc19-highlight-fg', 'selection-colors'], ['extensions.sixel', 'static-images'], ['extensions.sixel', 'sixel-native-fidelity']]);
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
        // An owned-only publication ran no stock collector at all (the Unicode placeholder suite): it has no stock provenance, and none is invented for it.
        if (suite.stock === null) { assert.deepEqual(['kitty-placeholders'].filter(name => name === suite.suite), [suite.suite]); continue; }
        assert.ok(['collected-run-id-recorded', 'collected-raw-digest-recorded', 'carried-forward'].includes(suite.stock.basis), suite.suite);
        // Runs from the rectangle-checksum suite on (policy v33 and later) turn the off-by-default DECRQCRA report on in their initial
        // settings, so their stock run answers editing.decrqcra: exactly that one case moves from inconclusive to supported. Every earlier
        // collected suite matches the baseline.
        const gated = stored.suites.findIndex(item => item.suite === 'rect-checksum');
        // The notification suite's App answers the unchanged OSC 99 probe (query policy v3), so its stock run also grades extensions.osc99-kitty-notify supported.
        const notice = stored.suites.findIndex(item => item.suite === 'notify');
        // The device-query suite's run turns the off-by-default title reports on and the App answers XTGETXRES, so its stock run also grades device.xtgetxres, device.xtwinops-20 and device.xtwinops-21 supported.
        const device = stored.suites.findIndex(item => item.suite === 'devq');
        // The extended-mouse suite's App answers DECRPM 1/2 for ?1005 (a stock effect of its production change, not an effect-contract upgrade), so its stock run also grades modes.utf8-mouse-1005 supported.
        const mouse = stored.suites.findIndex(item => item.suite === 'mex');
        // The kitty color-control suite's App answers the unchanged OSC 21 probe (query policy v5), so its stock run also grades extensions.osc21-kitty-color supported.
        const kitty = stored.suites.findIndex(item => item.suite === 'kcl');
        // The special-color suite's App answers the unchanged OSC 5;0;? probe (query policy v6), so its stock run also grades extensions.osc5-special-color supported.
        const osc5Live = stored.suites.findIndex(item => item.suite === 'osc5');
        // The OSC 3008 suite was measured on the integrated App, so its stock run carries the special-color grade too.
        const osc3008Live = stored.suites.findIndex(item => item.suite === 'osc3008');
        // Reflow keeps its original registry position, but its replacement measurement follows all five production changes.
        const measurementPosition = suite.suite === 'ascii-reflow' ? stored.suites.length : stored.suites.indexOf(suite);
        const moved = (gated >= 0 && measurementPosition >= gated ? 1 : 0) + (notice >= 0 && measurementPosition >= notice ? 1 : 0) + (device >= 0 && measurementPosition >= device ? 3 : 0)
            + (mouse >= 0 && measurementPosition >= mouse ? 1 : 0) + (kitty >= 0 && measurementPosition >= kitty ? 1 : 0)
            + (osc5Live >= 0 && (measurementPosition === osc5Live || measurementPosition === osc3008Live || ['osc720', 'osc22', 'font-size', 'caps', 'pcl', 'dcl', 'dsl'].includes(suite.suite)) ? 1 : 0)
            // The read-only font queries move exactly their two pinned stock cases; every suite measured after them carries that movement.
            + (suite.suite === 'font-size' || suite.suite === 'caps' || suite.suite === 'pcl' || suite.suite === 'dcl' || suite.suite === 'dsl' ? 2 : 0)
            + (suite.suite === 'caps' || suite.suite === 'pcl' || suite.suite === 'dcl' || suite.suite === 'dsl' ? 1 : 0);
        const expected = { ...base.counts, inconclusive: base.counts.inconclusive - moved, supported: base.counts.supported + moved };
        if (suite.suite === 'sixel-native-fidelity') {
            assert.deepEqual(suite.stock.counts, { inconclusive: 157, supported: 97, unsupported: 1 });
        } else if (suite.stock.basis !== 'carried-forward' && suite.suite !== 'ascii-reflow') {
            assert.deepEqual(suite.stock.counts, expected, suite.suite);
        }
        assert.equal(suite.stock.suiteComplete ?? false, false);
    }
    const carried = stored.suites.filter(suite => suite.stock?.basis === 'carried-forward');
    assert.deepEqual(carried.map(suite => suite.suite), ['char-edit', 'attr-rect', 'erase-chars-history', 'selective-erase', 'osc633-metadata', 'osc633-vscode-reassessment', 'osc5522-clipboard']);
    for (const suite of carried) assert.match(suite.stock.note, /carried, not re-measured|carried forward from/, suite.suite);
    const runIds = stored.suites.filter(suite => suite.stock?.basis !== 'carried-forward').map(suite => suite.stock?.runId).filter(Boolean);
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

test('extensions.reflow selects the fresh supported effect while preserving its inconclusive pinned and stock verdicts', () => {
    const stored = committed(), item = row(stored, 'extensions.reflow');
    assert.equal(item.stockOutcome, 'inconclusive');
    assert.equal(item.pinned.best, 'inconclusive');
    assert.equal(item.effect.verdict, 'supported');
    assert.deepEqual(item.claims.map(claim => [claim.suite, claim.status, claim.pinned.verdict, claim.effect.verdict]), [['ascii-reflow', 'qualified', 'inconclusive', 'supported']]);
    const [claim] = item.claims;
    assert.deepEqual(claim.divergence, { recordedInReport: true, kind: 'pinned-inconclusive-by-design', pinned: 'inconclusive', effect: 'supported', reason: claim.divergence.reason });
    assert.deepEqual(claim.measuredLayers, ['stored-state', 'actual-scrollback']);
    assert.equal(claim.reportedConclusion.outcome, 'inconclusive');
    assert.equal(claim.reportedConclusion.grading, 'pinned-termless-preserved-by-design');
    const suite = stored.suites.find(entry => entry.suite === 'ascii-reflow');
    assert.equal(suite.suiteId, 'ntilde-owned-ascii-reflow-v1');
    assert.equal(suite.caseCount, 1);
    assert.deepEqual(suite.caseIds, ['extensions.reflow']);
    assert.equal(suite.publication, '2026-11-16-ascii-reflow-fix/');
    assert.equal(suite.rederivation.status, 'passed');
    assert.equal(suite.rederivation.command, 'node --test scripts/terminfo/ascii-reflow-fix-publication.test.mjs');
    assert.equal(suite.run.sourceSnapshotPolicy, 'readonly-owned-source-graph-v62');
    assert.equal(suite.receipt.schemaVersion, 69);
    assert.equal(suite.receipt.runId, suite.run.ownedRunId);
    assert.equal(suite.stock.basis, 'collected-run-id-recorded');
    assert.deepEqual(suite.stock.counts, { inconclusive: 161, supported: 93, unsupported: 1 });
    const historical = read('2026-11-12-ascii-reflow/report.json');
    assert.equal(historical.cases[0].effectContract.outcome, 'unsupported');
});

test('OSC cell-size qualification adds two effect-supported ids while preserving pinned and stock outcomes', () => {
    const stored = committed();
    const ids = ['extensions.osc1337-cellsize', 'extensions.osc776-cell-size'];
    const suite = stored.suites.find(entry => entry.suite === 'cell-size');
    assert.deepEqual(suite.caseIds, ids);
    assert.equal(suite.caseCount, 2);
    assert.equal(suite.rederivation.status, 'passed');
    assert.equal(suite.publication, '2026-11-17-osc-cell-size-integrated/');
    assert.equal(suite.rederivation.command, 'node --test scripts/terminfo/cell-size-integrated-publication.test.mjs');
    assert.equal(suite.receipt.schemaVersion, 71);
    assert.equal(suite.run.sourceSnapshotPolicy, 'readonly-owned-source-graph-v64');
    assert.equal(suite.run.appRevision, '8da07482a849946cf213d1d4b6781759bde1c56f');
    assert.equal(suite.run.scriptsRevision, '8da07482a849946cf213d1d4b6781759bde1c56f');
    assert.ok('cell-size-publication.test.mjs' in nonInventoryPublicationTests);
    for (const id of ids) {
        const item = row(stored, id);
        assert.equal(item.effect.verdict, 'supported');
        assert.equal(item.pinned.best, 'inconclusive');
        assert.equal(item.stockOutcome, 'inconclusive');
    }
    const before = totalsOf(stored.benchmark.filter(item => !ids.includes(item.id)), baseline());
    assert.equal(stored.totals.uniqueBenchmarkIds, before.uniqueBenchmarkIds + 2);
    assert.equal(stored.totals.suiteClaims, before.suiteClaims + 2);
    assert.equal(stored.totals.effectSupportedIds, before.effectSupportedIds + 2);
    assert.equal(stored.totals.pinnedSupportedIds, before.pinnedSupportedIds);
});

test('current effect totals preserve the separate unsupported and measured-other claims without inflating pinned scores', () => {
    const stored = committed(), { totals } = stored;
    // Sixel retains the old unsupported result and the fixed supported result, so its effect roll-up is mixed.
    assert.deepEqual(totals.effectUnsupportedPinnedInconclusiveIds, ['extensions.osc113-reset-pointer-fg', 'extensions.osc114-reset-pointer-bg', 'extensions.osc720-scroll-up', 'modes.deccolm', 'modes.decsclm']);
    assert.equal(totals.effectUnsupportedIds, 5);
    assert.equal(totals.effectOtherIds, 1);
    assert.ok(totals.effectSupportedPinnedInconclusiveIds.includes('extensions.reflow'));
    assert.ok(totals.effectSupportedPinnedInconclusiveIds.includes('extensions.iterm2-images'));
    assert.equal(totals.effectSupportedIds, stored.benchmark.filter(item => item.effect.verdict === 'supported').length);
    assert.equal(totals.uniqueBenchmarkIds, totals.effectSupportedIds + totals.effectUnsupportedIds + totals.effectOtherIds);
    assert.equal(totals.suiteClaims, stored.suites.reduce((count, suite) => count + suite.caseCount, 0));
    for (const id of ['extensions.osc7770-font-size', 'extensions.osc7777-font-window-size']) {
        const font = row(stored, id);
        assert.equal(font.pinned.best, 'supported');
        assert.equal(font.effect.verdict, 'supported');
    }
    assert.equal(row(stored, 'extensions.osc710-font-normal'), undefined);
    assert.equal(totals.effectSupportedIds, 152);
    assert.equal(totals.pinnedSupportedIds, 98);
    assert.equal(totals.pinnedAndEffectSupportedIds, 98);
    assert.equal(totals.effectSupportedPinnedInconclusiveIds.length, 54);
    assert.equal(totals.effectSupportedOfStockInconclusive, 150);
    assert.equal(totals.pinnedSupportedOfStockInconclusive, 96);
    assert.deepEqual(stored.stockBaseline.counts, baseline().counts);
    assert.equal(baseline().outcomes['extensions.reflow'], 'inconclusive');
    assert.deepEqual(totals.remainingAgainstStockInconclusive, { effectUnqualified: 18, effectMeasuredUnsupported: 5, effectMeasuredOther: 1, effectNotMeasured: 12, notPinnedSupported: 72 });
});

test('OSC 5522 adds an owned verdict to the historical unsupported row without regrading stock', () => {
    const stored = committed(), id = 'extensions.osc5522-clipboard', base = baseline();
    const clipboard = row(stored, id);
    assert.deepEqual([clipboard.stockOutcome, clipboard.pinned.best, clipboard.effect.verdict], ['unsupported', 'supported', 'supported']);
    const without = totalsOf(stored.benchmark.filter(item => item.id !== id), base);
    assert.equal(stored.totals.uniqueBenchmarkIds, without.uniqueBenchmarkIds + 1);
    assert.equal(stored.totals.suiteClaims, without.suiteClaims + 1);
    assert.deepEqual(stored.totals.baseline, without.baseline);
    assert.deepEqual(stored.totals.remainingAgainstStockInconclusive, without.remainingAgainstStockInconclusive);
});

test('totalsOf separates unsupported from supported and unmeasured for any rows, and pinned inconclusive stays pinned inconclusive', () => {
    const stored = committed(), base = baseline();
    const without = stored.benchmark.filter(item => item.id !== 'extensions.reflow');
    const historicalRows = clone(stored.benchmark);
    const reflow = historicalRows.find(item => item.id === 'extensions.reflow');
    reflow.effect.verdict = 'unsupported';
    reflow.claims[0].effect.verdict = 'unsupported';
    const before = totalsOf(without, base), after = totalsOf(historicalRows, base);
    assert.equal(after.uniqueBenchmarkIds, before.uniqueBenchmarkIds + 1);
    assert.equal(after.suiteClaims, before.suiteClaims + 1);
    for (const key of ['effectSupportedIds', 'pinnedSupportedIds', 'pinnedAndEffectSupportedIds', 'effectSupportedOfStockInconclusive', 'pinnedSupportedOfStockInconclusive']) assert.equal(after[key], before[key], key);
    assert.deepEqual(after.effectSupportedPinnedInconclusiveIds, before.effectSupportedPinnedInconclusiveIds);
    assert.equal(after.effectUnsupportedIds, before.effectUnsupportedIds + 1);
    assert.equal(after.remainingAgainstStockInconclusive.effectUnqualified, before.remainingAgainstStockInconclusive.effectUnqualified);
    assert.equal(after.remainingAgainstStockInconclusive.effectNotMeasured, before.remainingAgainstStockInconclusive.effectNotMeasured - 1);
    assert.equal(after.remainingAgainstStockInconclusive.effectMeasuredUnsupported, before.remainingAgainstStockInconclusive.effectMeasuredUnsupported + 1);
    // Flipping an existing supported id to a retained unsupported effect verdict moves it only out of the supported measures.
    const flipped = clone(historicalRows), target = flipped.find(item => item.id === 'text.basic');
    target.claims[0].effect.verdict = 'unsupported'; target.effect = { verdict: 'unsupported', byClaim: [{ suite: target.claims[0].suite, verdict: 'unsupported' }] };
    const moved = totalsOf(flipped, base);
    assert.equal(moved.effectSupportedIds, after.effectSupportedIds - 1);
    assert.equal(moved.effectUnsupportedIds, after.effectUnsupportedIds + 1);
    assert.equal(moved.uniqueBenchmarkIds, after.uniqueBenchmarkIds);
    assert.equal(moved.remainingAgainstStockInconclusive.effectUnqualified, after.remainingAgainstStockInconclusive.effectUnqualified + 1);
    assert.equal(moved.remainingAgainstStockInconclusive.effectNotMeasured, after.remainingAgainstStockInconclusive.effectNotMeasured, 'a measured id is never "not measured"');
    assert.ok(moved.effectSupportedPinnedInconclusiveIds.every(id => id !== 'text.basic'));
});

test('guard: a separate effect verdict cannot be collapsed into its pinned verdict or altered without evidence', () => {
    const collapsed = committed();
    row(collapsed, 'extensions.reflow').claims[0].divergence = null;
    assert.throws(() => check(collapsed), /differ but no divergence is recorded/);
    const promoted = committed();
    const item = row(promoted, 'extensions.reflow');
    item.claims[0].effect.verdict = 'unsupported';
    assert.throws(() => check(promoted), /roll-up differs|divergence does not match|totals differ/);
    const rolled = committed();
    row(rolled, 'extensions.reflow').effect.verdict = 'unsupported';
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

test('the replacement publication is re-derived from retained evidence while the historical negative remains intact', async () => {
    const suite = benchmarkSuites.find(entry => entry.suite === 'ascii-reflow');
    assert.ok(suite, 'registered');
    const { report, summary } = loadPublication(defaultQaRoot, suite);
    assert.deepEqual(report.cases.map(item => [item.caseId, pinnedVerdictOf(item), effectVerdictOf(item)]), [['extensions.reflow', 'inconclusive', 'supported']]);
    assert.equal(summary.ownedRunId, '7a3c3f1779fb10cfd83ff5f0b8c70c39');
    const historical = read('2026-11-12-ascii-reflow/report.json');
    assert.deepEqual(historical.cases.map(item => [item.caseId, pinnedVerdictOf(item), effectVerdictOf(item)]), [['extensions.reflow', 'inconclusive', 'unsupported']]);
    // A fresh derivation that trusts the recorded rederivation results reproduces the committed row byte for byte, and one whose
    // publication test did not pass refuses to count the claim at all.
    const stored = committed();
    const rederivations = new Map([...stored.suites, ...stored.findings].map(entry => [entry.suite, entry.rederivation]));
    const fresh = await buildInventory({ rederivation: entry => ({ ...rederivations.get(entry.suite), testSha256: testDigest(entry) }) });
    assert.equal(JSON.stringify(row(fresh, 'extensions.reflow')), JSON.stringify(row(stored, 'extensions.reflow')));
    const failing = await buildInventory({ rederivation: entry => ({ status: entry.suite === 'ascii-reflow' ? 'failed' : 'passed', command: 'x', testSha256: testDigest(entry) }) });
    assert.equal(row(failing, 'extensions.reflow').claims[0].status, 'declared-not-rederived');
    assert.equal(failing.totals.uniqueBenchmarkIds, stored.totals.uniqueBenchmarkIds - 1);
    assert.equal(failing.totals.effectSupportedIds, stored.totals.effectSupportedIds - 1);
    assert.equal(failing.totals.effectUnsupportedIds, stored.totals.effectUnsupportedIds);
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
    assert.match(text, /\| Effect contract unsupported \(measured, retained, not supported\) \| 5 \|/);
    assert.match(text, /Effect unsupported, pinned inconclusive \(by design\): `extensions\.osc113-reset-pointer-fg`, `extensions\.osc114-reset-pointer-bg`, `extensions\.osc720-scroll-up`, `modes\.deccolm`, `modes\.decsclm`\./);
    assert.match(text, /\| of which measured, effect unsupported \| 5 \|/);
    assert.match(text.split('Effect supported but pinned inconclusive:')[1].split('\n')[0], /`extensions\.osc633-a`.*`extensions\.osc633-d`/);
    assert.match(text, /\| of which not measured \(no qualified effect evidence\) \| 12 \|/);
    assert.match(text, /is \*\*not\*\* counted as effect-supported, pinned-supported or as any stock, Core or common-harness improvement/);
    assert.match(text.split('Effect supported but pinned inconclusive:')[1].split('\n')[0], /extensions\.reflow/);
    assert.match(text, /\| `extensions\.reflow` \| inconclusive \| inconclusive \| supported \| stored-state \+ actual-scrollback \| ascii-reflow \| ascii-reflow: pinned-inconclusive-by-design \|/);
});

test('the generated summary states its baseline and keeps the diagnostic out of the counts', () => {
    const text = renderSummary(committed());
    assert.match(text, /Baseline: stock run `[0-9a-f]{32}`/);
    assert.match(text, /cursor\.reverse-wrap.*suiteComplete=false.*not a graded stock case/);
    assert.match(text, /Ntilde-specific findings \(not benchmark ids\)/);
    // Measured in the LF form the inventory itself hashes: a Windows checkout adds one CR per line and must not change the verdict.
    const lfLength = bytes => Buffer.from(bytes.toString('latin1').replaceAll('\r\n', '\n'), 'latin1').length;
    // 240 KiB: the modifyOtherKeys suite entry (receipt schema 56, its receipt and run bindings) joins the merged inventory.
    // 288 KiB: the selection-colors suite entry (receipt schema 60, four effect-supported ids) joins the merged inventory.
    // 336 KiB: the truecolor projection and four OSC 633 cases add their receipt bindings.
    // 352 KiB: the cell-size and OSC 5 cases add their separate source-bound runs and receipts.
    // 368 KiB: the OSC 3008 and OSC 720 cases add their source-bound runs and receipts.
    // 384 KiB: the Unicode placeholder suite entry (no stock run, its own receipt binding) joins the merged inventory.
    // 400 KiB: the DECSCLM suite entry (receipt schema 82, its run and receipt bindings) joins the merged inventory.
    // 404 KiB includes the owned OSC 5522 reassessment and its carried stock provenance.
    assert.ok(lfLength(Buffer.from(text)) < 40 * 1024 && lfLength(readFileSync(join(defaultQaRoot, inventoryFiles.json))) < 404 * 1024, 'the generated files stay reviewable');
});

test('the Core-gaps reconciliation admits exactly text.reverse-index-scroll and reset.method, with the reset() API layer named and no stock or Core movement', () => {
    const stored = committed();
    const suite = stored.suites.find(item => item.suite === 'core-gaps');
    assert.deepEqual(suite.caseIds, ['text.reverse-index-scroll', 'reset.method']);
    assert.equal(suite.suiteId, 'ntilde-owned-core-gaps-v1');
    assert.equal(suite.rederivation.command, 'node --test scripts/terminfo/core-gaps-publication.test.mjs');
    // The projection restates the newest merged run's Core-gaps measurement; its stock counts are that run's, carried under no stock run id of its own.
    assert.equal(suite.stock.basis, 'collected-raw-digest-recorded');
    assert.equal(suite.stock.runId, null);
    for (const id of suite.caseIds) {
        const item = row(stored, id);
        assert.equal(item.stockOutcome, 'inconclusive', id);
        assert.equal(baseline().outcomes[id], 'inconclusive', 'the retained stock grade is untouched');
        assert.deepEqual(item.claims.map(claim => claim.suite), ['core-gaps'], id);
        assert.equal(item.claims[0].status, 'qualified');
        assert.deepEqual([item.pinned.best, item.effect.verdict, item.claims[0].divergence], ['supported', 'supported', null], id);
    }
    assert.deepEqual(row(stored, 'text.reverse-index-scroll').claims[0].measuredLayers, ['stored-state', 'actual-scrollback']);
    assert.deepEqual(row(stored, 'reset.method').claims[0].measuredLayers, ['stored-state', 'actual-scrollback', 'desktop-backend-reset-api']);
    assert.deepEqual(measuredLayersOf('owned-desktop-backend-reset-api-stored-cells-and-actual-scrollback', ''), ['stored-state', 'actual-scrollback', 'desktop-backend-reset-api']);
    // Neither id is a Core score change, a stock grade change or an effect-supported-only id: the baseline counts, and the Core results, are not inputs here.
    const without = stored.benchmark.filter(item => !suite.caseIds.includes(item.id)), before = totalsOf(without, baseline()), after = stored.totals;
    assert.equal(after.uniqueBenchmarkIds, before.uniqueBenchmarkIds + 2);
    assert.equal(after.suiteClaims, before.suiteClaims + 2);
    assert.equal(after.effectSupportedIds, before.effectSupportedIds + 2);
    assert.equal(after.pinnedSupportedIds, before.pinnedSupportedIds + 2);
    assert.deepEqual(after.effectSupportedPinnedInconclusiveIds, before.effectSupportedPinnedInconclusiveIds);
    assert.equal(after.remainingAgainstStockInconclusive.effectNotMeasured, before.remainingAgainstStockInconclusive.effectNotMeasured - 2);
    assert.deepEqual(after.baseline, before.baseline);
    // The other two ids of the same measured suite are deliberately not admitted by this suite: device.env-term is outside this reconciliation, and
    // extensions.osc1-icon keeps its own (osc-title) claim.
    assert.equal(row(stored, 'device.env-term') ?? null, null);
    assert.deepEqual(row(stored, 'extensions.osc1-icon').claims.map(claim => claim.suite), ['osc-title']);
});


test('extensions.truecolor projection has its own suite identity and cites its carrying receipt', () => {
    const suite = committed().suites.find(item => item.suite === 'extensions-truecolor');
    assert.equal(suite.suiteId, 'ntilde-owned-extensions-truecolor-projection-v1');
    assert.equal(suite.receipt.suiteEvidence.suiteId, 'ntilde-owned-sgr-truecolor-graded-v1');
    assert.equal(suite.receipt.runId, suite.run.ownedRunId);
});


test('inventory hashes compressed evidence as raw bytes, including CRLF byte pairs', () => {
    const root = mkdtempSync(join(tmpdir(), 'nt-inventory-gzip-'));
    try {
        const original = readFileSync(join(defaultQaRoot, '2026-11-16-extensions-truecolor/terminfo-dev-4.0.0.bundle.mjs.gz'));
        const index = original.indexOf(Buffer.from([13, 10]));
        assert.ok(index >= 0, 'the retained binary contains the tested byte pair');
        const path = join(root, 'bundle.gz'), suite = { layout: 'single', file: 'bundle.gz' };
        writeFileSync(path, original);
        const before = evidenceDigest(root, suite);
        writeFileSync(path, Buffer.concat([original.subarray(0, index), original.subarray(index + 1)]));
        const after = evidenceDigest(root, suite);
        assert.equal(before.bytes, original.length);
        assert.equal(after.bytes, original.length - 1);
        assert.notEqual(after.sha256, before.sha256);
    } finally { rmSync(root, { recursive: true, force: true }); }
});

test('the OSC 633 implementation publication is the active claim while the historical unsupported negative remains intact', async () => {
    const suite = benchmarkSuites.find(entry => entry.suite === 'osc633');
    assert.equal(suite.dir, '2026-11-16-osc633-implementation');
    const { report, summary } = loadPublication(defaultQaRoot, suite);
    const ids = ['extensions.osc633-a', 'extensions.osc633-b', 'extensions.osc633-c', 'extensions.osc633-d'];
    assert.deepEqual(report.cases.map(item => [item.caseId, pinnedVerdictOf(item), effectVerdictOf(item)]), ids.map(id => [id, 'inconclusive', 'supported']));
    assert.equal(summary.ownedRunId, '323882a7d7b0e3c86ce4152fa54e6aa3');
    const historical = read('2026-11-16-osc633/report.json');
    assert.deepEqual(historical.cases.map(item => [item.caseId, pinnedVerdictOf(item), effectVerdictOf(item)]), ids.map(id => [id, 'inconclusive', 'unsupported']));
    assert.ok('osc633-publication.test.mjs' in nonInventoryPublicationTests, 'the frozen baseline test is classified, not dropped');
    const stored = committed();
    for (const id of ids) {
        const item = row(stored, id);
        assert.equal(item.claims.length, 1);
        assert.equal(item.effect.verdict, 'supported');
        assert.equal(item.pinned.best, 'inconclusive');
        assert.equal(item.claims[0].divergence.kind, 'pinned-inconclusive-by-design');
    }
    // A failed rederivation of the replacement refuses to count the claim; the frozen negative is not silently substituted.
    // The refusal is transitive and per case: the OSC 633 aggregate names the OSC 633 carrier, so it is declared-not-rederived
    // too, while the semantic-prompts aggregate (an OSC 133 carrier claim) keeps its qualified status and verdict.
    const failing = await buildInventory({ rederivation: entry => ({ status: entry.suite === 'osc633' ? 'failed' : 'passed', command: 'x', testSha256: testDigest(entry) }) });
    assert.equal(row(failing, ids[0]).claims[0].status, 'declared-not-rederived');
    assert.equal(failing.totals.uniqueBenchmarkIds, stored.totals.uniqueBenchmarkIds - 5);
    assert.equal(failing.totals.effectSupportedIds, stored.totals.effectSupportedIds - 5);
    assert.equal(row(failing, 'extensions.osc-633-vscode').claims[0].status, 'declared-not-rederived');
    assert.deepEqual(row(failing, 'extensions.osc-633-vscode').claims[0].carrierSuites, ['osc633']);
    assert.equal(row(failing, 'extensions.osc-633-vscode').claims[1].status, 'declared-not-rederived');
    assert.equal(row(failing, 'extensions.semantic-prompts').claims[0].status, 'qualified');
    assert.deepEqual(row(failing, 'extensions.semantic-prompts').claims[0].carrierSuites, ['osc133']);
    assert.equal(failing.totals.effectOtherIds, stored.totals.effectOtherIds);
});

test('an aggregate case naming a carrier no registered suite owns is refused, not silently unvalidated', () => {
    const recordBySuite = new Map([['osc133', { suite: 'osc133', suiteId: 'ntilde-owned-osc133-graded-v1' }]]);
    const statusBySuite = new Map([['osc133', 'passed']]);
    const suite = { carrierSuites: ['osc133'] };
    assert.deepEqual(resolveCarriers(suite, { caseId: 'x', aggregateOf: { carrier: 'ntilde-owned-osc133-graded-v1' } }, recordBySuite, statusBySuite),
        { names: ['osc133'], statuses: ['passed'] });
    assert.deepEqual(resolveCarriers(suite, { caseId: 'x' }, recordBySuite, statusBySuite), { names: ['osc133'], statuses: ['passed'] });
    // A carrier id nothing owns must fail the build: dropping it would qualify the claim without any carrier rederivation.
    assert.throws(() => resolveCarriers(suite, { caseId: 'x', aggregateOf: { carrier: 'ntilde-owned-nowhere-graded-v1' } }, recordBySuite, statusBySuite),
        /no registered suite owns/);
    assert.equal(resolveCarriers({ carrierSuites: undefined }, { caseId: 'x' }, recordBySuite, statusBySuite), null);
});

test('the original aggregate projection remains attributable after the OSC 633 reassessment', async () => {
    const stored = committed();
    const suite = stored.suites.find(entry => entry.suite === 'shell-integration-aggregates');
    assert.equal(suite.suiteId, 'ntilde-owned-shell-integration-aggregates-projection-v1');
    assert.equal(suite.publication, '2026-11-16-shell-integration-aggregates/');
    assert.equal(suite.doc, '2026-11-16-shell-integration-aggregates-qualification.md');
    assert.deepEqual(suite.caseIds, ['extensions.osc-633-vscode', 'extensions.semantic-prompts']);
    // The suite-level receipt binding is the primary (OSC 633) carrier's; the OSC 133 carrier is bound per case.
    assert.equal(suite.receipt.suiteEvidence.suiteId, 'ntilde-owned-osc633-graded-v1');
    assert.equal(suite.receipt.runId, suite.run.ownedRunId);
    assert.equal(suite.run.sourceSnapshotPolicy, 'readonly-owned-source-graph-v62');
    // The grading identity is the OSC 633 carrier's, not one inherited through the context fallback.
    assert.equal(suite.run.gradingAdapterId, 'ntilde-owned-osc633-grading-v1');
    assert.match(suite.run.gradingAdapterBundleSha256, /^[0-9a-f]{64}$/);
    assert.deepEqual(suite.carrierSuites, ['osc133', 'osc633']);
    assert.equal(suite.stock.basis, 'collected-raw-digest-recorded');
    assert.equal(suite.stock.runId, null, 'the projection claims no stock run of its own');
    for (const [id, effect, divergent] of [
        ['extensions.osc-633-vscode', 'inconclusive', false],
        ['extensions.semantic-prompts', 'supported', true],
    ]) {
        const item = row(stored, id);
        assert.equal(item.stockOutcome, 'inconclusive', id);
        assert.equal(baseline().outcomes[id], 'inconclusive', `${id}: the retained stock grade is untouched`);
        assert.equal(item.claims[0].suite, 'shell-integration-aggregates', id);
        assert.equal(item.claims[0].status, 'qualified', id);
        assert.deepEqual([item.pinned.best, item.claims[0].effect.verdict], ['inconclusive', effect], id);
        if (divergent) {
            assert.deepEqual(item.claims[0].divergence, { recordedInReport: true, kind: 'pinned-inconclusive-by-design', pinned: 'inconclusive', effect, reason: item.claims[0].divergence.reason }, id);
        } else {
            assert.equal(item.claims[0].divergence, null, `${id}: agreeing verdicts record no divergence`);
        }
        assert.deepEqual(item.claims[0].measuredLayers, ['stored-state', 'pane-metadata-and-session-status'], id);
        assert.equal(item.claims[0].reportedConclusion.outcome, 'inconclusive', id);
        assert.equal(item.claims[0].reportedConclusion.grading, 'aggregate-pinned-replay-preserved-by-design', id);
    }
    assert.equal(stored.totals.remainingAgainstStockInconclusive.effectMeasuredOther, 1);
    const reassessed = row(stored, 'extensions.osc-633-vscode');
    assert.deepEqual(reassessed.claims.map(claim => claim.effect.verdict), ['inconclusive', 'supported']);
    assert.equal(reassessed.effect.verdict, 'supported');
    assert.equal(reassessed.claims[1].supersedesInconclusive, 'shell-integration-aggregates');
    assert.deepEqual(reassessed.claims[1].carrierSuites, ['osc633', 'osc633-metadata']);
    assert.ok(totalsOf(stored.benchmark, baseline()).effectSupportedPinnedInconclusiveIds.includes('extensions.semantic-prompts'));
    // The reassessment has independently verified OSC 633 carriers. A simulated failure of the original projection drops its claims alone.
    const failing = await buildInventory({ rederivation: entry => ({ status: entry.suite === 'shell-integration-aggregates' ? 'failed' : 'passed', command: 'x', testSha256: testDigest(entry) }) });
    for (const id of suite.caseIds) assert.equal(row(failing, id).claims[0].status, 'declared-not-rederived', id);
    assert.equal(failing.totals.uniqueBenchmarkIds, stored.totals.uniqueBenchmarkIds - 1);
    assert.equal(failing.totals.effectSupportedIds, stored.totals.effectSupportedIds - 1);
    assert.equal(failing.totals.effectOtherIds, stored.totals.effectOtherIds);
    assert.equal(failing.totals.remainingAgainstStockInconclusive.effectNotMeasured, stored.totals.remainingAgainstStockInconclusive.effectNotMeasured + 1);
    // A failed carrier lends its verdict to nothing built on top of it: an OSC 133 carrier failure drops the four
    // OSC 133 ids and the semantic-prompts aggregate, while the OSC 633 aggregate keeps its qualified status.
    const osc133Failed = await buildInventory({ rederivation: entry => ({ status: entry.suite === 'osc133' ? 'failed' : 'passed', command: 'x', testSha256: testDigest(entry) }) });
    assert.equal(row(osc133Failed, 'extensions.semantic-prompts').claims[0].status, 'declared-not-rederived');
    assert.equal(row(osc133Failed, 'extensions.osc-633-vscode').claims[0].status, 'qualified');
    assert.equal(osc133Failed.totals.effectSupportedIds, stored.totals.effectSupportedIds - 5);
    assert.equal(osc133Failed.totals.uniqueBenchmarkIds, stored.totals.uniqueBenchmarkIds - 5);
});

test('the latest OSC 633 aggregate requires both carriers and falls back to the historical inconclusive claim', async () => {
    for (const missing of ['osc633-vscode-reassessment', 'osc633-metadata']) {
        const failed = await buildInventory({ rederivation: suite => ({
            status: suite.suite === missing ? 'failed' : 'passed', command: 'x', testSha256: testDigest(suite),
        }) });
        const item = row(failed, 'extensions.osc-633-vscode');
        assert.equal(item.claims[0].status, 'qualified');
        assert.equal(item.claims[1].status, 'declared-not-rederived');
        assert.equal(item.effect.verdict, 'inconclusive');
        assert.equal(failed.totals.effectOtherIds, committed().totals.effectOtherIds + 1);
    }
});

test('the two SIXEL raster measurements retain their revision-specific verdicts alongside the original iTerm2 claim', async () => {
    const suite = benchmarkSuites.find(entry => entry.suite === 'static-images');
    assert.equal(suite.dir, '2026-11-16-static-images');
    const { report, summary } = loadPublication(defaultQaRoot, suite);
    assert.deepEqual(report.cases.map(item => [item.caseId, pinnedVerdictOf(item), effectVerdictOf(item)]),
        [['extensions.sixel', 'inconclusive', 'unsupported'], ['extensions.iterm2-images', 'inconclusive', 'supported']]);
    assert.equal(summary.ownedRunId, '569294d0402a900696bef00b68490d9d');
    const stored = committed();
    for (const id of ['extensions.sixel', 'extensions.iterm2-images']) {
        const item = row(stored, id);
        assert.equal(item.claims.length, id === 'extensions.sixel' ? 2 : 1);
        assert.equal(item.stockOutcome, 'inconclusive');
        assert.equal(item.pinned.best, 'inconclusive');
        assert.equal(item.claims[0].divergence.kind, 'pinned-inconclusive-by-design');
        assert.deepEqual(item.claims[0].measuredLayers, ['controlled-pixels']);
        assert.equal(item.claims[0].status, 'qualified');
    }
    assert.deepEqual(row(stored, 'extensions.sixel').effect, {
        verdict: 'mixed',
        byClaim: [{ suite: 'static-images', verdict: 'unsupported' }, { suite: 'sixel-native-fidelity', verdict: 'supported' }],
    });
    assert.equal(row(stored, 'extensions.iterm2-images').effect.verdict, 'supported');
    // The unsupported verdict is a measured result: it is never promoted, counted as supported, or silently dropped.
    assert.ok(!stored.totals.effectSupportedPinnedInconclusiveIds.includes('extensions.sixel'));
    const failing = await buildInventory({ rederivation: entry => ({ status: entry.suite === 'static-images' ? 'failed' : 'passed', command: 'x', testSha256: testDigest(entry) }) });
    assert.equal(row(failing, 'extensions.sixel').claims[0].status, 'declared-not-rederived');
    assert.equal(failing.totals.uniqueBenchmarkIds, stored.totals.uniqueBenchmarkIds - 1);
    assert.equal(failing.totals.effectUnsupportedIds, 5, 'the OSC 720, the two pointer-color, the DECCOLM and the DECSCLM measured negatives remain');
});

test('the Kitty animation qualification keeps the supported effect, the inconclusive pinned callbacks and the stock grade as separate rederived claims', async () => {
    const suite = benchmarkSuites.find(entry => entry.suite === 'kitty-animation');
    assert.equal(suite.dir, '2026-11-16-kitty-animation');
    const { report, summary } = loadPublication(defaultQaRoot, suite);
    assert.deepEqual(report.cases.map(item => [item.caseId, pinnedVerdictOf(item), effectVerdictOf(item)]), [['extensions.kitty-graphics.animation', 'inconclusive', 'supported']]);
    assert.equal(summary.ownedRunId, '99d639356e6c79215fd29c5cd655b4b8');
    const stored = committed(), item = row(stored, 'extensions.kitty-graphics.animation');
    assert.equal(item.claims.length, 1);
    assert.equal(item.stockOutcome, 'inconclusive');
    assert.equal(item.pinned.best, 'inconclusive');
    assert.equal(item.effect.verdict, 'supported');
    assert.equal(item.claims[0].divergence.kind, 'pinned-inconclusive-by-design');
    assert.deepEqual(item.claims[0].measuredLayers, ['controlled-pixels']);
    assert.equal(item.claims[0].status, 'qualified');
    assert.ok(stored.totals.effectSupportedPinnedInconclusiveIds.includes('extensions.kitty-graphics.animation'));
    const failing = await buildInventory({ rederivation: entry => ({ status: entry.suite === 'kitty-animation' ? 'failed' : 'passed', command: 'x', testSha256: testDigest(entry) }) });
    assert.equal(row(failing, 'extensions.kitty-graphics.animation').claims[0].status, 'declared-not-rederived');
    assert.equal(failing.totals.uniqueBenchmarkIds, stored.totals.uniqueBenchmarkIds - 1);
});

test('OSC22 counts one rederived App effect while preserving pinned and stock inconclusive outcomes', async () => {
    const suite = benchmarkSuites.find(entry => entry.suite === 'osc22');
    assert.equal(suite.dir, '2026-11-17-osc22');
    const { report, summary } = loadPublication(defaultQaRoot, suite);
    assert.deepEqual(report.cases.map(item => [item.caseId, pinnedVerdictOf(item), effectVerdictOf(item)]), [['extensions.osc22-pointer', 'inconclusive', 'supported']]);
    assert.equal(summary.ownedRunId, 'b70c675d73b252ff1bedfcfc0fe4aff4');
    const stored = committed(), item = row(stored, 'extensions.osc22-pointer');
    assert.equal(item.claims.length, 1);
    assert.equal(item.stockOutcome, 'inconclusive'); assert.equal(item.pinned.best, 'inconclusive'); assert.equal(item.effect.verdict, 'supported');
    assert.equal(item.claims[0].divergence.kind, 'pinned-inconclusive-by-design');
    assert.deepEqual(item.claims[0].measuredLayers, ['pane-metadata-and-session-status']);
    assert.equal(item.claims[0].status, 'qualified');
    const failing = await buildInventory({ rederivation: entry => ({ status: entry.suite === 'osc22' ? 'failed' : 'passed', command: 'x', testSha256: testDigest(entry) }) });
    assert.equal(row(failing, 'extensions.osc22-pointer').claims[0].status, 'declared-not-rederived');
    assert.equal(failing.totals.uniqueBenchmarkIds, stored.totals.uniqueBenchmarkIds - 1);
});
