import assert from 'node:assert/strict';
import { test } from 'node:test';
import { attrRectContracts, attrRectCaseIds, attrRectContractsVersion, attrRectSuiteId, attrRectEffectContractId, attrRectOracle, attrRectFeeds,
    attrRectReads, gradeAttrRectCase, attrRectObservation, validateAttrRectFeeds, pinnedNotes } from './attr-rect-contracts.mjs';
import { syntheticAttrRectReads, syntheticAttrRectScreens, syntheticAttrRectGrade } from './fixtures/attr-rect-evidence.mjs';

const [deccara, decrara, decsace] = attrRectContracts;
const { applyOp, plain4, fix4x8, pinnedC, pinnedR, setScenarios } = attrRectOracle;
const marksOf = screens => screens;
void marksOf;

/** Grade one case from the synthetic correct App after `edit` changed some of its reads. */
const grade = (caseId, edit = () => {}) => {
    const readsOf = id => { const reads = syntheticAttrRectReads(id); if (id === caseId) edit(reads); return reads; };
    const contract = attrRectContracts.find(item => item.caseId === caseId);
    return gradeAttrRectCase(contract, attrRectObservation(contract, syntheticAttrRectScreens(caseId, undefined, readsOf)));
};
const outcome = (caseId, edit) => grade(caseId, edit).outcome;
const withAttrs = (grid, places, attrs) => { const copy = new Map(grid); for (const place of places) copy.set(place, { ...copy.get(place), attrs }); return copy; };
const op = (top, left, bottom, right, selectors, rectangle, reverse = false) => ({ top, left, bottom, right, selectors, reverse, rectangle });

test('the three pinned ids, the versioned identity and the separately identified effect contract', () => {
    assert.deepEqual(attrRectCaseIds, ['editing.deccara', 'editing.decrara', 'editing.decsace']);
    assert.equal(attrRectContractsVersion, 1);
    assert.equal(attrRectSuiteId, 'ntilde-owned-attr-rect-graded-v1');
    assert.equal(attrRectEffectContractId, 'ntilde.attr-rect.effect/v1');
    for (const contract of attrRectContracts) {
        assert.equal(contract.effectContract, attrRectEffectContractId);
        assert.equal(contract.pinnedPath, 'termless');
        assert.equal(Object.isFrozen(contract), true);
    }
    assert.equal(decsace.pinnedAlwaysInconclusive, true);
    assert.equal(deccara.pinnedAlwaysInconclusive, false);
    assert.throws(() => gradeAttrRectCase(deccara, {}, 2), /Unknown attribute-rectangle contracts revision 2/);
});

test('the exact pinned fixture bytes and the pinned note texts are carried verbatim', () => {
    assert.deepEqual([...deccara.pinnedFeeds], ['\x1b[1;1HaaaaaZ\x1b[2;1HbbbbbY\x1b[3;1HcccccX', '\x1b[1;1H', '\x1b[1;1;3;5;7$r']);
    assert.deepEqual([...decrara.pinnedFeeds], ['\x1b[1;1H\x1b[7maaaaa\x1b[0mZ\x1b[2;1H\x1b[7mbbbbb\x1b[0mY\x1b[3;1H\x1b[7mccccc\x1b[0mX', '\x1b[1;1H', '\x1b[1;1;3;5;7$t']);
    assert.deepEqual([...decsace.pinnedFeeds], ['\x1b[1;1H\x1b[2*x']);
    assert.equal(pinnedNotes['editing.deccara'].control, 'DECCARA character or outside attribute control changed');
    assert.equal(pinnedNotes['editing.decrara'].control, 'DECRARA character or outside attribute control changed');
    // Every pinned feed appears byte for byte in the plan, so the pinned replay cannot drift from what was written.
    for (const contract of attrRectContracts) for (const feed of contract.pinnedFeeds) assert.ok(attrRectFeeds(contract).includes(feed), JSON.stringify(feed));
});

test('plans start from the measured reset, read once per group and never RIS', () => {
    for (const contract of attrRectContracts) {
        const feeds = attrRectFeeds(contract);
        assert.equal(feeds[0], '\x1b[?5l\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[2J\x1b[H');
        assert.equal(attrRectReads(contract), contract.roles.length);
        assert.ok(feeds.every(feed => !feed.includes('\x1bc')), 'no RIS');
        assert.equal(new Set(contract.roles).size, contract.roles.length);
        assert.deepEqual(contract.pinnedReads.length, contract.pinnedFeeds.length);
        validateAttrRectFeeds(contract, feeds);
        assert.throws(() => validateAttrRectFeeds(contract, [...feeds.slice(1)]), /do not match the pinned contract/);
        // The extent is explicitly restored to the stream after every rectangle selection: the last extent feed of a plan is a stream selection.
        const extentFeeds = feeds.filter(feed => /\x1b\[\d*\*x/.test(feed) && feed !== '\x1b[1;1H\x1b[2*x');
        if (extentFeeds.length) assert.equal(extentFeeds.at(-1), '\x1b[0*x', contract.caseId);
    }
    assert.ok(decsace.plan.some(step => step.feed === '\x1b[1;1H\x1b[2*x'));
});

test('a correctly behaving App is supported by the effect contract and the stream is the measured default', () => {
    for (const caseId of attrRectCaseIds) assert.deepEqual(syntheticAttrRectGrade(caseId), { outcome: 'supported', defaultExtent: 'stream' });
});

test('the pinned fixture at the default extent is the VT510 stream: the flank cells change, the rectangle experiment keeps them', () => {
    const set = syntheticAttrRectReads('editing.deccara');
    assert.equal([...set.edited.grid].filter(([place, cell]) => cell.attrs.includes('inverse')).map(([place]) => place).join(' '),
        '0,0 0,1 0,2 0,3 0,4 0,5 1,0 1,1 1,2 1,3 1,4 1,5 2,0 2,1 2,2 2,3 2,4');
    assert.equal([...set.rect.grid].filter(([, cell]) => cell.attrs.includes('inverse')).length, 15);
    assert.ok(![...set.rect.grid].some(([place, cell]) => place.endsWith(',5') && cell.attrs.includes('inverse')));
    const reverse = syntheticAttrRectReads('editing.decrara');
    assert.equal([...reverse.edited.grid].filter(([, cell]) => cell.attrs.includes('inverse')).map(([place]) => place).join(' '), '0,5 1,5');
    assert.equal([...reverse.rect.grid].filter(([, cell]) => cell.attrs.includes('inverse')).length, 0);
});

test('an App whose default extent is the rectangle is unsupported, with the default extent recorded', () => {
    for (const [caseId, reverse] of [['editing.deccara', false], ['editing.decrara', true]]) {
        const result = grade(caseId, reads => { reads.edited = reads.rect; reads.witness = reads.rect; });
        assert.equal(result.outcome, 'unsupported');
        assert.equal(result.defaultExtent, 'rectangle');
        assert.match(result.note, /default extent is the rectangle/);
        void reverse;
    }
});

test('an App that cannot select the rectangle, or does nothing, is unsupported', () => {
    for (const caseId of ['editing.deccara', 'editing.decrara']) {
        assert.equal(outcome(caseId, reads => { reads.rect = reads.edited; }), 'unsupported', `${caseId} rectangle ignored`);
        const seedGrid = caseId === 'editing.decrara' ? pinnedR.grid : pinnedC.grid;
        assert.equal(outcome(caseId, reads => { reads.edited = { grid: seedGrid, cursor: [0, 0] }; }), 'unsupported', `${caseId} no-op default`);
    }
});

test('DECCARA must set rather than replace, clear only what it names, and leave unrelated attributes and colors alone', () => {
    // Replace: the pre-attributed row loses its italic, colors and bold/underline/blink.
    assert.equal(outcome('editing.deccara', reads => { reads.pre = { grid: new Map(), cursor: [5, 10] }; }), 'unsupported');
    const pre = setScenarios.find(item => item.name === 'pre');
    const wiped = new Map([...syntheticAttrRectReads('editing.deccara').pre.grid].map(([place, cell]) => [place, { char: cell.char, attrs: cell.attrs.includes('inverse') ? ['inverse'] : [] }]));
    assert.equal(outcome('editing.deccara', reads => { reads.pre = { grid: wiped, cursor: pre.cursor }; }), 'unsupported');
    // Selector 22 clearing italic as well as bold.
    const cleared = new Map(syntheticAttrRectReads('editing.deccara').clear.grid);
    cleared.set('0,0', { ...cleared.get('0,0'), attrs: ['blink', 'inverse'] });
    assert.equal(outcome('editing.deccara', reads => { reads.clear = { grid: cleared, cursor: [5, 10] }; }), 'unsupported');
    // Selector 0 clearing the color.
    const uncolored = new Map(syntheticAttrRectReads('editing.deccara').clear.grid);
    uncolored.set('1,0', { char: 'g', attrs: ['italic'] });
    assert.equal(outcome('editing.deccara', reads => { reads.clear = { grid: uncolored, cursor: [5, 10] }; }), 'unsupported');
});

test('DECCARA must not change the cursor, the SGR pen, cells outside the area or the buffer state', () => {
    const base = syntheticAttrRectReads('editing.deccara');
    assert.equal(outcome('editing.deccara', reads => { reads.pen = { ...base.pen, cursor: [2, 0] }; }), 'unsupported');
    const noBold = new Map(base.pen.grid);
    noBold.set('3,0', { char: 'P', attrs: ['inverse'], bg: 4 });
    assert.equal(outcome('editing.deccara', reads => { reads.pen = { grid: noBold, cursor: [3, 1] }; }), 'unsupported');
    const noBg = new Map(base.pen.grid);
    noBg.set('3,0', { char: 'P', attrs: ['bold'] });
    assert.equal(outcome('editing.deccara', reads => { reads.pen = { grid: noBg, cursor: [3, 1] }; }), 'unsupported');
    assert.equal(outcome('editing.deccara', reads => { reads.pre = { ...base.pre, cursor: [0, 0] }; }), 'unsupported');
    assert.equal(outcome('editing.deccara', reads => { reads.origin = { ...base.origin, buffer: { scrollTop: 0, scrollBottom: 3, originMode: true } }; }), 'unsupported');
});

test('off-by-one bounds, the wrong rectangle and ignored unknown selectors are measured mismatches', () => {
    const base = syntheticAttrRectReads('editing.deccara');
    const shifted = new Map(base.bounds.grid);
    shifted.delete('1,4');
    assert.equal(outcome('editing.deccara', reads => { reads.bounds = { grid: shifted, cursor: [0, 0] }; }), 'unsupported');
    const wide = applyOp(plain4.grid, op(2, 2, 3, 6, [7], true));
    assert.equal(outcome('editing.deccara', reads => { reads.bounds = { grid: wide, cursor: [0, 0] }; }), 'unsupported');
    const unknownAsClear = applyOp(plain4.grid, op(1, 1, 1, 3, [0], true));
    assert.equal(outcome('editing.deccara', reads => { reads.unknown = { grid: unknownAsClear, cursor: [0, 0] }; }), 'unsupported');
    assert.equal(outcome('editing.deccara', reads => { reads.unknown = { grid: applyOp(applyOp(applyOp(plain4.grid, op(1, 1, 1, 3, [1], true)), op(2, 1, 2, 3, [1, 4], true)), op(3, 1, 3, 3, [1], true)), cursor: [0, 0] }; }), 'unsupported');
});

test('origin mode must offset the rows and the margins must not clip the rectangle', () => {
    const origin = setScenarios.find(item => item.name === 'origin');
    const base = syntheticAttrRectReads('editing.deccara');
    const noOffset = applyOp(origin.seed.grid, op(2, 2, 5, 4, [7], true));
    assert.equal(outcome('editing.deccara', reads => { reads.origin = { ...base.origin, grid: noOffset }; }), 'unsupported');
    const clipped = applyOp(origin.seed.grid, op(3, 2, 4, 4, [7], true));
    assert.equal(outcome('editing.deccara', reads => { reads.origin = { ...base.origin, grid: clipped }; }), 'unsupported');
    assert.equal(outcome('editing.deccara', reads => { reads.originBefore = { ...base.originBefore, buffer: undefined }; }), 'inconclusive');
});

test('invalid setup is inconclusive: a wrong seed, cursor, geometry, DECSCNM, dirty screen or an unrestored extent', () => {
    for (const caseId of ['editing.deccara', 'editing.decrara']) {
        const base = syntheticAttrRectReads(caseId);
        assert.equal(outcome(caseId, reads => { reads.seed = { ...base.seed, grid: new Map() }; }), 'inconclusive', `${caseId} seed`);
        assert.equal(outcome(caseId, reads => { reads.cursor = { ...base.cursor, cursor: [1, 1] }; }), 'inconclusive', `${caseId} cursor`);
        assert.equal(outcome(caseId, reads => { reads.plainBefore = { grid: new Map(), cursor: [0, 0] }; }), 'inconclusive', `${caseId} plain seed`);
        assert.equal(outcome(caseId, reads => { reads.initial = { grid: new Map([['0,0', { char: 'x', attrs: [] }]]), cursor: [0, 0] }; }), 'inconclusive', `${caseId} blank control`);
        assert.equal(outcome(caseId, reads => { reads.witness = reads.rect; }), 'inconclusive', `${caseId} restoration witness`);
    }
    assert.equal(outcome('editing.deccara', reads => { reads.preBefore = { grid: new Map(), cursor: [5, 10] }; }), 'inconclusive');
    assert.equal(outcome('editing.deccara', reads => { reads.clearBefore = { grid: new Map(), cursor: [5, 10] }; }), 'inconclusive');
    const contract = deccara;
    const screens = syntheticAttrRectScreens('editing.deccara');
    const tooSmall = attrRectObservation(contract, screens);
    tooSmall.initial.rows = 4;
    assert.deepEqual(gradeAttrRectCase(contract, tooSmall), { outcome: 'inconclusive', note: 'Measured geometry cannot fit the attribute-rectangle controls' });
    const reversed = attrRectObservation(contract, screens);
    reversed.reverseVideo = true;
    assert.match(gradeAttrRectCase(contract, reversed).note, /DECSCNM reverse video was not proven off/);
    const unknown = attrRectObservation(contract, screens);
    unknown.reverseVideo = null;
    assert.equal(gradeAttrRectCase(contract, unknown).outcome, 'inconclusive');
    assert.throws(() => gradeAttrRectCase(contract, { ...unknown, plainBefore: undefined }), /Missing editing.deccara read captures/);
});

test('DECRARA must toggle, restore on a second application, follow the operand table and never XOR unrelated flags', () => {
    const base = syntheticAttrRectReads('editing.decrara');
    // Toggle implemented as set: the inverse cells stay inverse.
    const asSet = applyOp(attrRectOracle.reverseMix.seed.grid, op(1, 1, 2, 6, [7], true, false));
    assert.equal(outcome('editing.decrara', reads => { reads.mix = { grid: asSet, cursor: [5, 10] }; }), 'unsupported');
    // The second application does not restore (it toggles a different attribute).
    assert.equal(outcome('editing.decrara', reads => { reads.mixAgain = { grid: applyOp(base.mix.grid, op(1, 1, 2, 6, [1], true, true)), cursor: [5, 10] }; }), 'unsupported');
    // Operand table: 22 honored as a clear, selector 0 ignored, omitted list ignored, repeats not cancelling.
    const seedGrid = attrRectOracle.operandSeed.grid;
    const rows = [op(1, 1, 1, 6, [1], true, true), op(2, 1, 2, 6, [0], true, true), op(3, 1, 3, 6, [], true, true), op(4, 1, 4, 6, [22, 24, 25, 27], true, true), op(5, 1, 5, 6, [1, 1], true, true), op(6, 1, 6, 6, [4, 5], true, true)];
    const wrong = [[], [22], [22], [0], [1], [4]]; // each row's selector list replaced by one the table does not give it
    const mutated = [...rows.keys()].map(index => ({ index, grid: rows.reduce((grid, item, at) => applyOp(grid, at === index ? { ...item, selectors: wrong[index] } : item), seedGrid) }));
    for (const { index, grid } of mutated) assert.equal(outcome('editing.decrara', reads => { reads.ops = { grid, cursor: [5, 10] }; }), 'unsupported', `operand row ${index}`);
    // A blind XOR of every internal flag also flips italic: the toggle must leave italic alone.
    const xor = new Map(base.mix.grid);
    xor.set('1,1', { ...xor.get('1,1'), attrs: ['inverse'] });
    assert.equal(outcome('editing.decrara', reads => { reads.mix = { grid: xor, cursor: [5, 10] }; }), 'unsupported');
    const xorOps = new Map(base.ops.grid);
    xorOps.set('1,0', { ...xorOps.get('1,0'), attrs: xorOps.get('1,0').attrs.filter(name => name !== 'italic') });
    assert.equal(outcome('editing.decrara', reads => { reads.ops = { grid: xorOps, cursor: [5, 10] }; }), 'unsupported');
    assert.equal(outcome('editing.decrara', reads => { reads.mixBefore = { grid: new Map(), cursor: [5, 10] }; }), 'inconclusive');
    assert.equal(outcome('editing.decrara', reads => { reads.opsBefore = { grid: new Map(), cursor: [5, 10] }; }), 'inconclusive');
});

test('DECSACE: stream and rectangle cells across the first, intermediate and last rows are told apart, and the pinned verdict stays out of it', () => {
    const base = syntheticAttrRectReads('editing.decsace');
    const stream = applyOp(fix4x8.grid, { ...attrRectOracle.extentOp(), rectangle: false }), rect = applyOp(fix4x8.grid, { ...attrRectOracle.extentOp(), rectangle: true });
    assert.deepEqual(['stream0', 'stream1', 'omitted', 'witness'].map(role => JSON.stringify([...base[role].grid])), Array(4).fill(JSON.stringify([...stream])));
    assert.notEqual(JSON.stringify([...stream]), JSON.stringify([...rect]));
    // Each way of ignoring the selection is a measured mismatch.
    for (const role of ['stream0', 'stream1', 'omitted']) assert.equal(outcome('editing.decsace', reads => { reads[role] = reads.rectangle; }), 'unsupported', role);
    assert.equal(outcome('editing.decsace', reads => { reads.rectangle = reads.stream0; }), 'unsupported');
    assert.equal(outcome('editing.decsace', reads => { reads.rectReverse = reads.streamReverse; }), 'unsupported');
    assert.equal(outcome('editing.decsace', reads => { reads.streamReverse = reads.rectReverse; }), 'unsupported');
    // An extent that changes nothing (consumed but ignored) is not support.
    assert.equal(outcome('editing.decsace', reads => { for (const role of ['stream0', 'stream1', 'omitted', 'rectangle', 'rectReverse', 'streamReverse', 'witness']) reads[role] = reads.fixBefore; }), 'unsupported');
    // The extent must be independent of margins, origin mode, the cursor and the pen.
    assert.equal(outcome('editing.decsace', reads => { reads.isolation = { ...base.isolation, buffer: { scrollTop: 0, scrollBottom: 3, originMode: true } }; }), 'unsupported');
    assert.equal(outcome('editing.decsace', reads => { reads.isolation = { ...base.isolation, cursor: [0, 0] }; }), 'unsupported');
    assert.equal(outcome('editing.decsace', reads => { reads.isolationPen = { ...base.isolationPen, grid: fix4x8.grid }; }), 'unsupported');
    // Setup and restoration problems are inconclusive.
    assert.equal(outcome('editing.decsace', reads => { reads.fixBefore = { grid: new Map(), cursor: [0, 0] }; }), 'inconclusive');
    assert.equal(outcome('editing.decsace', reads => { reads.witness = reads.rectangle; }), 'inconclusive');
    assert.equal(outcome('editing.decsace', reads => { reads.stream0 = reads.rectangle; reads.fixBefore = { grid: new Map(), cursor: [0, 0] }; }), 'inconclusive');
    assert.match(grade('editing.decsace', reads => { reads.stream0 = reads.rectangle; }).note, /DECSACE 0 left the rectangle extent in force/);
});

test('an undefined DECSACE value is observed, not graded', () => {
    const rectangleKept = grade('editing.decsace');
    assert.equal(rectangleKept.outcome, 'supported');
    assert.equal(grade('editing.decsace', reads => { reads.ignored = reads.stream0; }).outcome, 'supported');
});

test('the contract adds no attribute grade of its own beyond stored cells: the scope names what is not claimed', () => {
    for (const contract of attrRectContracts) {
        assert.match(contract.appearanceScope, /no rendered appearance|never read|extent setting itself is never read/);
        assert.doesNotMatch(contract.appearanceScope, /pixel|glyph/i);
    }
    assert.match(decsace.pinnedCriterion, /always the pinned inconclusive/);
    assert.match(deccara.pinnedCriterion, /right flank column NOT inverse/);
});
