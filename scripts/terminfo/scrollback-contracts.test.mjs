import assert from 'node:assert/strict';
import { test } from 'node:test';
import { makeScrollbackContracts, scrollbackCaseIds, scrollbackDecidingIds, scrollbackReset, scrollbackMinimum, scrollbackMismatch,
    scrollbackView, scrollbackObservation, gradeScrollbackCase, compactLines, splitTrailingNumber, validateScrollbackFeeds, scrollbackFeeds, scrollbackReads } from './scrollback-contracts.mjs';
import { createScrollbackModel, feedScrollbackModel, scrollbackModelState } from './scrollback-model.mjs';
import { blankState, syntheticScrollbackScreens, syntheticScrollbackScreen, syntheticScrollbackGrade, syntheticScrollbackContracts,
    scrollbackRows, paletteBinding } from './fixtures/scrollback-evidence.mjs';

const real = makeScrollbackContracts(40, 140);
const byId = (contracts, id) => contracts.find(contract => contract.caseId === id);
const expectedOf = (contracts, id, role) => byId(contracts, id).roles.find(entry => entry.role === role).expected;
const numbered = (prefix, from, to) => Array.from({ length: to - from + 1 }, (_, index) => `${prefix}${from + index}`);
const synthetic = syntheticScrollbackContracts();
const grade = (id, edit) => syntheticScrollbackGrade(byId(synthetic, id), paletteBinding(), edit);
const only = (target, change) => (role, state) => { if (role === target) change(state); return state; };

test('the exact nine pinned ids in suite order, and which of them can decide', () => {
    assert.deepEqual([...scrollbackCaseIds], ['scrollback.accumulate', 'scrollback.total-lines', 'scrollback.scroll-up', 'scrollback.reverse-index',
        'scrollback.scroll-down', 'scrollback.set-region', 'scrollback.alt-screen', 'scrollback.decstbm', 'scrollback.decstbm-reset']);
    assert.deepEqual(real.map(contract => contract.caseId), [...scrollbackCaseIds]);
    assert.deepEqual(real.filter(contract => !contract.decides).map(contract => contract.caseId),
        ['scrollback.set-region', 'scrollback.alt-screen', 'scrollback.decstbm-reset']);
    assert.deepEqual([...scrollbackDecidingIds], real.filter(contract => contract.decides).map(contract => contract.caseId));
});

test('the pinned fixture bytes are exactly the pinned probe bytes', () => {
    const feeds = id => [...byId(real, id).pinnedFeeds];
    const lines = (prefix, count) => Array.from({ length: count }, (_, index) => `${prefix}${index}\r\n`);
    assert.deepEqual(feeds('scrollback.accumulate'), lines('line ', 50));
    assert.deepEqual(feeds('scrollback.total-lines'), lines('line ', 50));
    assert.deepEqual(feeds('scrollback.scroll-up'), ['\x1b[2J', '\x1b[1;1HA', '\x1b[2;1HB', '\x1b[3;1HC', '\x1b[4;1HD', '\x1b[5;1HE', '\x1b[2;4r', '\x1b[S', '\x1b[r']);
    assert.deepEqual(feeds('scrollback.reverse-index'), ['A\r\nB\r\nC', '\x1b[H\x1bM']);
    assert.deepEqual(feeds('scrollback.scroll-down'), ['LINE1\r\nLINE2\r\nLINE3', '\x1b[T']);
    assert.deepEqual(feeds('scrollback.set-region'), ['\x1b[5;10r', '\x1b[r']);
    assert.deepEqual(feeds('scrollback.alt-screen'), ['NORMAL', '\x1b[?1049h']);
    assert.deepEqual(feeds('scrollback.decstbm'), ['FIXED_TOP\r\n', '\x1b[3;10r', '\x1b[3;1HINNER', '\x1b[10;1HZ\r\n', '\x1b[r']);
    assert.deepEqual(feeds('scrollback.decstbm-reset'), ['\x1b[5;10r', '\x1b[r', '\x1b[H', ...lines('line-', 50)]);
});

test('every plan starts from the measured reset and the pinned feeds appear in the plan, in order and byte for byte', () => {
    for (const contract of real) {
        const all = scrollbackFeeds(contract);
        assert.equal(all[0], scrollbackReset, contract.caseId);
        let cursor = 0;
        for (const feed of contract.pinnedFeeds) {
            cursor = all.indexOf(feed, cursor) + 1;
            assert.ok(cursor > 0, `${contract.caseId} lost ${JSON.stringify(feed)}`);
        }
        assert.equal(scrollbackReads(contract), contract.roles.length);
        assert.doesNotThrow(() => validateScrollbackFeeds(contract, all));
        assert.throws(() => validateScrollbackFeeds(contract, all.slice(1)), /do not match the pinned contract/);
        assert.throws(() => validateScrollbackFeeds(contract, all.map((feed, index) => (index === 1 ? feed + ' ' : feed))), /do not match the pinned contract/);
    }
    assert.match(scrollbackReset, /\x1b\[2J\x1b\[3J/, 'ED 3 clears history on the measured reset');
    assert.equal(real.reduce((sum, contract) => sum + contract.roles.length, 0) + 3, 48, 'the whole suite stays inside the 64-snapshot observer budget');
});

test('accumulation: initial history is empty, nothing reaches history before the overflow, and exactly lines 0-10 do afterwards', () => {
    const accumulate = id => ({ initial: expectedOf(real, id, 'initial'), full: expectedOf(real, id, 'justFull'), final: expectedOf(real, id, 'final') });
    for (const id of ['scrollback.accumulate', 'scrollback.total-lines']) {
        const { initial, full, final } = accumulate(id);
        assert.deepEqual(initial.history, []);
        assert.ok(initial.lines.every(line => line === ''));
        assert.deepEqual(full.history, []);
        assert.deepEqual(full.lines.slice(0, 39), numbered('line ', 0, 38));
        assert.equal(full.lines[39], '');
        assert.deepEqual(full.cursor, [39, 0]);
        assert.deepEqual(final.history, numbered('line ', 0, 10));
        assert.deepEqual(final.lines.slice(0, 39), numbered('line ', 11, 49));
        assert.equal(final.lines[39], '');
        assert.deepEqual(final.cursor, [39, 0]);
        // The pinned total line count is rows plus retained history lines: 40 -> 51.
        assert.equal(40 + final.history.length, 51);
    }
});

test('interior-region SU, SD and RI move exactly the region, in the right direction, with no history growth', () => {
    const lines = id => role => expectedOf(real, id, role).lines.slice(0, 6);
    assert.deepEqual(lines('scrollback.scroll-up')('seed'), ['A', 'B', 'C', 'D', 'E', '']);
    assert.deepEqual(lines('scrollback.scroll-up')('after'), ['A', 'C', 'D', '', 'E', '']);
    assert.deepEqual(expectedOf(real, 'scrollback.scroll-up', 'seed').history, []);
    assert.deepEqual(expectedOf(real, 'scrollback.scroll-up', 'after').history, []);
    assert.deepEqual([expectedOf(real, 'scrollback.scroll-up', 'after').top, expectedOf(real, 'scrollback.scroll-up', 'after').bottom], [1, 3]);
    assert.deepEqual(lines('scrollback.scroll-up')('restored'), ['A', 'C', 'D', '', 'E', '']);
    assert.deepEqual(expectedOf(real, 'scrollback.scroll-up', 'restored').bottom, 39);
    const wide = expectedOf(real, 'scrollback.scroll-up', 'wideAfter').lines;
    assert.deepEqual(wide.slice(0, 5), ['ROW-0', 'ROW-2', 'ROW-3', '', 'ROW-4']);
    assert.equal(wide[39], 'ROW-39');
    assert.deepEqual(lines('scrollback.reverse-index')('before'), ['A', 'B', 'C', '', '', '']);
    assert.deepEqual(lines('scrollback.reverse-index')('after'), ['', 'A', 'B', 'C', '', '']);
    assert.deepEqual(lines('scrollback.reverse-index')('moveOnly'), ['A', 'B', 'C', '', '', '']);
    assert.deepEqual(expectedOf(real, 'scrollback.reverse-index', 'moveOnly').cursor, [0, 0]);
    assert.deepEqual(lines('scrollback.reverse-index')('regionAfter'), ['A', '', 'B', 'C', 'E', '']);
    assert.deepEqual(lines('scrollback.scroll-down')('before'), ['LINE1', 'LINE2', 'LINE3', '', '', '']);
    assert.deepEqual(lines('scrollback.scroll-down')('after'), ['', 'LINE1', 'LINE2', 'LINE3', '', '']);
    assert.deepEqual(expectedOf(real, 'scrollback.scroll-down', 'after').cursor, [2, 5]);
    assert.deepEqual(lines('scrollback.scroll-down')('regionAfter'), ['A', '', 'B', 'C', 'E', '']);
    const full = expectedOf(real, 'scrollback.scroll-down', 'fullAfter');
    assert.deepEqual(full.lines.slice(0, 3), ['', 'ROW-0', 'ROW-1']);
    assert.equal(full.lines[39], 'ROW-38', 'the bottom row falls off the screen');
    assert.deepEqual(full.history, [], 'a pushed-down row never becomes history');
});

test('margins: DECSTBM sets 5;10, the reset restores full boundaries, scrolling is confined and unreset regions keep history empty', () => {
    const set = expectedOf(real, 'scrollback.set-region', 'set');
    assert.deepEqual([set.top, set.bottom, set.cursor], [4, 9, [0, 0]]);
    const reset = expectedOf(real, 'scrollback.set-region', 'reset');
    assert.deepEqual([reset.top, reset.bottom, reset.cursor], [0, 39, [0, 0]]);
    const scrolled = expectedOf(real, 'scrollback.set-region', 'ctlScrolled');
    assert.deepEqual(scrolled.lines.slice(0, 11), ['ROW-0', 'ROW-1', 'ROW-2', 'ROW-3', 'ROW-5', 'ROW-6', 'ROW-7', 'ROW-8', 'ROW-9', '', 'ROW-10']);
    assert.deepEqual(scrolled.history, []);
    const full = expectedOf(real, 'scrollback.set-region', 'ctlFull');
    assert.deepEqual(full.history, ['ROW-0']);
    assert.equal(full.lines[39], '');
    const region = expectedOf(real, 'scrollback.decstbm-reset', 'ctlRegion');
    assert.deepEqual(region.lines.slice(0, 4), ['line-0', 'line-1', 'line-2', 'line-3']);
    assert.deepEqual(region.lines.slice(4, 9), numbered('line-', 45, 49));
    assert.ok(region.lines.slice(9).every(line => line === ''));
    assert.deepEqual(region.history, []);
    const final = expectedOf(real, 'scrollback.decstbm-reset', 'final');
    assert.deepEqual(final.history, numbered('line-', 0, 10));
    assert.deepEqual(final.lines.slice(0, 39), numbered('line-', 11, 49));
    assert.deepEqual([final.top, final.bottom], [0, 39]);
    assert.deepEqual(expectedOf(real, 'scrollback.decstbm-reset', 'set'), { ...expectedOf(real, 'scrollback.decstbm-reset', 'set'), top: 4, bottom: 9 });
});

test('DECSTBM 3;10: FIXED_TOP survives, INNER scrolls out, Z moves up, and rows outside the region stay intact without history', () => {
    const after = expectedOf(real, 'scrollback.decstbm', 'after');
    assert.equal(after.lines[0], 'FIXED_TOP');
    assert.ok(!after.lines.includes('INNER'));
    assert.equal(after.lines[8], 'Z');
    assert.equal(after.lines[9], '');
    assert.deepEqual([after.cursor, after.top, after.bottom], [[9, 0], 2, 9]);
    assert.deepEqual(after.history, []);
    const inner = expectedOf(real, 'scrollback.decstbm', 'innerBefore');
    assert.equal(inner.lines[2], 'INNER');
    assert.deepEqual(inner.cursor, [2, 5]);
    const control = expectedOf(real, 'scrollback.decstbm', 'ctlAfter');
    assert.deepEqual(control.lines.slice(0, 2), ['ROW-0', 'ROW-1']);
    assert.equal(control.lines[8], 'ZOW-9');
    assert.deepEqual(control.lines.slice(10), numbered('ROW-', 10, 39));
    assert.deepEqual(expectedOf(real, 'scrollback.decstbm', 'restored').bottom, 39);
});

test('alternate screen: main contents and exactly three history lines are seeded, the alternate screen is isolated, and main is restored', () => {
    const seeded = expectedOf(real, 'scrollback.alt-screen', 'seeded');
    assert.deepEqual(seeded.history, ['ROW-0', 'ROW-1', 'ROW-2']);
    assert.deepEqual(seeded.lines.slice(0, 2), ['ROW-3', 'ROW-4']);
    const normal = expectedOf(real, 'scrollback.alt-screen', 'normal');
    assert.equal(normal.lines[39], 'NORMAL');
    assert.deepEqual(normal.cursor, [39, 6]);
    const alt = expectedOf(real, 'scrollback.alt-screen', 'alt');
    assert.equal(alt.alternate, true);
    assert.ok(alt.lines.every(line => line === ''), 'no main content on the alternate screen');
    const overflow = expectedOf(real, 'scrollback.alt-screen', 'altOverflow');
    assert.deepEqual(overflow.lines.slice(0, 39), numbered('ALT-', 6, 44));
    assert.deepEqual(overflow.history, ['ROW-0', 'ROW-1', 'ROW-2'], 'alternate overflow never reaches the main history');
    const restored = expectedOf(real, 'scrollback.alt-screen', 'restored');
    assert.equal(restored.alternate, false);
    assert.deepEqual(restored.lines, normal.lines);
    assert.deepEqual(restored.cursor, normal.cursor);
    assert.deepEqual(restored.history, seeded.history);
    const contract = byId(real, 'scrollback.alt-screen');
    assert.deepEqual(contract.roles.find(entry => entry.role === 'alt').options, { cursor: false, history: 'main-or-empty' });
});

test('the model refuses anything it does not model instead of inventing an expectation', () => {
    const model = createScrollbackModel(10, 40);
    for (const bytes of ['\x1b[?1000h', '\x1b[1;5r\x1b[9;1r', '\x1b[1J', '\x1b[3m', '\x1b[4K', '\x1b[3@', '\x07', '\x1b7', 'é']) {
        assert.throws(() => feedScrollbackModel(model, bytes), /does not model|could not parse/, JSON.stringify(bytes));
    }
    assert.throws(() => feedScrollbackModel(createScrollbackModel(10, 8), 'ABCDEFGHI'), /wrapping/);
    assert.throws(() => createScrollbackModel(2, 80), /geometry/);
    const live = createScrollbackModel(5, 20);
    feedScrollbackModel(live, 'a\r\nb\r\nc\r\nd\r\ne\r\nf');
    assert.deepEqual(scrollbackModelState(live).history, ['a']);
    assert.deepEqual(scrollbackModelState(live).lines, ['b', 'c', 'd', 'e', 'f']);
    feedScrollbackModel(live, '\x1b[2;4r\x1b[4;1H\n');
    assert.deepEqual(scrollbackModelState(live).history, ['a'], 'a region that does not start at the top never feeds history');
});

test('contracts refuse geometry below the floor and above the observer limits', () => {
    assert.deepEqual({ ...scrollbackMinimum }, { rows: 12, cols: 48 });
    assert.throws(() => makeScrollbackContracts(11, 140), /at least 12 rows/);
    assert.throws(() => makeScrollbackContracts(40, 47), /at least 12 rows/);
    assert.throws(() => makeScrollbackContracts(129, 140), /at least 12 rows/);
    assert.throws(() => makeScrollbackContracts(40, 257), /at most 128 rows and 256 columns/);
    assert.throws(() => makeScrollbackContracts(40, 1e9), /at most 128 rows and 256 columns/);
    assert.throws(() => makeScrollbackContracts(40, undefined), /at most 128 rows and 256 columns/);
    assert.doesNotThrow(() => makeScrollbackContracts(12, 48));
    for (const [rows, cols] of [[12, 48], [24, 80], [40, 140], [128, 256]]) {
        const history = byId(makeScrollbackContracts(rows, cols), 'scrollback.accumulate').roles.find(entry => entry.role === 'final').expected.history;
        assert.equal(history.length, 11, `${rows}x${cols}: the overflow is always 11 lines, inside the 64-line observer limit`);
    }
});

test('a faithful synthetic run grades every case supported', () => {
    for (const contract of synthetic) assert.equal(grade(contract.caseId).outcome, 'supported', contract.caseId);
});

test('missing, duplicated, reordered and miscounted history lines are unsupported, never supported', () => {
    for (const id of ['scrollback.accumulate', 'scrollback.total-lines']) {
        const edits = {
            missing: state => { state.history.splice(3, 1); },
            duplicated: state => { state.history.splice(3, 0, state.history[3]); },
            reordered: state => { [state.history[2], state.history[3]] = [state.history[3], state.history[2]]; },
            truncated: state => { state.history.pop(); },
            extra: state => { state.history.push('line 99'); },
            cleared: state => { state.history = []; },
        };
        for (const [name, change] of Object.entries(edits)) {
            const result = grade(id, only('final', change));
            assert.equal(result.outcome, 'unsupported', `${id} ${name}`);
            assert.match(result.note, /^final: The actual scrollback differs/, `${id} ${name}`);
        }
    }
});

test('history growth before the first overflow, a rewritten viewport and a missing viewport row are unsupported', () => {
    assert.match(grade('scrollback.accumulate', only('justFull', state => { state.history = ['line 0']; })).note, /^justFull: The actual scrollback differs/);
    const viewport = grade('scrollback.accumulate', only('final', state => { state.lines[4] = 'line 4'; }));
    assert.equal(viewport.outcome, 'unsupported');
    assert.match(viewport.note, /^final: Viewport row 4 differs/);
    assert.match(grade('scrollback.total-lines', only('final', state => { state.cursor = [0, 0]; })).note, /^final: The cursor is at 0,0 instead of the contracted 15,0/);
});

test('false fixtures cannot produce a conclusive grade: pre-existing history, a dirty start and a missing setup are inconclusive', () => {
    for (const id of ['scrollback.accumulate', 'scrollback.total-lines', 'scrollback.scroll-up', 'scrollback.decstbm', 'scrollback.decstbm-reset']) {
        const result = grade(id, only('initial', state => { state.history = ['stale']; }));
        assert.equal(result.outcome, 'inconclusive', id);
        assert.match(result.note, /^Control initial was not established/);
    }
    assert.equal(grade('scrollback.scroll-up', only('seed', state => { state.lines[0] = ''; })).outcome, 'inconclusive');
    assert.equal(grade('scrollback.scroll-up', only('seed', state => { state.top = 0; state.bottom = 15; })).outcome, 'inconclusive');
    assert.equal(grade('scrollback.alt-screen', only('seeded', state => { state.history = []; })).outcome, 'inconclusive', 'without seeded history isolation is vacuous');
    assert.equal(grade('scrollback.reverse-index', only('before', state => { state.lines[2] = 'X'; })).outcome, 'inconclusive');
    assert.equal(grade('scrollback.decstbm', only('innerBefore', state => { state.lines[2] = ''; })).outcome, 'inconclusive');
    assert.equal(grade('scrollback.set-region', only('ctlSet', state => { state.lines[7] = 'ROW-X'; })).outcome, 'inconclusive');
});

test('wrong direction, no-op and whole-screen scrolling are unsupported with a labeled cause', () => {
    const up = grade('scrollback.scroll-up', only('after', state => { state.lines = ['A', '', 'B', 'C', 'E', ...state.lines.slice(5)]; }));
    assert.deepEqual([up.outcome, up.note], ['unsupported', 'after: wrong direction: the region scrolled down']);
    const noop = grade('scrollback.scroll-up', only('after', state => { state.lines = ['A', 'B', 'C', 'D', 'E', ...state.lines.slice(5)]; }));
    assert.deepEqual([noop.outcome, noop.note], ['unsupported', 'after: no-op: the region did not scroll']);
    const whole = grade('scrollback.scroll-up', only('after', state => { state.lines = ['B', 'C', 'D', 'E', '', ...state.lines.slice(5)]; state.history = ['A']; state.top = 0; state.bottom = 15; }));
    assert.deepEqual([whole.outcome, whole.note], ['unsupported', 'after: whole-screen scrolling instead of region scrolling']);
    const wide = grade('scrollback.scroll-up', only('wideAfter', state => { state.history = ['ROW-0']; state.lines = [...state.lines.slice(1), '']; }));
    assert.equal(wide.outcome, 'unsupported');
    assert.match(wide.note, /^wideAfter:/);
    const down = grade('scrollback.scroll-down', only('after', state => { state.lines = ['LINE2', 'LINE3', '', '', ...state.lines.slice(4)]; state.history = ['LINE1']; }));
    assert.deepEqual([down.outcome, down.note], ['unsupported', 'after: wrong direction: the screen scrolled up']);
    const reverse = grade('scrollback.reverse-index', only('after', state => { state.lines = ['A', 'B', 'C', ...Array.from({ length: 13 }, () => '')]; }));
    assert.deepEqual([reverse.outcome, reverse.note], ['unsupported', 'after: no-op: no blank row was inserted']);
    const decstbm = grade('scrollback.decstbm', only('after', state => { state.lines[0] = ''; state.history = ['FIXED_TOP']; }));
    assert.equal(decstbm.outcome, 'unsupported');
    assert.match(decstbm.note, /^after:/);
});

test('wrong boundaries, a reset that did not reset and a region that did not confine are unsupported', () => {
    assert.match(grade('scrollback.set-region', only('set', state => { state.bottom = 8; })).note, /^set: Margins 5;9 differ from the contracted 5;10/);
    assert.match(grade('scrollback.set-region', only('reset', state => { state.top = 4; state.bottom = 9; })).note, /^reset: Margins 5;10 differ/);
    const stuck = grade('scrollback.decstbm-reset', only('final', state => {
        state.top = 4; state.bottom = 9; state.history = []; state.lines = [...state.lines.slice(0, 4), ...Array.from({ length: 12 }, () => '')];
    }));
    assert.equal(stuck.outcome, 'unsupported');
    assert.match(stuck.note, /^final: /);
    assert.match(grade('scrollback.decstbm-reset', only('ctlRegion', state => { state.history = ['line-0']; })).note, /^ctlRegion: The actual scrollback differs/);
    assert.match(grade('scrollback.decstbm', only('after', state => { state.top = 0; })).note, /^after: Margins 1;10 differ/);
});

test('origin mode, insert mode, pending wrap, auto-wrap and cursor visibility drift are unsupported', () => {
    const drifts = [{ originMode: true }, { insertMode: true }, { pendingWrap: true }, { autoWrap: false }];
    for (const drift of drifts) {
        const result = grade('scrollback.reverse-index', only('after', state => { state.details = drift; }));
        assert.equal(result.outcome, 'unsupported', JSON.stringify(drift));
        assert.match(result.note, /Origin mode, insert mode, auto-wrap, pending wrap or cursor visibility drifted/);
    }
    const hidden = syntheticScrollbackScreen(expectedOf(synthetic, 'scrollback.reverse-index', 'after'));
    assert.equal(scrollbackMismatch(scrollbackView({ ...hidden, cursorVisible: false }), expectedOf(synthetic, 'scrollback.reverse-index', 'after')).part, 'modes');
});

test('main/alternate contamination and an unrestored main screen are unsupported', () => {
    const leaked = grade('scrollback.alt-screen', only('alt', state => { state.lines[15] = 'NORMAL'; }));
    assert.deepEqual([leaked.outcome, leaked.note], ['unsupported', 'alt: Viewport row 15 differs from the contracted contents']);
    assert.match(grade('scrollback.alt-screen', only('alt', state => { state.alternate = false; })).note, /^alt: The alternate screen was not active/);
    assert.match(grade('scrollback.alt-screen', only('altOverflow', state => { state.history = [...state.history, 'ALT-0']; })).note, /^altOverflow: The actual scrollback differs/);
    assert.match(grade('scrollback.alt-screen', only('altOverflow', state => { state.history = ['ROW-0']; })).note, /^altOverflow: The actual scrollback differs/);
    assert.match(grade('scrollback.alt-screen', only('restored', state => { state.alternate = true; })).note, /^restored: The alternate screen was not active|^restored: The main screen was not active|^restored:/);
    assert.match(grade('scrollback.alt-screen', only('restored', state => { state.history = []; })).note, /^restored: The actual scrollback differs/);
    assert.match(grade('scrollback.alt-screen', only('restored', state => { state.lines[15] = ''; })).note, /^restored: Viewport row 15 differs/);
    assert.match(grade('scrollback.alt-screen', only('restored', state => { state.cursor = [0, 0]; })).note, /^restored: The cursor is at 0,0/);
    // An alternate screen that keeps no history of its own, or reports the main history, are both acceptable observations.
    assert.equal(grade('scrollback.alt-screen', only('alt', state => { state.history = []; })).outcome, 'supported');
    assert.equal(grade('scrollback.alt-screen', only('alt', state => { state.cursor = [0, 0]; })).outcome, 'supported', 'the alternate-screen cursor is observed, not required');
});

test('unrelated stored-attribute drift and stale reverse video are refused', () => {
    const contract = byId(synthetic, 'scrollback.reverse-index');
    const screens = syntheticScrollbackScreens(contract);
    const drifted = structuredClone(screens);
    drifted[1].attributeRuns[0].flags = 0x301;
    drifted[1].cellDetails[0].flags = 0x301;
    const result = gradeScrollbackCase(contract, scrollbackObservation(contract, drifted));
    assert.equal(result.outcome, 'unsupported');
    assert.match(result.note, /non-default attribute or color/);
    const reversed = structuredClone(screens);
    reversed[0].colorBinding.reverseVideo = true;
    assert.equal(gradeScrollbackCase(contract, scrollbackObservation(contract, reversed)).outcome, 'inconclusive');
    assert.throws(() => scrollbackObservation(contract, screens.slice(1)), /Missing scrollback read captures/);
    assert.throws(() => gradeScrollbackCase(contract, scrollbackObservation(contract, screens), 2), /Unknown scrollback contracts revision/);
    const noHistory = structuredClone(screens[0]);
    delete noHistory.history;
    assert.throws(() => scrollbackView(noHistory), /missing buffer state or actual history/);
    const inconsistent = structuredClone(screens[0]);
    inconsistent.bufferState.scrollbackLines = 2;
    assert.throws(() => scrollbackView(inconsistent), /missing buffer state or actual history/);
});

test('geometry that does not match the contract is inconclusive', () => {
    const small = makeScrollbackContracts(12, 48);
    const mismatch = gradeScrollbackCase(byId(small, 'scrollback.accumulate'), scrollbackObservation(byId(small, 'scrollback.accumulate'),
        syntheticScrollbackScreens(byId(synthetic, 'scrollback.accumulate'))));
    assert.equal(mismatch.outcome, 'inconclusive');
    assert.match(mismatch.note, /Measured geometry 16x60/);
    assert.equal(scrollbackRows, 16);
});

test('compact lines collapse numbered runs and keep the rest', () => {
    assert.deepEqual(compactLines(['line 0', 'line 1', 'line 2', '', 'x', 'ROW-7', 'ROW-8']), ['0-2: line 0..2', '4: x', '5-6: ROW-7..8']);
    assert.deepEqual(compactLines([]), []);
    assert.deepEqual(compactLines(['a', 'b'], 3), ['3: a', '4: b']);
    assert.deepEqual(blankState(4).lines, ['', '', '', '']);
});

// The reference is the expression compactLines used before the linear scan replaced it (SonarCloud S8786, super-linear backtracking).
const legacyTrailingNumber = text => { const match = /^(.*?)(\d+)$/.exec(text); return match ? { prefix: match[1], digits: match[2] } : null; };
const legacyCompactLines = (lines, firstRow = 0) => {
    const out = [];
    for (let index = 0; index < lines.length; index++) {
        const text = lines[index];
        if (text === '') continue;
        const match = /^(.*?)(\d+)$/.exec(text);
        let end = index;
        if (match) {
            while (end + 1 < lines.length) {
                const next = /^(.*?)(\d+)$/.exec(lines[end + 1]);
                const last = /^(.*?)(\d+)$/.exec(lines[end]);
                if (!next || next[1] !== match[1] || Number(next[2]) !== Number(last[2]) + 1) break;
                end++;
            }
        }
        out.push(end === index ? `${firstRow + index}: ${text}`
            : `${firstRow + index}-${firstRow + end}: ${text}..${/^(.*?)(\d+)$/.exec(lines[end])[2]}`);
        index = end;
    }
    return out;
};
const sampleLines = (() => {
    const alphabet = ['0', '1', '9', 'a', ' ', '-', '\n', '\r', '\u2028', 'é', '٣'];
    const lines = ['', '7', '0007', 'x', 'x1', 'line 9', 'line 10', 'a\n1', '1\n', 'a\u20281', 'é٣1'];
    let seed = 7;
    const next = () => (seed = (seed * 1103515245 + 12345) % 2147483648);
    for (let count = 0; count < 600; count++) lines.push(Array.from({ length: next() % 9 }, () => alphabet[next() % alphabet.length]).join(''));
    return lines;
})();

test('splitTrailingNumber matches the former expression on every sampled line, including terminators and non-ASCII digits', () => {
    for (const line of sampleLines) assert.deepEqual(splitTrailingNumber(line), legacyTrailingNumber(line), JSON.stringify(line));
    assert.deepEqual(splitTrailingNumber('line 12'), { prefix: 'line ', digits: '12' });
    assert.deepEqual(splitTrailingNumber('123'), { prefix: '', digits: '123' });
    assert.equal(splitTrailingNumber('no digits'), null);
    assert.equal(splitTrailingNumber('12x'), null);
    assert.equal(splitTrailingNumber('a\n1'), null, 'a line terminator in the text still prevents a match');
});

test('compactLines output is unchanged over sampled and numbered-run inputs', () => {
    for (let offset = 0; offset < sampleLines.length; offset += 25) {
        const window = sampleLines.slice(offset, offset + 25);
        assert.deepEqual(compactLines(window, offset), legacyCompactLines(window, offset), `window ${offset}`);
    }
    const runs = [...Array.from({ length: 12 }, (_, index) => `line ${index + 8}`), 'line 20', 'ROW-3', 'ROW-4', 'ROW-6', '', 'tail 0009', 'tail 0010'];
    assert.deepEqual(compactLines(runs), legacyCompactLines(runs));
    assert.deepEqual(compactLines(runs, 5), legacyCompactLines(runs, 5));
});

test('compactLines is linear on maximum-admitted and far larger adversarial lines', () => {
    const widest = `${'1'.repeat(255)}x${'1'.repeat(256)}`;
    assert.equal(splitTrailingNumber(widest).digits.length, 256);
    // 256 columns by 128 rows is the largest admitted viewport; each row is digit-rich with a late non-digit.
    const viewport = Array.from({ length: 128 }, (_, row) => `${'9'.repeat(255)}${row % 2 ? 'x' : ''}`);
    assert.equal(compactLines(viewport).length, legacyCompactLines(viewport).length);
    // The former expression needs on the order of n^2 steps for this; a linear scan finishes at once even at a million characters.
    const huge = `${'1'.repeat(1_000_000)}x`;
    assert.equal(splitTrailingNumber(huge), null);
    assert.equal(splitTrailingNumber(`${huge}7`).digits, '7');
    assert.deepEqual(compactLines([huge, huge]), [`0: ${huge}`, `1: ${huge}`]);
});

test('the observed record carries every role, kind, screen, margins, cursor and history for review', () => {
    const contract = byId(synthetic, 'scrollback.alt-screen');
    const observed = contract.observed(scrollbackObservation(contract, syntheticScrollbackScreens(contract)));
    assert.deepEqual(Object.keys(observed.roles), ['seeded', 'normal', 'alt', 'altOverflow', 'restored']);
    assert.equal(observed.roles.alt.screen, 'alternate');
    assert.equal(observed.roles.restored.screen, 'main');
    assert.deepEqual(observed.roles.seeded.history, ['0-2: ROW-0..2']);
    assert.equal(observed.roles.seeded.historyCount, 3);
    assert.ok(Object.values(observed.roles).every(role => role.matchesModel === true));
    assert.deepEqual(observed.geometry, { rows: 16, cols: 60 });
});
