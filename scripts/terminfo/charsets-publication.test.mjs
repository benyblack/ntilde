// Frozen real-App publication. Synthetic fixtures do not replace this measurement; every digest below is checked
// against the committed evidence. The publication is split into reviewable files (summary, report, run log, common
// frames and one file per read frame); the test reassembles the exact journal order.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, readdirSync } from 'node:fs';
import { buildCharsetsGradingReport } from './charsets-grading.mjs';
import { charsetsAdapterId } from './charsets-grading-collector.mjs';
import { charsetsSuiteId, charsetsContracts } from './charsets-contracts.mjs';
import { asciiSgrContracts } from './ascii-sgr-contracts.mjs';
import { sgrColorsContracts } from './sgr-colors-contracts.mjs';
import { sgrRgbContracts } from './sgr-rgb-contracts.mjs';
import { sgrAttrsContracts } from './sgr-attrs-contracts.mjs';
import { sgrPaletteContracts } from './sgr-palette-contracts.mjs';
import { sgrTruecolorContracts } from './sgr-truecolor-contracts.mjs';
import { sgrUnderlineContracts } from './sgr-underline-contracts.mjs';
import { sgrUnderlineColorContracts } from './sgr-underline-color-contracts.mjs';
import { sgrResetContracts } from './sgr-reset-contracts.mjs';
import { sgrClosureContracts } from './sgr-closure-contracts.mjs';
import { eraseLineContracts } from './erase-line-contracts.mjs';
import { charEditContracts } from './char-edit-contracts.mjs';
import { eraseDisplayContracts } from './erase-display-contracts.mjs';

const dir = new URL('../../docs/qa/terminfo/2026-11-09-charsets/', import.meta.url);
const read = name => JSON.parse(readFileSync(new URL(name, dir)));
const ids = ['charsets.utf8', 'charsets.dec-special', 'charsets.dec-line-drawing', 'charsets.g0-g1-switching'];
const load = () => {
    const summary = read('summary.json'), report = read('report.json'), run = read('run-log.json'), common = read('journal-common.json');
    const snapshots = [common.frames.start, common.frames.bind];
    for (const contract of charsetsContracts) {
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
const byId = () => Object.fromEntries(load().report.cases.map(item => [item.caseId, item]));

test('the published charset evidence re-derives exactly from its retained journal and run log', () => {
    const { summary, run, report } = load();
    const derived = buildCharsetsGradingReport(report.snapshots, run, summary.context.runId, report.adapter, { contractsVersion: report.contractsVersion });
    assert.equal(JSON.stringify(report), JSON.stringify(derived));
    assert.equal(report.suiteId, charsetsSuiteId);
    assert.equal(report.adapter.id, charsetsAdapterId);
    assert.equal(report.adapter.derivedFromAdapterId, 'ntilde-owned-erase-display-grading-v1');
    assert.equal(report.snapshots.length, 3 + 12 + 12 + 12 + 14);
    assert.deepEqual(report.cases.map(item => item.caseId), ids);
    assert.deepEqual(report.cases.map(item => [item.conclusion.outcome, item.pinned.observation.outcome]), ids.map(() => ['supported', 'supported']));
    for (const item of report.cases) assert.equal(item.divergence, undefined);
});

test('charsets.utf8 stored U+00E9 narrow and U+4E16 wide with its continuation, Z at column 4, and never normalized', () => {
    const item = byId()['charsets.utf8'];
    assert.equal(item.pinned.response, JSON.stringify({ before: [...'Axy?Z'], setup: [{ x: 1, y: 0 }, { x: 2, y: 0 }], after: ['A', 'é', '世', '', 'Z'] }));
    const { groups } = item.observed;
    assert.deepEqual(groups.target.cells, ['0,0,A', '0,1,é U+00E9', '0,2,世 U+4E16 wide', '0,3,<continuation>', '0,4,Z']);
    assert.deepEqual(groups.target.cursor, [0, 4]);
    assert.deepEqual(groups.wideAlone.cells, ['0,0,A', '0,1,世 U+4E16 wide', '0,2,<continuation>', '0,3,Z']);
    assert.deepEqual(groups.precomposed.cells, ['0,0,é U+00E9', '0,1,Z']);
    assert.equal(groups.decomposed.class, 'observed');
    assert.ok(groups.decomposed.cells.some(cell => cell.includes('extended')), 'a decomposed sequence is stored as extended text, distinct from the precomposed cell');
    assert.match(item.conclusion.scope, /not general UTF-8, grapheme, normalization or width completeness/);
});

test('the DEC cases stored the exact mapped code points with ASCII controls intact and distinguishable source-character controls', () => {
    const cases = byId();
    const special = cases['charsets.dec-special'].observed.groups, lines = cases['charsets.dec-line-drawing'].observed.groups, g01 = cases['charsets.g0-g1-switching'].observed.groups;
    assert.deepEqual(special.target.cells, ['0,0,q', '0,1,─ U+2500', '0,2,q']);
    assert.deepEqual(lines.target.cells, ['0,0,┘ U+2518', '0,1,┐ U+2510', '0,2,┌ U+250C', '0,3,└ U+2514', '0,4,─ U+2500', '0,5,│ U+2502', '0,6,j']);
    assert.deepEqual(g01.target.cells, ['0,0,A', '0,1,l', '0,2,┌ U+250C', '0,3,l', '0,4,Z']);
    assert.equal(special.seed.text[0], 'qqq_________');
    assert.equal(lines.seed.text[0], 'jklmqxj_____');
    assert.equal(special.upper.text[0], 'QQQ_________', 'uppercase never maps, so it cannot witness restoration');
    assert.equal(lines.upper.text[0], 'JKLMQX______');
});

test('G0/G1: designation without invocation and invocation without designation change nothing, and the G1 designation persists past SI', () => {
    const { groups } = byId()['charsets.g0-g1-switching'].observed;
    assert.equal(groups.designateOnly.text[0], 'AlllZ_______');
    assert.equal(groups.shiftOnly.text[0], 'AlllZ_______');
    assert.equal(groups.g0Direct.text[0], 'A┌┌┌Z_______');
    assert.equal(groups.g1Invoked.text[0], 'A┌┌┌Z_______');
    assert.deepEqual(groups.persistShift.text.slice(0, 2), ['Al┌lZ_______', '┘┐┌_________']);
    assert.equal(groups.persistRestore, undefined);
});

test('lowercase restoration witnesses: ASCII after the fixture, mapped under an explicit designation, ASCII again after returning', () => {
    for (const item of load().report.cases) {
        const { groups } = item.observed;
        assert.equal(groups.witnessAscii.text[1], 'jklmqxl_____', item.caseId);
        assert.equal(groups.witnessDec.text[2], '┘┐┌└─│┌_____', item.caseId);
        assert.equal(groups.witnessAfter.text[3], 'jklmqxl_____', item.caseId);
        assert.deepEqual(groups.witnessAfter.cursor, [3, 7]);
    }
    const { special, lines } = { special: byId()['charsets.dec-special'].observed.groups, lines: byId()['charsets.dec-line-drawing'].observed.groups };
    assert.equal(special.persistRestore.text[0], '───qqq______');
    assert.equal(lines.persistRestore.text[0], '┘┐┌└─│jklmqx');
});

test('overlapping fixtures keep separate identities with distinct pinned bytes', () => {
    const { report } = load();
    assert.equal(new Set(report.cases.map(item => JSON.stringify(item.pinnedFeeds))).size, 4);
    const overlaps = Object.fromEntries(report.cases.map(item => [item.caseId, item.overlaps.map(entry => entry.with)]));
    assert.deepEqual(overlaps['charsets.dec-special'], ['charsets.dec-line-drawing']);
    assert.deepEqual(overlaps['charsets.utf8'], []);
});

test('the receipt binds the run, adapters and counts, and every predecessor and finding reconfirm in the same run', () => {
    const { summary, run, report } = load();
    const { receipt } = summary;
    assert.equal(receipt.schemaVersion, 28);
    assert.equal(receipt.charsetsEvidence.suiteId, charsetsSuiteId);
    assert.equal(receipt.charsetsEvidence.runId, summary.context.runId);
    assert.deepEqual(receipt.charsetsEvidence.counts, { supported: 4 });
    assert.equal(receipt.charsetsEvidence.adapterBundleSha256, summary.context.charsetsBundleSha256);
    const observationsHash = hash(JSON.stringify(report, null, 2) + '\n');
    const runHash = hash(JSON.stringify(run) + '\n');
    const journalHash = hash(report.snapshots.map(frame => JSON.stringify(frame) + '\n').join(''));
    assert.equal(receipt.charsetsEvidence.sha256, observationsHash);
    assert.equal(receipt.charsetsEvidence.runSha256, runHash);
    assert.equal(summary.retained.charsetsObservationsSha256, observationsHash);
    assert.equal(summary.retained.charsetsRunSha256, runHash);
    assert.equal(summary.retained.charsetsJournalSha256, journalHash);
    assert.equal(summary.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v22');
    assert.equal(receipt.fixtureRemoved, true);
    assert.equal(summary.ownedRunId, summary.context.runId);
    assert.notEqual(summary.stockCollectorRunId, summary.ownedRunId);
    // The predecessor inventory is derived from the merged contracts, not assumed.
    const expected = new Set([...asciiSgrContracts, ...sgrColorsContracts, ...sgrRgbContracts, ...sgrAttrsContracts, ...sgrPaletteContracts,
        ...sgrTruecolorContracts, ...sgrUnderlineContracts, ...sgrUnderlineColorContracts, ...sgrResetContracts, ...sgrClosureContracts,
        ...eraseLineContracts, ...charEditContracts, ...eraseDisplayContracts].map(contract => contract.caseId));
    const unique = new Set(Object.values(summary.sameRun).flatMap(suite => suite.cases.map(item => item.caseId)));
    assert.deepEqual([...unique].sort(), [...expected].sort());
    for (const suite of Object.values(summary.sameRun)) assert.ok(suite.cases.every(item => item.outcome === 'supported'));
    assert.ok(!ids.some(id => unique.has(id)), 'the four new cases are counted separately');
    assert.equal(summary.truecolorFinding.cases.length, 10);
    assert.ok(summary.truecolorFinding.cases.every(item => item.caseId.startsWith('finding.truecolor-literal.') && item.outcome === 'supported'));
    assert.equal(summary.underlineResolved.cases.length, 3);
    assert.ok(summary.underlineResolved.cases.every(item => item.outcome === 'supported'));
});

test('the untouched stock run keeps its own grades: all four pinned cases are inconclusive there (consumed bytes only)', () => {
    const { summary } = load();
    assert.deepEqual(summary.stockCollector.counts, { inconclusive: 168, supported: 86, unsupported: 1 });
    assert.equal(summary.stockCollector.suiteComplete, false);
    assert.equal(summary.stockCollector.probeHash, '16a690f34c0e');
    for (const id of ids) {
        assert.equal(summary.stockCollector.probes[id].outcome, 'inconclusive');
        assert.equal(summary.stockCollector.probes[id].note, 'Cursor movement does not verify charset glyph rendering or mapping');
    }
});

test('every publication file stays small enough to review in one piece', () => {
    for (const name of readdirSync(dir)) {
        const lines = readFileSync(new URL(name, dir), 'utf8').split('\n').length;
        assert.ok(lines <= 1500, `${name} has ${lines} lines`);
    }
});

test('prior publications still rederive beside this batch and attempts are preserved', () => {
    const erase = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-08-erase-line/summary.json', import.meta.url)));
    assert.equal(erase.receipt.schemaVersion, 25);
    assert.equal(erase.receipt.charsetsEvidence, undefined);
    assert.equal(erase.receipt.charEditEvidence, undefined);
    assert.equal(erase.receipt.eraseDisplayEvidence, undefined);
    const { attempts, context } = load().summary;
    assert.ok(attempts.length >= 1);
    assert.equal(attempts.at(-1).runId, context.runId);
    assert.equal(attempts.at(-1).receiptCompleted, true);
});
