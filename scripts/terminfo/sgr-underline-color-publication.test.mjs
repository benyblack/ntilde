// Frozen real-App publication. Synthetic fixtures do not replace this
// measurement; every digest below is checked against the committed evidence.
// The publication is split into reviewable files (summary, report, run log and
// one journal per case); the test reassembles the exact journal order.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { buildSgrUnderlineColorGradingReport } from './sgr-underline-color-grading.mjs';
import { sgrUnderlineColorAdapterId } from './sgr-underline-color-grading-collector.mjs';
import { sgrUnderlineColorSuiteId, sgrUnderlineColorContracts, requestedUnderlineColor, mutation } from './sgr-underline-color-contracts.mjs';
const dir = new URL('../../docs/qa/terminfo/2026-11-08-sgr-underline-color/', import.meta.url);
const read = name => JSON.parse(readFileSync(new URL(name, dir)));
const ids = ['sgr.underline-color-indexed', 'sgr.underline-color-rgb', 'sgr.underline-color-reset'];
const load = () => {
    const summary = read('summary.json'), report = read('report.json'), run = read('run-log.json'), common = read('journal-common.json');
    const snapshots = [common.frames.start, common.frames.bind];
    for (const id of ids) {
        const journal = read(`journal-${id}.json`);
        assert.equal(journal.caseId, id);
        snapshots.push(...journal.frames);
    }
    snapshots.push(common.frames.finish);
    return { summary, run, report: { ...report, snapshots } };
};
const rgb = cell => cell.underlineColor && [cell.underlineColor.r, cell.underlineColor.g, cell.underlineColor.b];

test('the published SGR-underline-color evidence re-derives exactly from its retained journal and run log', () => {
    const { summary, run, report } = load();
    const derived = buildSgrUnderlineColorGradingReport(report.snapshots, run, summary.context.runId, report.adapter,
        { contractsVersion: report.contractsVersion });
    assert.equal(JSON.stringify(report), JSON.stringify(derived));
    assert.equal(report.suiteId, sgrUnderlineColorSuiteId);
    assert.equal(report.adapter.id, sgrUnderlineColorAdapterId);
    assert.equal(report.adapter.derivedFromAdapterId, 'ntilde-owned-sgr-underline-grading-v1');
    assert.equal(report.snapshots.length, 3 + 4 + 2 + 2);
    assert.deepEqual(report.cases.map(item => item.caseId), ids);
    assert.deepEqual(report.cases.map(item => [item.conclusion.outcome, item.pinned.observation.outcome]), ids.map(() => ['supported', 'supported']));
    assert.equal(sgrUnderlineColorContracts.length, 3);
});

test('every conclusion is bound to the exact stored underline color, never an RGB-inferred index', () => {
    const { report } = load();
    const [indexed, literal, reset] = report.cases.map(item => item.observed);
    assert.deepEqual(indexed.fixture.map(cell => [cell.char, cell.underlineSource, cell.underlineColorIndex, cell.underlineArgb]),
        [['A', 'default', -1, null], ['X', 'palette', 5, null], ['B', 'default', -1, null], ['Y', 'palette', 4, null]]);
    assert.deepEqual(indexed.fixture.map(cell => [cell.fgSource, cell.fgIndex]), [['palette', 4], ['palette', 4], ['palette', 5], ['palette', 5]]);
    assert.deepEqual(literal.fixture[2].underlineSource, 'literal');
    assert.equal(literal.fixture[2].underlineColorIndex, -1);
    assert.equal(literal.fixture[2].underlineArgb & 0xffffff, 0xff0080);
    assert.deepEqual(rgb(literal.fixture[2]), [requestedUnderlineColor.r, requestedUnderlineColor.g, requestedUnderlineColor.b]);
    assert.deepEqual(reset.fixture.map(cell => [cell.char, cell.underlineSource, cell.underlineStyle]), [['C', 'default', 1], ['X', 'literal', 1], ['Y', 'default', 1]]);
    // The default is a marker with no color of its own, and it keeps the underline style.
    for (const cell of [...indexed.fixture, ...literal.fixture, ...reset.fixture].filter(item => item.underlineSource === 'default')) {
        assert.equal(cell.underlineColor, null);
        assert.equal(cell.underlineInheritsForeground, true);
    }
    // Equal resolved RGB, different storage: literal 95,135,175 versus palette 67.
    for (const observed of [indexed, literal]) {
        const [P, Q, R, N] = observed.control;
        assert.deepEqual([P.underlineSource, Q.underlineSource, Q.underlineColorIndex], ['literal', 'palette', 67]);
        assert.deepEqual(rgb(P), rgb(Q));
        assert.equal(N.underlineStyle, 0);
        assert.ok(R);
    }
    // Foreground substitution control: fg is exactly the requested color but the underline stays the default marker.
    assert.deepEqual([literal.control[2].underlineSource, literal.control[2].fgSource], ['default', 'literal']);
    // SGR 59 preserves the curly style, foreground and background of a styled underline, from indexed and from literal colors.
    const [S, T, U, V] = reset.control;
    assert.deepEqual([S.underlineStyle, S.underlineSource, T.underlineStyle, T.underlineSource, T.fgIndex], [3, 'palette', 3, 'default', 2]);
    assert.deepEqual([U.underlineSource, V.underlineSource], ['literal', 'default']);
    for (const item of report.cases) {
        assert.equal(item.divergence, undefined);
        assert.equal(item.observed.reverseVideo, false);
        assert.match(item.conclusion.scope, /not claimed/);
        assert.equal(item.pinned.assertions[0].kind, 'positive');
    }
});

test('the OSC 4 mutation changed exactly the contracted entries, resolved colors followed, stored indices did not, and everything restored', () => {
    const { report } = load();
    const { fixture, mutated, restored } = report.cases[0].observed;
    assert.deepEqual(mutated.slice(0, 4).map(cell => cell.underlineColorIndex), fixture.map(cell => cell.underlineColorIndex));
    assert.deepEqual(rgb(fixture[1]), [205, 0, 205]);
    assert.deepEqual(rgb(mutated[1]), [0x12, 0x34, 0x56], 'underline index 5 follows the mutated entry');
    assert.deepEqual(rgb(mutated[3]), [0x65, 0x43, 0x21], 'underline index 4 follows the mutated entry');
    assert.deepEqual(rgb(mutated[4]), [95, 135, 175], 'the literal control does not follow the palette');
    assert.deepEqual(rgb(mutated[5]), [0xaa, 0xbb, 0xcc], 'index 67 follows the mutated entry');
    assert.deepEqual(restored.map(rgb), [...fixture, ...report.cases[0].observed.control].map(rgb));
    // The recorded binding of every read: only the contracted entries differ, and only in the mutated read.
    const frames = report.snapshots.filter(frame => frame.kind === 'sguc-read').slice(0, 4);
    const start = frames[0].screen.colorBinding.ansiPaletteArgb;
    const palettes = frames.map(frame => frame.screen.colorBinding);
    assert.deepEqual(palettes[1].ansiPaletteArgb, start);
    assert.deepEqual(palettes[3].ansiPaletteArgb, start);
    assert.deepEqual(palettes[3].paletteOverrideIndices, []);
    assert.deepEqual(palettes[2].paletteOverrideIndices, [4, 5, 67]);
    for (let index = 0; index < 256; index++) {
        const expected = index in mutation ? mutation[index] : start[index];
        assert.equal(palettes[2].ansiPaletteArgb[index], expected, `entry ${index}`);
    }
});

test('the receipt binds the run, adapters and counts, and every predecessor and the finding reconfirm in the same run', () => {
    const { summary } = load();
    const { receipt } = summary;
    assert.equal(receipt.schemaVersion, 21);
    assert.equal(receipt.sgrUnderlineColorEvidence.suiteId, sgrUnderlineColorSuiteId);
    assert.equal(receipt.sgrUnderlineColorEvidence.runId, summary.context.runId);
    assert.equal(receipt.sgrUnderlineColorEvidence.contractsVersion, 1);
    assert.equal(receipt.sgrUnderlineColorEvidence.adapterBundleSha256, summary.context.sgrUnderlineColorBundleSha256);
    assert.deepEqual(receipt.sgrUnderlineColorEvidence.counts, { supported: 3 });
    // The published digests are those of the exact retained bytes: rebuild them from the reassembled evidence.
    const { run, report } = load();
    const hash = bytes => createHash('sha256').update(bytes).digest('hex');
    const observationsHash = hash(JSON.stringify(report, null, 2) + '\n');
    const runHash = hash(JSON.stringify(run) + '\n');
    const journalHash = hash(report.snapshots.map(frame => JSON.stringify(frame) + '\n').join(''));
    assert.equal(receipt.sgrUnderlineColorEvidence.sha256, observationsHash);
    assert.equal(receipt.sgrUnderlineColorEvidence.runSha256, runHash);
    assert.equal(summary.retained.sgrUnderlineColorObservationsSha256, observationsHash);
    assert.equal(summary.retained.sgrUnderlineColorRunSha256, runHash);
    assert.equal(summary.retained.sgrUnderlineColorJournalSha256, journalHash);
    assert.deepEqual(receipt.sgrUnderlineEvidence.counts, { supported: 4 });
    assert.deepEqual(receipt.sgrTruecolorEvidence.counts, { supported: 2 });
    assert.deepEqual(receipt.sgrPaletteEvidence.counts, { supported: 6 });
    assert.deepEqual(receipt.sgrAttrsEvidence.counts, { supported: 5 });
    assert.deepEqual(receipt.sgrRgbEvidence.counts, { supported: 2 });
    assert.deepEqual(receipt.sgrColorsEvidence.counts, { supported: 5 });
    assert.deepEqual(receipt.gradingEvidence.counts, { supported: 4 });
    assert.deepEqual(receipt.truecolorFindingEvidence.counts, { supported: 10 });
    assert.equal(receipt.fixtureRemoved, true);
    assert.equal(summary.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v15');
    const unique = new Set(Object.values(summary.sameRun).flatMap(suite => suite.cases.map(item => item.caseId)));
    assert.equal(unique.size, 26, 'the prior twenty-six unique qualifications');
    for (const suite of Object.values(summary.sameRun)) assert.ok(suite.cases.every(item => item.outcome === 'supported'));
    assert.equal(summary.truecolorFinding.cases.length, 10);
    assert.ok(summary.truecolorFinding.cases.every(item => item.caseId.startsWith('finding.truecolor-literal.') && item.outcome === 'supported'));
    assert.ok(![...unique].some(id => id.startsWith('finding.')));
});

test('the untouched stock run keeps its own grades; the three cases and the aggregate stay inconclusive there', () => {
    const { summary } = load();
    assert.deepEqual(summary.stockCollector.counts, { inconclusive: 168, supported: 86, unsupported: 1 });
    assert.equal(summary.stockCollector.suiteComplete, false);
    assert.equal(summary.stockCollector.probeHash, '16a690f34c0e');
    for (const id of [...ids, 'sgr.underline.color']) {
        assert.equal(summary.stockCollector.probes[id].outcome, 'inconclusive', id);
        assert.equal(summary.stockCollector.probes[id].note, 'Cursor advance does not verify SGR styling', id);
    }
    assert.ok(!report().cases.some(item => item.caseId === 'sgr.underline.color'), 'the aggregate case is not graded here');
});

function report() { return load().report; }

test('every publication file stays small enough to review in one piece', () => {
    for (const name of ['summary.json', 'report.json', 'run-log.json', 'journal-common.json', ...ids.map(id => `journal-${id}.json`)]) {
        const lines = readFileSync(new URL(name, dir), 'utf8').split('\n').length;
        assert.ok(lines <= 1500, `${name} has ${lines} lines`);
    }
});

test('failed and superseded attempts are preserved and prior publications still rederive beside this batch', () => {
    const { summary } = load();
    assert.equal(summary.attempts.length, 4);
    assert.deepEqual(summary.attempts.map(attempt => attempt.receiptCompleted), [true, true, false, false]);
    assert.ok(summary.attempts.slice(0, 2).every(attempt => attempt.outcome === 'complete, superseded'));
    assert.ok(summary.attempts.slice(2).every(attempt => attempt.outcome === 'failed before collection'));
    assert.ok(summary.attempts.every(attempt => attempt.runId !== summary.context.runId));
    const underline = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-08-sgr-underline/summary.json', import.meta.url)));
    assert.equal(underline.receipt.schemaVersion, 20);
    assert.equal(underline.receipt.sgrUnderlineColorEvidence, undefined);
    const truecolor = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-08-sgr-truecolor/summary.json', import.meta.url)));
    assert.equal(truecolor.receipt.schemaVersion, 18);
});
