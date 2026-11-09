import assert from 'node:assert/strict';
import { test } from 'node:test';
import { lineEditContracts, lineEditCaseIds, lineEditContractsVersion, lineEditSuiteId, lineEditNoncePrefix, lineEditModel,
    gradeLineEditCase, lineEditObservation, lineEditReset, lineEditFeeds, lineEditReads, validateLineEditFeeds, pinnedGeometryNote,
    lineEditFixture } from './line-edit-contracts.mjs';
import { syntheticLineEditReads, syntheticLineEditScreens, syntheticLineEditGrade, rowOf, rowsOfText, colorCell, paletteBinding,
    lineEditRows } from './fixtures/line-edit-evidence.mjs';

const IL = 'editing.insert-lines', DL = 'editing.delete-lines';
const byId = id => lineEditContracts.find(contract => contract.caseId === id);
/** Grade a synthetic case after editing its reads; `edit` mutates the per-role grids. */
const grade = (caseId, edit, binding) => syntheticLineEditGrade(caseId, binding, id => {
    const reads = syntheticLineEditReads(id);
    if (id === caseId && edit) edit(reads);
    return reads;
});
const outcome = (caseId, edit, binding) => grade(caseId, edit, binding).outcome;
const note = (caseId, edit) => grade(caseId, edit).note;
/** Replace a role's rows by texts, keeping its cursor and state. */
const texts = (reads, role, rows, cursor) => { reads[role] = { ...reads[role], rows: rowsOfText(rows), ...(cursor ? { cursor } : {}) }; };
const pad = (rows, count = lineEditRows) => [...rows, ...Array.from({ length: count - rows.length }, () => '')];

test('the contracts pin exactly the two verified feature ids, fixture bytes and the explicit-count spelling', () => {
    assert.deepEqual([...lineEditCaseIds], [IL, DL]);
    const seeds = ['\x1b[1;1H\x1b[2KAAAAA', '\x1b[2;1H\x1b[2KBBBBB', '\x1b[3;1H\x1b[2KCCCCC', '\x1b[4;1H\x1b[2KDDDDD'];
    assert.deepEqual(lineEditContracts.map(contract => [...contract.pinnedFeeds]), [
        [...seeds, '\x1b[2;1H', '\x1b[1L'], [...seeds, '\x1b[2;1H', '\x1b[1M']]);
    assert.deepEqual(lineEditContracts.map(contract => contract.operator), ['L', 'M']);
    assert.deepEqual(lineEditContracts.map(contract => contract.pinnedExpected), [
        'IL inserts a blank row 2 and shifts measured rows down while row 1 stays intact',
        'DL removes row 2 and shifts measured rows up while row 1 stays intact']);
    assert.equal(lineEditContractsVersion, 1);
    assert.equal(lineEditSuiteId, 'ntilde-owned-line-editing-graded-v1');
    assert.equal(lineEditNoncePrefix, 'NTILDE_LE_');
    for (const contract of lineEditContracts) {
        assert.match(contract.appearanceScope, /pixel path is capture-only/);
        assert.match(contract.terminalFixture.note, /never a verdict/);
        assert.equal(lineEditReads(contract), contract.roles.length);
        assert.doesNotThrow(() => validateLineEditFeeds(contract, lineEditFeeds(contract)));
        assert.throws(() => validateLineEditFeeds(contract, [...lineEditFeeds(contract).slice(0, -1), 'x']), /do not match/);
    }
});

test('the pinned fixture is replayed byte-exactly as four seed feeds, the cursor feed and the edit, each read where the pinned probe reads', () => {
    for (const contract of lineEditContracts) {
        const plan = contract.plan;
        assert.deepEqual([contract.beforeRead, contract.setupRead, contract.pinnedRead], [1, 2, 3]);
        assert.equal(plan[0].feed, lineEditReset);
        const first = plan.findIndex(step => step.feed === contract.pinnedFeeds[0]);
        // initial read, four seed feeds, the before read, the cursor feed, the setup read, the edit feed, the edited read
        assert.equal(plan[first - 1].read, 'cells');
        for (let index = 0; index < 4; index++) assert.equal(plan[first + index].feed, contract.pinnedFeeds[index]);
        assert.equal(plan[first + 4].read, 'cells');
        assert.equal(plan[first + 5].feed, '\x1b[2;1H');
        assert.equal(plan[first + 6].read, 'cells');
        assert.equal(plan[first + 7].feed, contract.sequence);
        assert.equal(plan[first + 8].read, 'cells');
        assert.equal(contract.roles.length, 20);
    }
});

test('count spellings stay distinct, labeled controls: omitted, zero, two and oversized, never the pinned explicit one', () => {
    for (const contract of lineEditContracts) {
        assert.deepEqual([...contract.others], ['omitted', 'explicit0', 'explicit2', 'oversized']);
        const op = contract.operator;
        assert.deepEqual(Object.values(contract.spellings).map(item => item.bytes), [`\x1b[${op}`, `\x1b[0${op}`, `\x1b[2${op}`, `\x1b[999${op}`]);
        assert.equal(contract.sequence, `\x1b[1${op}`);
    }
});

test('naming: scrolling, character-editing and erase sequences are never substituted for IL and DL', () => {
    for (const contract of lineEditContracts) {
        const own = new Set([lineEditReset, lineEditFixture.clear]);
        const allowed = new RegExp(`\\x1b\\[[\\d;]*[HAmr]|\\x1b\\[\\?6[hl]|\\x1b\\[2[KJ]|\\x1b\\[\\d*${contract.operator}`, 'g');
        for (const feed of lineEditFeeds(contract).filter(item => !own.has(item))) {
            assert.ok(!feed.replace(allowed, '').includes('\x1b['), JSON.stringify(feed));
        }
    }
});

test('the model is independent of the fixtures: IL and DL on a region, with clamping and cursor outside the region', () => {
    const rows = ['a', 'b', 'c', 'd', 'e', 'f'];
    assert.deepEqual(lineEditModel(rows, 'insert', 2, 1, 1, 4), ['a', 'b', '', 'c', 'd', 'f']);
    assert.deepEqual(lineEditModel(rows, 'delete', 2, 1, 1, 4), ['a', 'b', 'd', 'e', '', 'f']);
    assert.deepEqual(lineEditModel(rows, 'insert', 2, 99, 1, 4), ['a', 'b', '', '', '', 'f']);
    assert.deepEqual(lineEditModel(rows, 'delete', 5, 1, 1, 4), rows);
    assert.deepEqual(lineEditModel(rows, 'insert', 0, 1, 1, 4), rows);
});

test('a complete synthetic run is conclusively supported for both cases', () => {
    for (const id of lineEditCaseIds) assert.deepEqual(syntheticLineEditGrade(id), { outcome: 'supported' }, id);
});

test('no-op line editing is unsupported once the seed is proven', () => {
    for (const id of lineEditCaseIds) {
        const noop = reads => { reads.edited = { ...reads.setup }; };
        assert.equal(outcome(id, noop), 'unsupported', id);
        assert.match(note(id, noop), /no-op/);
    }
});

test('reversed movement is unsupported and named', () => {
    assert.match(note(IL, reads => texts(reads, 'edited', ['AAAAA', 'CCCCC', 'DDDDD'], [1, 0])), /match DL instead/);
    assert.match(note(DL, reads => texts(reads, 'edited', ['AAAAA', '', 'BBBBB', 'CCCCC', 'DDDDD'], [1, 0])), /match IL instead/);
});

test('wrong counts and wrong blank lines are unsupported', () => {
    assert.match(note(IL, reads => texts(reads, 'edited', ['AAAAA', '', '', 'BBBBB', 'CCCCC', 'DDDDD'], [1, 0])), /larger count/);
    assert.match(note(DL, reads => texts(reads, 'edited', ['AAAAA', 'DDDDD'], [1, 0])), /larger count/);
    // IL that inserts the blank on the wrong row, or leaves a non-blank copy in the new row.
    assert.equal(outcome(IL, reads => texts(reads, 'edited', ['AAAAA', 'BBBBB', '', 'CCCCC', 'DDDDD'], [1, 0])), 'unsupported');
    assert.equal(outcome(IL, reads => texts(reads, 'edited', ['AAAAA', 'BBBBB', 'BBBBB', 'CCCCC', 'DDDDD'], [1, 0])), 'unsupported');
    // DL that leaves the removed row's text at the bottom instead of a blank row.
    assert.equal(outcome(DL, reads => texts(reads, 'edited', ['AAAAA', 'CCCCC', 'DDDDD', 'DDDDD'], [1, 0])), 'unsupported');
});

test('retained-order corruption and the pinned five-cell blind spots are unsupported: the whole screen is compared', () => {
    assert.equal(outcome(IL, reads => texts(reads, 'edited', ['AAAAA', '', 'CCCCC', 'BBBBB', 'DDDDD'], [1, 0])), 'unsupported');
    assert.equal(outcome(DL, reads => texts(reads, 'edited', ['AAAAA', 'DDDDD', 'CCCCC'], [1, 0])), 'unsupported');
    // The pinned IL never reads row 5, which the insert shifts DDDDD into; the pinned DL never reads row 4.
    assert.equal(outcome(IL, reads => texts(reads, 'edited', ['AAAAA', '', 'BBBBB', 'CCCCC'], [1, 0])), 'unsupported');
    assert.equal(outcome(DL, reads => texts(reads, 'edited', ['AAAAA', 'CCCCC', 'DDDDD', '', 'stray'], [1, 0])), 'unsupported');
    // Text past the fifth column is outside the pinned five cells but inside the contract.
    assert.equal(outcome(IL, reads => texts(reads, 'edited', ['AAAAA', '', 'BBBBB', 'CCCCC', 'DDDDDXX'], [1, 0])), 'unsupported');
});

test('character insertion or deletion instead of line editing is unsupported and named', () => {
    assert.match(note(IL, reads => texts(reads, 'edited', ['AAAAA', ' BBBBB', 'CCCCC', 'DDDDD'], [1, 0])), /characters were edited instead of lines/);
    assert.match(note(DL, reads => texts(reads, 'edited', ['AAAAA', 'BBBB', 'CCCCC', 'DDDDD'], [1, 0])), /characters were edited instead of lines/);
});

test('whole-screen scrolling instead of editing at the cursor row is unsupported', () => {
    assert.match(note(IL, reads => texts(reads, 'edited', ['', 'AAAAA', 'BBBBB', 'CCCCC', 'DDDDD'], [1, 0])), /scrolled instead/);
    assert.equal(outcome(DL, reads => texts(reads, 'edited', ['BBBBB', 'CCCCC', 'DDDDD'], [1, 0])), 'unsupported');
});

test('the cursor must end at row 2, column 1; the pinned probe never reads it, so the contract is stricter', () => {
    for (const id of lineEditCaseIds) {
        assert.equal(outcome(id, reads => { reads.edited = { ...reads.edited, cursor: [1, 3] }; }), 'unsupported', id);
        assert.match(note(id, reads => { reads.edited = { ...reads.edited, cursor: [2, 0] }; }), /cursor drifted/);
        assert.equal(outcome(id, reads => { reads.bottom = { ...reads.bottom, cursor: [lineEditRows - 1, 0] }; }), 'unsupported', id);
    }
});

test('the cursor column after an edit away from column 1 is observed, not required', () => {
    for (const id of lineEditCaseIds) {
        assert.deepEqual(grade(id, reads => { reads.bottom = { ...reads.bottom, cursor: [lineEditRows - 2, 10] }; }), { outcome: 'supported' }, id);
        const contract = byId(id);
        const observed = contract.observed(lineEditObservation(contract, syntheticLineEditScreens(id)));
        assert.deepEqual([observed.cursorColumn.beforeColumn, observed.cursorColumn.afterColumn], [10, 0]);
    }
});

test('the full-height boundary: IL discards the last row and DL leaves a blank last row, with the top row unaffected', () => {
    const [top, next, last] = ['TOP-ROW---', 'NEXT-LAST-', 'LAST-ROW--'];
    const blank = Array.from({ length: 8 }, () => '');
    // IL that keeps the discarded last row, scrolls the whole screen, or drops the top row.
    assert.equal(outcome(IL, reads => texts(reads, 'bottom', [top, ...blank, '', next, last].slice(0, lineEditRows))), 'unsupported');
    assert.equal(outcome(IL, reads => texts(reads, 'bottom', pad(['', ...blank, '', next]))), 'unsupported');
    assert.equal(outcome(DL, reads => texts(reads, 'bottom', pad([top, ...blank, '', last, last]))), 'unsupported');
    assert.equal(outcome(DL, reads => texts(reads, 'bottom', [top, ...blank, '', next, ''].slice(0, lineEditRows))), 'unsupported');
    assert.match(note(IL, reads => texts(reads, 'bottom', pad([top]))), /full-height boundary/);
});

test('a scroll region confines the edit: rows outside it never change, and a cursor outside it edits nothing', () => {
    const region = ['AAAAAAAAAA', 'BBBBBBBBBB', 'CCCCCCCCCC', 'DDDDDDDDDD', 'EEEEEEEEEE', 'FFFFFFFFFF', 'GGGGGGGGGG'];
    // The edit leaks below the region (row 6 receives the discarded row) or ignores the margins altogether.
    assert.equal(outcome(IL, reads => texts(reads, 'region', ['AAAAAAAAAA', 'BBBBBBBBBB', 'CCCCCCCCCC', '', 'DDDDDDDDDD', 'EEEEEEEEEE', 'FFFFFFFFFF', 'GGGGGGGGGG'])), 'unsupported');
    assert.equal(outcome(DL, reads => texts(reads, 'region', ['AAAAAAAAAA', 'BBBBBBBBBB', 'CCCCCCCCCC', 'EEEEEEEEEE', 'FFFFFFFFFF', 'GGGGGGGGGG'])), 'unsupported');
    assert.match(note(IL, reads => texts(reads, 'region', ['AAAAAAAAAA', 'BBBBBBBBBB', 'CCCCCCCCCC', '', 'DDDDDDDDDD', 'EEEEEEEEEE', 'FFFFFFFFFF', 'GGGGGGGGGG'])), /scroll-region control differs \(region\)/);
    // Editing outside the region.
    for (const role of ['regionAbove', 'regionBelow']) {
        for (const id of lineEditCaseIds) {
            assert.equal(outcome(id, reads => texts(reads, role, id === IL ? ['AAAAAAAAAA', '', 'BBBBBBBBBB', 'CCCCCCCCCC'] : ['AAAAAAAAAA', 'CCCCCCCCCC', 'DDDDDDDDDD'])), 'unsupported', `${id} ${role}`);
        }
    }
    assert.deepEqual(grade(IL, reads => texts(reads, 'regionAbove', region)), { outcome: 'supported' });
});

test('origin mode inside the region is graded: the edit acts on the cursor row inside the margins', () => {
    assert.equal(outcome(IL, reads => texts(reads, 'origin', ['AAAAAAAAAA', 'BBBBBBBBBB', '', 'CCCCCCCCCC', 'DDDDDDDDDD', 'EEEEEEEEEE', 'FFFFFFFFFF', 'GGGGGGGGGG'])), 'unsupported');
    assert.match(note(DL, reads => texts(reads, 'origin', ['AAAAAAAAAA', 'BBBBBBBBBB', 'CCCCCCCCCC', 'DDDDDDDDDD', 'EEEEEEEEEE', '', 'FFFFFFFFFF', 'GGGGGGGGGG'])), /origin-mode/);
});

test('margins, origin mode and insert mode are observed state: a setup the App did not confirm is inconclusive', () => {
    for (const id of lineEditCaseIds) {
        // The region margins never took effect: the same rows would otherwise read as a whole-screen edit.
        assert.match(note(id, reads => { reads.regionBefore = { ...reads.regionBefore, state: {} }; }), /margins, origin mode or insert mode differ .*regionBefore/);
        assert.equal(outcome(id, reads => { reads.originBefore = { ...reads.originBefore, state: { margins: [2, 4] } }; }), 'inconclusive', id);
        assert.equal(outcome(id, reads => { reads.edited = { ...reads.edited, state: { margins: [1, lineEditRows - 1] } }; }), 'inconclusive', id);
        assert.equal(outcome(id, reads => { reads.edited = { ...reads.edited, state: { insert: true } }; }), 'inconclusive', id);
        assert.equal(outcome(id, reads => { reads.before = { ...reads.before, state: { origin: true } }; }), 'inconclusive', id);
    }
});

test('required attributes: moved cells keep their stored tuple and new blank lines carry no attribute but a background', () => {
    const bold = { attrs: ['bold'] }, redOnBlue = { fg: { kind: 'palette', index: 1 }, bg: { kind: 'palette', index: 4 } };
    const before = () => [rowOf('KEEP-TOP--'), rowOf('BOLD-ROW--', bold), rowOf('RED-ON-BLU', redOnBlue), rowOf('PLAIN-ROW-')];
    const blank = bg => Array.from({ length: 60 }, () => colorCell(' ', bg === undefined ? {} : { bg }));
    const literalBlue = { kind: 'literal', rgb: { r: 0, g: 0, b: 238 } };
    const attrIl = (rows, ...rest) => reads => { reads.attr = { ...reads.attr, rows: [...rows, ...rest] }; };
    const [keep, boldRow, red, plain] = before();
    // The moved bold row lost its bold, or the moved colored row lost its colors.
    assert.equal(outcome(IL, attrIl([keep, blank(literalBlue), rowOf('BOLD-ROW--'), red, plain])), 'unsupported');
    assert.equal(outcome(IL, attrIl([keep, blank(literalBlue), boldRow, rowOf('RED-ON-BLU'), plain])), 'unsupported');
    assert.equal(outcome(IL, attrIl([keep, blank(literalBlue), boldRow, rowOf('RED-ON-BLU', { fg: { kind: 'palette', index: 1 } }), plain])), 'unsupported');
    // The new blank line inherited the bold of the row it was inserted above, or an italic from nowhere.
    assert.equal(outcome(IL, attrIl([keep, Array.from({ length: 60 }, () => colorCell(' ', { attrs: ['bold'] })), boldRow, red, plain])), 'unsupported');
    assert.equal(outcome(IL, attrIl([keep, blank(literalBlue).map(cell => ({ ...cell, attrs: ['italic'] })), boldRow, red, plain])), 'unsupported');
    // A new blank line that kept text, or DL that moved the colored row without its tuple.
    assert.equal(outcome(IL, attrIl([keep, rowOf('BOLD-ROW--', bold), boldRow, red, plain])), 'unsupported');
    assert.equal(outcome(DL, reads => { reads.attr = { ...reads.attr, rows: [keep, rowOf('RED-ON-BLU'), plain, ...Array.from({ length: 8 }, () => []), blank(literalBlue)] }; }), 'unsupported');
    // The attribute cursor row must not move.
    assert.equal(outcome(IL, reads => { reads.attr = { ...reads.attr, cursor: [2, 0] }; }), 'unsupported');
});

test('background fill of new blank lines is observed, never required, and compared with the SGR 44 pen reference', () => {
    const bold = { attrs: ['bold'] }, redOnBlue = { fg: { kind: 'palette', index: 1 }, bg: { kind: 'palette', index: 4 } };
    const rows = bg => (bg === undefined ? [] : Array.from({ length: 60 }, () => colorCell(' ', { bg })));
    const observe = (caseId, bg, pen = null) => {
        const contract = byId(caseId);
        const reads = syntheticLineEditReads(caseId);
        const attrBefore = [rowOf('KEEP-TOP--'), rowOf('BOLD-ROW--', bold), rowOf('RED-ON-BLU', redOnBlue), rowOf('PLAIN-ROW-')];
        reads.attr = { ...reads.attr, rows: caseId === IL ? [attrBefore[0], rows(bg), attrBefore[1], attrBefore[2], attrBefore[3]]
            : [attrBefore[0], attrBefore[2], attrBefore[3], ...Array.from({ length: 8 }, () => []), rows(bg)] };
        if (pen) reads.pen = pen;
        const observation = lineEditObservation(contract, syntheticLineEditScreens(caseId, paletteBinding(), () => reads));
        return { outcome: gradeLineEditCase(contract, observation).outcome, fill: contract.observed(observation).attributes.backgroundFill };
    };
    const palette4 = { kind: 'palette', index: 4 }, literal = { kind: 'literal', rgb: { r: 0, g: 0, b: 238 } };
    assert.deepEqual(observe(IL, undefined), { outcome: 'supported', fill: 'new blank lines keep the default background' });
    assert.deepEqual(observe(IL, palette4), { outcome: 'supported', fill: 'new blank lines carry the current background across the full line' });
    assert.deepEqual(observe(DL, literal), { outcome: 'supported',
        fill: 'new blank lines carry the current background color across the full line, stored differently from the pen cell (palette index versus literal ARGB)' });
    assert.deepEqual(observe(IL, { kind: 'palette', index: 1 }), { outcome: 'supported', fill: 'new blank lines carry a different non-default background color than the current pen' });
    assert.equal(observe(IL, literal, { rows: [[colorCell('X')]], cursor: [0, 1] }).fill, 'unmeasured: the reference pen cell was not stored with a background');
});

test('a sibling count spelling that misbehaves is reported but never changes the pinned grade', () => {
    const wrong = reads => { texts(reads, 'explicit2', ['AAAAA', 'BBBBB', 'CCCCC', 'DDDDD'], [1, 0]); texts(reads, 'oversized', ['AAAAA', 'BBBBB'], [1, 0]); };
    for (const id of lineEditCaseIds) {
        assert.deepEqual(grade(id, wrong), { outcome: 'supported' }, id);
        const contract = byId(id);
        const reads = syntheticLineEditReads(id);
        wrong(reads);
        const observed = contract.observed(lineEditObservation(contract, syntheticLineEditScreens(id, paletteBinding(), () => reads)));
        assert.equal(observed.countControls.explicit0.matchesModel, true);
        assert.equal(observed.countControls.explicit0.sameAsPinned, true);
        assert.equal(observed.countControls.explicit2.matchesModel, false);
        assert.equal(observed.countControls.oversized.matchesModel, false);
        assert.equal(observed.countControls.oversized.count, 'oversized');
    }
});

test('history is observed: the stored scrollback count of every read is reported, and no history requirement is graded', () => {
    for (const id of lineEditCaseIds) {
        const contract = byId(id);
        const observed = contract.observed(lineEditObservation(contract, syntheticLineEditScreens(id)));
        assert.deepEqual(observed.history.scrollbackLines, [0]);
        assert.equal(observed.history.historyLines, 0);
        assert.match(observed.history.policy, /alternate screen/);
        // An implementation that pushed edited-out lines into history is a different criterion: the verdict does not change.
        assert.deepEqual(grade(id, reads => { reads.edited = { ...reads.edited, state: { scrollback: 2 } }; }), { outcome: 'supported' }, id);
    }
});

test('missing or invalid setup is inconclusive, never unsupported', () => {
    for (const id of lineEditCaseIds) {
        assert.equal(outcome(id, reads => texts(reads, 'before', ['AAAAA', 'BBBBB', 'CCCCC', 'DDDDX'])), 'inconclusive', id);
        assert.equal(outcome(id, reads => { reads.setup = { ...reads.setup, cursor: [1, 3] }; }), 'inconclusive', id);
        assert.equal(outcome(id, reads => { reads.setup = { ...reads.setup, cursor: [0, 0] }; }), 'inconclusive', id);
        assert.equal(outcome(id, reads => texts(reads, 'initial', ['x'])), 'inconclusive', id);
        assert.equal(outcome(id, reads => { reads.setup = { rows: [rowOf('AAAAA', { attrs: ['bold'] }), ...rowsOfText(['BBBBB', 'CCCCC', 'DDDDD'])], cursor: [1, 0] }; }), 'inconclusive', id);
        assert.match(note(id, reads => texts(reads, 'bottomBefore', ['TOP-ROW---'])), /full-height boundary control seed/);
        assert.match(note(id, reads => texts(reads, 'regionBefore', ['AAAAAAAAAA'])), /scroll-region control seed/);
        assert.match(note(id, reads => texts(reads, 'originBefore', ['AAAAAAAAAA'])), /scroll-region control seed was not measured \(originBefore\)/);
        assert.match(note(id, reads => { reads.attrBefore = { ...reads.attrBefore, rows: [rowOf('KEEP-TOP--')] }; }), /attribute control seed/);
        assert.match(note(id, reads => { reads.attrBefore = { ...reads.attrBefore, cursor: [2, 0] }; }), /attribute control seed/);
    }
});

test('DECSCNM not proven off is inconclusive', () => {
    const on = { ...paletteBinding(), reverseVideo: true };
    for (const id of lineEditCaseIds) assert.equal(syntheticLineEditGrade(id, on).outcome, 'inconclusive');
});

test('too small a screen is inconclusive with the contract geometry note', () => {
    for (const id of lineEditCaseIds) {
        const contract = byId(id);
        const observation = lineEditObservation(contract, syntheticLineEditScreens(id));
        observation.setup = { ...observation.setup, cols: 6 };
        assert.deepEqual(gradeLineEditCase(contract, observation), { outcome: 'inconclusive', note: pinnedGeometryNote });
        observation.setup = { ...observation.setup, cols: 60, rows: 9 };
        assert.equal(gradeLineEditCase(contract, observation).outcome, 'inconclusive');
    }
});

test('grader refuses missing groups and unknown revisions', () => {
    const contract = byId(IL);
    const observation = lineEditObservation(contract, syntheticLineEditScreens(IL));
    delete observation.attr;
    assert.throws(() => gradeLineEditCase(contract, observation), /Missing editing\.insert-lines read captures/);
    assert.throws(() => lineEditObservation(contract, []), /Missing line-edit read captures/);
    assert.throws(() => gradeLineEditCase(contract, lineEditObservation(contract, syntheticLineEditScreens(IL)), 2), /Unknown line-edit contracts revision 2/);
});

test('the observed record reports the exact before and after rows of the pinned fixture and every control', () => {
    const il = byId(IL), dl = byId(DL);
    const observedIl = il.observed(lineEditObservation(il, syntheticLineEditScreens(IL)));
    const observedDl = dl.observed(lineEditObservation(dl, syntheticLineEditScreens(DL)));
    assert.deepEqual(observedIl.before.text, ['AAAAA_____', 'BBBBB_____', 'CCCCC_____', 'DDDDD_____', '__________', '__________']);
    assert.deepEqual(observedIl.edited.text, ['AAAAA_____', '__________', 'BBBBB_____', 'CCCCC_____', 'DDDDD_____', '__________']);
    assert.deepEqual(observedDl.edited.text, ['AAAAA_____', 'CCCCC_____', 'DDDDD_____', '__________', '__________', '__________']);
    assert.deepEqual([observedIl.setup.cursor, observedIl.edited.cursor], [[1, 0], [1, 0]]);
    assert.deepEqual(observedIl.scrollRegion.margins, [2, 4]);
    assert.equal(observedIl.originMode.before.origin, true);
    assert.equal(observedIl.edited.origin, false);
    assert.deepEqual(observedIl.scrollRegion.after.text.slice(0, 7), ['AAAAAAAAAA', 'BBBBBBBBBB', 'CCCCCCCCCC', '__________', 'DDDDDDDDDD', 'FFFFFFFFFF', 'GGGGGGGGGG']);
    assert.deepEqual(observedDl.scrollRegion.after.text.slice(0, 7), ['AAAAAAAAAA', 'BBBBBBBBBB', 'CCCCCCCCCC', 'EEEEEEEEEE', '__________', 'FFFFFFFFFF', 'GGGGGGGGGG']);
    assert.deepEqual(observedIl.bottomBoundary.after.text, ['TOP-ROW---', '__________', '__________', 'NEXT-LAST-']);
    assert.deepEqual(observedDl.bottomBoundary.after.text, ['TOP-ROW---', '__________', 'LAST-ROW--', '__________']);
    assert.equal(observedIl.attributes.movedKeepTuple, true);
    assert.equal(observedIl.attributes.blankLinesBackgroundOnly, true);
    assert.deepEqual(observedIl.attributes.blankRows, [1]);
    assert.deepEqual(observedDl.attributes.blankRows, [lineEditRows - 1]);
    assert.equal(observedIl.attributes.backgroundFill, 'new blank lines carry the current background color across the full line, stored differently from the pen cell (palette index versus literal ARGB)');
});

test('a shifted or gapped attribute seed is inconclusive: the moved-cell reference must sit at its columns', () => {
    const bold = { attrs: ['bold'] };
    for (const id of lineEditCaseIds) {
        const shifted = reads => { reads.attrBefore = { ...reads.attrBefore, rows: reads.attrBefore.rows.map((row, index) => (index === 1 ? [colorCell(' '), ...rowOf('BOLD-ROW--', bold).slice(0, 9)] : row)) }; };
        assert.match(note(id, shifted), /attribute control seed/);
        const gapped = reads => { reads.attrBefore = { ...reads.attrBefore, rows: reads.attrBefore.rows.map((row, index) => (index === 0 ? rowOf('KEEP TOP--') : row)) }; };
        assert.equal(outcome(id, gapped), 'inconclusive', id);
    }
});

test('observation-only count reads never gate the verdict: odd margins there are reported, not graded', () => {
    for (const id of lineEditCaseIds) {
        const odd = reads => { for (const role of ['omitted', 'explicit0', 'explicit2', 'oversized']) reads[role] = { ...reads[role], state: { margins: [1, 5], insert: true } }; };
        assert.deepEqual(grade(id, odd), { outcome: 'supported' }, id);
        // The graded pinned read still gates.
        assert.equal(outcome(id, reads => { reads.edited = { ...reads.edited, state: { margins: [1, 5] } }; }), 'inconclusive', id);
    }
});
