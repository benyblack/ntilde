import assert from 'node:assert/strict';
import { test } from 'node:test';
import { charEditContracts, charEditCaseIds, charEditContractsVersion, charEditSuiteId, charEditNoncePrefix,
    gradeCharEditCase, charEditObservation, charEditReset, charEditFeeds, charEditReads, validateCharEditFeeds,
    pinnedGeometryNote, pinnedNotes } from './char-edit-contracts.mjs';
import { syntheticCharEditReads, syntheticCharEditScreens, syntheticCharEditGrade, rowOf, shifted, repeated, colorCell, plainCell,
    paletteBinding } from './fixtures/char-edit-evidence.mjs';

const ICH = 'editing.insert-chars', DCH = 'editing.delete-chars', REP = 'editing.repeat-char';
const byId = id => charEditContracts.find(contract => contract.caseId === id);
/** Grade a synthetic case after editing its reads; `edit` mutates the per-role grids. */
const grade = (caseId, edit, binding) => syntheticCharEditGrade(caseId, binding, id => {
    const reads = syntheticCharEditReads(id);
    if (id === caseId && edit) edit(reads);
    return reads;
});
const outcome = (caseId, edit, binding) => grade(caseId, edit, binding).outcome;
const note = (caseId, edit) => grade(caseId, edit).note;
const bold = char => colorCell(char, { attrs: ['bold'] });

test('the contracts pin exactly the three verified feature ids, fixture bytes and spellings', () => {
    assert.deepEqual([...charEditCaseIds], [ICH, DCH, REP]);
    const seed = '\x1b[1;1H\x1b[2KABCDEZQH';
    assert.deepEqual(charEditContracts.map(contract => [...contract.pinnedFeeds]), [
        [seed, '\x1b[1;3H', '\x1b[1@'], [seed, '\x1b[1;3H', '\x1b[1P'],
        ['\x1b[1;1H\x1b[2KAX   Z', '\x1b[1;2H', 'X', '\x1b[3b']]);
    assert.deepEqual(charEditContracts.map(contract => contract.pinnedExpected), [
        'ICH inserts a blank cell at column 3 and shifts measured text right; prefix stays intact',
        'DCH deletes the cell at column 3 and shifts measured text left; prefix stays intact',
        'REP repeats the immediately preceding X into three target cells without changing flank controls']);
    assert.equal(charEditContractsVersion, 1);
    assert.equal(charEditSuiteId, 'ntilde-owned-char-edit-graded-v1');
    assert.equal(charEditNoncePrefix, 'NTILDE_CE_');
    for (const contract of charEditContracts) {
        assert.match(contract.appearanceScope, /stored ASCII cells only/);
        assert.match(contract.appearanceScope, /no Unicode, wide-cell or combining-character editing/);
        assert.match(contract.terminalFixture.note, /never measures cells|carries no verdict/);
        assert.equal(charEditReads(contract), contract.roles.length);
        assert.doesNotThrow(() => validateCharEditFeeds(contract, charEditFeeds(contract)));
        assert.throws(() => validateCharEditFeeds(contract, [...charEditFeeds(contract).slice(0, -1), 'x']), /do not match/);
        assert.equal(charEditFeeds(contract)[0], charEditReset);
    }
});

test('the pinned spellings are the explicit count 1 for ICH/DCH and count 3 for REP; every other spelling is a labeled control', () => {
    assert.equal(byId(ICH).spellings.pinned.bytes, '\x1b[1@');
    assert.equal(byId(DCH).spellings.pinned.bytes, '\x1b[1P');
    assert.equal(byId(REP).spellings.pinned.bytes, 'X\x1b[3b');
    assert.deepEqual([...byId(ICH).others], ['omitted', 'explicit0', 'explicit2', 'explicit999']);
    assert.deepEqual([...byId(REP).others], ['omitted', 'explicit0', 'explicit1', 'explicit5']);
    for (const id of [ICH, DCH]) {
        const contract = byId(id), letter = id === ICH ? '@' : 'P';
        assert.deepEqual(contract.others.map(name => contract.spellings[name].bytes), [`\x1b[${letter}`, `\x1b[0${letter}`, `\x1b[2${letter}`, `\x1b[999${letter}`]);
    }
});

test('the pinned feeds are read in their own order: ICH/DCH read after the seed, the cursor feed and the edit; REP after its four feeds', () => {
    for (const id of [ICH, DCH]) assert.deepEqual([...byId(id).pinnedReads], [1, 2, 3]);
    assert.deepEqual([...byId(REP).feedRoles], ['seed', 'target', 'before', 'repeated']);
    const rep = byId(REP);
    assert.deepEqual(rep.roles.slice(0, 6), ['initial', 'seed', 'target', 'before', 'split', 'repeated']);
    assert.deepEqual([...rep.pinnedReads], [1, 2, 3, 5]);
    const plan = rep.plan;
    // The pinned X is its own write ending the 'before' group; the pinned REP bytes travel WITH the graphic in one write, never after a barrier.
    const split = plan.findIndex(step => step.feed === '\x1b[3b');
    assert.ok(split > 0);
    assert.equal(plan[split - 1].read, 'cells');
    assert.ok(plan.some(step => step.feed === 'X\x1b[3b'));
    assert.ok(!plan.some(step => step.feed === '\x1b[3b' && plan.indexOf(step) !== split));
});

test('naming: no sibling editing case is substituted for the three pinned ids', () => {
    for (const contract of charEditContracts) {
        assert.ok(/^editing\.(insert-chars|delete-chars|repeat-char)$/.test(contract.caseId));
        assert.ok(!/^editing\.(insert-characters|delete-characters|repeat)\./.test(contract.caseId));
        const own = new Set([charEditReset, '\x1b[0m\x1b[2J\x1b[H']);
        for (const feed of charEditFeeds(contract).filter(item => !own.has(item))) {
            assert.ok(!/\x1b\[[0-9]*[LMX]|\x1b\[\??[0-9]*J|\x1b\[[0-9;]*\$[xz{|]|\x1b\[[0-9;]*'[}~]/.test(feed), JSON.stringify(feed));
        }
    }
});

test('a complete synthetic run is conclusively supported for all three cases', () => {
    for (const id of charEditCaseIds) assert.deepEqual(syntheticCharEditGrade(id), { outcome: 'supported' }, id);
});

test('the fixture oracle is independent: ICH inserts and drops at the edge, DCH deletes and exposes, REP overwrites', () => {
    assert.equal(shifted('ich', 'ABCDEZQH', 2, 1), 'AB CDEZQH');
    assert.equal(shifted('dch', 'ABCDEZQH', 2, 1), 'ABDEZQH');
    assert.equal(shifted('ich', 'ABCDEZQH', 2, 999), 'AB');
    assert.equal(shifted('dch', 'ABCDEZQH', 2, 999), 'AB');
    assert.equal(repeated(3), 'AXXXXZ');
    assert.equal(repeated(0), 'AX   Z');
    assert.equal(repeated(5), 'AXXXXXX');
});

test('no-op edits are unsupported once the seed is proven', () => {
    for (const id of [ICH, DCH]) {
        const edit = reads => { reads.edited = { ...reads.cursor }; };
        assert.equal(outcome(id, edit), 'unsupported', id);
        assert.match(note(id, edit), /no-op/);
    }
    const repEdit = reads => { reads.repeated = { rows: [rowOf('AX   Z')], cursor: [0, 2] }; };
    assert.equal(outcome(REP, repEdit), 'unsupported');
    assert.match(note(REP, repEdit), /nothing was repeated/);
});

test('reversed shifts are unsupported and named', () => {
    assert.match(note(ICH, reads => { reads.edited = { rows: [rowOf(shifted('dch', 'ABCDEZQH', 2, 1))], cursor: [0, 2] }; }), /matches DCH instead of ICH/);
    assert.match(note(DCH, reads => { reads.edited = { rows: [rowOf(shifted('ich', 'ABCDEZQH', 2, 1))], cursor: [0, 2] }; }), /matches ICH instead of DCH/);
});

test('overwrite instead of insertion or deletion is unsupported and named', () => {
    // A blank written over C (erase in place) instead of a shift, in both operations.
    const overwritten = reads => { reads.edited = { rows: [rowOf('AB DEZQH')], cursor: [0, 2] }; };
    assert.match(note(ICH, overwritten), /overwrite or erase instead of a shift/);
    assert.match(note(DCH, overwritten), /overwrite or erase instead of a shift/);
    // The suffix overwritten by the inserted blank loses a character: no cell shifted past the blank.
    assert.equal(outcome(ICH, reads => { reads.edited = { rows: [rowOf('AB CDEZQ')], cursor: [0, 2] }; }), 'unsupported');
});

test('wrong shift count and incorrect blank insertion are unsupported', () => {
    assert.match(note(ICH, reads => { reads.edited = { rows: [rowOf(shifted('ich', 'ABCDEZQH', 2, 2))], cursor: [0, 2] }; }), /shifted by 2 cells instead of 1/);
    assert.match(note(DCH, reads => { reads.edited = { rows: [rowOf(shifted('dch', 'ABCDEZQH', 2, 2))], cursor: [0, 2] }; }), /shifted by 2 cells instead of 1/);
    // ICH that inserted a visible character instead of a blank, and one that inserted no blank at all.
    assert.equal(outcome(ICH, reads => { reads.edited = { rows: [rowOf('ABxCDEZQH')], cursor: [0, 2] }; }), 'unsupported');
    assert.equal(outcome(ICH, reads => { reads.edited = { rows: [rowOf('ABCDEZQH')], cursor: [0, 2] }; }), 'unsupported');
    // The suffix beyond the pinned eight cells must also move: a shift that stopped at column 7 left H behind.
    assert.equal(outcome(ICH, reads => { reads.edited = { rows: [rowOf('AB CDEZQ'), [], [], [], [], [], []].slice(0, 1), cursor: [0, 2] }; }), 'unsupported');
});

test('the contract compares the whole row: text past the pinned eight cells and stray rows are unsupported', () => {
    assert.equal(outcome(ICH, reads => { reads.edited = { rows: [rowOf('AB CDEZQHJ')], cursor: [0, 2] }; }), 'unsupported');
    assert.equal(outcome(DCH, reads => { reads.edited = { rows: [rowOf('ABDEZQH'), null, rowOf('stray')], cursor: [0, 2] }; }), 'unsupported');
    // DCH must expose a blank at the right edge: a stale copy of the last cell there is unsupported.
    assert.equal(outcome(DCH, reads => { reads.edited = { rows: [rowOf('ABDEZQHH')], cursor: [0, 2] }; }), 'unsupported');
});

test('retained-text corruption in the prefix is unsupported', () => {
    assert.equal(outcome(ICH, reads => { reads.edited = { rows: [rowOf('AX CDEZQH')], cursor: [0, 2] }; }), 'unsupported');
    assert.match(note(DCH, reads => { reads.edited = { rows: [rowOf('XBDEZQH')], cursor: [0, 2] }; }), /prefix AB changed/);
    assert.equal(outcome(ICH, reads => { reads.edited = { rows: [rowOf('AB CDEZQX')], cursor: [0, 2] }; }), 'unsupported');
});

test('REP: wrong counts, overwritten flank and wrong characters are unsupported and named', () => {
    assert.match(note(REP, reads => { reads.repeated = { rows: [rowOf(repeated(2))], cursor: [0, 4] }; }), /2 times instead of 3/);
    assert.match(note(REP, reads => { reads.repeated = { rows: [rowOf(repeated(4))], cursor: [0, 6] }; }), /flank cell changed/);
    assert.match(note(REP, reads => { reads.repeated = { rows: [rowOf(repeated(1))], cursor: [0, 3] }; }), /1 time instead of 3/);
    assert.equal(outcome(REP, reads => { reads.repeated = { rows: [rowOf('AXYYXZ')], cursor: [0, 5] }; }), 'unsupported');
    assert.equal(outcome(REP, reads => { reads.repeated = { rows: [rowOf('AXXXXZ'), rowOf('XX')], cursor: [0, 5] }; }), 'unsupported');
});

test('REP after a fence barrier is an observation, never a grade: a repeating or a non-repeating split control leaves the verdict supported', () => {
    for (const split of ['AX   Z', repeated(3)]) {
        assert.equal(outcome(REP, reads => { reads.split = { rows: [rowOf(split)], cursor: [0, split === 'AX   Z' ? 2 : 5] }; }), 'supported');
    }
    const observed = byId(REP).observed(charEditObservation(byId(REP), syntheticCharEditScreens(REP)));
    assert.equal(observed.splitControl.repeats, false);
    assert.match(observed.splitControl.policy, /not graded/);
});

test('REP invalid setup: a seed, cursor or X setup that was not measured is inconclusive, never unsupported', () => {
    assert.equal(outcome(REP, reads => { reads.seed = { rows: [rowOf('AX  YZ')], cursor: [0, 6] }; }), 'inconclusive');
    assert.equal(note(REP, reads => { reads.seed = { rows: [rowOf('AX  YZ')], cursor: [0, 6] }; }), pinnedNotes[REP].seed);
    assert.equal(note(REP, reads => { reads.target = { rows: [rowOf('AX   Z')], cursor: [0, 3] }; }), pinnedNotes[REP].cursor);
    assert.equal(note(REP, reads => { reads.before = { rows: [rowOf('AX   Z')], cursor: [0, 3] }; }), pinnedNotes[REP].setup);
    assert.equal(note(REP, reads => { reads.before = { rows: [rowOf('AY   Z')], cursor: [0, 2] }; }), pinnedNotes[REP].setup);
    assert.equal(outcome(REP, reads => { reads.attrBefore = { rows: [rowOf('AX   Z')], cursor: [0, 2] }; }), 'inconclusive');
    // A repeat that overwrote the flank cannot be separated from a bad flank seed: a changed Z seed is inconclusive.
    assert.equal(outcome(REP, reads => { reads.wideBefore = { rows: [rowOf('ABOVE-ROW!')], cursor: [1, 1] }; }), 'inconclusive');
});

test('ICH/DCH invalid setup is inconclusive with the pinned notes', () => {
    for (const id of [ICH, DCH]) {
        assert.equal(note(id, reads => { reads.seed = { rows: [rowOf('ABCDEZQX')], cursor: [0, 8] }; }), pinnedNotes[id].seed);
        assert.equal(note(id, reads => { reads.cursor = { rows: [rowOf('ABCDEZQH')], cursor: [0, 3] }; }), pinnedNotes[id].cursor);
        assert.equal(outcome(id, reads => { reads.edgeBefore = { rows: [rowOf('WXY')], cursor: [0, 0] }; }), 'inconclusive');
        assert.equal(outcome(id, reads => { reads.attrBefore = { rows: [rowOf('ABCDEZQH')], cursor: [0, 2] }; }), 'inconclusive');
        assert.equal(outcome(id, reads => { reads.wideBefore = { rows: [rowOf('ABOVE-ROW!')], cursor: [1, 2] }; }), 'inconclusive');
    }
});

test('the cursor must stay (ICH, DCH) or advance past the last repeat (REP); the pinned probes never read it after the edit', () => {
    for (const id of [ICH, DCH]) {
        assert.equal(outcome(id, reads => { reads.edited = { ...reads.edited, cursor: [0, 3] }; }), 'unsupported', id);
        assert.match(note(id, reads => { reads.edited = { ...reads.edited, cursor: [0, 0] }; }), /cursor moved/);
    }
    assert.equal(outcome(REP, reads => { reads.repeated = { ...reads.repeated, cursor: [0, 4] }; }), 'unsupported');
    assert.match(note(REP, reads => { reads.repeated = { ...reads.repeated, cursor: [0, 2] }; }), /did not advance/);
});

test('unaffected rows and columns: wide-row damage is unsupported for every case', () => {
    assert.equal(outcome(ICH, reads => { reads.wide = { rows: [rowOf('ABOVE-ROW!'), rowOf('AB CDEFGHI'), rowOf('BELOW-ROW!')], cursor: [1, 2] }; }), 'unsupported');
    assert.equal(outcome(DCH, reads => { reads.wide = { rows: [rowOf('ABOVE-ROW!'), rowOf('ABDEFGHIJ'), rowOf('BELOW-ROW?')], cursor: [1, 2] }; }), 'unsupported');
    assert.equal(outcome(REP, reads => { reads.wide = { rows: [rowOf('ABOVE-ROW!'), rowOf('AXXXXZQRSX'), rowOf('BELOW-ROW!')], cursor: [1, 5] }; }), 'unsupported');
    assert.match(note(ICH, reads => { reads.wide = { rows: [], cursor: [1, 2] }; }), /wide-row control differs/);
});

test('the right edge: ICH truncates at the last column and DCH exposes a blank at it; a wrapped or lost edge is unsupported', () => {
    const edge = 'W'.padStart(58, ' ') + 'XY';
    assert.equal(outcome(ICH, reads => { reads.edge = { rows: [rowOf(' '.repeat(58) + 'WXY'.slice(0, 2))], cursor: [0, 57] }; }), 'supported');
    assert.equal(outcome(ICH, reads => { reads.edge = { rows: [rowOf(edge), rowOf('Y')], cursor: [0, 57] }; }), 'unsupported');
    assert.equal(outcome(ICH, reads => { reads.edge = { rows: [rowOf(' '.repeat(58) + 'WX'), rowOf('Y')], cursor: [0, 57] }; }), 'unsupported');
    assert.match(note(DCH, reads => { reads.edge = { rows: [rowOf(' '.repeat(57) + 'XYY')], cursor: [0, 57] }; }), /right-edge control differs/);
    assert.equal(outcome(DCH, reads => { reads.edge = { rows: [rowOf(' '.repeat(57) + 'WXY')], cursor: [0, 57] }; }), 'unsupported');
});

test('attributes: moved cells must keep their stored tuple; dropped bold, smeared bold and lost flank attributes are unsupported', () => {
    const plain = ['A', 'B', 'C', 'D', 'E', 'Z', 'Q', 'H'].map(char => plainCell(char));
    assert.equal(outcome(ICH, reads => { reads.attr = { rows: [[plain[0], plain[1], plainCell(' '), plain[2], plain[3], plain[4], plain[5], plain[6], plain[7]]], cursor: [0, 2] }; }), 'unsupported');
    assert.equal(outcome(DCH, reads => { reads.attr = { rows: [[plain[0], plain[1], bold('D'), bold('E'), bold('Z'), plain[6], plain[7]]], cursor: [0, 2] }; }), 'unsupported');
    assert.equal(outcome(REP, reads => { reads.attr = { rows: [[bold('A'), ...Array.from({ length: 4 }, () => bold('X')), plainCell('Z')]], cursor: [0, 5] }; }), 'unsupported');
    // The attributes of the blank cell an edit creates, and whether REP cells carry the pen, are observed and not required.
    assert.equal(outcome(ICH, reads => { reads.attr = { rows: [[plain[0], plain[1], colorCell(' ', { bg: { kind: 'palette', index: 4 } }), plain[2], bold('D'), bold('E'), plain[5], plain[6], plain[7]]], cursor: [0, 2] }; }), 'supported');
    assert.equal(outcome(REP, reads => { reads.attr = { rows: [[plainCell('A'), ...Array.from({ length: 4 }, () => plainCell('X')), plainCell('Z')]], cursor: [0, 5] }; }), 'supported');
});

test('background of created cells is observed against the same-pen reference, separately from the stored representation', () => {
    const paint = (index, extra = {}) => colorCell(' ', { bg: { kind: 'palette', index }, ...extra });
    const plain = ['A', 'B', 'C', 'D', 'E', 'Z', 'Q', 'H'].map(char => plainCell(char));
    const attrRow = created => [plain[0], plain[1], created, plain[2], bold('D'), bold('E'), plain[5], plain[6], plain[7]];
    const observe = created => byId(ICH).observed(charEditObservation(byId(ICH), syntheticCharEditScreens(ICH, undefined, id => {
        const reads = syntheticCharEditReads(id);
        reads.attr = { rows: [attrRow(created)], cursor: [0, 2] };
        return reads;
    }))).attributes;
    // ICH inserts one blank at column 2: the seed row ABCDEZQH has D and E bold, so the row is A B _ C D E Z Q H with plain Z Q H.
    assert.equal(observe(plainCell(' ')).createdBlankBackground, 'created cells keep the default background');
    assert.equal(observe(paint(4)).createdBlankBackground, 'created cells carry the current background');
    assert.equal(observe(paint(5)).createdBlankBackground, 'created cells carry a different non-default background color than the current pen');
});

test('DECSCNM, blank-screen and geometry gates make every case inconclusive, not unsupported', () => {
    for (const id of charEditCaseIds) {
        assert.equal(outcome(id, null, { ...paletteBinding(), reverseVideo: true }), 'inconclusive', id);
        assert.equal(outcome(id, reads => { reads.initial = { rows: [rowOf('x')], cursor: [0, 0] }; }), 'inconclusive', id);
    }
    assert.equal(pinnedGeometryNote, 'Measured geometry cannot fit the character-editing controls');
    const contract = byId(ICH);
    const observation = charEditObservation(contract, syntheticCharEditScreens(ICH));
    assert.equal(gradeCharEditCase(contract, { ...observation, seed: { ...observation.seed, cols: 8 } }).note, pinnedGeometryNote);
    assert.equal(gradeCharEditCase(contract, { ...observation, seed: { ...observation.seed, rows: 2 } }).outcome, 'inconclusive');
});

test('graders refuse unknown revisions and missing reads', () => {
    const contract = byId(ICH);
    const observation = charEditObservation(contract, syntheticCharEditScreens(ICH));
    assert.throws(() => gradeCharEditCase(contract, observation, 2), /Unknown character-editing contracts revision 2/);
    assert.throws(() => gradeCharEditCase(contract, { ...observation, edge: undefined }), /Missing editing\.insert-chars read captures/);
    assert.throws(() => charEditObservation(contract, []), /Missing character-editing read captures/);
});
