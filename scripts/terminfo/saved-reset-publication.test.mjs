// Frozen real-App publication. Synthetic fixtures do not replace this measurement; every digest below is checked against
// the committed evidence. The publication is split into reviewable files (summary, report header, one file per case, run
// log, common frames and one file per read frame); the test reassembles the exact journal order.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, readdirSync } from 'node:fs';
import { buildSavedResetGradingReport } from './saved-reset-grading.mjs';
import { savedResetAdapterId } from './saved-reset-grading-collector.mjs';
import { savedResetSuiteId, savedResetContracts, savedResetEffectContractId } from './saved-reset-contracts.mjs';
import { savedResetCounts } from './saved-reset-receipt.mjs';
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
import { lineEditContracts } from './line-edit-contracts.mjs';
import { charsetsContracts } from './charsets-contracts.mjs';
import { scrollbackContracts } from './scrollback-contracts.mjs';

const dir = new URL('../../docs/qa/terminfo/2026-11-09-saved-reset/', import.meta.url);
const read = name => JSON.parse(readFileSync(new URL(name, dir)));
const ids = ['modes.alt-screen.exit', 'modes.altscreen-47', 'modes.altscreen-1047', 'reset.sgr', 'reset.decaln'];
const load = () => {
    const summary = read('summary.json'), header = read('report.json'), run = read('run-log.json'), common = read('journal-common.json');
    const snapshots = [common.frames.start, common.frames.bind];
    for (const contract of savedResetContracts) {
        for (let index = 0; index < contract.roles.length; index++) {
            const journal = read(`journal-${contract.caseId}-read${index}.json`);
            assert.equal(journal.caseId, contract.caseId);
            assert.equal(journal.read, index);
            snapshots.push(journal.frame);
        }
    }
    snapshots.push(common.frames.finish);
    const cases = header.cases.map(caseId => read(`case-${caseId}.json`));
    return { summary, run, report: { ...header, cases, snapshots } };
};
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const byId = (report, id) => report.cases.find(item => item.caseId === id);

test('the published saved-state/reset evidence re-derives exactly from its retained journal and run log', () => {
    const { summary, run, report } = load();
    const derived = buildSavedResetGradingReport(report.snapshots, run, summary.context.runId, report.adapter, { contractsVersion: report.contractsVersion });
    assert.equal(JSON.stringify(report), JSON.stringify(derived));
    assert.equal(report.suiteId, savedResetSuiteId);
    assert.equal(report.adapter.id, savedResetAdapterId);
    assert.equal(report.adapter.derivedFromAdapterId, 'ntilde-owned-scrollback-grading-v1');
    assert.equal(report.snapshots.length, 3 + 13 + 9 + 12 + 5 + 6);
    assert.deepEqual(report.cases.map(item => item.caseId), ids);
});

test('pinned verdicts and separately identified effect verdicts are recorded apart', () => {
    const { report } = load();
    assert.deepEqual(report.cases.map(item => [item.caseId, item.pinned.observation.outcome, item.effectContract.outcome, item.conclusion.outcome]), [
        ['modes.alt-screen.exit', 'supported', 'supported', 'supported'],
        ['modes.altscreen-47', 'inconclusive', 'supported', 'inconclusive'],
        ['modes.altscreen-1047', 'inconclusive', 'supported', 'inconclusive'],
        ['reset.sgr', 'supported', 'supported', 'supported'],
        ['reset.decaln', 'supported', 'supported', 'supported']]);
    for (const id of ['modes.altscreen-47', 'modes.altscreen-1047']) {
        const item = byId(report, id);
        assert.equal(item.pinned.note, 'Mode metadata does not measure the buffer');
        assert.equal(item.pinned.response, '{"entered":true,"exited":true}');
        assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'supported', kind: 'pinned-inconclusive-by-design' });
        assert.equal(item.effectContract.id, savedResetEffectContractId);
        assert.match(item.effectContract.attribution, /not the pinned probe/);
    }
    for (const id of ['modes.alt-screen.exit', 'reset.sgr', 'reset.decaln']) assert.equal(byId(report, id).divergence, undefined);
    assert.equal(byId(report, 'reset.sgr').pinnedFeeds[0], '\x1b[1;3;7mX\x1b[0mY', 'reset.sgr ran its own fixture, not sgr.reset');
});

test('alternate-screen exit restored the main contents, the saved cursor and kept the banks distinct', () => {
    const { roles, observations } = byId(load().report, 'modes.alt-screen.exit').observed;
    const seeds = [{ row: 0, cols: [0, 8], text: 'PRIMARY-A', stored: 'neutral' }, { row: 2, cols: [0, 8], text: 'PRIMARY-B', stored: 'neutral' }];
    assert.deepEqual(roles.seed.ink, seeds);
    assert.deepEqual(roles.seed.cursor, [4, 6]);
    assert.equal(roles.entered.alt, true);
    assert.deepEqual(roles.entered.ink, []);
    assert.deepEqual(roles.marked.ink, [{ row: 0, cols: [0, 7], text: 'ALT-MARK', stored: 'neutral' }]);
    for (const role of ['exited', 'final', 'back', 'staleExited']) {
        assert.equal(roles[role].alt, false, role);
        assert.deepEqual(roles[role].ink, seeds, role);
        assert.deepEqual(roles[role].cursor, [4, 6], role);
        assert.deepEqual(roles[role].margins, [0, 39], role);
        assert.equal(roles[role].scrollbackLines, 0, role);
    }
    assert.equal(roles.reentry.alt, true);
    assert.deepEqual(roles.reentry.ink, roles.marked.ink, 'the alternate bank kept its own contents; the main text never appeared on it');
    assert.deepEqual(roles.styledExited.ink, roles.styledSeed.ink);
    assert.equal(observations.alternateRetainedAfter1049Exit, true);
    assert.deepEqual(observations.savedPenWitness, { char: 'W', bold: true, fgIndex: 4, restored: true });
});

test('?47 retains the alternate buffer across exit and ?1047 clears it, each with the main screen intact', () => {
    const { report } = load();
    const v47 = byId(report, 'modes.altscreen-47').observed.roles, v1047 = byId(report, 'modes.altscreen-1047').observed.roles;
    for (const roles of [v47, v1047]) {
        assert.equal(roles.active.alt, true);
        assert.deepEqual(roles.active.ink, []);
        assert.equal(roles.exited.alt, false);
        assert.equal(roles.back.alt, false);
        assert.deepEqual(roles.exited.cursor, [2, 4]);
    }
    assert.equal(v47.reentry.alt, true);
    assert.deepEqual(v47.reentry.ink, [{ row: 1, cols: [1, 6], text: 'ALT-47', stored: 'neutral' }]);
    assert.deepEqual(v1047.reentry.ink, [], '?1047 cleared the alternate buffer on exit');
    assert.deepEqual(v47.back.ink, [{ row: 0, cols: [0, 6], text: 'MAIN-47', stored: 'neutral' }]);
    assert.deepEqual(v1047.back.ink, [{ row: 0, cols: [0, 8], text: 'MAIN-1047', stored: 'neutral' }]);
    assert.equal(byId(report, 'modes.altscreen-1047').observed.observations.retainsAlternateContentsOnEntry, true);
});

test('reset.sgr stored the requested styles on X and cleared every styled field on Y', () => {
    const { roles, observations } = byId(load().report, 'reset.sgr').observed;
    assert.equal(roles.pinned.ink.length, 2);
    assert.equal(roles.pinned.ink[0].stored.flags, 775, 'bold + italic + inverse on X, default storage');
    assert.equal(roles.pinned.ink[0].text, 'X');
    assert.equal(roles.pinned.ink[1].text, 'Y');
    assert.equal(roles.pinned.ink[1].stored, 'neutral');
    assert.deepEqual(roles.pinned.cursor, [0, 2]);
    assert.deepEqual(roles.persist.ink.map(entry => entry.cols), [[0, 1]], 'without the reset the pen stayed active on Y');
    assert.equal(roles.full.ink[1].stored, 'neutral');
    assert.equal(observations.omittedSpellingClearsY, true);
});

test('reset.decaln filled the stored grid with E, homed the cursor and kept the stored cell criterion separate from the pixel path', () => {
    const item = byId(load().report, 'reset.decaln');
    const { roles, observations } = item.observed;
    assert.equal(roles.pinned.ink.length, 40);
    assert.ok(roles.pinned.ink.every(run => run.cols[0] === 0 && run.cols[1] === 139 && /^E{140}$/.test(run.text)));
    assert.deepEqual(roles.pinned.cursor, [0, 0]);
    assert.equal(roles.seed.ink.length, 3, 'the known A/B seed grid was measured before DECALN');
    assert.equal(observations.gridCoversScreen, true);
    assert.deepEqual(observations.marginsAfterDecalnWithRegion, [2, 5], 'observed, not required: an active region survives DECALN');
    assert.equal(observations.eCellStoredNeutral, true);
    assert.match(item.terminalFixture.note, /capture-only pixel fixture and is not qualified/);
});

test('the receipt binds the run, adapters and counts, and every predecessor and finding reconfirm in the same run', () => {
    const { summary, run, report } = load();
    const { receipt } = summary;
    assert.equal(receipt.schemaVersion, 31);
    assert.equal(receipt.savedResetEvidence.suiteId, savedResetSuiteId);
    assert.equal(receipt.savedResetEvidence.runId, summary.context.runId);
    const { counts, effectCounts } = savedResetCounts(report);
    assert.deepEqual(counts, { supported: 3, inconclusive: 2 });
    assert.deepEqual(effectCounts, { supported: 5 });
    assert.deepEqual(receipt.savedResetEvidence.counts, counts);
    assert.deepEqual(receipt.savedResetEvidence.effectCounts, effectCounts);
    assert.equal(receipt.savedResetEvidence.adapterBundleSha256, summary.context.savedResetBundleSha256);
    const observationsHash = hash(JSON.stringify(report, null, 2) + '\n');
    const runHash = hash(JSON.stringify(run) + '\n');
    const journalHash = hash(report.snapshots.map(frame => JSON.stringify(frame) + '\n').join(''));
    assert.equal(receipt.savedResetEvidence.sha256, observationsHash);
    assert.equal(receipt.savedResetEvidence.runSha256, runHash);
    assert.equal(summary.retained.savedResetObservationsSha256, observationsHash);
    assert.equal(summary.retained.savedResetRunSha256, runHash);
    assert.equal(summary.retained.savedResetJournalSha256, journalHash);
    assert.equal(summary.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v25');
    assert.equal(receipt.fixtureRemoved, true);
    assert.equal(summary.ownedRunId, summary.context.runId);
    assert.notEqual(summary.stockCollectorRunId, summary.ownedRunId);
    // The predecessor inventory is derived from the merged contracts, not assumed.
    const expected = new Set([...asciiSgrContracts, ...sgrColorsContracts, ...sgrRgbContracts, ...sgrAttrsContracts, ...sgrPaletteContracts,
        ...sgrTruecolorContracts, ...sgrUnderlineContracts, ...sgrUnderlineColorContracts, ...sgrResetContracts, ...sgrClosureContracts, ...eraseLineContracts, ...charEditContracts, ...eraseDisplayContracts, ...lineEditContracts, ...charsetsContracts, ...scrollbackContracts].map(contract => contract.caseId));
    const unique = new Set(Object.values(summary.sameRun).flatMap(suite => suite.cases.map(item => item.caseId)));
    assert.deepEqual([...unique].sort(), [...expected].sort());
    for (const suite of Object.values(summary.sameRun)) assert.ok(suite.cases.every(item => item.outcome === 'supported'));
    assert.ok(!ids.some(id => unique.has(id)), 'the five new cases are counted separately');
    assert.equal(summary.truecolorFinding.cases.length, 10);
    assert.ok(summary.truecolorFinding.cases.every(item => item.caseId.startsWith('finding.truecolor-literal.') && item.outcome === 'supported'));
    assert.equal(summary.underlineResolved.cases.length, 3);
    assert.ok(summary.underlineResolved.cases.every(item => item.outcome === 'supported'));
});

test('the untouched stock run keeps its own grades: all five pinned cases are inconclusive there', () => {
    const { summary } = load();
    assert.deepEqual(summary.stockCollector.counts, { inconclusive: 168, supported: 86, unsupported: 1 });
    assert.equal(summary.stockCollector.suiteComplete, false);
    assert.equal(summary.stockCollector.probeHash, '16a690f34c0e');
    for (const id of ids) assert.equal(summary.stockCollector.probes[id].outcome, 'inconclusive', id);
    assert.equal(summary.stockCollector.probes['modes.alt-screen.exit'].note, 'Cursor responsiveness after exit does not measure the restored buffer');
});

test('every publication file stays small enough to review in one piece', () => {
    for (const name of readdirSync(dir)) {
        const lines = readFileSync(new URL(name, dir), 'utf8').split('\n').length;
        assert.ok(lines <= 1500, `${name} has ${lines} lines`);
    }
});

test('prior publications still rederive beside this batch and the attempt record is preserved', () => {
    const earlier = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-09-scrollback/summary.json', import.meta.url)));
    assert.equal(earlier.receipt.schemaVersion, 30);
    assert.equal(earlier.receipt.savedResetEvidence, undefined);
    const { attempts, context } = load().summary;
    assert.equal(attempts.length, 4);
    assert.equal(attempts[3].runId, context.runId);
    for (const attempt of attempts.slice(0, 3)) assert.notEqual(attempt.runId, context.runId);
    assert.deepEqual(attempts.map(attempt => attempt.receiptCompleted), [true, true, true, true]);
    assert.deepEqual(Object.values(attempts[0].verdicts), ['supported', 'inconclusive', 'inconclusive', 'supported', 'supported']);
});
