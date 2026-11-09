import assert from 'node:assert/strict';
import { test } from 'node:test';
import { eraseAttrsRegionContracts, eraseAttrsRegionCaseIds, eraseAttrsRegionContractsVersion, eraseAttrsRegionSuiteId, eraseAttrsRegionNoncePrefix,
    eraseAttrsRegionObservation, eraseAttrsRegionFeeds, eraseAttrsRegionReads, gradeEraseAttrsRegionCase, validateEraseAttrsRegionFeeds,
    pinnedElSetupNote, pinnedEdSetupNote, contractGeometryNote, elSetup, elErase, elFinally, edSetup, edErase, edFinally } from './erase-attrs-region-contracts.mjs';
import { syntheticEraseAttrsRegionReads, syntheticEraseAttrsRegionScreens, syntheticEraseAttrsRegionGrade, blankRow, textCells, bufferOf,
    paletteBinding, colorCell } from './fixtures/erase-attrs-region-evidence.mjs';

const EL = 'erase.el-with-attrs', ED = 'erase.ed-scroll-region';
const green = { bg: { kind: 'palette', index: 2 } };
/** The pinned `ESC[r` leaves the erased screen's text untouched, so an edit of the ED erased read propagates to the restored read unless the edit sets that read itself. */
const propagateRestored = (id, reads, edit) => {
    const restored = reads.restored;
    edit(reads);
    if (id === 'erase.ed-scroll-region' && reads.restored === restored) reads.restored = { ...restored, cells: reads.erased.cells };
};
const grade = (caseId, edit, binding) => syntheticEraseAttrsRegionGrade(caseId, binding, id => {
    const reads = syntheticEraseAttrsRegionReads(id);
    if (id === caseId && edit) propagateRestored(id, reads, edit);
    return reads;
});
const contractOf = caseId => eraseAttrsRegionContracts.find(item => item.caseId === caseId);

test('the suite pins exactly the two registered ids, in order, with its identities', () => {
    assert.deepEqual([...eraseAttrsRegionCaseIds], [EL, ED]);
    assert.equal(eraseAttrsRegionContractsVersion, 1);
    assert.equal(eraseAttrsRegionSuiteId, 'ntilde-owned-erase-attrs-region-graded-v1');
    assert.equal(eraseAttrsRegionNoncePrefix, 'NTILDE_EA_');
    for (const contract of eraseAttrsRegionContracts) assert.equal(contract.probeId, contract.caseId);
});

test('the pinned fixture bytes are exactly the upstream feeds, byte for byte', () => {
    assert.equal(elSetup, '\x1B[42m\x1B[1;1HXXXXX\x1B[1;1H');
    assert.equal(elErase, '\x1B[K');
    assert.equal(elFinally, '\x1B[0m');
    assert.equal(edSetup, '\x1B[1;1HKEEP!\x1B[3;1HERASE\x1B[3;10r\x1B[3;1H');
    assert.equal(edErase, '\x1B[J');
    assert.equal(edFinally, '\x1B[r');
    assert.deepEqual([...contractOf(EL).pinnedFeeds], [elSetup, elErase, elFinally]);
    assert.deepEqual([...contractOf(ED).pinnedFeeds], [edSetup, edErase, edFinally]);
    // The pinned feeds occur in the plan in order, each exactly once, and the omitted parameter is never spelled out in them.
    for (const contract of eraseAttrsRegionContracts) {
        const feeds = eraseAttrsRegionFeeds(contract);
        let at = -1;
        for (const pinned of contract.pinnedFeeds) {
            const index = feeds.indexOf(pinned, at + 1);
            assert.ok(index > at, `${contract.caseId} plan carries ${JSON.stringify(pinned)} after position ${at}`);
            at = index;
        }
        assert.ok(!contract.pinnedFeeds.some(feed => /\x1b\[0[KJ]/.test(feed)), 'pinned fixtures use the omitted spelling');
    }
});

test('the roles, reads and plans are consistent and the pinned reads are the contracted groups', () => {
    for (const contract of eraseAttrsRegionContracts) {
        assert.equal(eraseAttrsRegionReads(contract), contract.roles.length);
        assert.equal(contract.roles[contract.initialRead], 'initial');
        assert.equal(contract.roles[contract.setupRead], 'setup');
        assert.equal(contract.roles[contract.pinnedRead], 'erased');
        assert.equal(contract.roles[contract.restoredRead], 'restored');
        assert.deepEqual([...contract.readsAtFeed].map(index => contract.roles[index]), ['initial', 'setup', 'erased', 'restored']);
        assert.doesNotThrow(() => validateEraseAttrsRegionFeeds(contract, eraseAttrsRegionFeeds(contract)));
        assert.throws(() => validateEraseAttrsRegionFeeds(contract, [...eraseAttrsRegionFeeds(contract).slice(0, -1), 'x']), /do not match/);
        assert.equal(contract.plan[0].feed, '\x1b[?5l\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[2J\x1b[H', 'every plan starts with the measured reset (margins and pen cleared)');
    }
    assert.deepEqual(contractOf(EL).roles, ['initial', 'setup', 'erased', 'restored', 'explicit0', 'fillBefore', 'fill', 'penBefore', 'penChanged', 'penResetBefore', 'penReset']);
    assert.deepEqual(contractOf(ED).roles, ['initial', 'setup', 'erased', 'restored', 'explicit0', 'fillBefore', 'fill']);
});

test('a conclusive run grades both cases supported', () => {
    assert.deepEqual(grade(EL), { outcome: 'supported' });
    assert.deepEqual(grade(ED), { outcome: 'supported' });
});

// ---- erase.el-with-attrs ------------------------------------------------------------------------------------------
test('EL: a no-op erase, a cell left holding text and a partial erase are unsupported', () => {
    assert.match(grade(EL, reads => { reads.erased = reads.setup; }).note, /no-op/);
    assert.equal(grade(EL, reads => { reads.erased = { cells: [...textCells(0, 'X', green), ...blankRow(0, green, 1)], cursor: [0, 0] }; }).outcome, 'unsupported');
    assert.equal(grade(EL, reads => { reads.erased = { cells: [...blankRow(0, green, 0, 5), ...textCells(0, 'XXXXX', green).slice(1)], cursor: [0, 0] }; }).outcome, 'unsupported');
});

test('EL: clearing to the default background or to a different background is unsupported, never supported', () => {
    const defaulted = grade(EL, reads => { reads.erased = { cells: [], cursor: [0, 0] }; });
    assert.equal(defaulted.outcome, 'unsupported');
    assert.match(defaulted.note, /default/);
    const wrong = grade(EL, reads => { reads.erased = { cells: blankRow(0, { bg: { kind: 'palette', index: 4 } }), cursor: [0, 0] }; });
    assert.equal(wrong.outcome, 'unsupported');
    assert.match(wrong.note, /differs/);
});

test('EL: the pinned criterion compares RESOLVED RGB, so an erased blank stored as a literal ARGB still matches a palette seed', () => {
    const { r, g, b } = { r: 0, g: 205, b: 0 };
    const literal = grade(EL, reads => { reads.erased = { cells: blankRow(0, { bg: { kind: 'literal', rgb: { r, g, b } } }), cursor: [0, 0] }; });
    assert.deepEqual(literal, { outcome: 'supported' }, 'the same resolved color in another representation is supported');
    const near = grade(EL, reads => { reads.erased = { cells: blankRow(0, { bg: { kind: 'literal', rgb: { r, g, b: 1 } } }), cursor: [0, 0] }; });
    assert.equal(near.outcome, 'unsupported', 'a one-step different RGB is a different resolved color');
    const [screens] = [syntheticEraseAttrsRegionScreens(EL, paletteBinding(), id => {
        const reads = syntheticEraseAttrsRegionReads(id);
        reads.erased = { cells: blankRow(0, { bg: { kind: 'literal', rgb: { r, g, b } } }), cursor: [0, 0] };
        return reads;
    })];
    const observation = contractOf(EL).observed(eraseAttrsRegionObservation(contractOf(EL), screens));
    assert.equal(observation.background.resolvedMatchesBefore, true);
    assert.equal(observation.background.storedRepresentationMatches, false, 'the representation difference is observed, not required');
    assert.equal(observation.background.before.bgIndex, 2);
    assert.equal(observation.background.after.bgIndex, -1);
    assert.equal(observation.background.after.source, 'literal');
    assert.equal(observation.background.erasedCells, 140);
});

test('EL: only the pinned cell (0,0) carries the background requirement; the rest of the erased row is observed', () => {
    const mixed = grade(EL, reads => { reads.erased = { cells: [...blankRow(0, green, 0, 100), ...blankRow(0, { bg: { kind: 'palette', index: 4 } }, 100)], cursor: [0, 0] }; });
    assert.equal(mixed.outcome, 'supported', 'a tail painted differently is not a pinned or contracted requirement');
    const reads = syntheticEraseAttrsRegionReads(EL);
    reads.erased = { cells: [...blankRow(0, green, 0, 100), ...blankRow(0, { bg: { kind: 'palette', index: 4 } }, 100)], cursor: [0, 0] };
    const observed = contractOf(EL).observed(eraseAttrsRegionObservation(contractOf(EL), syntheticEraseAttrsRegionScreens(EL, undefined, () => reads)));
    assert.equal(observed.background.sameResolvedAsBefore, 100);
    assert.equal(observed.background.paintedCells, 140);
});

test('EL: the current-pen controls are observed and never change the grade', () => {
    assert.equal(grade(EL, reads => { reads.penChanged = { cells: [], cursor: [0, 0] }; reads.penReset = { cells: blankRow(0, green), cursor: [0, 0] }; }).outcome, 'supported');
    const screens = syntheticEraseAttrsRegionScreens(EL);
    const observed = contractOf(EL).observed(eraseAttrsRegionObservation(contractOf(EL), screens));
    assert.equal(observed.penControls.penChanged.seeded, true);
    assert.equal(observed.penControls.penChanged.background.after.bgIndex, 4, 'the fill follows the pen active at erase time');
    assert.equal(observed.penControls.penChanged.background.resolvedMatchesBefore, false);
    assert.equal(observed.penControls.penReset.background.after, null, 'with the pen reset the erased cell carries the default background');
    assert.equal(observed.penControls.penReset.seeded, true);
});

test('EL: an accidental whole-screen clear is unsupported even though the pinned cell reads correctly', () => {
    const cleared = grade(EL, reads => { reads.fill = { cells: reads.erased.cells, cursor: [0, 0] }; });
    assert.equal(cleared.outcome, 'unsupported');
    assert.match(cleared.note, /whole-screen/);
    const drifted = grade(EL, reads => { reads.fill = { cells: [...reads.fill.cells.slice(0, 140), [5, 0, colorCell('Z')]], cursor: [0, 0] }; });
    assert.equal(drifted.outcome, 'unsupported');
});

test('EL: a moved cursor or a changed mode is unsupported', () => {
    assert.match(grade(EL, reads => { reads.erased = { ...reads.erased, cursor: [0, 5] }; }).note, /cursor moved/);
    assert.match(grade(EL, reads => { reads.erased = { ...reads.erased, buffer: bufferOf({ insertMode: true }) }; }).note, /mode/);
    assert.match(grade(EL, reads => { reads.erased = { ...reads.erased, buffer: bufferOf({ scrollTop: 2, scrollBottom: 9 }) }; }).note, /margin/);
});

test('EL: a cursor moved by the erase stays unsupported: the restoration witness is positioned by its own feed, not by the erase result', () => {
    const moved = grade(EL, reads => { reads.erased = { ...reads.erased, cursor: [0, 5] }; });
    assert.equal(moved.outcome, 'unsupported');
    assert.match(moved.note, /cursor moved/);
    assert.ok(contractOf(EL).plan.some(step => step.feed === '\x1b[1;1HW'), 'the witness is written at an explicitly positioned (0,0)');
    assert.deepEqual(contractOf(EL).plan.filter(step => step.feed).map(step => step.feed).slice(4, 6), [elFinally, '\x1b[1;1HW']);
});

test('EL: an invalid or incomplete setup, wrong geometry or reverse video is inconclusive, never supported or unsupported', () => {
    assert.deepEqual(grade(EL, reads => { reads.setup = { cells: textCells(0, 'XXXXX'), cursor: [0, 0] }; }), { outcome: 'inconclusive', note: pinnedElSetupNote });
    assert.equal(grade(EL, reads => { reads.setup = { cells: textCells(0, 'XXXX', green), cursor: [0, 0] }; }).outcome, 'inconclusive');
    assert.equal(grade(EL, reads => { reads.setup = { cells: textCells(0, 'XXXXX', green), cursor: [0, 1] }; }).outcome, 'inconclusive');
    assert.equal(grade(EL, reads => { reads.setup = { cells: textCells(0, 'XXXXX', { bg: { kind: 'palette', index: 4 } }), cursor: [0, 0] }; }).outcome, 'inconclusive', 'the seed must be the measured green');
    assert.equal(grade(EL, reads => { reads.setup = { cells: textCells(0, 'XXXXX', { ...green, attrs: ['bold'] }), cursor: [0, 0] }; }).outcome, 'inconclusive');
    assert.equal(grade(EL, reads => { reads.initial = { cells: textCells(3, 'LEFT'), cursor: [0, 0] }; }).outcome, 'inconclusive');
    assert.equal(grade(EL, reads => { reads.fillBefore = { cells: reads.setup.cells, cursor: [0, 0] }; }).outcome, 'inconclusive', 'the full-screen control must be seeded');
    assert.equal(grade(EL, undefined, { ...paletteBinding(), reverseVideo: true }).outcome, 'inconclusive');
    const stale = grade(EL, reads => { reads.erased = { cells: [], cursor: [0, 0] }; reads.setup = { cells: [], cursor: [0, 0] }; });
    assert.equal(stale.outcome, 'inconclusive', 'an invalid setup is never graded as unsupported');
});

test('EL: an incomplete restoration (the pinned finally feed left a pen) is inconclusive', () => {
    const pen = grade(EL, reads => { reads.restored = { cells: [[0, 0, colorCell('W', green)], ...blankRow(0, green, 1)], cursor: [0, 1] }; });
    assert.equal(pen.outcome, 'inconclusive');
    assert.match(pen.note, /restore the pen/);
    assert.equal(grade(EL, reads => { reads.restored = { cells: [[0, 0, colorCell('W', { attrs: ['bold'] })]], cursor: [0, 1] }; }).outcome, 'inconclusive');
    assert.equal(grade(EL, reads => { reads.restored = { cells: [], cursor: [0, 1] }; }).outcome, 'inconclusive', 'no witness at all');
});

// ---- erase.ed-scroll-region ---------------------------------------------------------------------------------------
const keep = () => textCells(0, 'KEEP!');
const margins = { scrollTop: 2, scrollBottom: 9 };
test('ED: a no-op, an erased preceding row, a retained target and a wrong-direction erase are unsupported', () => {
    assert.match(grade(ED, reads => { reads.erased = reads.setup; }).note, /no-op/);
    assert.match(grade(ED, reads => { reads.erased = { cells: [], cursor: [2, 0], buffer: margins }; }).note, /preceding row was erased/);
    assert.match(grade(ED, reads => { reads.erased = { cells: [...keep(), ...textCells(2, 'ERASE')], cursor: [2, 0], buffer: margins }; }).outcome, /unsupported/);
    // ED 1 from (2,0): the rows above and the cursor cell go, so KEEP! is gone and "RASE" remains.
    assert.equal(grade(ED, reads => { reads.erased = { cells: textCells(2, 'RASE').map(([row, col, spec]) => [row, col + 1, spec]), cursor: [2, 0], buffer: margins }; }).outcome, 'unsupported');
});

test('ED: failures propagate through the restoration read: a no-op or wrong erase stays unsupported, never masked as inconclusive restoration', () => {
    const full = { scrollTop: 0, scrollBottom: 39 };
    const noop = grade(ED, reads => { reads.erased = reads.setup; reads.restored = { cells: reads.setup.cells, cursor: [0, 0], buffer: full }; });
    assert.equal(noop.outcome, 'unsupported');
    assert.match(noop.note, /no-op/);
    const wrong = grade(ED, reads => { reads.erased = { cells: [], cursor: [2, 0], buffer: margins }; reads.restored = { cells: [], cursor: [0, 0], buffer: full }; });
    assert.equal(wrong.outcome, 'unsupported');
    assert.match(wrong.note, /preceding row was erased/);
    const stray = grade(ED, reads => { reads.erased = { cells: [...keep(), [20, 4, colorCell('Z')]], cursor: [2, 0], buffer: margins }; reads.restored = { cells: [...keep(), [20, 4, colorCell('Z')]], cursor: [0, 0], buffer: full }; });
    assert.equal(stray.outcome, 'unsupported');
    // The restoration itself still must not disturb the erased screen.
    assert.equal(grade(ED, reads => { reads.restored = { cells: [...keep(), [20, 4, colorCell('Z')]], cursor: [0, 0] }; }).outcome, 'inconclusive');
});

test('ED: erasing only part of the target or stray ink anywhere is unsupported (the contract compares the whole screen)', () => {
    assert.equal(grade(ED, reads => { reads.erased = { cells: [...keep(), ...textCells(2, 'ERASE').slice(3)], cursor: [2, 0], buffer: margins }; }).outcome, 'unsupported');
    assert.equal(grade(ED, reads => { reads.erased = { cells: [...keep(), [20, 4, colorCell('Z')]], cursor: [2, 0], buffer: margins }; }).outcome, 'unsupported');
    assert.equal(grade(ED, reads => { reads.erased = { cells: [...keep()].map(([row, col, spec]) => [row, col, colorCell(spec.char, { attrs: ['bold'] })]), cursor: [2, 0], buffer: margins }; }).outcome, 'unsupported', 'KEEP! must keep its neutral attributes');
});

test('ED: a moved cursor, changed margins or changed modes are unsupported; the margins are read, never inferred', () => {
    assert.match(grade(ED, reads => { reads.erased = { cells: keep(), cursor: [0, 0], buffer: margins }; }).note, /cursor moved/);
    assert.match(grade(ED, reads => { reads.erased = { cells: keep(), cursor: [2, 0] }; }).note, /margins/, 'margins reset during the erase');
    assert.match(grade(ED, reads => { reads.erased = { cells: keep(), cursor: [2, 0], buffer: { scrollTop: 2, scrollBottom: 8 } }; }).note, /margins/);
    assert.match(grade(ED, reads => { reads.erased = { cells: keep(), cursor: [2, 0], buffer: { ...margins, insertMode: true } }; }).note, /mode/);
    assert.match(grade(ED, reads => { reads.erased = { cells: keep(), cursor: [2, 0], buffer: { ...margins, originMode: true } }; }).note, /mode/);
    assert.match(grade(ED, reads => { reads.erased = { cells: keep(), cursor: [2, 0], buffer: margins, history: ['A', 'B', 'C'] }; }).note, /scrollback/);
});

test('ED: an invalid setup is inconclusive: wrong seed, wrong cursor, margins not configured, origin mode on', () => {
    assert.deepEqual(grade(ED, reads => { reads.setup = { cells: [...keep(), ...textCells(2, 'ERAS')], cursor: [2, 0], buffer: margins }; }), { outcome: 'inconclusive', note: pinnedEdSetupNote });
    assert.equal(grade(ED, reads => { reads.setup = { ...reads.setup, cursor: [2, 1] }; }).outcome, 'inconclusive');
    assert.equal(grade(ED, reads => { reads.setup = { ...reads.setup, buffer: {} }; }).outcome, 'inconclusive', 'the margins were not set');
    assert.equal(grade(ED, reads => { reads.setup = { ...reads.setup, buffer: { ...margins, scrollBottom: 8 } }; }).outcome, 'inconclusive', 'the margins differ from CSI 3;10 r');
    assert.equal(grade(ED, reads => { reads.setup = { ...reads.setup, buffer: { ...margins, originMode: true } }; }).outcome, 'inconclusive');
    assert.equal(grade(ED, reads => { reads.initial = { cells: [], cursor: [0, 0], buffer: margins }; }).outcome, 'inconclusive', 'margins left over from an earlier read');
    assert.equal(grade(ED, undefined, { ...paletteBinding(), reverseVideo: true }).outcome, 'inconclusive');
});

test('ED: incomplete restoration (margins still set, or the stored text disturbed) is inconclusive even when the erase itself looks right', () => {
    const leftover = grade(ED, reads => { reads.restored = { cells: keep(), cursor: [0, 0], buffer: margins }; });
    assert.equal(leftover.outcome, 'inconclusive');
    assert.match(leftover.note, /restore the margins/);
    assert.equal(grade(ED, reads => { reads.restored = { cells: [], cursor: [0, 0] }; }).outcome, 'inconclusive');
    assert.equal(grade(ED, reads => { reads.restored = { cells: keep(), cursor: [0, 0], buffer: { scrollTop: 0, scrollBottom: 38 } }; }).outcome, 'inconclusive');
});

test('ED: where ED stopped relative to the margins is an observation, never a grade, and states no standards claim', () => {
    const observe = edit => {
        const reads = syntheticEraseAttrsRegionReads(ED);
        edit?.(reads);
        return contractOf(ED).observed(eraseAttrsRegionObservation(contractOf(ED), syntheticEraseAttrsRegionScreens(ED, undefined, () => reads)));
    };
    const unclipped = observe();
    assert.equal(unclipped.boundary.seeded, true);
    assert.equal(unclipped.boundary.erasedInsideRegion, true);
    assert.equal(unclipped.boundary.erasedBelowBottomMargin, true);
    assert.equal(unclipped.boundary.keptAboveCursor, true);
    assert.equal(unclipped.boundary.partialRows, 0);
    assert.deepEqual(unclipped.boundary.margins, [2, 9]);
    assert.match(unclipped.boundary.policy, /observed, not graded, no standards claim/);
    // A terminal that kept every row below the bottom margin is recorded differently and graded identically.
    const clippedReads = syntheticEraseAttrsRegionReads(ED);
    const rows = [0, 1, ...Array.from({ length: 30 }, (_, index) => 10 + index)];
    const markers = syntheticEraseAttrsRegionReads(ED).fillBefore.cells.filter(([row]) => rows.includes(row));
    const clipped = observe(reads => { reads.fill = { cells: markers, cursor: [2, 0], buffer: margins }; });
    assert.equal(clipped.boundary.erasedBelowBottomMargin, false);
    assert.equal(clipped.boundary.erasedInsideRegion, true);
    assert.equal(grade(ED, reads => { reads.fill = { cells: markers, cursor: [2, 0], buffer: margins }; }).outcome, 'supported');
    assert.equal(grade(ED, reads => { reads.fill = { cells: [], cursor: [9, 9] }; reads.fillBefore = { cells: [], cursor: [0, 0] }; }).outcome, 'supported', 'an unseeded boundary control is not a grade input');
    assert.equal(clippedReads.setup.buffer.scrollTop, 2);
});

test('ED: the boundary observation inspects every cell of a row, so a partial erase is neither reported as erased nor as kept', () => {
    const observe = edit => {
        const reads = syntheticEraseAttrsRegionReads(ED);
        edit(reads);
        return contractOf(ED).observed(eraseAttrsRegionObservation(contractOf(ED), syntheticEraseAttrsRegionScreens(ED, undefined, () => reads))).boundary;
    };
    // ED erased only column 0 of every row from the cursor: the remaining marker text and the tail marker are still there.
    const partial = observe(reads => {
        reads.fill = { cells: reads.fillBefore.cells.filter(([row, col]) => row < 2 || col > 0), cursor: [2, 0], buffer: margins };
    });
    assert.equal(partial.erasedInsideRegion, false);
    assert.equal(partial.erasedBelowBottomMargin, false);
    assert.equal(partial.partialRows, 38, 'rows 2-39 each lost one cell');
    assert.deepEqual(partial.rowsKept, { first: 0, last: 1, count: 2 }, 'only the two untouched rows are whole');
    // A row that lost its tail marker only is also partial.
    const tail = observe(reads => { reads.fill = { cells: reads.fillBefore.cells.filter(([row, col]) => !(row === 5 && col === 139)), cursor: [2, 0], buffer: margins }; });
    assert.equal(tail.partialRows, 1);
    assert.equal(tail.erasedInsideRegion, false);
    assert.equal(tail.rowsKept.count, 39);
});

test('ED: the explicit-0 spelling is a labeled observation and never changes the grade', () => {
    assert.equal(grade(ED, reads => { reads.explicit0 = { cells: [], cursor: [2, 0], buffer: margins }; }).outcome, 'supported');
    const reads = syntheticEraseAttrsRegionReads(ED);
    reads.explicit0 = { cells: [], cursor: [2, 0], buffer: margins };
    const observed = contractOf(ED).observed(eraseAttrsRegionObservation(contractOf(ED), syntheticEraseAttrsRegionScreens(ED, undefined, () => reads)));
    assert.equal(observed.spellingControls.explicit0.sameAsPinned, false);
    assert.match(observed.spellingControls.policy, /observed, not graded/);
    assert.equal(observed.restoration.marginsRestored, true);
    assert.deepEqual(observed.setup.margins, [2, 9]);
});

// ---- shared -------------------------------------------------------------------------------------------------------
test('the grader refuses a missing read group, an unknown revision and a wrong geometry', () => {
    for (const contract of eraseAttrsRegionContracts) {
        const screens = syntheticEraseAttrsRegionScreens(contract.caseId);
        assert.throws(() => eraseAttrsRegionObservation(contract, screens.slice(1)), /Missing erase-attributes read captures/);
        const observation = eraseAttrsRegionObservation(contract, screens);
        assert.throws(() => gradeEraseAttrsRegionCase(contract, observation, 2), /Unknown erase-attributes contracts revision 2/);
        assert.throws(() => gradeEraseAttrsRegionCase(contract, { ...observation, setup: undefined }), /Missing .* read captures/);
        assert.equal(gradeEraseAttrsRegionCase(contract, { ...observation, setup: { ...observation.setup, rows: 24 } }).note, contractGeometryNote);
    }
});

test('stored-detail and attribute projections that contradict each other are refused rather than graded', () => {
    const screens = syntheticEraseAttrsRegionScreens(EL);
    const tampered = structuredClone(screens);
    tampered[1].cellDetails[0] = { ...tampered[1].cellDetails[0], flags: tampered[1].cellDetails[0].flags | 0x01 };
    assert.throws(() => eraseAttrsRegionObservation(contractOf(EL), tampered), /Contradictory stored cell projections|Resolved|stored/);
});

test('a stale palette binding (resolved colors no longer matching the binding they claim) is refused', () => {
    const screens = syntheticEraseAttrsRegionScreens(EL);
    const stale = structuredClone(screens);
    const other = paletteBinding();
    other.ansiPaletteArgb = [...other.ansiPaletteArgb];
    other.ansiPaletteArgb[2] = 0xff123456;
    stale[1].colorBinding = other;
    assert.throws(() => eraseAttrsRegionObservation(contractOf(EL), stale), /resolved-color|Resolved/i);
});
