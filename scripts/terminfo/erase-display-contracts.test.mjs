import assert from 'node:assert/strict';
import { test } from 'node:test';
import { eraseDisplayContracts, eraseDisplayCaseIds, eraseDisplayContractsVersion, eraseDisplaySuiteId, eraseDisplayNoncePrefix,
    gradeEraseDisplayCase, eraseDisplayObservation, eraseDisplayReset, eraseDisplayFeeds, eraseDisplayReads, validateEraseDisplayFeeds,
    pinnedGeometryNote, pinnedFixtureNote, contractGeometryNote, pinnedSetupFor } from './erase-display-contracts.mjs';
import { syntheticEraseDisplayReads, syntheticEraseDisplayScreens, syntheticEraseDisplayGrade, survivors, erasedBy, textCells, colorCell,
    paletteBinding, historySeed } from './fixtures/erase-display-evidence.mjs';

const BELOW = 'erase.screen.below', ABOVE = 'erase.screen.above', ALL = 'erase.screen.all';
const byId = id => eraseDisplayContracts.find(contract => contract.caseId === id);
const seed = () => [...textCells(0, 'AAAAA'), ...textCells(20, 'BBBBB'), ...textCells(39, 'CCCCC')];
const fill = () => Array.from({ length: 40 }, (_, row) => [...textCells(row, `ROW${String(row).padStart(2, '0')}-ABCD`), [row, 139, colorCell('|')]]).flat();
const grade = (caseId, edit, binding) => syntheticEraseDisplayGrade(caseId, binding, id => {
    const reads = syntheticEraseDisplayReads(id);
    if (id === caseId && edit) edit(reads);
    return reads;
});
const outcome = (caseId, edit, binding) => grade(caseId, edit, binding).outcome;
const note = (caseId, edit) => grade(caseId, edit).note;
const at = (cells, mode, cursor) => ({ cells: survivors(cells, mode, cursor), cursor });

test('the contracts pin exactly the three verified feature ids, fixture bytes and explicit spellings', () => {
    assert.deepEqual([...eraseDisplayCaseIds], [BELOW, ABOVE, ALL]);
    const setup = '\x1b[1;1HAAAAA\x1b[21;1HBBBBB\x1b[40;1HCCCCC\x1b[21;3H';
    assert.equal(pinnedSetupFor(40), setup);
    assert.equal(pinnedSetupFor(8), '\x1b[1;1HAAAAA\x1b[5;1HBBBBB\x1b[8;1HCCCCC\x1b[5;3H');
    assert.deepEqual(eraseDisplayContracts.map(contract => [...contract.pinnedFeeds]), [[setup, '\x1b[0J'], [setup, '\x1b[1J'], [setup, '\x1b[2J']]);
    assert.deepEqual(eraseDisplayContracts.map(contract => contract.spelling), ['explicit0', 'explicit1', 'explicit2']);
    assert.deepEqual(eraseDisplayContracts.map(contract => [...contract.pinnedCells]), [
        ['AAAAA', 'BB   ', '     '], ['     ', '   BB', 'CCCCC'], ['     ', '     ', '     ']]);
    assert.deepEqual(eraseDisplayContracts.map(contract => contract.pinnedExpected), [
        'rows 0,20,39: AAAAA/BB   /     ; cursor preserved', 'rows 0,20,39:      /   BB/CCCCC; cursor preserved', 'rows 0,20,39:      /     /     ; cursor preserved']);
    assert.equal(eraseDisplayContractsVersion, 1);
    assert.equal(eraseDisplaySuiteId, 'ntilde-owned-erase-display-graded-v1');
    assert.equal(eraseDisplayNoncePrefix, 'NTILDE_ED_');
    for (const contract of eraseDisplayContracts) {
        assert.match(contract.appearanceScope, /pixel capture path is capture-only/);
        assert.match(contract.terminalFixture.note, /capture-only/);
        assert.equal(eraseDisplayReads(contract), contract.roles.length);
        assert.doesNotThrow(() => validateEraseDisplayFeeds(contract, eraseDisplayFeeds(contract)));
        assert.throws(() => validateEraseDisplayFeeds(contract, [...eraseDisplayFeeds(contract).slice(0, -1), 'x']), /do not match/);
    }
});

test('the pinned fixture is replayed byte-exactly as setup then erase, read between and after, and the pinned reads are the first three groups', () => {
    for (const contract of eraseDisplayContracts) {
        assert.deepEqual([contract.initialRead, contract.setupRead, contract.pinnedRead], [0, 1, 2]);
        assert.deepEqual(contract.roles.slice(0, 3), ['initial', 'setup', 'erased']);
        const plan = contract.plan;
        const setupAt = plan.findIndex(step => step.feed === contract.pinnedFeeds[0]);
        assert.equal(plan[setupAt + 1].read, 'cells');
        assert.equal(plan[setupAt + 2].feed, contract.pinnedFeeds[1]);
        assert.equal(plan[setupAt + 3].read, 'cells');
        assert.equal(contract.others.length, 3);
        assert.ok(!contract.others.includes(contract.spelling));
    }
});

test('spellings stay distinct: the pinned fixtures are explicit, and the omitted spelling is a labeled control only', () => {
    assert.deepEqual([...byId(BELOW).others], ['omitted', 'explicit1', 'explicit2']);
    assert.deepEqual([...byId(ABOVE).others], ['omitted', 'explicit0', 'explicit2']);
    assert.deepEqual([...byId(ALL).others], ['omitted', 'explicit0', 'explicit1']);
    for (const contract of eraseDisplayContracts) {
        assert.notEqual(contract.sequence, '\x1b[J');
        const feeds = eraseDisplayFeeds(contract).join('|');
        for (const bytes of ['\x1b[J', '\x1b[0J', '\x1b[1J', '\x1b[2J']) assert.ok(feeds.includes(bytes), JSON.stringify(bytes));
    }
});

test('naming and scope: CSI 3J is never an erase under test, only the cleanup of the disposable main screen', () => {
    const cleanup = eraseDisplayContracts[0].plan.filter(step => step.feed?.includes('\x1b[3J')).map(step => step.feed);
    for (const contract of eraseDisplayContracts) {
        assert.ok(!/^erase\.(line|display|characters)/.test(contract.caseId));
        assert.notEqual(contract.caseId, 'erase.screen.scrollback');
        const withThree = contract.plan.filter(step => step.feed?.includes('\x1b[3J')).map(step => step.feed);
        assert.equal(withThree.length, 2, 'exactly the main-screen entry and leave cleanups mention ED 3');
        assert.ok(withThree[0].startsWith('\x1b[?1049l') && withThree[1].endsWith('\x1b[?1049h'));
        for (const feed of eraseDisplayFeeds(contract)) {
            if (feed === eraseDisplayReset || cleanup.includes(feed)) continue;
            assert.ok(!feed.includes('\x1b[3J') && !/\x1b\[\?[0-9]*J|\x1b\[[0-9]*[KXPL@M]/.test(feed.replace(eraseDisplayReset, '')), JSON.stringify(feed));
        }
    }
});

test('a complete synthetic run is conclusively supported for all three cases', () => {
    for (const id of eraseDisplayCaseIds) assert.deepEqual(syntheticEraseDisplayGrade(id), { outcome: 'supported' }, id);
});

test('the oracle in the fixtures is independent: it erases the cursor cell in both directions', () => {
    assert.equal(erasedBy(0, [20, 2], 20, 2), true);
    assert.equal(erasedBy(0, [20, 2], 20, 1), false);
    assert.equal(erasedBy(1, [20, 2], 20, 2), true);
    assert.equal(erasedBy(1, [20, 2], 20, 3), false);
    assert.equal(erasedBy(2, [20, 2], 0, 0), true);
    assert.equal(erasedBy(0, [20, 0], 19, 139), false);
    assert.equal(erasedBy(1, [20, 139], 21, 0), false);
});

test('no-op erasure is unsupported once the seed is proven', () => {
    for (const id of eraseDisplayCaseIds) {
        assert.equal(outcome(id, reads => { reads.erased = { ...reads.setup }; }), 'unsupported', id);
        assert.match(note(id, reads => { reads.erased = { ...reads.setup }; }), /no-op/);
    }
});

test('wrong-direction and whole-screen erasure are unsupported and named', () => {
    assert.match(note(BELOW, reads => { reads.erased = at(seed(), 1, [20, 2]); }), /ED 1 instead of ED 0/);
    assert.match(note(ABOVE, reads => { reads.erased = at(seed(), 0, [20, 2]); }), /ED 0 instead of ED 1/);
    assert.match(note(BELOW, reads => { reads.erased = at(seed(), 2, [20, 2]); }), /ED 2 instead of ED 0: the whole screen was cleared/);
    assert.match(note(ABOVE, reads => { reads.erased = at(seed(), 2, [20, 2]); }), /whole screen was cleared/);
    assert.match(note(ALL, reads => { reads.erased = at(seed(), 0, [20, 2]); }), /ED 0 instead of ED 2/);
    assert.equal(outcome(BELOW, reads => { reads.wide = at(fill(), 2, [20, 4]); }), 'unsupported');
    assert.equal(outcome(ABOVE, reads => { reads.wide = at(fill(), 0, [20, 4]); }), 'unsupported');
    assert.equal(outcome(ALL, reads => { reads.wide = at(fill(), 1, [20, 4]); }), 'unsupported');
});

test('cursor inclusion boundary: the cursor cell is erased by ED 0 and ED 1, and a one-cell error is unsupported', () => {
    const keepsCursorCell = (cells, cursor) => cells.filter(([row, col]) => !(row === 20 && col === 2) || true).filter(([row, col]) => !erasedBy(row === cursor[0] && col === cursor[1] ? 9 : 9, cursor, row, col));
    assert.ok(keepsCursorCell);
    // ED 0 that spares the cursor cell: BBB remains.
    assert.equal(outcome(BELOW, reads => { reads.erased = { cells: [...survivors(seed(), 0, [20, 2]), ...textCells(20, 'BBB')], cursor: [20, 2] }; }), 'unsupported');
    // ED 1 that spares the cursor cell: BBB remains.
    assert.equal(outcome(ABOVE, reads => { reads.erased = { cells: [...survivors(seed(), 1, [20, 2]), [20, 2, colorCell('B')]], cursor: [20, 2] }; }), 'unsupported');
    // ED 0 that erases one cell too many; ED 1 one too many.
    assert.equal(outcome(BELOW, reads => { reads.erased = { cells: [...textCells(0, 'AAAAA'), ...textCells(20, 'B')], cursor: [20, 2] }; }), 'unsupported');
    assert.equal(outcome(ABOVE, reads => { reads.erased = { cells: [[20, 4, colorCell('B')], ...textCells(39, 'CCCCC')], cursor: [20, 2] }; }), 'unsupported');
    // The fill row: first-column cursor with ED 0 must erase the whole row; last-column cursor with ED 1 must erase the whole row.
    assert.equal(outcome(BELOW, reads => { reads.edge = { cells: survivors(fill(), 0, [20, 1]), cursor: [20, 0] }; }), 'unsupported');
    assert.equal(outcome(ABOVE, reads => { reads.edge = { cells: survivors(fill(), 1, [20, 138]), cursor: [20, 139] }; }), 'unsupported', 'the last-column cell must be erased too');
    assert.equal(outcome(BELOW, reads => { reads.edge = { cells: survivors(fill(), 0, [20, 1]), cursor: [20, 0] }; }), 'unsupported', 'the first-column cell must be erased too');
    assert.match(note(BELOW, reads => { reads.edge = { cells: survivors(fill(), 0, [20, 1]), cursor: [20, 0] }; }), /boundary control differs/);
});

test('retained-cell corruption and unrelated-row changes are unsupported', () => {
    assert.equal(outcome(BELOW, reads => { reads.erased = { cells: [...textCells(0, 'AAAAA'), ...textCells(20, 'BX')], cursor: [20, 2] }; }), 'unsupported');
    assert.equal(outcome(ABOVE, reads => { reads.erased = { cells: [...textCells(20, 'BBBBB').slice(3).map(([r, c, s]) => [r, c, s]), ...textCells(39, 'CCCCX')], cursor: [20, 2] }; }), 'unsupported');
    assert.equal(outcome(BELOW, reads => { reads.erased = { cells: [...survivors(seed(), 0, [20, 2]), [30, 5, colorCell('z')]], cursor: [20, 2] }; }), 'unsupported');
    // Text beyond the five cells the pinned probe reads, and a row-marker change in the fill control.
    assert.equal(outcome(BELOW, reads => { reads.erased = { cells: [...survivors(seed(), 0, [20, 2]), [0, 90, colorCell('z')]], cursor: [20, 2] }; }), 'unsupported');
    assert.equal(outcome(ABOVE, reads => { reads.wide = { cells: [...survivors(fill(), 1, [20, 4]), [39, 9, colorCell('Q')]], cursor: [20, 4] }; }), 'unsupported');
    assert.equal(outcome(BELOW, reads => { reads.wide = { cells: survivors(fill(), 0, [20, 4]).filter(([row]) => row !== 3), cursor: [20, 4] }; }), 'unsupported');
});

test('the cursor must stay at its seeded cell; homing it is named, and every control cursor is checked', () => {
    for (const id of eraseDisplayCaseIds) {
        assert.equal(outcome(id, reads => { reads.erased = { ...reads.erased, cursor: [20, 5] }; }), 'unsupported', id);
        assert.match(note(id, reads => { reads.erased = { ...reads.erased, cursor: [0, 0] }; }), /cursor was homed/);
        assert.match(note(id, reads => { reads.erased = { ...reads.erased, cursor: [21, 2] }; }), /cursor moved/);
    }
    assert.equal(outcome(BELOW, reads => { reads.wide = { ...reads.wide, cursor: [20, 5] }; }), 'unsupported');
    assert.equal(outcome(ALL, reads => { reads.edge = { ...reads.edge, cursor: [0, 0] }; }), 'unsupported');
    assert.equal(outcome(ABOVE, reads => { reads.bce = { ...reads.bce, cursor: [0, 0] }; }), 'unsupported');
    assert.equal(outcome(BELOW, reads => { reads.history = { ...reads.history, cursor: [0, 0] }; }), 'unsupported');
});

test('unaffected modes: the margins, origin, wrap, insert, alternate-screen flags and cursor visibility must not change', () => {
    for (const buffer of [{ originMode: true }, { autoWrap: false }, { insertMode: true }, { pendingWrap: true }, { scrollTop: 2 }, { scrollBottom: 30 }]) {
        assert.equal(outcome(BELOW, reads => { reads.erased = { ...reads.erased, buffer }; }), 'unsupported', JSON.stringify(buffer));
    }
    assert.equal(outcome(ABOVE, reads => { reads.erased = { ...reads.erased, cursorVisible: false }; }), 'unsupported');
    assert.equal(outcome(ALL, reads => { reads.erased = { ...reads.erased, buffer: { alternateScreen: false } }; }), 'unsupported');
    assert.match(note(ALL, reads => { reads.erased = { ...reads.erased, cursorVisible: false }; }), /stored mode/);
    assert.equal(outcome(BELOW, reads => { reads.wide = { ...reads.wide, buffer: { originMode: true } }; }), 'unsupported');
});

test('mode preservation is checked in the attribute and history controls too, while saved-line growth stays reportable', () => {
    for (const id of eraseDisplayCaseIds) {
        assert.equal(outcome(id, reads => { reads.bce = { ...reads.bce, buffer: { insertMode: true } }; }), 'unsupported', id);
        assert.equal(outcome(id, reads => { reads.bce = { ...reads.bce, cursorVisible: false }; }), 'unsupported', id);
        assert.match(note(id, reads => { reads.bce = { ...reads.bce, buffer: { originMode: true } }; }), /attribute control/);
        assert.equal(outcome(id, reads => { reads.history = { ...reads.history, buffer: { insertMode: true } }; }), 'unsupported', id);
        assert.equal(outcome(id, reads => { reads.history = { ...reads.history, buffer: { scrollTop: 3 } }; }), 'unsupported', id);
        assert.equal(outcome(id, reads => { reads.history = { ...reads.history, cursorVisible: false }; }), 'unsupported', id);
        assert.match(note(id, reads => { reads.history = { ...reads.history, buffer: { autoWrap: false } }; }), /main screen/);
    }
    // Saved-line growth (a terminal that pushes the viewport into history) is the explicitly permitted difference.
    assert.equal(outcome(ALL, reads => { reads.history = { ...reads.history, history: [...historySeed, 'pushed'] }; }), 'supported');
});

test('every erased position counts in the background observation, including the seeded letters inside the erased region', () => {
    const { record } = observeOf(ALL, reads => {
        const full = painted(2, bluePen, false);
        // The 15 seeded letters become default-background blanks: the other 5,585 erased cells stay painted.
        reads.bce = { cells: full.cells, cursor: [20, 2] };
    });
    assert.equal(record.attributes.backgroundColorEraseDetail.erasedCells, 5600);
    assert.equal(record.attributes.backgroundColorErase, 'mixed');
    assert.equal(observeOf(ALL, reads => { reads.bce = painted(2); }).record.attributes.backgroundColorErase, 'erased cells carry the current background');
    assert.deepEqual([BELOW, ABOVE, ALL].map(id => observeOf(id, () => {}).record.attributes.backgroundColorEraseDetail.erasedCells), [2798, 2803, 5600]);
});

test('required attributes: retained cells keep bold and no stored color; an erased cell must read blank', () => {
    assert.equal(outcome(BELOW, reads => { reads.bce = { cells: survivors(seed(), 0, [20, 2]), cursor: [20, 2] }; }), 'unsupported');
    assert.equal(outcome(ABOVE, reads => { reads.bce = { cells: [...reads.bce.cells.slice(0, -1), [39, 4, colorCell('C', { attrs: ['bold', 'italic'] })]], cursor: [20, 2] }; }), 'unsupported');
    assert.equal(outcome(BELOW, reads => { reads.bce = { cells: [...reads.bce.cells, [30, 5, colorCell('X', { attrs: ['bold'] })]], cursor: [20, 2] }; }), 'unsupported');
    assert.equal(outcome(ALL, reads => { reads.bce = { cells: [[0, 0, colorCell('A', { attrs: ['bold'] })]], cursor: [20, 2] }; }), 'unsupported');
});

const bluePen = { kind: 'palette', index: 4 };
const painted = (mode, bg = bluePen, includeSeeds = true) => {
    const blanks = [];
    for (let row = 0; row < 40; row++) for (let col = 0; col < 140; col++) {
        if (erasedBy(mode, [20, 2], row, col) && (includeSeeds || !seed().some(([r, c]) => r === row && c === col))) blanks.push([row, col, colorCell(' ', { bg })]);
    }
    const bold = survivors(seed(), mode, [20, 2]).map(([r, c, s]) => [r, c, colorCell(s.char, { attrs: ['bold'] })]);
    return { cells: [...bold, ...blanks], cursor: [20, 2] };
};
const observeOf = (caseId, edit) => {
    const contract = byId(caseId);
    const reads = syntheticEraseDisplayReads(caseId);
    edit(reads);
    const observation = eraseDisplayObservation(contract, syntheticEraseDisplayScreens(caseId, paletteBinding(), () => reads));
    return { outcome: gradeEraseDisplayCase(contract, observation).outcome, record: contract.observed(observation) };
};

test('background-color erase is observed, never required, and its resolved color is kept apart from its stored representation', () => {
    for (const [id, mode] of [[BELOW, 0], [ABOVE, 1], [ALL, 2]]) {
        const { outcome: verdict, record } = observeOf(id, reads => { reads.bce = painted(mode); });
        assert.equal(verdict, 'supported', id);
        assert.equal(record.attributes.backgroundColorErase, 'erased cells carry the current background', id);
        assert.equal(record.attributes.backgroundColorEraseDetail.paintedCells, record.attributes.backgroundColorEraseDetail.erasedCells);
    }
    assert.equal(observeOf(BELOW, () => {}).record.attributes.backgroundColorErase, 'erased cells keep the default background');
    assert.equal(observeOf(BELOW, reads => { reads.bce = painted(0, { kind: 'palette', index: 1 }); }).record.attributes.backgroundColorErase,
        'erased cells carry a different non-default background color than the current pen');
    const literal = observeOf(BELOW, reads => { reads.bce = painted(0, { kind: 'literal', rgb: { r: 0, g: 0, b: 238 } }); });
    assert.equal(literal.outcome, 'supported');
    assert.equal(literal.record.attributes.backgroundColorErase,
        'erased cells carry the current background color, stored differently from the pen cell (palette index versus literal ARGB)');
    assert.equal(observeOf(BELOW, reads => { reads.bce = painted(0); reads.pen = { cells: [[0, 0, colorCell('X')]], cursor: [0, 1] }; }).record.attributes.backgroundColorErase,
        'unmeasured: the reference pen cell was not stored with a background');
    const mixed = observeOf(BELOW, reads => { const full = painted(0); reads.bce = { ...full, cells: full.cells.filter(([row]) => row !== 30) }; });
    assert.equal(mixed.record.attributes.backgroundColorErase, 'mixed');
    // A painted blank outside the erased region is damage, not background-color erase.
    assert.equal(observeOf(BELOW, reads => { const full = painted(0); reads.bce = { ...full, cells: [...full.cells, [3, 3, colorCell(' ', { bg: bluePen })]] }; }).outcome, 'unsupported');
});

test('history preservation: viewport erasure that removes or rewrites saved lines is unsupported; ED 2 is not ED 3', () => {
    for (const id of eraseDisplayCaseIds) {
        assert.equal(outcome(id, reads => { reads.history = { ...reads.history, history: [] }; }), 'unsupported', id);
        assert.equal(outcome(id, reads => { reads.history = { ...reads.history, history: ['HIST-A', 'HIST-X', 'HIST-C', ''] }; }), 'unsupported', id);
        assert.match(note(id, reads => { reads.history = { ...reads.history, history: ['HIST-B', 'HIST-C', ''] }; }), /must not remove scrollback \(that is ED 3\)/);
    }
    // Added lines (a terminal that pushes the viewport into history) keep the old ones and are reported, not failed.
    const grown = observeOf(ALL, reads => { reads.history = { ...reads.history, history: [...historySeed, 'pushed'] }; });
    assert.equal(grown.outcome, 'supported');
    assert.equal(grown.record.history.linesAdded, 1);
    assert.equal(grown.record.history.linesKept, true);
    const plain = observeOf(ALL, () => {});
    assert.deepEqual([plain.record.history.seeded, plain.record.history.linesKept, plain.record.history.linesAdded], [true, true, 0]);
});

test('a sibling spelling that misbehaves is reported but never changes the pinned grade', () => {
    const wrong = reads => { reads.explicit1 = { ...reads.explicit1, cells: seed() }; reads.explicit2 = at(seed(), 0, [20, 2]); };
    assert.deepEqual(grade(BELOW, wrong), { outcome: 'supported' });
    const { record } = observeOf(BELOW, wrong);
    assert.equal(record.spellingControls.omitted.matchesEcma48, true);
    assert.equal(record.spellingControls.omitted.sameAsPinned, true);
    assert.equal(record.spellingControls.explicit1.matchesEcma48, false);
    assert.equal(record.spellingControls.explicit2.matchesEcma48, false);
    assert.match(record.spellingControls.policy, /pinned fixtures use only the explicit spelling/);
});

test('missing or invalid setup is inconclusive, never unsupported', () => {
    for (const id of eraseDisplayCaseIds) {
        assert.deepEqual(grade(id, reads => { reads.setup = { cells: [...textCells(0, 'AAAAX')], cursor: [20, 2] }; }), { outcome: 'inconclusive', note: pinnedFixtureNote }, id);
        assert.deepEqual(grade(id, reads => { reads.setup = { ...reads.setup, cursor: [20, 3] }; }), { outcome: 'inconclusive', note: pinnedFixtureNote }, id);
        assert.deepEqual(grade(id, reads => { reads.setup = { ...reads.setup, cursor: [19, 2] }; }), { outcome: 'inconclusive', note: pinnedFixtureNote }, id);
        assert.equal(outcome(id, reads => { reads.setup = { cells: [], cursor: [20, 2] }; }), 'inconclusive', id);
        assert.equal(outcome(id, reads => { reads.setup = { cells: seed().map(([r, c, s]) => [r, c, colorCell(s.char, { attrs: ['bold'] })]), cursor: [20, 2] }; }), 'inconclusive', id);
        assert.equal(outcome(id, reads => { reads.setup = { cells: [...seed(), [10, 10, colorCell('x')]], cursor: [20, 2] }; }), 'inconclusive', id);
        assert.equal(outcome(id, reads => { reads.initial = { cells: [[5, 5, colorCell('x')]], cursor: [0, 0] }; }), 'inconclusive', id);
    }
});

test('invalid controls make the grade inconclusive: every seed must be measured', () => {
    for (const id of eraseDisplayCaseIds) {
        assert.match(note(id, reads => { reads.wideBefore = { ...reads.wideBefore, cursor: [0, 2] }; }), /fill control seed/);
        assert.match(note(id, reads => { reads.wideBefore = { cells: fill().slice(1), cursor: [20, 4] }; }), /fill control seed/);
        assert.match(note(id, reads => { reads.edgeBefore = { ...reads.edgeBefore, cursor: [20, 77] }; }), /boundary control seed/);
        assert.match(note(id, reads => { reads.bceBefore = { cells: seed(), cursor: [20, 2] }; }), /attribute control seed/);
        assert.match(note(id, reads => { reads.historyBefore = { ...reads.historyBefore, main: false }; }), /history control seed/);
        assert.match(note(id, reads => { reads.historyBefore = { ...reads.historyBefore, history: [] }; }), /history control seed/);
        assert.match(note(id, reads => { reads.historyBefore = { ...reads.historyBefore, history: ['x', 'y', 'z'] }; }), /history control seed/);
        assert.equal(outcome(id, reads => { reads.historyBefore = { ...reads.historyBefore, cursor: [20, 3] }; }), 'inconclusive', id);
    }
});

test('DECSCNM not proven off is inconclusive', () => {
    assert.equal(syntheticEraseDisplayGrade(BELOW, { ...paletteBinding(), reverseVideo: true }).outcome, 'inconclusive');
});

test('a geometry other than the contracted 40x140 screen is inconclusive, and a tiny one carries the pinned geometry note', () => {
    const contract = byId(BELOW);
    const observation = eraseDisplayObservation(contract, syntheticEraseDisplayScreens(BELOW));
    assert.deepEqual(gradeEraseDisplayCase(contract, { ...observation, setup: { ...observation.setup, cols: 120 } }), { outcome: 'inconclusive', note: contractGeometryNote });
    assert.deepEqual(gradeEraseDisplayCase(contract, { ...observation, setup: { ...observation.setup, rows: 30 } }), { outcome: 'inconclusive', note: contractGeometryNote });
    assert.deepEqual(gradeEraseDisplayCase(contract, { ...observation, setup: { ...observation.setup, cols: 5 } }), { outcome: 'inconclusive', note: pinnedGeometryNote });
});

test('grader refuses missing groups, missing buffer state and unknown revisions', () => {
    const contract = byId(BELOW);
    const observation = eraseDisplayObservation(contract, syntheticEraseDisplayScreens(BELOW));
    delete observation.history;
    assert.throws(() => gradeEraseDisplayCase(contract, observation), /Missing erase\.screen\.below read captures/);
    assert.throws(() => eraseDisplayObservation(contract, []), /Missing erase-in-display read captures/);
    assert.throws(() => gradeEraseDisplayCase(contract, eraseDisplayObservation(contract, syntheticEraseDisplayScreens(BELOW)), 2), /Unknown erase-in-display contracts revision 2/);
    const stripped = syntheticEraseDisplayScreens(BELOW).map(screen => { const { bufferState, ...rest } = screen; return rest; });
    assert.throws(() => eraseDisplayObservation(contract, stripped));
});

test('the observed record keeps the pinned rows, the full-screen rows and the saved lines', () => {
    const { record } = observeOf(BELOW, () => {});
    assert.deepEqual(record.pinnedRows, [0, 20, 39]);
    assert.deepEqual(record.erased.text.map(row => row.slice(0, 5)), ['AAAAA', 'BB___', '_____']);
    assert.deepEqual(record.setup.cursor, [20, 2]);
    assert.equal(record.modesUnchanged, true);
    assert.equal(record.fullScreen.after.text[19], 'ROW19-ABCD');
    assert.equal(record.fullScreen.after.text[20], 'ROW2______');
    assert.equal(record.fullScreen.after.text[21], '__________');
    assert.equal(record.boundary.after.text[20], '__________');
    assert.equal(record.setup.scrollback.alternateScreen, true);
    assert.equal(record.history.before.scrollback.alternateScreen, false);
    assert.deepEqual(record.history.before.scrollback.history, [...historySeed]);
});
