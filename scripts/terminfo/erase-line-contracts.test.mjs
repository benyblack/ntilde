import assert from 'node:assert/strict';
import { test } from 'node:test';
import { eraseLineContracts, eraseLineCaseIds, eraseLineContractsVersion, eraseLineSuiteId, eraseLineNoncePrefix,
    gradeEraseLineCase, eraseLineObservation, eraseLineReset, eraseLineFeeds, eraseLineReads, validateEraseLineFeeds,
    pinnedGeometryNote, pinnedFixtureNote } from './erase-line-contracts.mjs';
import { syntheticEraseLineReads, syntheticEraseLineScreens, syntheticEraseLineGrade, rowOf, survivors, colorCell,
    paletteBinding } from './fixtures/erase-line-evidence.mjs';

const RIGHT = 'erase.line.right', LEFT = 'erase.line.left', ALL = 'erase.line.all';
const byId = id => eraseLineContracts.find(contract => contract.caseId === id);
/** Grade a synthetic case after editing its reads; `edit` mutates the per-role grids. */
const grade = (caseId, edit, binding) => syntheticEraseLineGrade(caseId, binding, id => {
    const reads = syntheticEraseLineReads(id);
    if (id === caseId && edit) edit(reads);
    return reads;
});
const outcome = (caseId, edit, binding) => grade(caseId, edit, binding).outcome;
const note = (caseId, edit) => grade(caseId, edit).note;

test('the contracts pin exactly the three verified feature ids, fixture bytes and spellings', () => {
    assert.deepEqual([...eraseLineCaseIds], [RIGHT, LEFT, ALL]);
    assert.deepEqual(eraseLineContracts.map(contract => [...contract.pinnedFeeds]), [
        ['ABCDE\x1b[3G', '\x1b[K'], ['ABCDE\x1b[3G', '\x1b[1K'], ['ABCDE\r\nKEEP!\x1b[1;3H', '\x1b[2K']]);
    assert.deepEqual(eraseLineContracts.map(contract => contract.spelling), ['omitted', 'explicit1', 'explicit2']);
    assert.deepEqual(eraseLineContracts.map(contract => [...contract.pinnedCells]), [
        ['A', 'B', 'blank', 'blank', 'blank'], ['blank', 'blank', 'blank', 'D', 'E'], ['blank', 'blank', 'blank', 'blank', 'blank']]);
    assert.deepEqual(eraseLineContracts.map(contract => contract.pinnedExpected), [
        'row 0 A,B,blank,blank,blank; cursor before 2,0', 'row 0 blank,blank,blank,D,E; cursor before 2,0',
        'row 0 blank,blank,blank,blank,blank; row 1 KEEP!']);
    assert.equal(eraseLineContractsVersion, 1);
    assert.equal(eraseLineSuiteId, 'ntilde-owned-erase-line-graded-v1');
    assert.equal(eraseLineNoncePrefix, 'NTILDE_EL_');
    for (const contract of eraseLineContracts) {
        assert.match(contract.appearanceScope, /pixel capture path is capture-only/);
        assert.match(contract.terminalFixture.note, /capture-only/);
        assert.equal(eraseLineReads(contract), contract.roles.length);
        assert.doesNotThrow(() => validateEraseLineFeeds(contract, eraseLineFeeds(contract)));
        assert.throws(() => validateEraseLineFeeds(contract, [...eraseLineFeeds(contract).slice(0, -1), 'x']), /do not match/);
    }
});

test('the pinned fixture is replayed byte-exactly as the setup feed then the erase feed, and the other spellings stay labeled controls', () => {
    for (const contract of eraseLineContracts) {
        const feeds = eraseLineFeeds(contract);
        assert.equal(contract.setupRead, 1);
        assert.equal(contract.pinnedRead, 2);
        assert.equal(feeds.filter(feed => feed === contract.pinnedFeeds[0]).length, 4, 'the setup bytes are fed for the pinned read and re-fed for each of the three spelling controls');
        assert.equal(contract.others.length, 3);
        assert.ok(!contract.others.includes(contract.spelling));
        // The pinned setup read is followed immediately by the pinned erase feed, with nothing between them.
        const plan = contract.plan;
        const setupAt = plan.findIndex(step => step.feed === contract.pinnedFeeds[0]);
        assert.equal(plan[setupAt + 1].read, 'cells');
        assert.equal(plan[setupAt + 2].feed, contract.pinnedFeeds[1]);
        assert.equal(plan[setupAt + 3].read, 'cells');
    }
});

test('spellings stay distinct: omitted, explicit 0, 1 and 2 are all exercised and the right case is pinned to the omitted spelling', () => {
    const right = byId(RIGHT);
    const spelled = contract => Object.fromEntries(contract.roles.map(role => [role, contract.plan]));
    assert.equal(right.sequence, '\x1b[K');
    assert.deepEqual([...right.others], ['explicit0', 'explicit1', 'explicit2']);
    assert.deepEqual([...byId(LEFT).others], ['omitted', 'explicit0', 'explicit2']);
    assert.deepEqual([...byId(ALL).others], ['omitted', 'explicit0', 'explicit1']);
    for (const contract of eraseLineContracts) {
        const feeds = eraseLineFeeds(contract).join('|');
        for (const bytes of ['\x1b[K', '\x1b[0K', '\x1b[1K', '\x1b[2K']) assert.ok(feeds.includes(bytes), JSON.stringify(bytes));
    }
    assert.ok(spelled(right));
});

test('naming: no sibling erase case is substituted for the three pinned ids', () => {
    for (const contract of eraseLineContracts) {
        assert.ok(!/^erase\.(display|characters|screen)/.test(contract.caseId));
        const own = new Set([eraseLineReset, '\x1b[0m\x1b[2J\x1b[H']);
        for (const feed of eraseLineFeeds(contract).filter(item => !own.has(item))) {
            assert.ok(!/\x1b\[\??[0-9]*J|\x1b\[\?[0-9]*K|\x1b\[[0-9]*[XPL@M]/.test(feed), JSON.stringify(feed));
        }
    }
});

test('a complete synthetic run is conclusively supported for all three cases', () => {
    for (const id of eraseLineCaseIds) assert.deepEqual(syntheticEraseLineGrade(id), { outcome: 'supported' }, id);
});

test('the oracle in the fixtures is independent: it erases the cursor cell in both directions', () => {
    assert.equal(survivors('ABCDE', 0), 'AB   ');
    assert.equal(survivors('ABCDE', 1), '   DE');
    assert.equal(survivors('ABCDE', 2), '     ');
    assert.equal(survivors('ABCDEFGHIJ', 0), 'AB        ');
    assert.equal(survivors('ABCDEFGHIJ', 1), '   DEFGHIJ');
});

test('no-op erasure is unsupported once the seed is proven', () => {
    for (const id of eraseLineCaseIds) {
        assert.equal(outcome(id, reads => { reads.erased = { ...reads.setup }; }), 'unsupported', id);
        assert.match(note(id, reads => { reads.erased = { ...reads.setup }; }), /no-op/);
    }
});

test('wrong-direction erasure is unsupported and named', () => {
    assert.equal(outcome(RIGHT, reads => { reads.erased = { rows: [rowOf(survivors('ABCDE', 1))], cursor: [0, 2] }; }), 'unsupported');
    assert.match(note(RIGHT, reads => { reads.erased = { rows: [rowOf(survivors('ABCDE', 1))], cursor: [0, 2] }; }), /EL 1 instead of EL 0/);
    assert.match(note(LEFT, reads => { reads.erased = { rows: [rowOf(survivors('ABCDE', 0))], cursor: [0, 2] }; }), /EL 0 instead of EL 1/);
    assert.match(note(ALL, reads => { reads.erased = { rows: [rowOf(survivors('ABCDE', 0)), rowOf('KEEP!')], cursor: [0, 2] }; }), /EL 0 instead of EL 2/);
});

test('accidental whole-screen clearing is unsupported: the neighbor row and an emptied screen are both diagnosed', () => {
    assert.match(note(ALL, reads => { reads.erased = { rows: [], cursor: [0, 2] }; }), /Row 1 lost KEEP!/);
    assert.match(note(RIGHT, reads => { reads.erased = { rows: [], cursor: [0, 2] }; }), /EL 2 instead of EL 0/);
    assert.equal(outcome(RIGHT, reads => { reads.wide = { rows: [], cursor: [1, 2] }; }), 'unsupported');
    assert.match(note(LEFT, reads => { reads.wide = { rows: [rowOf('ABOVE-ROW!')], cursor: [1, 2] }; }), /wide-row control differs/);
});

test('cursor inclusion boundary: the cursor cell is erased by EL 0 and EL 1, and a one-cell error is unsupported', () => {
    // EL 0 that spares the cursor cell, and EL 1 that spares it, are each one cell wrong.
    assert.equal(outcome(RIGHT, reads => { reads.erased = { rows: [rowOf('ABC  ')], cursor: [0, 2] }; }), 'unsupported');
    assert.equal(outcome(LEFT, reads => { reads.erased = { rows: [rowOf('  C DE')], cursor: [0, 2] }; }), 'unsupported');
    // EL 0 that erases one cell too many, EL 1 that erases one too few.
    assert.equal(outcome(RIGHT, reads => { reads.erased = { rows: [rowOf('A    ')], cursor: [0, 2] }; }), 'unsupported');
    assert.equal(outcome(LEFT, reads => { reads.erased = { rows: [rowOf('    E')], cursor: [0, 2] }; }), 'unsupported');
    assert.equal(outcome(RIGHT, reads => { reads.wide = { rows: [rowOf('ABOVE-ROW!'), rowOf('ABC       '), rowOf('BELOW-ROW!')], cursor: [1, 2] }; }), 'unsupported');
});

test('retained-text corruption and unrelated-row changes are unsupported', () => {
    assert.equal(outcome(RIGHT, reads => { reads.erased = { rows: [rowOf('AX   ')], cursor: [0, 2] }; }), 'unsupported');
    assert.equal(outcome(LEFT, reads => { reads.erased = { rows: [rowOf('   DX')], cursor: [0, 2] }; }), 'unsupported');
    assert.equal(outcome(ALL, reads => { reads.erased = { rows: [rowOf('     '), rowOf('KEEP?')], cursor: [0, 2] }; }), 'unsupported');
    assert.equal(outcome(RIGHT, reads => { reads.erased = { rows: [rowOf('AB   '), null, rowOf('stray')], cursor: [0, 2] }; }), 'unsupported');
    assert.equal(outcome(LEFT, reads => { reads.wide = { rows: [rowOf('ABOVE-ROW!'), rowOf('   DEFGHIJ'), rowOf('BELOW-ROW?')], cursor: [1, 2] }; }), 'unsupported');
    // Text past the fifth column is outside the pinned five cells but inside the contract.
    assert.equal(outcome(RIGHT, reads => { reads.erased = { rows: [rowOf('AB   ZZ')], cursor: [0, 2] }; }), 'unsupported');
});

test('the cursor must stay at its seeded cell; the pinned probe never reads it, so the contract is stricter', () => {
    for (const id of eraseLineCaseIds) {
        assert.equal(outcome(id, reads => { reads.erased = { ...reads.erased, cursor: [0, 0] }; }), 'unsupported', id);
        assert.match(note(id, reads => { reads.erased = { ...reads.erased, cursor: [0, 5] }; }), /cursor moved/);
    }
    assert.equal(outcome(RIGHT, reads => { reads.wide = { ...reads.wide, cursor: [1, 3] }; }), 'unsupported');
});

test('required attributes: retained cells must keep bold and no stored color; erased cells must read blank', () => {
    const plainA = ['bce'];
    assert.ok(plainA);
    assert.equal(outcome(RIGHT, reads => { reads.bce = { rows: [rowOf('AB   ')], cursor: [0, 2] }; }), 'unsupported');
    assert.equal(outcome(LEFT, reads => { reads.bce = { rows: [[...rowOf('   '), colorCell('D', { attrs: ['bold', 'italic'] }), colorCell('E', { attrs: ['bold'] })]], cursor: [0, 2] }; }), 'unsupported');
    assert.equal(outcome(RIGHT, reads => { reads.bce = { rows: [[colorCell('A', { attrs: ['bold'] }), colorCell('B', { attrs: ['bold'] }), colorCell('X', { attrs: ['bold'] })]], cursor: [0, 2] }; }), 'unsupported');
});

test('background-color erase is observed, never required: painted erased cells keep the verdict and are reported', () => {
    const painted = reads => {
        reads.bce = { rows: [[colorCell('A', { attrs: ['bold'] }), colorCell('B', { attrs: ['bold'] }),
            ...Array.from({ length: 58 }, () => colorCell(' ', { bg: { kind: 'palette', index: 4 } }))]], cursor: [0, 2] };
    };
    assert.deepEqual(grade(RIGHT, painted), { outcome: 'supported' });
    const contract = byId(RIGHT);
    const reads = syntheticEraseLineReads(RIGHT);
    painted(reads);
    const screens = contract.roles.map(role => syntheticEraseLineScreens(RIGHT, paletteBinding(), () => reads)[contract.roles.indexOf(role)]);
    assert.equal(contract.observed(eraseLineObservation(contract, screens)).attributes.backgroundColorErase, 'erased cells carry the current background');
    const plain = contract.roles.map((_, index) => syntheticEraseLineScreens(RIGHT)[index]);
    assert.equal(contract.observed(eraseLineObservation(contract, plain)).attributes.backgroundColorErase, 'erased cells keep the default background');
});

test('a sibling spelling that misbehaves is reported but never changes the pinned grade', () => {
    const wrongSiblings = reads => { reads.explicit1 = { ...reads.explicit1, rows: [rowOf('ABCDE')] }; reads.explicit2 = { rows: [rowOf('AB   ')], cursor: [0, 2] }; };
    assert.deepEqual(grade(RIGHT, wrongSiblings), { outcome: 'supported' });
    const contract = byId(RIGHT);
    const reads = syntheticEraseLineReads(RIGHT);
    wrongSiblings(reads);
    const screens = syntheticEraseLineScreens(RIGHT, paletteBinding(), () => reads);
    const observed = contract.observed(eraseLineObservation(contract, screens));
    assert.equal(observed.spellingControls.explicit0.matchesEcma48, true);
    assert.equal(observed.spellingControls.explicit0.sameAsPinned, true);
    assert.equal(observed.spellingControls.explicit1.matchesEcma48, false);
    assert.equal(observed.spellingControls.explicit2.matchesEcma48, false);
});

test('missing or invalid setup is inconclusive, never unsupported', () => {
    for (const id of eraseLineCaseIds) {
        assert.deepEqual(grade(id, reads => { reads.setup = { rows: [rowOf('ABCDX')], cursor: [0, 2] }; }), { outcome: 'inconclusive', note: pinnedFixtureNote }, id);
        assert.deepEqual(grade(id, reads => { reads.setup = { ...reads.setup, cursor: [0, 3] }; }), { outcome: 'inconclusive', note: pinnedFixtureNote }, id);
        assert.equal(outcome(id, reads => { reads.setup = { rows: [], cursor: [0, 2] }; }), 'inconclusive', id);
        assert.equal(outcome(id, reads => { reads.setup = { rows: [rowOf('ABCDE', ['bold'])], cursor: [0, 2] }; }), 'inconclusive', id);
        assert.equal(outcome(id, reads => { reads.initial = { rows: [rowOf('x')], cursor: [0, 0] }; }), 'inconclusive', id);
    }
    assert.equal(outcome(ALL, reads => { reads.setup = { rows: [rowOf('ABCDE')], cursor: [0, 2] }; }), 'inconclusive', 'the neighbor row must be seeded');
    assert.equal(outcome(ALL, reads => { reads.setup = { rows: [rowOf('ABCDE'), rowOf('KEEP!'), rowOf('x')], cursor: [0, 2] }; }), 'inconclusive', 'nothing else may be on screen');
});

test('invalid controls make the grade inconclusive: wide-row seed and attribute seed must be measured', () => {
    for (const id of eraseLineCaseIds) {
        assert.equal(outcome(id, reads => { reads.wideBefore = { ...reads.wideBefore, cursor: [0, 2] }; }), 'inconclusive', id);
        assert.match(note(id, reads => { reads.wideBefore = { rows: [rowOf('ABOVE-ROW!')], cursor: [1, 2] }; }), /wide-row control seed/);
        assert.equal(outcome(id, reads => { reads.bceBefore = { rows: [rowOf('ABCDE')], cursor: [0, 2] }; }), 'inconclusive', id);
        assert.match(note(id, reads => { reads.bceBefore = { rows: [rowOf('ABCDE')], cursor: [0, 2] }; }), /attribute control seed/);
    }
});

test('DECSCNM not proven off is inconclusive', () => {
    const on = { ...paletteBinding(), reverseVideo: true };
    assert.equal(syntheticEraseLineGrade(RIGHT, on).outcome, 'inconclusive');
});

test('too small a screen is inconclusive with the pinned geometry note', () => {
    const contract = byId(RIGHT);
    const observation = eraseLineObservation(contract, syntheticEraseLineScreens(RIGHT));
    observation.setup = { ...observation.setup, cols: 6 };
    assert.deepEqual(gradeEraseLineCase(contract, observation), { outcome: 'inconclusive', note: pinnedGeometryNote });
});

test('grader refuses missing groups and unknown revisions', () => {
    const contract = byId(RIGHT);
    const observation = eraseLineObservation(contract, syntheticEraseLineScreens(RIGHT));
    delete observation.bce;
    assert.throws(() => gradeEraseLineCase(contract, observation), /Missing erase\.line\.right read captures/);
    assert.throws(() => eraseLineObservation(contract, []), /Missing erase-in-line read captures/);
    assert.throws(() => gradeEraseLineCase(contract, eraseLineObservation(contract, syntheticEraseLineScreens(RIGHT)), 2), /Unknown erase-in-line contracts revision 2/);
});

test('the erased range of the attribute control is the real range: EL 0 runs to the last column, EL 1 stops at the cursor, EL 2 covers the row', () => {
    const bold = char => colorCell(char, { attrs: ['bold'] });
    const blue = () => colorCell(' ', { bg: { kind: 'palette', index: 4 } });
    const report = (caseId, cells) => {
        const contract = byId(caseId);
        const reads = syntheticEraseLineReads(caseId);
        reads.bce = { rows: [cells], cursor: [0, 2] };
        const screens = syntheticEraseLineScreens(caseId, paletteBinding(), () => reads);
        const observation = eraseLineObservation(contract, screens);
        return { outcome: gradeEraseLineCase(contract, observation).outcome, bce: contract.observed(observation).attributes.backgroundColorErase };
    };
    const blanks = count => Array.from({ length: count }, blue);
    assert.deepEqual(report(RIGHT, [bold('A'), bold('B'), ...blanks(58)]), { outcome: 'supported', bce: 'erased cells carry the current background' });
    assert.deepEqual(report(LEFT, [...blanks(3), bold('D'), bold('E')]), { outcome: 'supported', bce: 'erased cells carry the current background' });
    assert.deepEqual(report(ALL, blanks(60)), { outcome: 'supported', bce: 'erased cells carry the current background' });
    // Painting only part of the erased range is reported as mixed, and painting a retained column is a change to a retained cell.
    assert.equal(report(RIGHT, [bold('A'), bold('B'), ...blanks(10)]).bce, 'mixed');
    assert.equal(report(RIGHT, [bold('A'), blue(), ...blanks(58)]).outcome, 'unsupported');
    assert.equal(report(LEFT, [...blanks(3), bold('D'), bold('E'), blue()]).outcome, 'unsupported');
});

test('the observed record collapses a painted erased tail into one range entry and keeps neutral cells as strings', () => {
    const contract = byId(RIGHT);
    const reads = syntheticEraseLineReads(RIGHT);
    reads.bce = { rows: [[colorCell('A', { attrs: ['bold'] }), colorCell('B', { attrs: ['bold'] }),
        ...Array.from({ length: 58 }, () => colorCell(' ', { bg: { kind: 'palette', index: 4 } }))]], cursor: [0, 2] };
    const observed = contract.observed(eraseLineObservation(contract, syntheticEraseLineScreens(RIGHT, paletteBinding(), () => reads)));
    const ink = observed.attributes.after.ink;
    assert.equal(ink.length, 3);
    assert.deepEqual([ink[0].col, ink[0].char, ink[1].col, ink[1].char], [0, 'A', 1, 'B']);
    assert.deepEqual([ink[2].row, ink[2].cols, ink[2].char, ink[2].bgIndex], [0, [2, 59], ' ', 4]);
    assert.deepEqual(observed.setup.ink, ['0,0,A', '0,1,B', '0,2,C', '0,3,D', '0,4,E']);
});

test('the background observation compares erased cells with the SGR 44 pen reference, so a wrong color is never reported as the current background', () => {
    const bold = char => colorCell(char, { attrs: ['bold'] });
    const tail = (index, count = 58) => Array.from({ length: count }, () => colorCell(' ', { bg: { kind: 'palette', index } }));
    const observe = (edit, caseId = RIGHT) => {
        const contract = byId(caseId);
        const reads = syntheticEraseLineReads(caseId);
        edit(reads);
        const observation = eraseLineObservation(contract, syntheticEraseLineScreens(caseId, paletteBinding(), () => reads));
        return { outcome: gradeEraseLineCase(contract, observation).outcome, bce: contract.observed(observation).attributes.backgroundColorErase };
    };
    const right = cells => reads => { reads.bce = { rows: [[bold('A'), bold('B'), ...cells]], cursor: [0, 2] }; };
    // The pen stores palette 4; palette 1 is a different color. Non-gating: the verdict stays supported either way.
    assert.deepEqual(observe(right(tail(1))), { outcome: 'supported', bce: 'erased cells carry a different non-default background than the current pen' });
    assert.deepEqual(observe(right(tail(4))), { outcome: 'supported', bce: 'erased cells carry the current background' });
    assert.equal(observe(right([...tail(4, 30), ...tail(1, 28)])).bce, 'mixed');
    // A reference cell that did not store a background cannot support any claim about the pen.
    assert.equal(observe(reads => { right(tail(4))(reads); reads.pen = { rows: [[colorCell('X')]], cursor: [0, 1] }; }).bce,
        'unmeasured: the reference pen cell was not stored with a background');
});
