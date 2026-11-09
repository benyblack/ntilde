// Frozen real-App publication. Synthetic fixtures do not replace this measurement; every fact below is checked against the committed
// evidence. The publication is split into reviewable files (summary, report header, one file per case, run log, common frames, one file
// per read frame and every controlled PNG); the test reassembles the exact journal order and rederives the whole report, including
// every pixel comparison, from the retained bytes.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { buildCursorQualGradingReport, pinnedCallbackDigests } from './cursor-qual-grading.mjs';
import { cursorQualAdapterId } from './cursor-qual-grading-collector.mjs';
import { cursorQualCaseIds, cursorQualSuiteId, makeCursorQualContracts } from './cursor-qual-contracts.mjs';
import { benchmarkSuites } from './qualification-inventory.mjs';

const dirUrl = new URL('../../docs/qa/terminfo/2026-11-09-cursor/', import.meta.url);
const dir = fileURLToPath(dirUrl);
const read = name => JSON.parse(readFileSync(new URL(name, dirUrl)));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const load = () => {
    const summary = read('summary.json'), header = read('report.json'), run = read('run-log.json'), common = read('journal-common.json');
    const snapshots = [common.frames.start, common.frames.bind];
    for (const item of run.cases) {
        for (let index = 0; index < item.reads.length; index++) {
            const journal = read(`journal-${item.caseId}-read${index}.json`);
            assert.equal(journal.caseId, item.caseId);
            assert.equal(journal.read, index);
            snapshots.push(journal.frame);
        }
    }
    snapshots.push(common.frames.finish);
    const cases = header.cases.map(caseId => read(`case-${caseId}.json`));
    return { summary, run, report: { ...header, cases, snapshots } };
};
const byId = (report, id) => report.cases.find(item => item.caseId === id);
const reads = (item, group) => item.observed.groups.find(entry => entry.id === group).reads;

test('the published cursor evidence re-derives exactly from its retained journal, CPR exchanges and PNGs', () => {
    const { summary, run, report } = load();
    const derived = buildCursorQualGradingReport(report.snapshots, run, summary.context.runId, report.adapter, { contractsVersion: report.contractsVersion, output: dir });
    assert.equal(JSON.stringify(report), JSON.stringify(derived));
    assert.equal(report.suiteId, cursorQualSuiteId);
    assert.equal(report.adapter.id, cursorQualAdapterId);
    assert.equal(report.adapter.derivedFromAdapterId, 'ntilde-owned-attr-rect-grading-v1');
    const contracts = makeCursorQualContracts(report.snapshots[0].screen.rows, report.snapshots[0].screen.cols);
    assert.equal(report.snapshots.length, 3 + contracts.reduce((sum, contract) => sum + contract.plan.filter(step => step.read).length, 0));
    assert.deepEqual(report.cases.map(item => item.caseId), [...cursorQualCaseIds]);
    assert.deepEqual([report.snapshots[0].screen.rows, report.snapshots[0].screen.cols], [40, 140]);
});

test('every retained PNG is bound by digest and nothing else sits in the picture directory', () => {
    const { report } = load();
    const frames = report.snapshots.filter(frame => frame.pixel);
    assert.equal(frames.length, 10);
    assert.deepEqual(readdirSync(new URL('cursor-pixels/', dirUrl)).sort(), frames.map(frame => frame.pixel.file.split('/')[1]).sort());
    for (const frame of frames) assert.equal(hash(readFileSync(dir + frame.pixel.file)), frame.pixel.sha256);
    assert.deepEqual(frames.map(frame => frame.pixel.presentationTimeMs).filter(time => time !== 0), [530, 530].slice(0, frames.filter(frame => frame.pixel.presentationTimeMs === 530).length));
});

test('pinned termless, repaired app and effect verdicts are recorded apart, and the stock grades stay unchanged', () => {
    const { report, summary } = load();
    assert.deepEqual(report.cases.map(item => [item.caseId, item.pinned.observation.outcome, item.pinnedApp.observation.outcome, item.conclusion.outcome]), [
        ['cursor.cup-scroll-region', 'supported', 'inconclusive', 'supported'],
        ['cursor.hide', 'supported', 'inconclusive', 'supported'],
        ['cursor.shape', 'supported', 'inconclusive', 'supported'],
        ['cursor.reverse-wrap', 'supported', 'supported', 'supported']]);
    for (const id of ['cursor.cup-scroll-region', 'cursor.hide', 'cursor.shape']) {
        const item = byId(report, id);
        assert.equal(item.divergence, undefined);
        assert.deepEqual(item.appDivergence, { against: 'pinned-app', kind: 'contract-measures-beyond-pinned', pinnedOutcome: 'inconclusive', contractOutcome: 'supported' });
    }
    assert.equal(byId(report, 'cursor.reverse-wrap').appDivergence, undefined);
    assert.match(byId(report, 'cursor.cup-scroll-region').pinnedApp.observation.note, /measured 1;1, 2;1, 11;1 — the physical row 5 and the margins still need an independent observable/);
    assert.equal(byId(report, 'cursor.hide').pinnedApp.observation.note, 'Seeded cursor pixels require independent review: control must visibly show the cursor; blink phase or missing focus cannot establish a negative');
    assert.equal(byId(report, 'cursor.shape').pinnedApp.observation.note, 'Cursor pixels captured; shape difference requires review');
    assert.deepEqual(summary.stockCollector.counts, { inconclusive: 168, supported: 86, unsupported: 1 });
    assert.equal(summary.stockCollector.suiteComplete, false);
    assert.equal(summary.stockCollector.ungradedDiagnostics['cursor.reverse-wrap'].name, 'UndeclaredTerminalGeometry');
    assert.deepEqual(summary.receipt.cursorQualEvidence.counts, { supported: 4 });
});

test('CUP in the DECOM region: physical rows, actual margins and origin before and after, with the raw CPR retained separately', () => {
    const item = byId(load().report, 'cursor.cup-scroll-region');
    const app = reads(item, 'app');
    assert.deepEqual(app.slice(0, 3).map(read => [read.physicalCursor, read.rawCpr, read.margins, read.origin]),
        [[[4, 0], '\u001b[1;1R', [4, 14], true], [[5, 0], '\u001b[2;1R', [4, 14], true], [[14, 0], '\u001b[11;1R', [4, 14], true]]);
    assert.deepEqual([app[3].physicalCursor, app[3].margins, app[3].origin], [[0, 0], [0, 39], false], 'margins and origin restored');
    assert.ok(app.slice(0, 3).every(read => read.cprConsistentWithPhysicalCursor), 'the relative reply is consistent with the physical row, never read as it');
    assert.deepEqual(reads(item, 'origin-off').map(read => [read.physicalCursor, read.rawCpr]), [[[0, 0], '\u001b[1;1R'], [[14, 0], '\u001b[15;1R'], [[2, 2], '\u001b[3;3R']], 'DECOM off: the same bytes report absolute rows');
    assert.deepEqual(reads(item, 'origin-on').map(read => [read.physicalCursor, read.rawCpr]), [[[14, 0], '\u001b[11;1R'], [[14, 0], '\u001b[11;1R'], [[4, 0], '\u001b[1;1R'], [[2, 4], '\u001b[3;5R']]);
    assert.equal(reads(item, 'termless')[0].physicalCursor.join(), '4,0');
});

test('hide: stored visibility and controlled pixels for the visible control, the hidden target and the re-rendered control', () => {
    const item = byId(load().report, 'cursor.hide');
    assert.deepEqual(item.observed.pixelChecks.map(check => [check.id, check.role, check.passed]), [
        ['hide.control-cursor-visible', 'control', true], ['hide.target-cell-blank', 'measured', true],
        ['hide.unrelated-pixels-unchanged', 'measured', true], ['hide.rerender-matches-control', 'control', true]]);
    assert.deepEqual(reads(item, 'app').map(read => [read.visible, read.physicalCursor.join()]), [[true, '1,2'], [false, '1,2'], [true, '0,0'], [true, '1,2'], [true, '0,0']]);
    assert.deepEqual(reads(item, 'termless').map(read => read.visible), [true, true, false, true]);
    assert.deepEqual(item.conclusion.layers, ['stored state', 'physical cursor', 'controlled pixels']);
});

test('shape: stored mode, host-default intent, blink and effective visibility stay separate, with pixels bound to each phase', () => {
    const item = byId(load().report, 'cursor.shape');
    assert.deepEqual(item.observed.hostDefaultMode, { style: 2, blink: true });
    assert.deepEqual(reads(item, 'termless').map(read => read.cursorMode), [{ style: 2, blink: true, isDefault: true }, { style: 1, blink: false, isDefault: false }, { style: 2, blink: true, isDefault: true }]);
    assert.deepEqual(reads(item, 'app').map(read => read.cursorMode), [{ style: 2, blink: true, isDefault: true }, { style: 1, blink: true, isDefault: false }, { style: 1, blink: true, isDefault: false }, { style: 2, blink: true, isDefault: true }]);
    assert.deepEqual(item.reads.filter(read => read.kind === 'capture').map(read => read.presentationTimeMs), [0, 0, 530, 0, 0, 0, 0]);
    assert.ok(item.observed.pixelChecks.every(check => check.passed));
    assert.equal(item.observed.pixelChecks.find(check => check.id === 'shape.reference-shapes').note, 'steady references paint block, underline, bar');
    assert.equal(item.observed.pixelChecks.find(check => check.id === 'shape.target-is-bar').note, 'the requested shape paints bar');
    assert.equal(item.geometryRepair.original.threw.name, 'UndeclaredTerminalGeometry');
    assert.deepEqual(item.geometryRepair.original.writes, ['\u001b[5 q', '\u001b[0 q'], 'the original callback fails only after its captures and cleanup');
});

test('reverse-wrap: geometry proven before and after the repaired callback, movement across the boundary by CPR and physical cursor, mode-off control', () => {
    const item = byId(load().report, 'cursor.reverse-wrap');
    assert.deepEqual(reads(item, 'app').map(read => [read.physicalCursor.join(), read.rawCpr ?? null]), [['0,0', null], ['0,0', '\u001b[1;1R'], ['1,1', '\u001b[2;2R'], ['0,139', '\u001b[1;140R'], ['0,139', null]]);
    assert.deepEqual(reads(item, 'termless').map(read => read.physicalCursor.join()), ['0,0', '0,0', '1,1', '0,139', '0,139']);
    assert.deepEqual(reads(item, 'mode-off').map(read => [read.physicalCursor.join(), read.rawCpr ?? null]), [['1,1', '\u001b[2;2R'], ['1,0', '\u001b[2;1R'], ['0,0', null]], 'without ?45 the same bytes stay on row 2');
    const repair = item.geometryRepair;
    assert.deepEqual([repair.geometry, repair.geometryBefore, repair.geometryAfter], [{ rows: 40, cols: 140 }, { rows: 40, cols: 140 }, { rows: 40, cols: 140 }]);
    assert.equal(repair.original.threw.message, 'App callback cursor.reverse-wrap accessed undeclared or unavailable terminal geometry');
    assert.deepEqual(repair.original.writes, [], 'the original callback fails before its first byte');
    assert.equal(repair.repair.derivationId, 'ntilde-owned-cursor-geometry-repair-v1');
    assert.equal(repair.repair.termSha256, pinnedCallbackDigests['cursor.reverse-wrap'].termSha256);
    assert.equal(repair.repair.metadataChange, 'termNeedsGeometry: true added to the cursor.reverse-wrap probe record; both callbacks are the original function objects');
});

test('the preserved before-fix attempt keeps the unsupported reverse-wrap result and its digests apart from the final run', () => {
    const { summary } = load();
    const before = summary.attempts.find(attempt => attempt.id === 'before-fix');
    assert.deepEqual(before.counts, { supported: 3, unsupported: 1 });
    assert.equal(before.outcomes['cursor.reverse-wrap'], 'unsupported');
    assert.match(before.curqRunSha256, /^[0-9a-f]{64}$/);
    const retained = JSON.parse(readFileSync(new URL('before-fix/case-cursor.reverse-wrap.json', dirUrl)));
    assert.equal(retained.conclusion.outcome, 'unsupported');
    assert.equal(retained.pinned.observation.outcome, 'unsupported');
    assert.equal(retained.pinnedApp.observation.note, 'Measured wrap was not reversed to row 1 column 140');
    assert.deepEqual(reads(retained, 'app').map(read => read.rawCpr ?? null).slice(1, 4), ['\u001b[1;1R', '\u001b[2;2R', '\u001b[2;1R']);
    assert.deepEqual(reads(retained, 'mode-off').map(read => read.matchesExpectation), [true, true, true], 'the mode-off control was valid, so the failure was measured, not an invalid setup');
    assert.notEqual(before.ownedRunId, summary.ownedRunId);
    assert.ok(summary.attempts.some(attempt => attempt.id === 'environment-no-display'));
});

test('the receipt binds the run, adapters, counts and retained digests, and every predecessor and finding reconfirm in the same run', async () => {
    const { summary, run, report } = load();
    const { receipt } = summary;
    assert.equal(receipt.schemaVersion, 34);
    assert.equal(receipt.cursorQualEvidence.suiteId, cursorQualSuiteId);
    assert.equal(receipt.cursorQualEvidence.runId, summary.context.runId);
    assert.deepEqual(receipt.cursorQualEvidence.counts, { supported: 4 });
    assert.equal(receipt.cursorQualEvidence.adapterBundleSha256, summary.context.cursorQualBundleSha256);
    assert.equal(receipt.cursorQualEvidence.sha256, hash(JSON.stringify(report, null, 2) + '\n'));
    assert.equal(receipt.cursorQualEvidence.runSha256, hash(JSON.stringify(run) + '\n'));
    assert.equal(summary.retained.cursorObservationsSha256, receipt.cursorQualEvidence.sha256);
    assert.equal(summary.retained.cursorRunSha256, receipt.cursorQualEvidence.runSha256);
    assert.equal(summary.retained.cursorJournalSha256, hash(report.snapshots.map(frame => JSON.stringify(frame) + '\n').join('')));
    assert.equal(summary.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v28');
    assert.equal(receipt.fixtureRemoved, true);
    assert.notEqual(summary.stockCollectorRunId, summary.ownedRunId);
    // The predecessor inventory is derived from the merged contracts, not assumed: every other benchmark suite's case ids.
    const expected = [];
    for (const suite of benchmarkSuites.filter(item => item.suite !== 'cursor')) {
        const module = await import(new URL(suite.module, import.meta.url));
        expected.push(...module[suite.exports[2]]);
    }
    const unique = new Set(Object.values(summary.sameRun).flatMap(suite => suite.cases.map(item => item.caseId)));
    assert.deepEqual([...unique].sort(), [...new Set(expected)].sort());
    for (const [name, suite] of Object.entries(summary.sameRun)) {
        // Suites with pinned-inconclusive cases keep them; their separately identified effect contracts are all supported.
        if (suite.cases.some(item => item.effectContract)) assert.ok(suite.cases.every(item => item.effectContract === 'supported'), name);
        else assert.ok(suite.cases.every(item => item.outcome === 'supported'), name);
    }
    assert.equal(Object.keys(summary.sameRun).length, benchmarkSuites.length - 1, 'one same-run entry per merged predecessor suite');
    assert.ok(![...cursorQualCaseIds].some(id => unique.has(id)), 'the four cursor cases are counted separately');
    assert.equal(summary.truecolorFinding.cases.length, 10);
    assert.ok(summary.truecolorFinding.cases.every(item => item.caseId.startsWith('finding.truecolor-literal.') && item.outcome === 'supported'));
    assert.equal(summary.underlineResolved.cases.length, 3);
    assert.ok(summary.underlineResolved.cases.every(item => item.outcome === 'supported'));
});

test('the stock grades are re-measured in the same run and unchanged: three inconclusive and one ungraded', () => {
    const { summary } = load();
    assert.equal(summary.stockCollector.probeHash, '16a690f34c0e');
    for (const id of ['cursor.cup-scroll-region', 'cursor.hide', 'cursor.shape']) assert.equal(summary.stockCollector.probes[id].outcome, 'inconclusive', id);
    assert.equal(summary.stockCollector.probes['cursor.reverse-wrap'].outcome, 'ungraded');
    assert.equal(summary.stockCollector.probes['cursor.hide'].note, 'Cursor pixel fixture needs capture and measured 3x6 geometry; measured 40x140');
    assert.equal(summary.stockCollector.probes['cursor.shape'].note, 'No cursor pixel readback for shape');
    assert.match(summary.stockCollector.probes['cursor.cup-scroll-region'].note, /measured 1;1, 2;1, 11;1/, 'the stock run received the same CPR replies this batch retains');
    assert.match(summary.stockCollector.note, /no capture adapter/);
});

test('every publication file stays small enough to review in one piece', () => {
    const walk = url => readdirSync(url, { withFileTypes: true }).flatMap(entry => (entry.isDirectory() ? walk(new URL(`${entry.name}/`, url)) : entry.name.endsWith('.json') ? [new URL(entry.name, url)] : []));
    for (const file of walk(dirUrl)) {
        const lines = readFileSync(file, 'utf8').split('\n').length;
        assert.ok(lines <= 1500, `${file.pathname.split('/').at(-1)} has ${lines} lines`);
    }
});
