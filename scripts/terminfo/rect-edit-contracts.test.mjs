import assert from 'node:assert/strict';
import { test } from 'node:test';
import { rectEditContracts, rectEditCaseIds, rectEditContractsVersion, rectEditSuiteId, rectEditNoncePrefix, rectEditModel,
    gradeRectEditCase, rectEditObservation, rectEditReset, rectEditFeeds, rectEditReads, validateRectEditFeeds, pinnedGeometryNote,
    rectEditFixture } from './rect-edit-contracts.mjs';
import { syntheticRectEditReads, syntheticRectEditScreens, syntheticRectEditGrade, rowOf, rowsOfText, colorCell, paletteBinding, seg,
    rectEditRows } from './fixtures/rect-edit-evidence.mjs';

const FRA = 'editing.decfra', ERA = 'editing.decera', CRA = 'editing.deccra';
const byId = id => rectEditContracts.find(contract => contract.caseId === id);
/** Grade a synthetic case after editing its reads; `edit` mutates the per-role grids. */
const grade = (caseId, edit, binding) => syntheticRectEditGrade(caseId, binding, id => {
    const reads = syntheticRectEditReads(id);
    if (id === caseId && edit) edit(reads);
    return reads;
});
const outcome = (caseId, edit, binding) => grade(caseId, edit, binding).outcome;
const note = (caseId, edit) => grade(caseId, edit).note;
/** Replace a role's rows by texts, keeping its cursor and state. */
const texts = (reads, role, rows, cursor) => { reads[role] = { ...reads[role], rows: rowsOfText(rows), ...(cursor ? { cursor } : {}) }; };
const block = ['ABCDEFGHIJ', 'KLMNOPQRST', 'UVWXYZabcd', 'efghijklmn', 'opqrstuvwx', 'yz01234567', '89!@#$%^&*'];

test('the contracts pin exactly the three verified feature ids, fixture bytes and notes', () => {
    assert.deepEqual([...rectEditCaseIds], [FRA, ERA, CRA]);
    assert.deepEqual(rectEditContracts.map(contract => [...contract.pinnedFeeds]), [
        ['\x1b[1;1HaaaaaZ\x1b[2;1HbbbbbY\x1b[3;1HcccccW', '\x1b[1;1H', '\x1b[88;1;1;3;5$x'],
        ['\x1b[1;1HaaaaaZ\x1b[2;1HbbbbbY\x1b[3;1HcccccX', '\x1b[1;1H', '\x1b[1;1;3;5$z'],
        ['\x1b[1;1HABCDE.........\x1b[2;1HFGHIJ.........\x1b[3;1H..............\x1b[4;1H..............\x1b[5;1H.........12345\x1b[6;1H.........67890',
            '\x1b[1;1H', '\x1b[1;1;2;5;1;5;10$v']]);
    assert.deepEqual(rectEditContracts.map(contract => contract.pinnedExpected), [
        'DECFRA fills the measured 3x5 area and preserves its measured right flank',
        'DECERA blanks measured 3x5 cells and preserves their measured right flank',
        'DECCRA copies measured 2x5 source cells to row 5 column 10 without changing the source']);
    assert.equal(rectEditContractsVersion, 1);
    assert.equal(rectEditSuiteId, 'ntilde-owned-rectangle-editing-graded-v1');
    assert.equal(rectEditNoncePrefix, 'NTILDE_RC_');
    for (const contract of rectEditContracts) {
        assert.match(contract.appearanceScope, /protected-cell storage/);
        assert.match(contract.terminalFixture.note, /never a verdict/);
        assert.equal(rectEditReads(contract), contract.roles.length);
        assert.doesNotThrow(() => validateRectEditFeeds(contract, rectEditFeeds(contract)));
        assert.throws(() => validateRectEditFeeds(contract, [...rectEditFeeds(contract).slice(0, -1), 'x']), /do not match/);
    }
});

test('the pinned fixture is replayed byte-exactly as the seed feed, the cursor feed and the edit, each read where the pinned probe reads', () => {
    for (const contract of rectEditContracts) {
        const plan = contract.plan;
        assert.deepEqual([contract.beforeRead, contract.setupRead, contract.pinnedRead], [1, 2, 3]);
        assert.equal(plan[0].feed, rectEditReset);
        assert.equal(plan[1].read, 'cells');
        assert.equal(plan[2].feed, contract.pinnedFeeds[0]);
        assert.equal(plan[3].read, 'cells');
        assert.equal(plan[4].feed, '\x1b[1;1H');
        assert.equal(plan[5].read, 'cells');
        assert.equal(plan[6].feed, contract.sequence);
        assert.equal(plan[7].read, 'cells');
    }
    assert.deepEqual(rectEditContracts.map(contract => contract.roles.length), [17, 16, 20]);
});

test('every pinned fixture row is labeled: controls are separate read groups that never reuse the pinned bytes', () => {
    for (const contract of rectEditContracts) {
        const controlFeeds = contract.plan.filter(step => step.feed).slice(5).map(step => step.feed);
        for (const feed of controlFeeds) assert.ok(!contract.pinnedFeeds.includes(feed), `${contract.caseId} control reuses a pinned feed`);
        assert.deepEqual(contract.roles.slice(0, 5), ['initial', 'before', 'setup', 'edited', 'blockBefore']);
    }
    // each control is one fenced write that begins by clearing margins, origin mode, insert mode, SGR and character protection
    for (const contract of rectEditContracts) {
        const feeds = contract.plan.filter(step => step.feed).map(step => step.feed).slice(4);
        assert.ok(feeds.every(feed => feed.startsWith(rectEditFixture.clear)), contract.caseId);
    }
});

test('naming: selective erase, attribute rectangles, DECSACE and checksums are never substituted', () => {
    for (const contract of rectEditContracts) {
        const bytes = contract.plan.filter(step => step.feed).map(step => step.feed).join('');
        assert.ok(!bytes.includes('${'), `${contract.caseId} must not exercise DECSERA`);
        assert.ok(!/\$r|\$t|\*x|\*y/.test(bytes), `${contract.caseId} must not exercise DECCARA, DECRARA, DECSACE or DECRQCRA`);
    }
    // the protected-text control marks the seed with DECSCA, ends the marking, and uses the unconditional $z only
    const protectedFeed = byId(ERA).plan.filter(step => step.feed).map(step => step.feed).find(feed => feed.includes('\x1b[1"q'));
    assert.match(protectedFeed, /\x1b\[1"q.*\x1b\[0"q.*\$z$/s);
});

test('the model is independent of the fixtures: bounds, defaults, clamping, reversed borders, origin and read-first copying', () => {
    const { rectOf, border, copyOp, fillOp, eraseOp, gridOf, textsOf } = rectEditModel;
    const page = { rows: 12, cols: 60, origin: 0 };
    assert.equal(border(0, 1, 12, 0), 0);
    assert.equal(border(999, 1, 12, 0), 11);
    assert.equal(border(3, 1, 12, 2), 4);
    assert.deepEqual(rectOf([], page), { top: 0, left: 0, bottom: 11, right: 59 });
    assert.deepEqual(rectOf([2, 3, 4, 6], page), { top: 1, left: 2, bottom: 3, right: 5 });
    assert.equal(rectOf([4, 3, 2, 6], page), null);
    assert.equal(rectOf([2, 6, 4, 3], page), null);
    assert.deepEqual(rectOf([1, 2, 2, 4], { ...page, origin: 2 }), { top: 2, left: 1, bottom: 3, right: 3 });
    const grid = gridOf(block, 12, 60);
    fillOp([2, 3, 2, 4]).apply(grid, page);
    assert.equal(textsOf(grid)[1].slice(0, 6), 'KL++OP'.replace('OP', 'OP'));
    const invalid = gridOf(block, 12, 60);
    fillOp([1, 1, 7, 10], 31).apply(invalid, page);
    fillOp([1, 1, 7, 10, 1]).apply(invalid, page);
    eraseOp([1, 1, 7, 10, 1]).apply(invalid, page);
    copyOp([1, 1, 2, 5, 2, 5, 6]).apply(invalid, page);
    copyOp([1, 1, 2, 5, 1, 5, 6, 2]).apply(invalid, page);
    assert.deepEqual(textsOf(invalid), textsOf(gridOf(block, 12, 60)));
    const overlap = gridOf(['ABCDEFGH'], 12, 60);
    copyOp([1, 1, 1, 6, 1, 1, 3]).apply(overlap, page);
    assert.equal(textsOf(overlap)[0].trimEnd(), 'ABABCDEF');
    const left = gridOf(['ABCDEFGH'], 12, 60);
    copyOp([1, 3, 1, 8, 1, 1, 1]).apply(left, page);
    assert.equal(textsOf(left)[0].trimEnd(), 'CDEFGHGH');
});

test('a complete synthetic run is conclusively supported for all three cases', () => {
    for (const id of rectEditCaseIds) assert.deepEqual(syntheticRectEditGrade(id), { outcome: 'supported' }, id);
});

test('no-op rectangle operations are unsupported once the seed is proven, and the note says so', () => {
    for (const id of rectEditCaseIds) {
        const noop = reads => { reads.edited = { ...reads.setup }; };
        assert.equal(outcome(id, noop), 'unsupported', id);
        assert.match(note(id, noop), /no-op/);
    }
});

test('DECFRA: off-by-one borders, a wrong fill character, a missing target cell and flank corruption are unsupported', () => {
    for (const rows of [['XXXXXXXZ'], ['XXXXZ', 'XXXXXY', 'XXXXXW'], ['XXXXXZ', 'XXXXXY', 'XXXXXX'], ['XXXXXZ', 'XXXXXY', 'XXXXXW', 'XXXXX']]) {
        assert.equal(outcome(FRA, reads => texts(reads, 'edited', rows)), 'unsupported', rows.join('|'));
    }
    assert.equal(outcome(FRA, reads => texts(reads, 'edited', ['YYYYYZ', 'YYYYYY', 'YYYYYW'])), 'unsupported');
    assert.match(note(FRA, reads => texts(reads, 'edited', ['     Z', '     Y', '     W'])), /blanked instead of filled/);
});

test('DECERA: a partial erase, an erased flank and a filled rectangle are unsupported', () => {
    assert.equal(outcome(ERA, reads => texts(reads, 'edited', ['    aZ', '     Y', '     X'])), 'unsupported');
    assert.equal(outcome(ERA, reads => texts(reads, 'edited', ['', '', ''])), 'unsupported');
    assert.match(note(ERA, reads => texts(reads, 'edited', ['', '', ''])), /flank/);
    assert.equal(outcome(ERA, reads => texts(reads, 'edited', ['XXXXXZ', 'XXXXXY', 'XXXXXX'])), 'unsupported');
});

test('DECCRA: a move instead of a copy, a wrong destination and a changed source are unsupported', () => {
    const edited = ['', '', '..............', '..............', '.........ABCDE', '.........FGHIJ'];
    assert.equal(outcome(CRA, reads => texts(reads, 'edited', edited)), 'unsupported');
    assert.match(note(CRA, reads => texts(reads, 'edited', edited)), /moved instead of copied/);
    assert.equal(outcome(CRA, reads => texts(reads, 'edited', ['ABCDE.........', 'FGHIJ.........', '..............', '..............', '........ABCDE.', '........FGHIJ.'])), 'unsupported');
    assert.equal(outcome(CRA, reads => texts(reads, 'edited', ['ABCDE.........', 'FGHIJ.........', '..............', '..............', '.........ABCDE', '.........FGHI.'])), 'unsupported');
    assert.equal(outcome(CRA, reads => texts(reads, 'edited', ['ABCDE.........', 'FGHIJ.........', '..............', 'ABCDE.........', '.........ABCDE', '.........FGHIJ'])), 'unsupported');
});

test('the cursor must stay put: a moved cursor is unsupported although the pinned probe never reads it', () => {
    for (const id of rectEditCaseIds) {
        assert.equal(outcome(id, reads => { reads.edited = { ...reads.edited, cursor: [0, 5] }; }), 'unsupported', id);
        assert.match(note(id, reads => { reads.edited = { ...reads.edited, cursor: [2, 0] }; }), /cursor drifted/);
    }
});

test('every control is graded over the whole screen: one stray, missing or misplaced cell makes it unsupported', () => {
    const cases = { [FRA]: ['one', 'box', 'defaults', 'zero', 'clip', 'noop', 'origin', 'margins', 'insertMode'], [ERA]: ['one', 'box', 'defaults', 'zero', 'clip', 'noop', 'origin', 'margins', 'protected'],
        [CRA]: ['box', 'right', 'left', 'down', 'up', 'diagonal', 'self', 'cornerRow', 'cornerCol', 'noop', 'origin', 'margins'] };
    for (const [id, roles] of Object.entries(cases)) {
        for (const role of roles) {
            const stray = reads => { reads[role] = { ...reads[role], rows: [...reads[role].rows.slice(0, 11), rowOf('Q')] }; };
            assert.equal(outcome(id, stray), 'unsupported', `${id} ${role}`);
            assert.match(note(id, stray), new RegExp(`The ${role} control differs`));
            const cursor = reads => { reads[role] = { ...reads[role], cursor: [reads[role].cursor[0], reads[role].cursor[1] + 1] }; };
            assert.equal(outcome(id, cursor), 'unsupported', `${id} ${role} cursor`);
        }
    }
});

test('DECCRA overlap: an incrementally smeared copy is unsupported in every direction', () => {
    const smears = {
        right: { 0: 'ABABABABIJ' }, left: { 1: 'MNOPQRRRST' },
        down: { 1: 'ABCDEPQRST', 2: 'ABCDEZabcd', 3: 'ABCDEjklmn', 4: 'ABCDEtuvwx' },
        up: { 1: 'UVWXYPQRST', 2: 'UVWXYZabcd', 3: 'UVWXYjklmn', 4: 'UVWXYtuvwx' },
    };
    for (const [role, changes] of Object.entries(smears)) {
        const smeared = reads => { texts(reads, role, block.map((text, row) => changes[row] ?? text)); };
        assert.equal(outcome(CRA, smeared), 'unsupported', role);
    }
});

test('DECCRA clipping, defaults, reversed borders, extra parameters and pages: a changed screen is unsupported, pages are only observed', () => {
    assert.equal(outcome(CRA, reads => texts(reads, 'cornerRow', [...block, '', '', '', '', '', 'ABC'])), 'unsupported');
    assert.equal(outcome(CRA, reads => texts(reads, 'noop', ['ZBCDEFGHIJ', ...block.slice(1)])), 'unsupported');
    assert.equal(outcome(CRA, reads => texts(reads, 'self', ['ABCDEFGHIJ', 'KLMNOPQRST'])), 'unsupported');
    // the unsupported-page policy is observed: an aliased page copy is recorded, never graded
    const aliased = reads => texts(reads, 'pages', block.map((text, row) => ({ 4: 'opqrsABCDx', 5: 'yz012KLMN7' })[row] ?? text));
    assert.equal(outcome(CRA, aliased), 'supported');
    const contract = byId(CRA);
    const observation = rectEditObservation(contract, syntheticRectEditScreens(CRA, paletteBinding(), id => { const reads = syntheticRectEditReads(id); aliased(reads); return reads; }));
    assert.equal(contract.observed(observation).pages.ignored, false);
    assert.match(contract.observed(observation).pages.policy, /not a pinned or DEC criterion/);
    const ignored = rectEditObservation(contract, syntheticRectEditScreens(CRA));
    assert.equal(contract.observed(ignored).pages.ignored, true);
});

test('DECFRA invalid character codes and combined no-ops are graded: a fill with code 31, 127, 0 or 256 is unsupported', () => {
    for (const code of [31, 127, 0, 256]) {
        const filled = reads => texts(reads, 'noop', block.map((text, row) => (row < 7 ? text.replace(/./g, '+') : text)));
        assert.equal(outcome(FRA, filled), 'unsupported', `code ${code}`);
    }
    assert.match(byId(FRA).plan.filter(step => step.feed).map(step => step.feed).join(''), /\x1b\[31;1;1;7;10\$x.*\x1b\[127;1;1;7;10\$x.*\x1b\[0;1;1;7;10\$x.*\x1b\[256;1;1;7;10\$x/s);
});

test('DECFRA reads under the live pen: a fill without the pen, with the wrong pen or with a drifted pen is unsupported', () => {
    const pen = { attrs: ['italic'], fg: { kind: 'palette', index: 3 }, bg: { kind: 'palette', index: 2 } };
    const bold = { attrs: ['bold'] }, redOnBlue = { fg: { kind: 'palette', index: 1 }, bg: { kind: 'palette', index: 4 } };
    const attrBefore = [rowOf('KEEP-TOP--'), rowOf('BOLD-ROW--', bold), rowOf('RED-ON-BLU', redOnBlue), rowOf('PLAIN-ROW-')];
    const withFill = fillSpec => [attrBefore[0], seg(['BO', bold], ['++++++', fillSpec], ['--', bold]), seg(['RE', redOnBlue], ['++++++', fillSpec], ['LU', redOnBlue]), attrBefore[3],
        ...Array(7).fill([]), [colorCell('P', pen)]];
    for (const fillSpec of [{}, { attrs: ['bold'] }, { attrs: ['italic'], fg: { kind: 'palette', index: 3 } }]) {
        assert.equal(outcome(FRA, reads => { reads.attr = { rows: withFill(fillSpec), cursor: [11, 1] }; }), 'unsupported', JSON.stringify(fillSpec));
    }
    assert.match(note(FRA, reads => { reads.attr = { rows: withFill({}), cursor: [11, 1] }; }), /did not carry the live pen/);
    // a moved cursor in the attribute control: the reference cell lands elsewhere or the cursor ends elsewhere
    assert.equal(outcome(FRA, reads => { reads.attr = { rows: withFill(pen), cursor: [11, 2] }; }), 'unsupported');
    // changed cells outside the rectangle
    const damaged = withFill(pen); damaged[0] = rowOf('KEEP-TOP-X');
    assert.equal(outcome(FRA, reads => { reads.attr = { rows: damaged, cursor: [11, 1] }; }), 'unsupported');
    // the literal-pen fill must carry the literal colors
    assert.equal(outcome(FRA, reads => { reads.literal = { ...reads.literal, rows: reads.literal.rows.map((row, index) => (index === 1 ? rowOf('KL++++QRST') : row)) }; }), 'unsupported');
    assert.match(note(FRA, reads => { reads.literal = { ...reads.literal, rows: reads.literal.rows.map((row, index) => (index === 1 ? rowOf('KL++++QRST') : row)) }; }), /literal control/);
});

test('DECERA attributes: erased cells must carry no attribute but a background, and surrounding cells keep their tuples', () => {
    const bold = { attrs: ['bold'] }, redOnBlue = { fg: { kind: 'palette', index: 1 }, bg: { kind: 'palette', index: 4 } };
    const blue = { bg: { kind: 'palette', index: 4 } };
    const painted = (n, spec) => Array.from({ length: n }, () => colorCell(' ', spec));
    const rows = erased => [rowOf('KEEP-TOP--'), [...rowOf('BO', bold), ...erased(6), ...rowOf('--', bold)], [...rowOf('RE', redOnBlue), ...erased(6), ...rowOf('LU', redOnBlue)], rowOf('PLAIN-ROW-'),
        ...Array(7).fill([]), [colorCell('P', blue)]];
    const set = erased => reads => { reads.attr = { rows: rows(erased), cursor: [11, 1] }; };
    assert.equal(outcome(ERA, set(n => painted(n, blue))), 'supported');
    assert.equal(outcome(ERA, set(n => painted(n, {}))), 'supported', 'a default background is allowed: BCE is observed, not required');
    assert.equal(outcome(ERA, set(n => painted(n, { attrs: ['bold'], bg: blue.bg }))), 'unsupported');
    assert.equal(outcome(ERA, set(n => painted(n, { fg: { kind: 'palette', index: 1 }, bg: blue.bg }))), 'unsupported');
    assert.equal(outcome(ERA, set(n => rowOf('+'.repeat(n)))), 'unsupported');
    const contract = byId(ERA);
    const fill = erased => contract.observed(rectEditObservation(contract, syntheticRectEditScreens(ERA, paletteBinding(), id => { const reads = syntheticRectEditReads(id); set(erased)(reads); return reads; }))).attributes.backgroundFill;
    assert.match(fill(n => painted(n, blue)), /carry the current background color/);
    assert.match(fill(n => painted(n, {})), /default background/);
});

test('DECERA is unconditional: text marked protected is erased like any other, so surviving cells are unsupported', () => {
    assert.equal(outcome(ERA, reads => texts(reads, 'protected', block.map((text, row) => ({ 1: 'KLMNOPQRST', 2: 'UVWXYZabcd', 3: 'ef    klmn' })[row] ?? text))), 'unsupported');
    assert.match(note(ERA, reads => texts(reads, 'protected', block)), /protected control differs/);
});

test('DECCRA attributes: copied cells keep the stored tuples of their sources while a different pen stays active and the source is unchanged', () => {
    const pen = { attrs: ['italic'], fg: { kind: 'palette', index: 3 }, bg: { kind: 'palette', index: 2 } };
    const bold = { attrs: ['bold'] }, redOnBlue = { fg: { kind: 'palette', index: 1 }, bg: { kind: 'palette', index: 4 } };
    const source = [rowOf('KEEP-TOP--'), rowOf('BOLD-ROW--', bold), rowOf('RED-ON-BLU', redOnBlue), rowOf('PLAIN-ROW-')];
    const lost = [rowOf('KEEP-TOP--'), rowOf('BOLD-ROW--'), rowOf('RED-ON-BLU', redOnBlue), rowOf('PLAIN-ROW-')];
    const livePen = [rowOf('KEEP-TOP--', pen), rowOf('BOLD-ROW--', pen), rowOf('RED-ON-BLU', pen), rowOf('PLAIN-ROW-', pen)];
    const rows = copy => [...source, [], [], ...copy, [], [colorCell('P', pen)]];
    assert.equal(outcome(CRA, reads => { reads.attr = { rows: rows(source), cursor: [11, 1] }; }), 'supported');
    assert.equal(outcome(CRA, reads => { reads.attr = { rows: rows(lost), cursor: [11, 1] }; }), 'unsupported');
    assert.equal(outcome(CRA, reads => { reads.attr = { rows: rows(livePen), cursor: [11, 1] }; }), 'unsupported');
    assert.equal(outcome(CRA, reads => { reads.attr = { rows: [...lost, [], [], ...source, [], [colorCell('P', pen)]], cursor: [11, 1] }; }), 'unsupported');
    assert.match(note(CRA, reads => { reads.attr = { rows: rows(lost), cursor: [11, 1] }; }), /lost their stored attributes/);
});

test('margins, origin mode and insert mode are observed state: a setup the App did not confirm is inconclusive', () => {
    assert.equal(outcome(FRA, reads => { reads.origin = { ...reads.origin, state: { margins: [2, 4], origin: false } }; }), 'inconclusive');
    assert.equal(outcome(FRA, reads => { reads.margins = { ...reads.margins, state: { margins: [0, rectEditRows - 1] } }; }), 'inconclusive');
    assert.equal(outcome(FRA, reads => { reads.insertMode = { ...reads.insertMode, state: { insert: false } }; }), 'inconclusive');
    assert.equal(outcome(FRA, reads => { reads.box = { ...reads.box, state: { insert: true } }; }), 'inconclusive');
    assert.equal(outcome(CRA, reads => { reads.right = { ...reads.right, state: { origin: true } }; }), 'inconclusive');
    assert.match(note(ERA, reads => { reads.origin = { ...reads.origin, state: { margins: [0, 11], origin: true } }; }), /margins, origin mode or insert mode differ/);
});

test('missing or invalid setup is inconclusive, never unsupported', () => {
    for (const id of rectEditCaseIds) {
        const seedGone = reads => { reads.before = { ...reads.before, rows: rowsOfText(['aaaaaZ']) }; };
        assert.equal(outcome(id, seedGone), 'inconclusive', id);
        assert.equal(outcome(id, reads => { reads.setup = { ...reads.setup, cursor: [1, 0] }; }), 'inconclusive', id);
        assert.equal(outcome(id, reads => { reads.initial = { ...reads.initial, rows: rowsOfText(['x']) }; }), 'inconclusive', id);
        assert.equal(outcome(id, reads => { reads.blockBefore = { ...reads.blockBefore, rows: rowsOfText(block.slice(0, 6)) }; }), 'inconclusive', id);
        assert.equal(outcome(id, reads => { reads.attrBefore = { ...reads.attrBefore, rows: reads.attrBefore.rows.slice(0, 3) }; }), 'inconclusive', id);
        // a bad pen reference means the pen cannot be attributed
        assert.equal(outcome(id, reads => { reads.attr = { ...reads.attr, rows: [...reads.attr.rows.slice(0, 11), [colorCell('P')]] }; }), 'inconclusive', id);
    }
    assert.match(note(FRA, reads => { reads.literal = { ...reads.literal, rows: [...reads.literal.rows.slice(0, 11), [colorCell('P')]] }; }), /literal-color reference/);
});

test('DECSCNM not proven off is inconclusive', () => {
    const on = { ...paletteBinding(), reverseVideo: true };
    for (const id of rectEditCaseIds) assert.equal(outcome(id, null, on), 'inconclusive', id);
});

test('too small a screen is inconclusive with the contract geometry note', () => {
    for (const id of rectEditCaseIds) {
        const contract = byId(id);
        const screens = syntheticRectEditScreens(id);
        const observation = rectEditObservation(contract, screens);
        observation.setup = { ...observation.setup, rows: 11 };
        assert.deepEqual(gradeRectEditCase(contract, observation), { outcome: 'inconclusive', note: pinnedGeometryNote });
        observation.setup = { ...observation.setup, rows: rectEditRows, cols: 15 };
        assert.equal(gradeRectEditCase(contract, observation).note, pinnedGeometryNote);
    }
});

test('grader refuses missing groups and unknown revisions', () => {
    const contract = byId(FRA);
    const observation = rectEditObservation(contract, syntheticRectEditScreens(FRA));
    assert.throws(() => gradeRectEditCase(contract, observation, 2), /Unknown rectangle-edit contracts revision/);
    assert.throws(() => gradeRectEditCase(contract, { ...observation, box: undefined }), /Missing editing.decfra read captures/);
    assert.throws(() => rectEditObservation(contract, syntheticRectEditScreens(FRA).slice(1)), /Missing rectangle-edit read captures/);
});

test('the observed record reports the exact pinned rows and every control, with its sequences and a model match flag', () => {
    for (const id of rectEditCaseIds) {
        const contract = byId(id);
        const record = contract.observed(rectEditObservation(contract, syntheticRectEditScreens(id)));
        assert.deepEqual(record.geometry, { rows: rectEditRows, cols: 60 });
        assert.equal(record.pinnedSequence, contract.sequence);
        assert.equal(record.initial.ink, 0);
        for (const role of contract.others) {
            assert.equal(record.controls[role].matchesModel, true, `${id} ${role}`);
            assert.ok(record.controls[role].sequences.length > 0);
        }
        assert.equal(record.attributes.changedOk, true);
        assert.equal(record.history.historyLines, 0);
    }
    const fill = byId(FRA).observed(rectEditObservation(byId(FRA), syntheticRectEditScreens(FRA)));
    assert.deepEqual(fill.edited.text.slice(0, 3), ['XXXXXZ__________', 'XXXXXY__________', 'XXXXXW__________']);
    assert.equal(fill.literalPen.filledOk, true);
    const erase = byId(ERA).observed(rectEditObservation(byId(ERA), syntheticRectEditScreens(ERA)));
    assert.deepEqual(erase.edited.text.slice(0, 3), ['_____Z__________', '_____Y__________', '_____X__________']);
    assert.equal(erase.attributes.backgroundFill, 'erased cells carry the current background color');
    assert.match(erase.protectedText.policy, /DECSERA is not claimed/);
    const copy = byId(CRA).observed(rectEditObservation(byId(CRA), syntheticRectEditScreens(CRA)));
    assert.deepEqual(copy.edited.text.slice(4, 6), ['.........ABCDE__', '.........FGHIJ__']);
    assert.equal(copy.pages.ignored, true);
});

test('observed defaults on a full page report a complete fill without enumerating the cells', () => {
    const contract = byId(FRA);
    const record = contract.observed(rectEditObservation(contract, syntheticRectEditScreens(FRA)));
    assert.equal(record.controls.defaults.ink, rectEditRows * 60);
    assert.equal(record.controls.defaults.lastRow, '+'.repeat(16));
    assert.equal(record.controls.defaults.lastColumn, '+++');
});
