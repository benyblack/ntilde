import assert from 'node:assert/strict';
import { test } from 'node:test';
import { selectiveEraseContracts, selectiveEraseCaseIds, selectiveEraseContractsVersion, selectiveEraseSuiteId, selectiveEraseNoncePrefix,
    selectiveEraseReset, selectiveEraseFeeds, selectiveEraseReads, validateSelectiveEraseFeeds, gradeSelectiveEraseCase, selectiveEraseObservation,
    selectiveEraseInk, pinnedSeed, pinnedFinalizer, pinnedColumnsNote, pinnedSeedNote, selectiveEraseFixture } from './selective-erase-contracts.mjs';
import { syntheticSelectiveEraseReads, syntheticSelectiveEraseScreens, syntheticSelectiveEraseGrade, syntheticSelectiveScreen, seedCells,
    guarded, open, fillSyntheticCells, paletteBinding, colorCell } from './fixtures/selective-erase-evidence.mjs';

const SED = 'erase.selective', SERA = 'editing.decsera';
const contractOf = caseId => selectiveEraseContracts.find(item => item.caseId === caseId);
/** Grade a case from its synthetic reads after `edit` changed them. */
const grade = (caseId, edit, binding) => syntheticSelectiveEraseGrade(caseId, binding, id => {
    const reads = syntheticSelectiveEraseReads(id);
    if (id === caseId && edit) edit(reads);
    return reads;
});
const note = (caseId, edit) => grade(caseId, edit).note;
const outcome = (caseId, edit) => grade(caseId, edit).outcome;

test('the contract pins the exact seed, finalizer, erase bytes and ids of both cases', () => {
    assert.equal(selectiveEraseContractsVersion, 1);
    assert.equal(selectiveEraseSuiteId, 'ntilde-owned-selective-erase-graded-v1');
    assert.equal(selectiveEraseNoncePrefix, 'NTILDE_SE_');
    assert.deepEqual([...selectiveEraseCaseIds], [SED, SERA]);
    assert.equal(pinnedSeed, '\x1b[1;1H\x1b[1"qP\x1b[0"qABCDZ');
    assert.equal(pinnedFinalizer, '\x1b[0"q');
    assert.deepEqual([...contractOf(SED).pinnedFeeds], [pinnedSeed, '\x1b[?2J', pinnedFinalizer]);
    assert.deepEqual([...contractOf(SERA).pinnedFeeds], [pinnedSeed, '\x1b[1;1;1;5${', pinnedFinalizer]);
    assert.equal(contractOf(SED).rectangular, false);
    assert.equal(contractOf(SERA).rectangular, true);
    for (const contract of selectiveEraseContracts) {
        assert.equal(contract.pinnedPath, 'termless');
        assert.equal(contract.pinnedExpected, 'DECSCA-protected P survives while selective erase clears ABCD');
        assert.match(contract.appearanceScope, /pinned pixel path is not claimed/);
        assert.match(contract.terminalFixture.note, /never carries a verdict/);
    }
    assert.deepEqual(contractOf(SED).terminalFixture.writes, ['\x1B[1;1H\x1B[2K', 'ABCDE', '\x1B[?2J']);
    assert.deepEqual(contractOf(SERA).terminalFixture.writes, ['\x1B[1;1H', '\x1B[1;1;3;5${']);
});

test('the plan resets first (DECSCA off included), feeds the pinned bytes in order and restores the disposable screen last', () => {
    assert.ok(selectiveEraseReset.startsWith('\x1b[0"q'));
    for (const contract of selectiveEraseContracts) {
        const feeds = selectiveEraseFeeds(contract);
        assert.equal(feeds[0], selectiveEraseReset);
        const [seed, sequence, finalizer] = contract.pinnedFeeds;
        const at = (bytes, from = 0) => feeds.indexOf(bytes, from);
        assert.ok(at(seed) > 0 && at(sequence, at(seed)) > at(seed) && at(finalizer, at(sequence, at(seed))) > at(sequence, at(seed)));
        // the finalizer is fed after the erase read and before the next group, exactly as the pinned `finally` does
        const readIndex = contract.plan.findIndex((step, index) => step.read && contract.plan.slice(0, index).filter(item => item.read).length === contract.pinnedRead);
        assert.equal(contract.plan[readIndex + 1].feed, finalizer);
        assert.match(feeds.at(-1), /\x1b\[3J.*\x1b\[\?1049h$/);
        assert.equal(selectiveEraseReads(contract), contract.roles.length);
        assert.equal(contract.plan.filter(step => step.read).every(step => step.read === 'cells'), true);
        assert.doesNotThrow(() => validateSelectiveEraseFeeds(contract, feeds));
        assert.throws(() => validateSelectiveEraseFeeds(contract, [...feeds.slice(0, -1), 'x']), /do not match/);
        assert.equal(contract.roles[contract.initialRead], 'initial');
        assert.equal(contract.roles[contract.setupRead], 'seeded');
        assert.equal(contract.roles[contract.pinnedRead], 'erased');
        assert.equal(contract.roles[contract.finalRead], 'finalized');
    }
    // CSI 3J appears only as cleanup (the main-screen control entry and exit), never as an erase under test.
    for (const contract of selectiveEraseContracts) {
        for (const feed of selectiveEraseFeeds(contract).filter(item => item.includes('\x1b[3J'))) assert.match(feed, /\x1b\[\?1049[lh]/);
    }
});

test('controls are labeled per case: required bounds for DECSERA, observed other modes for DECSED', () => {
    assert.deepEqual(contractOf(SED).variants.map(item => item.role), ['mode0', 'mode1']);
    assert.deepEqual(contractOf(SERA).variants.map(item => item.role), ['rectInterior', 'rectWide', 'rectReversed']);
    assert.deepEqual(contractOf(SED).ordinary.bytes, '\x1b[2J');
    assert.deepEqual(contractOf(SERA).ordinary.bytes, '\x1b[1;1;1;5$z');
    assert.equal(contractOf(SERA).edgeErase.bytes, '\x1b[1;1;40;140${');
});

test('synthetic evidence is supported for both cases by the independent contract grader', () => {
    for (const caseId of selectiveEraseCaseIds) assert.deepEqual(syntheticSelectiveEraseGrade(caseId), { outcome: 'supported' });
});

test('P lost with valid seeds is unsupported (the pinned probe calls it inconclusive); protection survives only when stored', () => {
    for (const caseId of selectiveEraseCaseIds) {
        // everything erased, ordinary-erase style
        assert.match(note(caseId, reads => { reads.erased = { cells: [], cursor: [0, 6] }; }), /ignored protection|ordinary/);
        assert.equal(outcome(caseId, reads => { reads.erased = { cells: [], cursor: [0, 6] }; }), 'unsupported');
        // a survivor that reads UNprotected is not a protected survivor, even though the marker "remained"
        assert.equal(outcome(caseId, reads => { reads.erased = { cells: [open(0, 0, 'P')], cursor: [0, 6] }; }), 'unsupported');
        // a no-op erase
        assert.match(note(caseId, reads => { reads.erased = reads.seeded; }), /no-op/);
    }
});

test('the whole pinned seed must read back with its stored protection or the case is inconclusive', () => {
    for (const caseId of selectiveEraseCaseIds) {
        assert.equal(note(caseId, reads => { reads.seeded = { cells: seedCells().map(([row, col, spec]) => [row, col, spec, false]), cursor: [0, 6] }; }), pinnedSeedNote);
        assert.equal(note(caseId, reads => { reads.seeded = { cells: seedCells().map(([row, col, spec]) => [row, col, spec, true]), cursor: [0, 6] }; }), pinnedSeedNote);
        assert.equal(note(caseId, reads => { reads.seeded = { cells: seedCells().slice(0, 5), cursor: [0, 6] }; }), pinnedSeedNote);
        assert.equal(note(caseId, reads => { reads.seeded = { ...reads.seeded, cursor: [0, 5] }; }), pinnedSeedNote);
        assert.equal(outcome(caseId, reads => { reads.seeded = { cells: [], cursor: [0, 0] }; }), 'inconclusive');
    }
});

test('a lost outside Z (DECSERA) or an extra survivor is unsupported', () => {
    assert.equal(outcome(SERA, reads => { reads.erased = { cells: [guarded(0, 0, 'P')], cursor: [0, 6] }; }), 'unsupported');   // Z lost
    assert.equal(outcome(SERA, reads => { reads.erased = { cells: [...reads.erased.cells, open(0, 3, 'C')], cursor: [0, 6] }; }), 'unsupported');
    assert.equal(outcome(SED, reads => { reads.erased = { cells: [...reads.erased.cells, open(0, 5, 'Z')], cursor: [0, 6] }; }), 'unsupported');  // Z survives a DECSED
    assert.equal(outcome(SED, reads => { reads.erased = { cells: [guarded(0, 0, 'P'), guarded(0, 3, 'C')], cursor: [0, 6] }; }), 'unsupported');   // a new protected cell appeared
});

test('cursor, modes, finalizer and history damage is unsupported with the seed valid', () => {
    for (const caseId of selectiveEraseCaseIds) {
        assert.match(note(caseId, reads => { reads.erased = { ...reads.erased, cursor: [0, 0] }; }), /cursor moved/);
        assert.match(note(caseId, reads => { reads.erased = { ...reads.erased, buffer: { originMode: true } }; }), /stored mode/);
        assert.match(note(caseId, reads => { reads.finalized = { cells: [], cursor: [0, 6] }; }), /finalizer/);
        assert.match(note(caseId, reads => { reads.history = { ...reads.history, history: ['HIST-A'] }; }), /saved lines/);
        assert.match(note(caseId, reads => { reads.history = { ...reads.history, buffer: { originMode: true } }; }), /stored mode/);
    }
});

test('the ordinary erase control must erase the protected P: protection is not read-only text', () => {
    assert.match(note(SED, reads => { reads.ordinary = { cells: [guarded(0, 0, 'P')], cursor: [0, 6] }; }), /ordinary ED 2 control did not erase the protected P/);
    assert.match(note(SERA, reads => { reads.ordinary = { cells: [guarded(0, 0, 'P'), open(0, 5, 'Z')], cursor: [0, 6] }; }), /ordinary DECERA/);
    assert.equal(outcome(SED, reads => { reads.ordinary = { cells: [], cursor: [0, 0] }; }), 'unsupported');
});

test('DECSCA 0 changes only later writes: a reclassified existing cell or a protected later write fails the control', () => {
    for (const caseId of selectiveEraseCaseIds) {
        assert.match(note(caseId, reads => { reads.pen = { cells: [guarded(0, 0, 'A')], cursor: [0, 4] }; }), /protection-off control/);   // B lost
        assert.match(note(caseId, reads => { reads.pen = { cells: [guarded(0, 0, 'A'), guarded(0, 1, 'B'), open(0, 2, 'C')], cursor: [0, 4] }; }), /protection-off control/);
        assert.match(note(caseId, reads => { reads.penBefore = { cells: [guarded(0, 0, 'A'), guarded(0, 1, 'B'), guarded(0, 2, 'C'), open(0, 3, 'D')], cursor: [0, 4] }; }), /seed was not measured/);
        assert.equal(outcome(caseId, reads => { reads.penBefore = { cells: [], cursor: [0, 0] }; }), 'inconclusive');
    }
});

test('an overwrite takes the pen, and the pinned finalizer restores it', () => {
    for (const caseId of selectiveEraseCaseIds) {
        assert.match(note(caseId, reads => { reads.overwrite = { cells: [open(0, 0, 'X'), guarded(0, 1, 'B')], cursor: [0, 1] }; }), /overwrite control/);   // X survived
        assert.match(note(caseId, reads => { reads.overwrite = { cells: [], cursor: [0, 1] }; }), /overwrite control/);                                        // B lost
        assert.match(note(caseId, reads => { reads.restore = { cells: [guarded(0, 0, 'Q')], cursor: [0, 1] }; }), /restoration control/);
        assert.match(note(caseId, reads => { reads.restore = { cells: [], cursor: [0, 1] }; }), /restoration control/);
        assert.equal(note(caseId, reads => { reads.overwriteBefore = { cells: [guarded(0, 0, 'X'), guarded(0, 1, 'B')], cursor: [0, 1] }; }), 'The overwrite control seed was not measured, so a character written over a protected cell cannot be distinguished');
    }
});

test('full-screen and edge controls catch accidental clearing, partial erasure and cursor loss', () => {
    for (const caseId of selectiveEraseCaseIds) {
        const fill = fillSyntheticCells();
        assert.match(note(caseId, reads => { reads.fill = { cells: [], cursor: [20, 4] }; }), /full-screen fill control differs/);
        assert.match(note(caseId, reads => { reads.fill = { cells: reads.fillBefore.cells, cursor: [20, 4] }; }), /full-screen fill control differs/);
        assert.match(note(caseId, reads => { reads.fill = { ...reads.fill, cursor: [0, 0] }; }), /full-screen fill control differs/);
        assert.match(note(caseId, reads => { reads.fill = { cells: reads.fill.cells.filter(cell => cell[0] !== 39), cursor: [20, 4] }; }), /full-screen fill control differs/);
        assert.match(note(caseId, reads => { reads.edge = { cells: [], cursor: [39, 139] }; }), /edge control differs/);
        assert.match(note(caseId, reads => { reads.edge = { ...reads.edge, cursor: [0, 0] }; }), /edge control differs/);
        assert.match(note(caseId, reads => { reads.fillBefore = { cells: fill.slice(1), cursor: [20, 4] }; }), /fill control seed was not measured/);
        assert.match(note(caseId, reads => { reads.edgeBefore = { cells: fill.slice(1), cursor: [39, 139] }; }), /edge control seed was not measured/);
    }
});

test('DECSERA bounds controls are required: an interior rectangle spares its ends, a wider one reaches Z, a reversed one erases nothing', () => {
    assert.match(note(SERA, reads => { reads.rectInterior = { cells: [guarded(0, 0, 'P')], cursor: [0, 6] }; }), /interior-rectangle control differs/);
    assert.match(note(SERA, reads => { reads.rectInterior = { cells: reads.seeded.cells, cursor: [0, 6] }; }), /interior-rectangle control differs/);
    assert.match(note(SERA, reads => { reads.rectWide = { cells: [guarded(0, 0, 'P'), open(0, 5, 'Z')], cursor: [0, 6] }; }), /wider-rectangle control differs/);
    assert.match(note(SERA, reads => { reads.rectReversed = { cells: [guarded(0, 0, 'P')], cursor: [0, 6] }; }), /reversed-border control differs/);
    assert.match(note(SERA, reads => { reads.rectReversed = { ...reads.rectReversed, cursor: [0, 0] }; }), /reversed-border control differs/);
});

test('DECSED modes 0 and 1 are observed, never graded', () => {
    assert.deepEqual(grade(SED, reads => { reads.mode0 = { cells: [], cursor: [0, 0] }; reads.mode1 = { cells: seedCells(), cursor: [0, 3] }; }), { outcome: 'supported' });
    const contract = contractOf(SED);
    const observed = contract.observed(selectiveEraseObservation(contract, syntheticSelectiveEraseScreens(SED)));
    assert.match(observed.variants.policy, /observed, not graded/);
    assert.equal(observed.variants.mode0.text.startsWith('PAB'), true);
    assert.deepEqual(observed.variants.mode0.protectedColumns, [0]);
});

test('retained protected cells keep their stored bold tuple; background-color erase is observed and never required', () => {
    assert.match(note(SED, reads => { reads.attr = { cells: [guarded(0, 0, 'P')], cursor: [0, 6] }; }), /Retained cells lost/);
    assert.match(note(SERA, reads => { reads.attr = { cells: reads.attr.cells.map(([row, col, spec, prot]) => [row, col, { ...spec, attrs: [] }, prot]), cursor: [0, 6] }; }), /Retained cells lost/);
    // an erased blank that stays protected is not an erased cell
    assert.match(note(SED, reads => { reads.attr = { cells: [...reads.attr.cells, guarded(0, 1, ' ')], cursor: [0, 6] }; }), /Retained cells lost/);
    const contract = contractOf(SERA);
    const painted = count => id => {
        const reads = syntheticSelectiveEraseReads(id);
        const blue = { char: ' ', fg: { kind: 'default' }, bg: { kind: 'palette', index: 4 }, attrs: [], underlineStyle: 0, underlineColorIndex: -1, underlineArgb: null };
        reads.attr = { ...reads.attr, cells: [...reads.attr.cells, ...Array.from({ length: count }, (_, index) => [0, index + 1, blue, false])] };
        return reads;
    };
    const observe = count => selectiveEraseObservation(contract, syntheticSelectiveEraseScreens(SERA, paletteBinding(), painted(count)));
    // none, some and every erased cell (A-D) painted with the pen background: all supported, each described
    for (const [count, words] of [[0, 'erased cells keep the default background'], [1, 'mixed'], [4, 'erased cells carry the current background']]) {
        const observation = observe(count);
        assert.equal(gradeSelectiveEraseCase(contract, observation).outcome, 'supported');
        const brief = contract.observed(observation);
        assert.equal(brief.attributes.backgroundColorErase, words);
        assert.equal(brief.attributes.backgroundColorEraseDetail.paintedCells, count);
        assert.equal(brief.attributes.backgroundColorEraseDetail.erasedCells, 4);
    }
    // a blank outside the erased rectangle is not an erase: it is damage
    assert.match(note(SERA, reads => { reads.attr = { ...reads.attr, cells: [...reads.attr.cells, [0, 9, colorCell(' ', { bg: { kind: 'palette', index: 4 } }), false]] }; }), /Retained cells lost|outside the erased region/);
});

test('inconclusive when the measurement itself is not valid: geometry, reverse video, an unclean start', () => {
    for (const caseId of selectiveEraseCaseIds) {
        assert.equal(outcome(caseId, reads => { reads.initial = { cells: [open(0, 0, 'x')], cursor: [0, 1] }; }), 'inconclusive');
        assert.equal(outcome(caseId, reads => { reads.initial = { cells: [guarded(0, 0, ' ')], cursor: [0, 0] }; }), 'inconclusive');
    }
    const contract = contractOf(SED);
    const screens = syntheticSelectiveEraseScreens(SED);
    const observation = selectiveEraseObservation(contract, screens);
    observation.seeded = { ...observation.seeded, cols: 5 };
    assert.deepEqual(gradeSelectiveEraseCase(contract, observation), { outcome: 'inconclusive', note: pinnedColumnsNote });
    const small = selectiveEraseObservation(contract, screens);
    small.seeded = { ...small.seeded, rows: 30 };
    assert.equal(gradeSelectiveEraseCase(contract, small).outcome, 'inconclusive');
    const reverse = selectiveEraseObservation(contract, screens);
    assert.equal(gradeSelectiveEraseCase(contract, { ...reverse, reverseVideo: null }).outcome, 'inconclusive');
    assert.equal(gradeSelectiveEraseCase(contract, { ...reverse, reverseVideo: true }).outcome, 'inconclusive');
});

test('a screen without per-cell protection or complete buffer state cannot be graded at all', () => {
    const contract = contractOf(SED);
    const screens = syntheticSelectiveEraseScreens(SED);
    const stripped = screens.map(screen => ({ ...screen, cellDetails: screen.cellDetails.map(({ protected: _unused, ...run }) => run) }));
    assert.throws(() => selectiveEraseObservation(contract, stripped), /per-cell protection/);
    assert.throws(() => selectiveEraseObservation(contract, screens.map(screen => ({ ...screen, bufferState: undefined }))), /buffer state/);
    assert.throws(() => selectiveEraseObservation(contract, screens.slice(1)), /Missing selective-erase read captures/);
    assert.throws(() => gradeSelectiveEraseCase(contract, selectiveEraseObservation(contract, screens), 2), /Unknown selective-erase contracts revision/);
    assert.throws(() => gradeSelectiveEraseCase(contract, {}), /Missing erase.selective read captures/);
});

test('ink reports every protected blank and refuses a screen whose stored projections contradict each other', () => {
    const screen = syntheticSelectiveScreen({ cells: [guarded(0, 0, ' '), open(2, 3, 'x')], cursor: [0, 0] });
    assert.deepEqual(selectiveEraseInk(screen).map(cell => [cell.row, cell.col, cell.char, cell.protected]), [[0, 0, ' ', true], [2, 3, 'x', false]]);
    const clash = { ...screen, cellDetails: screen.cellDetails.map(run => ({ ...run, flags: run.flags | 1 })) };
    assert.throws(() => selectiveEraseInk(clash), /Contradictory|attribute/i);
});

test('the fixture exposes the same independent geometry as the contracts', () => {
    assert.equal(selectiveEraseFixture.fillCells.length, fillSyntheticCells().length);
    assert.deepEqual(selectiveEraseFixture.fillCells.filter(cell => cell[3]).map(cell => cell.slice(0, 2).join(',')),
        fillSyntheticCells().filter(cell => cell[3]).map(cell => cell.slice(0, 2).join(',')));
});
