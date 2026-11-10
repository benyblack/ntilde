import assert from 'node:assert/strict';
import { test } from 'node:test';
import { colEditContracts, colEditCaseIds, colEditSuiteId, colEditContractsVersion, colEditNoncePrefix, colEditReset, colEditFixture, colEditModel,
    colEditFeeds, colEditReads, validateColEditFeeds, gradeColEditCase, colEditObservation, pinnedNotes } from './col-edit-contracts.mjs';
import { syntheticColEditScreens, syntheticColEditGrade, syntheticColEditReads, plainCell } from './fixtures/col-edit-evidence.mjs';

const { columnOp, gridOf, textsOf, blockGrid, controlsFor, expectedControl } = colEditModel;
const page = (rows, cols, top = 0, bottom = rows - 1) => ({ rows, cols, top, bottom });
/** Apply one operation to rows of text on a page and return the rows as trimmed text. */
const run = (mode, count, texts, { rows = texts.length, cols = 10, cursor = [0, 0], top, bottom, form } = {}) => {
    const grid = gridOf(texts, rows, cols);
    columnOp(mode, count, form).apply([grid], [' '], page(rows, cols, top, bottom), cursor);
    return textsOf(grid).map(line => line.trimEnd());
};

test('identity: four pinned cases in bundle order, one versioned suite, one nonce prefix', () => {
    assert.deepEqual(colEditCaseIds, ['editing.sl', 'editing.sr', 'editing.decic', 'editing.decdc']);
    assert.equal(colEditSuiteId, 'ntilde-owned-column-editing-graded-v1');
    assert.equal(colEditContractsVersion, 1);
    assert.equal(colEditNoncePrefix, 'NTILDE_CE_');
    assert.equal(colEditReset, '\x1b[0"q\x1b[?5l\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[2J\x1b[H', 'protection, reverse video, origin, margins, IRM and SGR off; cleared; home');
});

test('the pinned fixture bytes are the exact upstream seeds, setup cursor moves and edits', () => {
    const feeds = Object.fromEntries(colEditContracts.map(contract => [contract.caseId, contract.pinnedFeeds]));
    assert.deepEqual(feeds['editing.sl'], ['\x1b[1;1HABCDEFGHIJK\x1b[2;1HJKLMNOPQRST', '\x1b[1;1H', '\x1b[2 @']);
    assert.deepEqual(feeds['editing.sr'], ['\x1b[1;1HABCDEFGHI\x1b[2;1HJKLMNOPQR', '\x1b[1;1H', '\x1b[2 A']);
    assert.deepEqual(feeds['editing.decic'], ['\x1b[1;1HABCDEFGH\x1b[2;1HIJKLMNOP\x1b[3;1HQRSTUVWX', '\x1b[2;3H', "\x1b[2'}"]);
    assert.deepEqual(feeds['editing.decdc'], ['\x1b[1;1HABCDEFGHIJ\x1b[2;1HIJKLMNOPQR\x1b[3;1HQRSTUVWXab', '\x1b[2;3H', "\x1b[2'~"]);
    for (const contract of colEditContracts) {
        const planFeeds = colEditFeeds(contract);
        assert.equal(planFeeds[0], colEditReset);
        assert.deepEqual(planFeeds.slice(1, 4), [...contract.pinnedFeeds], 'the pinned feeds directly follow the reset, one read after each');
        assert.equal(colEditReads(contract), contract.roles.length);
        assert.equal(contract.plan.at(-1).read, 'cells');
        assert.equal(contract.terminalFixture.note.includes('never a verdict'), true);
    }
});

test('the plan fits the shared observer budget of 64 captures per suite, including start, bind and finish', () => {
    const reads = colEditContracts.reduce((sum, contract) => sum + colEditReads(contract), 0);
    assert.equal(reads, 54);
    assert.ok(reads + 3 <= 64);
    assert.deepEqual(colEditContracts.map(contract => contract.others), [
        ['counts', 'clip', 'region', 'origin', 'modes'], ['counts', 'clip', 'region', 'origin', 'modes'],
        ['counts', 'clip', 'region', 'origin', 'modes', 'columns'], ['counts', 'clip', 'region', 'origin', 'modes', 'columns']]);
    for (const contract of colEditContracts) assert.ok(contract.roles.includes('pendingWrap') && !contract.others.includes('pendingWrap'), 'observed, never graded');
});

test('a feed list that differs by one byte is refused', () => {
    for (const contract of colEditContracts) {
        const feeds = colEditFeeds(contract);
        assert.doesNotThrow(() => validateColEditFeeds(contract, feeds));
        for (const index of [1, 3, feeds.length - 1]) {
            const changed = [...feeds];
            changed[index] = changed[index].slice(0, -1) + 'x';
            assert.throws(() => validateColEditFeeds(contract, changed));
        }
        assert.throws(() => validateColEditFeeds(contract, feeds.slice(0, -1)));
    }
});

test('the model: SL/SR shift from the left margin, DECIC/DECDC from the cursor column, blanks entering where the definitions say', () => {
    const seed = ['ABCDEFGHIJ', 'KLMNOPQRST'];
    assert.deepEqual(run('left', 2, seed, { cursor: [1, 5] }), ['CDEFGHIJ', 'MNOPQRST']);
    assert.deepEqual(run('right', 2, seed, { cursor: [1, 5] }), ['  ABCDEFGH', '  KLMNOPQR']);
    assert.deepEqual(run('insert', 2, seed, { cursor: [1, 5] }), ['ABCDE  FGH', 'KLMNO  PQR']);
    assert.deepEqual(run('delete', 2, seed, { cursor: [1, 5] }), ['ABCDEHIJ', 'KLMNORST']);
});

test('the model: omitted and zero counts are one; oversized counts clamp to the columns from the start', () => {
    const seed = ['ABCDEFGHIJ'];
    for (const count of [undefined, 0, 1]) {
        assert.deepEqual(run('insert', count, seed, { cursor: [0, 3] }), ['ABC DEFGHI']);
        assert.deepEqual(run('left', count, seed), ['BCDEFGHIJ']);
    }
    assert.deepEqual(run('insert', 999, seed, { cursor: [0, 3] }), ['ABC']);
    assert.deepEqual(run('delete', 999, seed, { cursor: [0, 3] }), ['ABC']);
    assert.deepEqual(run('right', 999, seed), ['']);
    assert.deepEqual(run('insert', 2, seed, { cursor: [0, 9] }), ['ABCDEFGHI'], 'at the last column the count clamps to one');
    assert.deepEqual(run('delete', 1, seed, { cursor: [0, 0] }), ['BCDEFGHIJ']);
});

test('the model: only the region rows move, and only while the cursor row is inside the region', () => {
    const seed = ['AAAA', 'BBBB', 'CCCC', 'DDDD'];
    assert.deepEqual(run('left', 1, seed, { cols: 4, cursor: [1, 0], top: 1, bottom: 2 }), ['AAAA', 'BBB', 'CCC', 'DDDD']);
    assert.deepEqual(run('left', 1, seed, { cols: 4, cursor: [0, 0], top: 1, bottom: 2 }), seed, 'cursor above the region');
    assert.deepEqual(run('insert', 1, seed, { cols: 4, cursor: [3, 0], top: 1, bottom: 2 }), seed, 'cursor below the region');
});

test('the model: non-admitted identities are no-ops', () => {
    const seed = ['ABCDEFGHIJ'];
    for (const form of [{ extra: ';1' }, { extra: ':1' }, { leader: '?' }]) {
        for (const mode of ['left', 'right', 'insert', 'delete']) {
            assert.deepEqual(run(mode, 2, seed, { cursor: [0, 3], form }), seed, `${mode} ${JSON.stringify(form)}`);
            assert.equal(columnOp(mode, 2, form).admitted, false);
        }
    }
    assert.equal(columnOp('left', 2, { extra: ';1' }).bytes, '\x1b[2;1 @');
    assert.equal(columnOp('insert', 2, { leader: '?' }).bytes, "\x1b[?2'}");
    assert.equal(columnOp('delete', 2, { extra: ':1' }).bytes, "\x1b[2:1'~");
    assert.equal(columnOp('right', undefined).bytes, '\x1b[ A');
});

test('the block seed writes ten characters at each edge, ending at the last column of row 7', () => {
    const { blockFeed, leftTexts, rightTexts } = colEditFixture;
    assert.equal(blockFeed.split('\x1b[').length - 1, 7 * 3, 'a left CUP, a far-right CUP and a CUB per row');
    assert.ok(blockFeed.startsWith('\x1b[1;1HABCDEFGHIJ\x1b[1;999H\x1b[9D0123456789'));
    const grid = textsOf(blockGrid(9, 40));
    assert.equal(grid[0], 'ABCDEFGHIJ' + ' '.repeat(20) + '0123456789');
    assert.equal(grid[6], '89!#%&*+-=' + ' '.repeat(20) + 'LKJIHGFEDC');
    assert.equal(grid[7].trim(), '');
    assert.equal(new Set([...leftTexts.join(''), ...rightTexts.join('')]).size >= 60, true, 'mostly distinct characters');
});

test('composed controls stay discriminating: each step of the counts group changes the final screen', () => {
    for (const mode of ['left', 'right', 'insert', 'delete']) {
        const counts = controlsFor[mode].find(item => item.role === 'counts');
        const full = textsOf(expectedControl(counts, 13, 60).texts).join('\n');
        for (let skip = 0; skip < counts.steps.length; skip++) {
            const partial = { ...counts, steps: counts.steps.filter((_, index) => index !== skip) };
            assert.notEqual(textsOf(expectedControl(partial, 13, 60).texts).join('\n'), full, `${mode} without step ${skip}`);
        }
    }
});

test('the region control: inside the margins it acts; above and below it does nothing; the cursor ends on row 7', () => {
    for (const mode of ['left', 'right', 'insert', 'delete']) {
        const region = controlsFor[mode].find(item => item.role === 'region');
        assert.deepEqual(region.steps.map(step => [step.park.row, step.op.bytes]), [[4, columnOp(mode, 2).bytes], [1, columnOp(mode, 2).bytes], [7, columnOp(mode, 2).bytes]]);
        const { texts, cursor } = expectedControl(region, 13, 60);
        const seed = blockGrid(13, 60);
        for (const row of [0, 1, 5, 6]) assert.deepEqual(texts[row], seed[row], `${mode} row ${row} outside the region`);
        for (const row of [2, 3, 4]) assert.notDeepEqual(texts[row], seed[row], `${mode} row ${row} inside the region`);
        assert.deepEqual(cursor, [6, 5]);
        assert.ok(region.feed.includes('\x1b[3;5r'));
    }
});

test('origin mode parks relative to the region top', () => {
    const origin = controlsFor.insert.find(item => item.role === 'origin');
    assert.ok(origin.feed.includes('\x1b[3;5r\x1b[?6h\x1b[2;3H'));
    assert.deepEqual(expectedControl(origin, 13, 60).cursor, [3, 2]);
});

test('each case\'s own synthetic run grades supported; the expectations are a second oracle written with string slicing', () => {
    for (const id of colEditCaseIds) assert.deepEqual(syntheticColEditGrade(id), { outcome: 'supported' }, id);
});

test('an off-by-one count, a wrong start column, a misread region, a lost blank and corrupted cells outside the edit are unsupported', () => {
    const plainRows = texts => texts.map(line => [...line].map(char => plainCell(char)));
    const variants = [
        ['editing.decic', 'counts', reads => { const rows = reads.counts.rows.map(line => line.map(cell => cell.char).join('')); rows[0] = 'ABCDE' + rows[0].slice(6) + ' '; reads.counts = { ...reads.counts, rows: plainRows(rows) }; }],
        ['editing.decdc', 'columns', reads => { reads.columns = { ...reads.columns, rows: syntheticColEditReads('editing.decdc').modes.rows }; }],
        ['editing.sl', 'clip', reads => { reads.clip = { ...reads.clip, rows: plainRows(['X']) }; }],
        ['editing.sr', 'origin', reads => { reads.origin = { ...reads.origin, rows: syntheticColEditReads('editing.sr').modes.rows }; }],
        ['editing.decic', 'modes', reads => { const rows = reads.modes.rows.map(line => [...line]); rows[12] = [plainCell('!')]; reads.modes = { ...reads.modes, rows }; }],
    ];
    for (const [id, role, edit] of variants) {
        const grade = syntheticColEditGrade(id, undefined, caseId => { const reads = syntheticColEditReads(caseId); if (caseId === id) edit(reads); return reads; });
        assert.equal(grade.outcome, 'unsupported', `${id} ${role}`);
        assert.match(grade.note, new RegExp(`The ${role} control differs from the column model`));
    }
});

test('a moved cursor in any control is unsupported; a wrong setup state is inconclusive', () => {
    const moved = syntheticColEditGrade('editing.sl', undefined, id => { const reads = syntheticColEditReads(id); reads.counts = { ...reads.counts, cursor: [3, 6] }; return reads; });
    assert.match(moved.note, /counts control differs/);
    const state = syntheticColEditGrade('editing.decdc', undefined, id => { const reads = syntheticColEditReads(id); reads.origin = { ...reads.origin, state: { margins: [2, 4] } }; return reads; });
    assert.equal(state.outcome, 'inconclusive');
    assert.match(state.note, /differ from the contracted setup in the origin read/);
    const small = colEditContracts[0];
    const screens = syntheticColEditScreens(small.caseId).map(screen => ({ ...screen }));
    const observation = colEditObservation(small, screens);
    observation.setup = { ...observation.setup, cols: 20 };
    assert.deepEqual(gradeColEditCase(small, observation), { outcome: 'inconclusive', note: 'Measured geometry cannot fit the column-edit fixture' });
});

test('missing read captures and unknown revisions are refused', () => {
    const contract = colEditContracts[2];
    assert.throws(() => colEditObservation(contract, syntheticColEditScreens(contract.caseId).slice(1)), /Missing column-edit read captures/);
    const observation = colEditObservation(contract, syntheticColEditScreens(contract.caseId));
    assert.throws(() => gradeColEditCase(contract, observation, 2), /Unknown column-edit contracts revision 2/);
    delete observation.attr;
    assert.throws(() => gradeColEditCase(contract, observation), /Missing editing.decic read captures/);
});

test('the pinned note texts are the upstream ones, byte for byte', () => {
    assert.equal(pinnedNotes.left.seed, 'SL seed and incoming sources were not measured');
    assert.equal(pinnedNotes.right.expected, 'SR shifts the first nine measured cells of two rows right by two columns');
    assert.equal(pinnedNotes.insert.controls, 'DECIC unchanged-prefix control changed');
    assert.equal(pinnedNotes.delete.expected, 'DECDC deletes two columns at column 3 on three measured rows');
    assert.equal(pinnedNotes.left.controls, null, 'SL and SR have no inconclusive control');
});
