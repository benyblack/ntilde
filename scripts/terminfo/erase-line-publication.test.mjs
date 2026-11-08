// Frozen real-App publication. Synthetic fixtures do not replace this measurement; every digest below is
// checked against the committed evidence. The publication is split into reviewable files (summary, report, run
// log, common frames and one file per read frame); the test reassembles the exact journal order.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, readdirSync } from 'node:fs';
import { buildEraseLineGradingReport } from './erase-line-grading.mjs';
import { eraseLineAdapterId } from './erase-line-grading-collector.mjs';
import { eraseLineSuiteId, eraseLineContracts } from './erase-line-contracts.mjs';
import { asciiSgrContracts } from './ascii-sgr-contracts.mjs';
import { sgrColorsContracts } from './sgr-colors-contracts.mjs';
import { sgrRgbContracts } from './sgr-rgb-contracts.mjs';
import { sgrAttrsContracts } from './sgr-attrs-contracts.mjs';
import { sgrPaletteContracts } from './sgr-palette-contracts.mjs';
import { sgrTruecolorContracts } from './sgr-truecolor-contracts.mjs';
import { sgrUnderlineContracts } from './sgr-underline-contracts.mjs';
import { sgrUnderlineColorContracts } from './sgr-underline-color-contracts.mjs';
import { sgrResetContracts } from './sgr-reset-contracts.mjs';

const dir = new URL('../../docs/qa/terminfo/2026-11-08-erase-line/', import.meta.url);
const read = name => JSON.parse(readFileSync(new URL(name, dir)));
const ids = ['erase.line.right', 'erase.line.left', 'erase.line.all'];
const load = () => {
    const summary = read('summary.json'), report = read('report.json'), run = read('run-log.json'), common = read('journal-common.json');
    const snapshots = [common.frames.start, common.frames.bind];
    for (const contract of eraseLineContracts) {
        for (let index = 0; index < contract.roles.length; index++) {
            const journal = read(`journal-${contract.caseId}-read${index}.json`);
            assert.equal(journal.caseId, contract.caseId);
            assert.equal(journal.read, index);
            snapshots.push(journal.frame);
        }
    }
    snapshots.push(common.frames.finish);
    return { summary, run, report: { ...report, snapshots } };
};
const hash = bytes => createHash('sha256').update(bytes).digest('hex');

test('the published erase-in-line evidence re-derives exactly from its retained journal and run log', () => {
    const { summary, run, report } = load();
    const derived = buildEraseLineGradingReport(report.snapshots, run, summary.context.runId, report.adapter, { contractsVersion: report.contractsVersion });
    assert.equal(JSON.stringify(report), JSON.stringify(derived));
    assert.equal(report.suiteId, eraseLineSuiteId);
    assert.equal(report.adapter.id, eraseLineAdapterId);
    assert.equal(report.adapter.derivedFromAdapterId, 'ntilde-owned-sgr-selective-reset-grading-v1');
    assert.equal(report.snapshots.length, 3 + 3 * 11);
    assert.deepEqual(report.cases.map(item => item.caseId), ids);
    assert.deepEqual(report.cases.map(item => [item.conclusion.outcome, item.pinned.observation.outcome]), ids.map(() => ['supported', 'supported']));
    for (const item of report.cases) assert.equal(item.divergence, undefined);
});

test('each case erased exactly the contracted cells, kept the neighbor row and left the cursor at column 2', () => {
    const [right, left, all] = load().report.cases.map(item => item.observed);
    for (const observed of [right, left, all]) {
        assert.deepEqual(observed.geometry, { rows: 40, cols: 140 });
        assert.equal(observed.reverseVideo, false);
        assert.equal(observed.initial.ink, 0);
        assert.deepEqual(observed.setup.cursor, [0, 2]);
        assert.deepEqual(observed.erased.cursor, [0, 2]);
        assert.equal(observed.setup.text[0], 'ABCDE_____');
    }
    assert.equal(right.erased.text[0], 'AB________');
    assert.equal(left.erased.text[0], '___DE_____');
    assert.equal(all.erased.text[0], '__________');
    assert.deepEqual(all.setup.text, ['ABCDE_____', 'KEEP!_____']);
    assert.deepEqual(all.erased.text, ['__________', 'KEEP!_____']);
    assert.deepEqual([right.pinnedSequence, left.pinnedSequence, all.pinnedSequence], ['\x1b[K', '\x1b[1K', '\x1b[2K']);
});

test('spellings stay distinct: omitted and explicit 0 agree, EL 1 and EL 2 differ, and the pinned spelling is the one graded', () => {
    const [right, left, all] = load().report.cases.map(item => item.observed.spellingControls);
    assert.equal(right.explicit0.sequence, '\x1b[0K');
    assert.equal(right.explicit0.sameAsPinned, true);
    assert.equal(right.explicit1.sameAsPinned, false);
    assert.equal(right.explicit2.sameAsPinned, false);
    assert.equal(left.omitted.text[0], left.explicit0.text[0]);
    assert.equal(left.omitted.text[0], 'AB________');
    assert.equal(all.explicit1.text[0], '___DE_____');
    for (const controls of [right, left, all]) {
        for (const [name, control] of Object.entries(controls).filter(([key]) => key !== 'policy')) assert.equal(control.matchesEcma48, true, name);
    }
});

test('unaffected rows and columns, and the cursor-cell boundary, hold in the wide-row control', () => {
    const cases = load().report.cases.map(item => item.observed.wideRow);
    for (const wide of cases) {
        assert.deepEqual(wide.before.text, ['ABOVE-ROW!', 'ABCDEFGHIJ', 'BELOW-ROW!']);
        assert.deepEqual(wide.before.cursor, [1, 2]);
        assert.deepEqual(wide.after.cursor, [1, 2]);
        assert.equal(wide.after.text[0], 'ABOVE-ROW!');
        assert.equal(wide.after.text[2], 'BELOW-ROW!');
    }
    assert.deepEqual(cases.map(wide => wide.after.text[1]), ['AB________', '___DEFGHIJ', '__________']);
});

test('retained cells keep bold; background-color erase is observed (not required) and the erased tail carries the background', () => {
    for (const item of load().report.cases) {
        const { attributes } = item.observed;
        assert.equal(attributes.retainedKeepBold, true);
        assert.match(attributes.policy, /observed, not required/);
        assert.equal(attributes.backgroundColorErase, 'erased cells carry the current background');
        assert.ok(attributes.after.ink.some(entry => Array.isArray(entry.cols) && entry.char === ' '), 'the painted tail is one range entry');
    }
});

test('the receipt binds the run, adapters and counts, and every predecessor and finding reconfirm in the same run', () => {
    const { summary, run, report } = load();
    const { receipt } = summary;
    assert.equal(receipt.schemaVersion, 24);
    assert.equal(receipt.eraseLineEvidence.suiteId, eraseLineSuiteId);
    assert.equal(receipt.eraseLineEvidence.runId, summary.context.runId);
    assert.deepEqual(receipt.eraseLineEvidence.counts, { supported: 3 });
    assert.equal(receipt.eraseLineEvidence.adapterBundleSha256, summary.context.eraseLineBundleSha256);
    const observationsHash = hash(JSON.stringify(report, null, 2) + '\n');
    const runHash = hash(JSON.stringify(run) + '\n');
    const journalHash = hash(report.snapshots.map(frame => JSON.stringify(frame) + '\n').join(''));
    assert.equal(receipt.eraseLineEvidence.sha256, observationsHash);
    assert.equal(receipt.eraseLineEvidence.runSha256, runHash);
    assert.equal(summary.retained.eraseLineObservationsSha256, observationsHash);
    assert.equal(summary.retained.eraseLineRunSha256, runHash);
    assert.equal(summary.retained.eraseLineJournalSha256, journalHash);
    assert.equal(summary.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v18');
    assert.equal(receipt.fixtureRemoved, true);
    assert.equal(summary.ownedRunId, summary.context.runId);
    assert.notEqual(summary.stockCollectorRunId, summary.ownedRunId);
    // The predecessor inventory is derived from the merged contracts, not assumed.
    const expected = new Set([...asciiSgrContracts, ...sgrColorsContracts, ...sgrRgbContracts, ...sgrAttrsContracts, ...sgrPaletteContracts,
        ...sgrTruecolorContracts, ...sgrUnderlineContracts, ...sgrUnderlineColorContracts, ...sgrResetContracts].map(contract => contract.caseId));
    const unique = new Set(Object.values(summary.sameRun).flatMap(suite => suite.cases.map(item => item.caseId)));
    assert.deepEqual([...unique].sort(), [...expected].sort());
    for (const suite of Object.values(summary.sameRun)) assert.ok(suite.cases.every(item => item.outcome === 'supported'));
    assert.ok(!ids.some(id => unique.has(id)), 'the three new cases are counted separately');
    assert.equal(summary.truecolorFinding.cases.length, 10);
    assert.ok(summary.truecolorFinding.cases.every(item => item.caseId.startsWith('finding.truecolor-literal.') && item.outcome === 'supported'));
    assert.equal(summary.underlineResolved.cases.length, 3);
    assert.ok(summary.underlineResolved.cases.every(item => item.outcome === 'supported'));
});

test('the untouched stock run keeps its own grades: all three pinned cases are inconclusive there', () => {
    const { summary } = load();
    assert.deepEqual(summary.stockCollector.counts, { inconclusive: 168, supported: 86, unsupported: 1 });
    assert.equal(summary.stockCollector.suiteComplete, false);
    assert.equal(summary.stockCollector.probeHash, '16a690f34c0e');
    for (const id of ids) assert.match(summary.stockCollector.probes[id].note, /^Erase pixel fixture needs capture/);
    for (const id of ids) assert.equal(summary.stockCollector.probes[id].outcome, 'inconclusive');
});

test('every publication file stays small enough to review in one piece', () => {
    for (const name of readdirSync(dir)) {
        const lines = readFileSync(new URL(name, dir), 'utf8').split('\n').length;
        assert.ok(lines <= 1500, `${name} has ${lines} lines`);
    }
});

test('prior publications still rederive beside this batch and the failed and superseded attempts are preserved', () => {
    const reset = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-08-sgr-selective-reset/summary.json', import.meta.url)));
    assert.equal(reset.receipt.schemaVersion, 23);
    assert.equal(reset.receipt.eraseLineEvidence, undefined);
    const { attempts, context } = load().summary;
    assert.equal(attempts.length, 4);
    assert.deepEqual(attempts.map(attempt => attempt.receiptCompleted), [false, false, true, true]);
    for (const attempt of attempts) assert.notEqual(attempt.runId, context.runId);
    const superseded = attempts.filter(attempt => attempt.verdicts);
    assert.equal(superseded.length, 2);
    assert.deepEqual(Object.values(superseded.find(attempt => /attribute control/.test(attempt.outcome)).verdicts).sort(), ['supported', 'unsupported', 'unsupported']);
});
