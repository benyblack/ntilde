import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildSelectiveEraseGradingReport, replaySelectiveErasePinned, validateSelectiveEraseEvidence } from './selective-erase-grading.mjs';
import { selectiveEraseContracts, selectiveEraseSuiteId, selectiveEraseContractsVersion, pinnedColumnsNote, pinnedSeedNote, pinnedControlNote } from './selective-erase-contracts.mjs';
import { selectiveEraseRecordingContext, expectedPinnedFeeds } from './selective-erase-collector.mjs';
import { snapshotHash } from './agent-observer.mjs';
import { syntheticSelectiveEraseEvidence, syntheticSelectiveEraseReads, syntheticSelectiveEraseScreens, seedCells, guarded, open,
    fillSyntheticCells } from './fixtures/selective-erase-evidence.mjs';

const SED = 'erase.selective', SERA = 'editing.decsera';
const runId = 'c'.repeat(32);
const evidenceWith = (caseId, edit, binding) => syntheticSelectiveEraseEvidence(runId, 'f'.repeat(64), binding, id => {
    const reads = syntheticSelectiveEraseReads(id);
    if (id === caseId && edit) edit(reads);
    return reads;
});
const build = evidence => buildSelectiveEraseGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
/** A JSON round trip: structuredClone would keep the shared screen and binding objects shared between frames. */
const clone = value => JSON.parse(JSON.stringify(value));
const contractOf = caseId => selectiveEraseContracts.find(item => item.caseId === caseId);

test('a complete run rederives both cases: pinned and contract agree and nothing diverges', () => {
    const report = build(syntheticSelectiveEraseEvidence(runId));
    assert.equal(report.suiteId, selectiveEraseSuiteId);
    assert.equal(report.contractsVersion, selectiveEraseContractsVersion);
    assert.deepEqual(report.cases.map(item => [item.caseId, item.pinned.observation.outcome, item.conclusion.outcome]),
        [[SED, 'supported', 'supported'], [SERA, 'supported', 'supported']]);
    for (const item of report.cases) {
        assert.equal(item.divergence, undefined);
        assert.equal(item.conclusion.grading, 'pinned-termless-plus-contract-v1');
        assert.equal(item.pinned.assertions[0].expected, 'DECSCA-protected P survives while selective erase clears ABCD');
        assert.equal(item.pinned.note, null);
        assert.equal(item.reads.length, contractOf(item.caseId).roles.length);
    }
    assert.equal(report.snapshots.length, 3 + 20 + 21);
    assert.match(report.scope, /cursor and\s+never carry a verdict|never carry a verdict/);
    assert.match(report.scope, /ASCII only/);
});

test('the pinned response carries the six characters before and after, exactly as the pinned probe prints them', () => {
    const [sed, sera] = build(syntheticSelectiveEraseEvidence(runId)).cases;
    assert.equal(sed.pinned.response, JSON.stringify({ before: ['P', 'A', 'B', 'C', 'D', 'Z'], after: ['P', ' ', ' ', ' ', ' ', ' '] }));
    assert.equal(sera.pinned.response, JSON.stringify({ before: ['P', 'A', 'B', 'C', 'D', 'Z'], after: ['P', ' ', ' ', ' ', ' ', 'Z'] }));
});

test('P lost: the pinned probe is inconclusive and the contract is unsupported, a recorded measured mismatch', () => {
    for (const caseId of [SED, SERA]) {
        const report = build(evidenceWith(caseId, reads => { reads.erased = { cells: [], cursor: [0, 6] }; }));
        const item = report.cases.find(entry => entry.caseId === caseId);
        assert.equal(item.pinned.observation.outcome, 'inconclusive');
        assert.equal(item.pinned.observation.note, pinnedControlNote);
        assert.equal(item.pinned.assertions, null);
        assert.equal(item.conclusion.outcome, 'unsupported');
        assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'unsupported', kind: 'contract-measured-mismatch' });
    }
});

test('Z lost in DECSERA is the same measured mismatch; Z kept by DECSED is unsupported by both', () => {
    const lost = build(evidenceWith(SERA, reads => { reads.erased = { cells: [guarded(0, 0, 'P')], cursor: [0, 6] }; })).cases[1];
    assert.equal(lost.pinned.observation.outcome, 'inconclusive');
    assert.equal(lost.divergence.kind, 'contract-measured-mismatch');
    const kept = build(evidenceWith(SED, reads => { reads.erased = { cells: [guarded(0, 0, 'P'), open(0, 5, 'Z')], cursor: [0, 6] }; })).cases[0];
    assert.equal(kept.pinned.observation.outcome, 'unsupported');
    assert.equal(kept.conclusion.outcome, 'unsupported');
    assert.equal(kept.divergence, undefined);
});

test('an invalid seed is inconclusive in both graders with the pinned note', () => {
    const report = build(evidenceWith(SED, reads => { reads.seeded = { cells: seedCells().slice(0, 4), cursor: [0, 6] }; }));
    assert.equal(report.cases[0].pinned.observation.outcome, 'inconclusive');
    assert.equal(report.cases[0].pinned.observation.note, pinnedSeedNote);
    assert.equal(report.cases[0].conclusion.outcome, 'inconclusive');
    assert.equal(report.cases[0].divergence, undefined);
    assert.equal(JSON.parse(report.cases[0].pinned.response).before.join(''), 'PABC  ');
});

test('damage the pinned probe cannot see (protection, cursor, fill, history) is a recorded stricter divergence', () => {
    // P survives as a marker but reads unprotected: the pinned probe cannot tell
    const marker = build(evidenceWith(SED, reads => { reads.erased = { cells: [open(0, 0, 'P')], cursor: [0, 6] }; })).cases[0];
    assert.equal(marker.pinned.observation.outcome, 'supported');
    assert.equal(marker.conclusion.outcome, 'unsupported');
    assert.equal(marker.divergence.kind, 'contract-stricter-than-pinned');
    const homed = build(evidenceWith(SERA, reads => { reads.erased = { ...reads.erased, cursor: [0, 0] }; })).cases[1];
    assert.equal(homed.pinned.observation.outcome, 'supported');
    assert.equal(homed.divergence.kind, 'contract-stricter-than-pinned');
    const fill = build(evidenceWith(SED, reads => { reads.fill = { cells: [], cursor: [20, 4] }; })).cases[0];
    assert.equal(fill.pinned.observation.outcome, 'supported');
    assert.equal(fill.conclusion.outcome, 'unsupported');
    const history = build(evidenceWith(SERA, reads => { reads.history = { ...reads.history, history: ['HIST-A'] }; })).cases[1];
    assert.equal(history.divergence.kind, 'contract-stricter-than-pinned');
    const ordinary = build(evidenceWith(SED, reads => { reads.ordinary = { cells: [guarded(0, 0, 'P')], cursor: [0, 6] }; })).cases[0];
    assert.equal(ordinary.conclusion.outcome, 'unsupported');
});

test('a pinned unsupported over an invalid control seed is recorded as the contract being more cautious', () => {
    const report = build(evidenceWith(SED, reads => {
        reads.erased = { cells: [guarded(0, 0, 'P'), open(0, 5, 'Z')], cursor: [0, 6] };
        reads.fillBefore = { cells: fillSyntheticCells().slice(1), cursor: [20, 4] };
    }));
    const item = report.cases[0];
    assert.equal(item.pinned.observation.outcome, 'unsupported');
    assert.equal(item.conclusion.outcome, 'inconclusive');
    assert.deepEqual(item.divergence, { pinnedOutcome: 'unsupported', contractOutcome: 'inconclusive', kind: 'contract-more-cautious-than-pinned' });
});

test('the pinned replay equals the live pinned shapes over the observed fixture screens', () => {
    for (const contract of selectiveEraseContracts) {
        const screens = syntheticSelectiveEraseScreens(contract.caseId);
        const replay = replaySelectiveErasePinned(contract, screens);
        assert.equal(replay.pass, true);
        assert.equal(replay.observation.outcome, 'supported');
        assert.equal(replay.assertions[0].expected, contract.pinnedExpected);
        const context = selectiveEraseRecordingContext(contract, screens);
        assert.equal(context.cols, 140);
        const [seed, sequence, finalizer] = contract.pinnedFeeds;
        assert.throws(() => context.getCell(0, 0), /before its fixture/);
        assert.throws(() => context.feed(sequence), /differs from the contract/);
        context.feed(seed);
        assert.equal(context.getCell(0, 0).char, 'P');
        assert.equal(context.getCell(0, 5).char, 'Z');
        context.feed(sequence);
        assert.equal(context.getCell(0, 5).char, contract.rectangular ? 'Z' : ' ');
        context.feed(finalizer);
        assert.deepEqual(context.feeds, [seed, sequence, finalizer]);
        assert.throws(() => context.feed(finalizer), /differs from the contract/);
        assert.throws(() => context.getText(), /must not read text/);
        assert.throws(() => context.getCursor(), /must not read the cursor/);
        assert.throws(() => context.getScrollback(), /must not read the scrollback/);
        // an early return still feeds the finalizer, and only the finalizer, after the seed
        const early = selectiveEraseRecordingContext(contract, screens);
        early.feed(seed); early.feed(finalizer);
        assert.equal(early.fedCount, 2);
    }
});

test('the pinned columns gate is the pinned inconclusive shape with its note only inside the observation', () => {
    const contract = contractOf(SED);
    const screens = syntheticSelectiveEraseScreens(SED).map(screen => ({ ...screen, cols: 5 }));
    const replay = replaySelectiveErasePinned(contract, screens);
    assert.equal(replay.pass, false);
    assert.equal(replay.response, JSON.stringify({ cols: 5 }));
    assert.equal(replay.note, pinnedColumnsNote);
    assert.deepEqual(replay.observation, { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'parser-state', note: pinnedColumnsNote });
    assert.equal(replay.assertions, undefined);
    assert.deepEqual(expectedPinnedFeeds(contract, replay), []);
    const seed = replaySelectiveErasePinned(contract, syntheticSelectiveEraseScreens(SED, undefined, id => {
        const reads = syntheticSelectiveEraseReads(id); reads.seeded = { cells: [], cursor: [0, 0] }; return reads;
    }));
    assert.deepEqual(expectedPinnedFeeds(contract, seed), [contract.pinnedFeeds[0], contract.pinnedFeeds[2]]);
    assert.deepEqual(expectedPinnedFeeds(contract, { note: undefined }), [...contract.pinnedFeeds]);
});

test('refuses a run whose pinned feeds are not exactly the contracted bytes, or whose pinned result was altered', () => {
    const wrongFeeds = syntheticSelectiveEraseEvidence(runId);
    wrongFeeds.run.cases[0].pinnedFed = [contractOf(SED).pinnedFeeds[0]];
    assert.throws(() => build(wrongFeeds), /did not replay exactly its contracted fixture/);
    const missing = syntheticSelectiveEraseEvidence(runId);
    delete missing.run.cases[1].pinnedFed;
    assert.throws(() => build(missing), /did not replay exactly its contracted fixture/);
    const altered = clone(syntheticSelectiveEraseEvidence(runId));
    altered.run.cases[0].pinned.response = '{}';
    assert.throws(() => build(altered), /does not match its observed evidence/);
    const flipped = clone(syntheticSelectiveEraseEvidence(runId));
    flipped.run.cases[0].pinned.observation.outcome = 'unsupported';
    assert.throws(() => build(flipped), /does not match its observed evidence/);
});

test('tampered journals, identities and restoration are refused', () => {
    const evidence = syntheticSelectiveEraseEvidence(runId);
    assert.doesNotThrow(() => validateSelectiveEraseEvidence(evidence.frames, evidence.run, evidence.runId, evidence.adapter));
    const stale = clone(evidence);
    stale.frames[5].screen.lines[0] = 'tampered';
    assert.throws(() => build(stale), /Changed Selective-erase snapshot evidence|snapshot/i);
    const rehashed = clone(evidence);
    rehashed.frames[5].screen.cursorCol = 1;
    rehashed.frames[5].sha256 = snapshotHash(rehashed.frames[5].screen);
    assert.throws(() => build(rehashed), /cursor differs from its read frame/);
    assert.throws(() => build({ ...evidence, runId: 'd'.repeat(32) }), /not bound to this run/);
    assert.throws(() => build({ ...evidence, adapter: { ...evidence.adapter, id: 'other' } }), /Invalid Selective-erase adapter identity/);
    assert.throws(() => build({ ...evidence, adapter: { ...evidence.adapter, derivedFromAdapterId: 'ntilde-owned-char-edit-grading-v1' } }), /Invalid Selective-erase adapter identity/);
    const unrestored = clone(evidence);
    unrestored.frames.at(-1).screen.lines[3] = 'left behind';
    unrestored.frames.at(-1).sha256 = snapshotHash(unrestored.frames.at(-1).screen);
    assert.throws(() => build(unrestored), /fixture was not restored/);
    const order = clone(evidence);
    order.run.cases.reverse();
    assert.throws(() => build(order), /case order differs|run does not cover/);
    const feeds = clone(evidence);
    feeds.run.cases[0].feeds[1] += 'x';
    assert.throws(() => build(feeds), /do not match the pinned contract/);
    const stripped = clone(evidence);
    for (const frame of stripped.frames) {
        for (const run of frame.screen.cellDetails) delete run.protected;
        frame.sha256 = snapshotHash(frame.screen);
    }
    assert.throws(() => build(stripped), /per-cell protection|protection/i);
});

test('an inconsistent screen (a cell detail that contradicts its attribute run) is refused before any grading', () => {
    const evidence = clone(syntheticSelectiveEraseEvidence(runId));
    const frame = evidence.frames[3];
    frame.screen.attributeRuns[0].flags |= 1;
    frame.sha256 = snapshotHash(frame.screen);
    assert.throws(() => build(evidence));
});
