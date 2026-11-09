import assert from 'node:assert/strict';
import { test } from 'node:test';
import { eraseCharsHistoryCaseIds, eraseCharsHistoryContractsVersion, eraseCharsHistoryNoncePrefix, eraseCharsHistoryReset, eraseCharsHistorySuiteId,
    eraseCharsHistoryMinimum, makeEraseCharsHistoryContracts, pinnedEraseCharsHistoryNotes, eraseCharsHistoryFeeds, eraseCharsHistoryReads,
    validateEraseCharsHistoryFeeds, gradeEraseCharsHistoryCase, eraseCharsHistoryObservation } from './erase-chars-history-contracts.mjs';
import { createScrollbackModel, feedScrollbackModel, scrollbackModelState } from './scrollback-model.mjs';
import { syntheticEraseCharsHistoryContracts, syntheticEraseCharsHistoryGrade, syntheticEraseCharsHistoryScreens, echStates, historyStates,
    erasedRow, shiftedRow, echRows, echCols, colorCell, plainCell } from './fixtures/erase-chars-history-evidence.mjs';

const contracts = syntheticEraseCharsHistoryContracts();
const [ech, ed3] = contracts;
const grade = (contract, edit) => syntheticEraseCharsHistoryGrade(contract, undefined, edit);
const only = (role, change) => (name, state) => { if (name === role) change(state); return state; };
const bold = char => colorCell(char, { attrs: ['bold'] });
const pen = { kind: 'palette', index: 4 };
const setLine = (state, row, text) => { state.lines[row] = text; };

test('identity: exactly the two pinned ids in suite order, version 1, nonce and reset', () => {
    assert.deepEqual([...eraseCharsHistoryCaseIds], ['erase.character', 'erase.screen.scrollback']);
    assert.deepEqual(contracts.map(contract => contract.caseId), [...eraseCharsHistoryCaseIds]);
    assert.equal(eraseCharsHistoryContractsVersion, 1);
    assert.equal(eraseCharsHistorySuiteId, 'ntilde-owned-erase-chars-history-graded-v1');
    assert.equal(eraseCharsHistoryNoncePrefix, 'NTILDE_EH_');
    // The reset leaves the main screen, clears the screen and the saved lines, and homes the cursor: the baseline each plan reads back.
    assert.equal(eraseCharsHistoryReset, '\x1b[?1049l\x1b[?5l\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[2J\x1b[3J\x1b[H');
    assert.deepEqual({ ...eraseCharsHistoryMinimum }, { rows: 12, cols: 48 });
});

test('the pinned fixtures are exactly the pinned bytes: ECH `ABCDE ESC[1G` then `ESC[3X`; ED 3 after rows+2 CRLF', () => {
    assert.deepEqual([...ech.pinnedFeeds], ['ABCDE\x1b[1G', '\x1b[3X']);
    assert.deepEqual([...ed3.pinnedFeeds], ['\r\n'.repeat(echRows + 2), '\x1b[3J']);
    assert.deepEqual({ ...ech.pinnedReadAt }, { 0: 0, 1: 1, 2: 2 });
    assert.deepEqual({ ...ed3.pinnedReadAt }, { 0: 0, 1: 1, 2: 2 });
    assert.equal(ech.pinnedExpected, 'row 0 blank,blank,blank,D,E; cursor before 0,0');
    assert.equal(ed3.pinnedExpected, 'ED3 removes measured scrollback while keeping screen geometry');
    assert.equal(ech.pinnedPath, 'termless');
    assert.match(ech.terminalFixture.note, /cursor-position query that never measures cells or saved lines: it carries no verdict and is not qualified/);
    // Neither case is graded on a substituted spelling.
    assert.ok(!ech.plan.some(step => step.feed === '\x1b[1X') || ech.roles.some(role => role.role === 'explicit1'));
    assert.equal(ech.plan.filter(step => step.feed === '\x1b[3X').length, 3);          // pinned erased, wide and attribute groups
    assert.equal(ed3.plan.filter(step => step.feed === '\x1b[3J').length >= 4, true);
});

test('ECH spellings other than the pinned one are observed roles, never graded', () => {
    const kinds = Object.fromEntries(ech.roles.map(role => [role.role, role.kind]));
    for (const name of ['omitted', 'explicit0', 'explicit1', 'explicit5', 'wideOversized']) assert.equal(kinds[name], 'observed', name);
    for (const name of ['erased', 'wide', 'edge', 'attr']) assert.equal(kinds[name], 'measured', name);
    // Every spelling is fed exactly as spelled.
    const feeds = eraseCharsHistoryFeeds(ech);
    for (const bytes of ['\x1b[X', '\x1b[0X', '\x1b[1X', '\x1b[5X', '\x1b[999X', '\x1b[2X']) assert.ok(feeds.includes(bytes), JSON.stringify(bytes));
    // A wrong observed spelling never changes the grade.
    const wrong = grade(ech, only('omitted', state => setLine(state, 0, 'ABCDE')));
    assert.equal(wrong.outcome, 'supported');
});

test('hand-derived expected states: erased cells, retained cells, viewports and saved lines', () => {
    const at = role => ech.roles.find(entry => entry.role === role).expected;
    assert.deepEqual(at('seed').lines.slice(0, 2), ['ABCDE', '']);
    assert.deepEqual(at('seed').cursor, [0, 0]);
    assert.deepEqual(at('erased').lines.slice(0, 2), ['   DE', '']);
    assert.deepEqual(at('erased').cursor, [0, 0]);
    assert.deepEqual(at('omitted').lines[0], ' BCDE');
    assert.deepEqual(at('explicit0').lines[0], ' BCDE');
    assert.deepEqual(at('explicit1').lines[0], ' BCDE');
    assert.deepEqual(at('explicit5').lines[0], '');
    assert.deepEqual(at('wide').lines.slice(0, 3), ['ABOVE-ROW!', 'AB   FGHIJ', 'BELOW-ROW!']);
    assert.deepEqual(at('wideOversized').lines.slice(0, 3), ['ABOVE-ROW!', 'AB', 'BELOW-ROW!']);
    assert.deepEqual(at('wide').cursor, [1, 2]);
    assert.equal(at('edgeBefore').lines[0], ' '.repeat(echCols - 3) + 'WXY');
    assert.equal(at('edge').lines[0], ' '.repeat(echCols - 1) + 'Y');
    assert.deepEqual(at('edge').cursor, [0, echCols - 3]);
    assert.deepEqual(at('attr').lines[0], 'AB   FG');
    const hist = role => ed3.roles.find(entry => entry.role === role).expected;
    assert.deepEqual(hist('before').history, ['', '', '']);
    assert.deepEqual(hist('after').history, []);
    assert.deepEqual(hist('seed').history, Array.from({ length: 9 }, (_, index) => `HIST-${index}`));
    assert.deepEqual(hist('seed').lines.slice(0, 2), ['HIST-9', 'HIST-10']);
    assert.deepEqual(hist('seed').cursor, [6, 11]);
    assert.deepEqual(hist('erase3').history, []);
    assert.deepEqual(hist('erase3').lines, hist('seed').lines);
    assert.deepEqual(hist('erase3').cursor, hist('seed').cursor);
    assert.deepEqual(hist('ed2').history, hist('seed').history);
    assert.ok(hist('ed2').lines.every(line => line === ''));
    assert.deepEqual(hist('scrolled').history, Array.from({ length: 12 }, (_, index) => `HIST-${index}`));
    assert.deepEqual(hist('regrow').history, ['HIST-9', 'HIST-10']);
    // The expected states are also derived independently in the fixture oracle: the two derivations agree.
    for (const [contract, states] of [[ech, echStates()], [ed3, historyStates()]]) {
        for (const role of contract.roles) {
            const oracle = states[role.role];
            assert.deepEqual(role.expected.lines, oracle.lines.map(line => line.trimEnd()), `${contract.caseId} ${role.role}`);
            assert.deepEqual(role.expected.cursor, oracle.cursor, role.role);
            assert.deepEqual(role.expected.history, oracle.history, role.role);
        }
    }
});

test('the byte-interpreting model: ECH, DCH, EL, CHA, CUF, CUB, clamping and the pending-wrap clamp', () => {
    const model = createScrollbackModel(12, 48);
    feedScrollbackModel(model, 'ABCDEFGH\x1b[3G\x1b[2X');
    assert.equal(scrollbackModelState(model).lines[0], 'AB  EFGH');
    assert.deepEqual(scrollbackModelState(model).cursor, [0, 2]);
    feedScrollbackModel(model, '\x1b[3P');
    assert.equal(scrollbackModelState(model).lines[0], 'ABFGH');
    feedScrollbackModel(model, '\x1b[K');
    assert.equal(scrollbackModelState(model).lines[0], 'AB');
    feedScrollbackModel(model, '\x1b[1;1HXYZ\x1b[1K');
    assert.equal(scrollbackModelState(model).lines[0], '');
    feedScrollbackModel(model, '\x1b[999C\x1b[5D');
    assert.deepEqual(scrollbackModelState(model).cursor, [0, 42]);
    feedScrollbackModel(model, '\x1b[1;1HKEEP\x1b[1;46HWXY\x1b[2D');
    assert.deepEqual(scrollbackModelState(model).cursor, [0, 45]);
    assert.equal(scrollbackModelState(model).lines[0], 'KEEP' + ' '.repeat(41) + 'WXY');
    feedScrollbackModel(model, '\x1b[999X');
    assert.equal(scrollbackModelState(model).lines[0], 'KEEP');
    assert.deepEqual(scrollbackModelState(model).cursor, [0, 45]);
    assert.throws(() => feedScrollbackModel(model, '\x1b[4K'), /does not model/);
    assert.throws(() => feedScrollbackModel(model, '\x1b[3m'), /does not model/);
    assert.throws(() => feedScrollbackModel(model, '\x1b[1;1HABC\x1b[48GXY'), /wrapping/);
});

test('the pinned ECH/ED 3 plans are fixed, exact and begin with the shared reset', () => {
    for (const contract of contracts) {
        assert.equal(eraseCharsHistoryFeeds(contract)[0], eraseCharsHistoryReset);
        assert.equal(eraseCharsHistoryReads(contract), contract.roles.length);
        assert.doesNotThrow(() => validateEraseCharsHistoryFeeds(contract, eraseCharsHistoryFeeds(contract)));
        assert.throws(() => validateEraseCharsHistoryFeeds(contract, [...eraseCharsHistoryFeeds(contract).slice(0, -1), 'tampered']), /do not match/);
        assert.throws(() => validateEraseCharsHistoryFeeds(contract, eraseCharsHistoryFeeds(contract).slice(1)), /do not match/);
    }
    assert.equal(ech.roles.length, 15);
    assert.equal(ed3.roles.length, 10);
    assert.throws(() => makeEraseCharsHistoryContracts(11, 140), /at least 12 rows/);
    assert.throws(() => makeEraseCharsHistoryContracts(40, 47), /at least 12 rows/);
    assert.throws(() => makeEraseCharsHistoryContracts(129, 140), /at most 128 rows/);
});

test('a complete run grades both cases supported, with the layers named', () => {
    const results = contracts.map(contract => grade(contract));
    assert.deepEqual(results.map(result => result.outcome), ['supported', 'supported']);
    assert.ok(results[0].layers.includes('stored attributes'));
    assert.ok(results[1].layers.includes('actual scrollback') && results[1].layers.includes('active buffer'));
});

// ---- erase.character ----------------------------------------------------------------------------------------------------------

test('ECH: a no-op, a wrong count, a shift, a whole-line erase and an erase to the line end are unsupported and named', () => {
    const change = (text, cursor = [0, 0]) => only('erased', state => { setLine(state, 0, text); state.cursor = cursor; });
    const cases = [
        ['ABCDE', /no-op: nothing was erased/],
        [erasedRow('ABCDE', 0, 2), /erased 2 cells instead of 3/],
        [erasedRow('ABCDE', 0, 4), /erased 4 cells instead of 3/],
        [shiftedRow('ABCDE', 0, 3), /DCH shifted the suffix left/],
        ['', /the whole line was erased/],
    ];
    for (const [text, note] of cases) {
        const result = grade(ech, change(text));
        assert.equal(result.outcome, 'unsupported', text);
        assert.match(result.note, note);
    }
    assert.match(grade(ech, change('   DE', [0, 3])).note, /cursor is at 0,3 instead of the contracted 0,0/);
    assert.match(grade(ech, change('XX  DE')).note, /Viewport row 0 differs/);
});

test('ECH: blanking in place is told apart from overwriting spaces over a wrong range or writing past the count', () => {
    // Spaces typed over cells 1..3 leave A and E and move the cursor: not the contracted cells, not the contracted cursor.
    assert.equal(grade(ech, only('erased', state => { setLine(state, 0, 'A   E'); state.cursor = [0, 4]; })).outcome, 'unsupported');
    assert.equal(grade(ech, only('erased', state => setLine(state, 0, 'A   E'))).outcome, 'unsupported');
    // The right cells but a stray character one row down (a spill): rejected.
    assert.equal(grade(ech, only('erased', state => setLine(state, 1, 'D'))).outcome, 'unsupported');
});

test('ECH: the wide, right-edge and unaffected-row controls catch shifting, row bleed, an unclamped count and a no-op', () => {
    assert.match(grade(ech, only('wide', state => setLine(state, 1, shiftedRow('ABCDEFGHIJ', 2, 3)))).note, /wide: DCH shifted the suffix left/);
    assert.match(grade(ech, only('wide', state => setLine(state, 2, ''))).note, /wide: /);
    assert.match(grade(ech, only('wide', state => setLine(state, 1, 'ABCDEFGHIJ'))).note, /wide: no-op/);
    assert.match(grade(ech, only('wide', state => setLine(state, 1, ''))).note, /wide: the whole line was erased/);
    assert.match(grade(ech, only('edge', state => setLine(state, 0, ' '.repeat(echCols - 3) + 'WXY'))).note, /edge: no-op/);
    assert.match(grade(ech, only('edge', state => setLine(state, 0, ''))).note, /edge: erased 3 cells instead of 2/);
    assert.equal(grade(ech, only('edge', state => { state.cursor = [0, echCols - 1]; })).outcome, 'unsupported');
    assert.equal(grade(ech, only('wide', state => { state.cursor = [2, 0]; })).outcome, 'unsupported');
});

test('ECH: stored attributes: retained cells keep them, erased cells carry no stale text attribute, the background is observed', () => {
    const attr = (cellsFor) => only('attr', state => { state.cells = cellsFor(state.cells); });
    // The supported baseline records the background observation (palette index 4, the same as the same-pen reference cell).
    const observation = eraseCharsHistoryObservation(ech, syntheticEraseCharsHistoryScreens(ech));
    const brief = ech.observed(observation);
    assert.equal(brief.attributes.createdBlankBackground, 'erased cells carry the current background');
    assert.equal(brief.attributes.retainedKeepAttributes, true);
    assert.equal(brief.attributes.erasedNoStaleAttributes, true);
    // A retained bold cell that lost its attribute.
    assert.equal(grade(ech, attr(cells => ({ ...cells, '0,0': plainCell('A') }))).outcome, 'unsupported');
    // A retained plain cell that gained one.
    assert.equal(grade(ech, attr(cells => ({ ...cells, '0,5': bold('F') }))).outcome, 'unsupported');
    // An erased cell that kept the bold of the character it replaced (the stale attribute).
    const stale = grade(ech, attr(cells => ({ ...cells, '0,3': colorCell(' ', { attrs: ['bold'], bg: pen }) })));
    assert.equal(stale.outcome, 'unsupported');
    assert.match(stale.note, /stale text attribute/);
    // An erased cell that kept an underline.
    assert.equal(grade(ech, attr(cells => ({ ...cells, '0,4': colorCell(' ', { underlineStyle: 1, bg: pen }) }))).outcome, 'unsupported');
    // A blank outside the erased range.
    assert.equal(grade(ech, attr(cells => ({ ...cells, '0,7': colorCell(' ', { bg: pen }) }))).outcome, 'unsupported');
    // Background observation only: default-background blanks are still a supported erase, and are observed as such.
    const defaults = attr(cells => ({ ...cells, '0,2': plainCell(' '), '0,3': plainCell(' '), '0,4': plainCell(' ') }));
    assert.equal(grade(ech, defaults).outcome, 'supported');
    const other = attr(cells => ({ ...cells, '0,2': colorCell(' ', { bg: { kind: 'palette', index: 2 } }), '0,3': colorCell(' ', { bg: { kind: 'palette', index: 2 } }), '0,4': colorCell(' ', { bg: { kind: 'palette', index: 2 } }) }));
    assert.equal(grade(ech, other).outcome, 'supported');
    const screens = syntheticEraseCharsHistoryScreens(ech, undefined, (role, state) => { if (role === 'attr') state.cells = other('attr', state).cells; return state; });
    assert.match(ech.observed(eraseCharsHistoryObservation(ech, screens)).attributes.createdBlankBackground, /different non-default background/);
});

test('ECH: missing, invalid or contradictory controls are inconclusive, never unsupported', () => {
    for (const [role, edit, note] of [
        ['initial', state => setLine(state, 5, 'junk'), /Control initial was not established/],
        ['seed', state => setLine(state, 0, 'ABCXE'), /Control seed was not established/],
        ['seed', state => { state.cursor = [0, 2]; }, /Control seed was not established/],
        ['wideBefore', state => setLine(state, 1, 'ABCDEFGHIX'), /Control wideBefore was not established/],
        ['edgeBefore', state => { state.cursor = [0, 0]; }, /Control edgeBefore was not established/],
        ['attrBefore', state => { state.cells['0,3'] = plainCell('D'); }, /attribute control seed was not measured/],
        ['pen', state => { state.cells = {}; }, null],
    ]) {
        const result = grade(ech, only(role, edit));
        if (note === null) { assert.equal(result.outcome, 'supported', 'a missing pen only weakens the background observation'); continue; }
        assert.equal(result.outcome, 'inconclusive', role);
        assert.match(result.note, note);
    }
    // Too-small geometry and a reverse-video screen cannot be graded.
    const small = eraseCharsHistoryObservation(ech, syntheticEraseCharsHistoryScreens(ech));
    small.initial.rows = 8;
    assert.equal(ech.grade(small).outcome, 'inconclusive');
    const reversed = eraseCharsHistoryObservation(ech, syntheticEraseCharsHistoryScreens(ech));
    reversed.reverseVideo = null;
    assert.match(ech.grade(reversed).note, /DECSCNM/);
    assert.throws(() => gradeEraseCharsHistoryCase(ech, { initial: {} }), /Missing erase.character read captures/);
    assert.throws(() => gradeEraseCharsHistoryCase(ech, {}, 2), /Unknown erase-character\/saved-lines contracts revision/);
});

test('ECH: preserved state: margins, modes, active buffer and saved lines may not drift', () => {
    for (const [name, edit] of [
        ['margins', state => { state.top = 2; }],
        ['alternate screen', state => { state.alternate = true; }],
        ['saved lines appeared', state => { state.history = ['junk']; }],
        ['pending wrap', state => { state.details = { pendingWrap: true }; }],
        ['origin mode', state => { state.details = { originMode: true }; }],
        ['insert mode', state => { state.details = { insertMode: true }; }],
    ]) {
        const result = grade(ech, only('erased', edit));
        assert.equal(result.outcome, 'unsupported', name);
        assert.match(result.note, /^erased: /);
    }
});

// ---- erase.screen.scrollback --------------------------------------------------------------------------------------------------

test('ED 3: an already-empty history cannot establish erasure: a pinned fixture that saved nothing is inconclusive', () => {
    const none = grade(ed3, only('before', state => { state.history = []; state.cursor = [echRows - 1, 0]; }));
    assert.equal(none.outcome, 'inconclusive');
    assert.match(none.note, /Control before was not established/);
    // The mere zero count after is not a verdict: the independent baseline (the ordered marked history) is read first.
    const noSeed = grade(ed3, only('seed', state => { state.history = []; }));
    assert.equal(noSeed.outcome, 'inconclusive');
    assert.match(noSeed.note, /Control seed was not established/);
});

test('ED 3: retained history after the pinned erase is unsupported, with the labeled wrong behavior', () => {
    const kept = grade(ed3, only('after', state => { state.history = ['', '', '']; }));
    assert.equal(kept.outcome, 'unsupported');
    assert.match(kept.note, /after: no-op or ED 2: the saved lines were not erased/);
    const partial = grade(ed3, only('after', state => { state.history = ['']; }));
    assert.equal(partial.outcome, 'unsupported');
    assert.match(partial.note, /actual scrollback differs/);
});

test('ED 3: accidental viewport clearing, retained marked history, cursor drift and a changed buffer are unsupported', () => {
    const cleared = grade(ed3, only('erase3', state => { state.lines = state.lines.map(() => ''); }));
    assert.equal(cleared.outcome, 'unsupported');
    assert.match(cleared.note, /erase3: the viewport was cleared along with the saved lines/);
    const retained = grade(ed3, only('erase3', state => { state.history = Array.from({ length: 9 }, (_, index) => `HIST-${index}`); }));
    assert.match(retained.note, /erase3: no-op: the saved lines were not erased/);
    const ed2Like = grade(ed3, only('erase3', state => { state.lines = state.lines.map(() => ''); state.history = Array.from({ length: 9 }, (_, index) => `HIST-${index}`); }));
    assert.match(ed2Like.note, /erase3: ED 2 instead of ED 3/);
    assert.equal(grade(ed3, only('erase3', state => { state.cursor = [0, 0]; })).outcome, 'unsupported');
    assert.equal(grade(ed3, only('erase3', state => { state.cursor = [echRows - 1, 0]; })).outcome, 'unsupported');
    assert.equal(grade(ed3, only('erase3', state => { state.alternate = true; })).outcome, 'unsupported');
    assert.equal(grade(ed3, only('erase3', state => { state.top = 3; state.bottom = 9; })).outcome, 'unsupported');
    assert.equal(grade(ed3, only('erase3', state => { state.history = ['HIST-9']; })).outcome, 'unsupported');
});

test('ED 3: the ordered history contents are required, not just a count', () => {
    const reordered = grade(ed3, only('seed', state => { [state.history[0], state.history[1]] = [state.history[1], state.history[0]]; }));
    assert.equal(reordered.outcome, 'inconclusive');
    assert.match(reordered.note, /Control seed was not established/);
    const dropped = grade(ed3, only('seed', state => { state.history = state.history.slice(1); }));
    assert.equal(dropped.outcome, 'inconclusive');
    const duplicated = grade(ed3, only('seed', state => { state.history = [...state.history.slice(0, 8), 'HIST-7']; }));
    assert.equal(duplicated.outcome, 'inconclusive');
});

test('ED 3: CSI 2J and ordinary scrolling are the opposite signatures and are required controls', () => {
    // ED 2 must clear the viewport and keep every saved line, or the detector cannot be trusted.
    assert.equal(grade(ed3, only('ed2', state => { state.history = []; })).outcome, 'inconclusive');
    assert.equal(grade(ed3, only('ed2', state => { state.lines[0] = 'HIST-9'; })).outcome, 'inconclusive');
    // Ordinary scrolling must only grow the saved lines.
    assert.equal(grade(ed3, only('scrolled', state => { state.history = state.history.slice(0, 9); })).outcome, 'inconclusive');
    // CSI 3J on an empty history changes nothing visible: a viewport cleared there is unsupported.
    const emptyCleared = grade(ed3, only('emptyErase', state => { state.lines = state.lines.map(() => ''); }));
    assert.equal(emptyCleared.outcome, 'unsupported');
    assert.match(emptyCleared.note, /emptyErase: the viewport was cleared although there were no saved lines/);
    assert.equal(grade(ed3, only('emptyBefore', state => { state.history = ['stale']; })).outcome, 'inconclusive');
});

test('ED 3: scrolling after the erase saves only the new rows: the old saved lines are gone, not hidden', () => {
    const hidden = grade(ed3, only('regrow', state => { state.history = [...Array.from({ length: 9 }, (_, index) => `HIST-${index}`), 'HIST-9', 'HIST-10']; }));
    assert.equal(hidden.outcome, 'unsupported');
    assert.match(hidden.note, /regrow: the earlier saved lines remained/);
    assert.equal(grade(ed3, only('regrow', state => { state.history = ['HIST-10']; })).outcome, 'unsupported');
});

test('ED 3: the pinned-fixture verdict needs only the controls that precede it on a pristine screen', () => {
    // If the reset that follows depended on a working ED 3, every later control would fail; the pinned verdict must still be unsupported.
    const result = grade(ed3, (role, state) => {
        if (role === 'after') state.history = ['', '', ''];
        if (['seed', 'erase3', 'ed2', 'scrolled', 'emptyBefore', 'emptyErase', 'regrow'].includes(role)) state.history = ['stale', ...state.history];
        return state;
    });
    assert.equal(result.outcome, 'unsupported');
    assert.match(result.note, /^after: /);
    // With the pinned stage valid, a broken later control is inconclusive.
    assert.equal(grade(ed3, only('seed', state => { state.history = ['stale', ...state.history]; })).outcome, 'inconclusive');
    // And a broken pinned baseline is inconclusive whatever the later controls say.
    assert.equal(grade(ed3, only('initial', state => { state.history = ['stale']; })).outcome, 'inconclusive');
});

test('ED 3: geometry, DECSCNM and a mismatched measured size cannot be graded', () => {
    const base = eraseCharsHistoryObservation(ed3, syntheticEraseCharsHistoryScreens(ed3));
    const tooSmall = structuredClone(base); tooSmall.initial.rows = 8;
    assert.match(ed3.grade(tooSmall).note, /Measured geometry 8x60 cannot fit/);
    const mismatched = structuredClone(base); mismatched.initial.cols = 70;
    assert.equal(ed3.grade(mismatched).outcome, 'inconclusive');
    const reversed = structuredClone(base); reversed.reverseVideo = true;
    assert.match(ed3.grade(reversed).note, /DECSCNM/);
});

test('ED 3 records physical rows, counts and the compact ordered contents of every read', () => {
    const brief = ed3.observed(eraseCharsHistoryObservation(ed3, syntheticEraseCharsHistoryScreens(ed3)));
    assert.deepEqual(brief.geometry, { rows: echRows, cols: echCols });
    assert.match(brief.units, /physical rows: no wrapped \(logical\) lines/);
    assert.deepEqual(brief.roles.seed.history, ['0-8: HIST-0..8']);
    assert.equal(brief.roles.seed.historyCount, 9);
    assert.equal(brief.roles.erase3.historyCount, 0);
    assert.deepEqual(brief.roles.erase3.viewport, brief.roles.seed.viewport);
    assert.deepEqual(brief.roles.before.history, []);          // blank rows have no content to list
    assert.equal(brief.roles.before.historyCount, 3);
    assert.equal(brief.roles.regrow.historyCount, 2);
    // The pinned total is the screen rows plus the saved physical rows.
    assert.deepEqual([brief.roles.initial.totalLines, brief.roles.before.totalLines, brief.roles.after.totalLines], [echRows, echRows + 3, echRows]);
    assert.equal(brief.roles.seed.totalLines, echRows + 9);
    assert.ok(Object.values(brief.roles).every(role => role.matchesModel === true && role.screen === 'main'));
});

test('the pinned notes are the pinned strings, byte for byte', () => {
    assert.deepEqual({ ...pinnedEraseCharsHistoryNotes }, {
        geometry: 'Measured geometry cannot fit the erase fixture',
        fixture: 'Erase fixture, cursor setup, or cell metadata unavailable',
        historyGeometry: 'Need a measured screen size for history setup',
        historyNone: 'No measured scrollback to erase',
        historyReadback: 'Scrollback readback or geometry changed unexpectedly',
    });
});
