import test from 'node:test';
import assert from 'node:assert/strict';
import { makeReflowCases, matchesReflowState, gradeReflowCase, reflowExperimentIds, reflowStageIds,
    asciiReflowCaseIds, asciiReflowNoncePrefix, asciiReflowReset, asciiReflowFinalRestore, asciiReflowPinned } from './ascii-reflow-contracts.mjs';

const text = row => row.map(cell => cell.text).join('').trimEnd();

test('the suite claims exactly the pinned reflow benchmark id with two fixed experiments', () => {
    assert.deepEqual(asciiReflowCaseIds, ['extensions.reflow']);
    assert.deepEqual([...reflowExperimentIds], ['reflow.width-roundtrip', 'reflow.history-roundtrip']);
    assert.deepEqual([...reflowStageIds], ['control', 'after', 'restored']);
    assert.match(asciiReflowNoncePrefix, /^NTILDE_RF_$/);
    assert.ok(asciiReflowPinned.termlessAlwaysInconclusive && asciiReflowPinned.feeds.length === 0);
    assert.ok(asciiReflowReset.startsWith('\x1b[?1049l') && asciiReflowFinalRestore.endsWith('\x1b[?25h'));
});

test('width round trip redistributes wrapped lines at the smaller width without history', () => {
    const [width] = makeReflowCases(40, 140);
    assert.deepEqual(width.target, { rows: 40, cols: 133 });
    assert.equal(width.before.history.length, 0);
    assert.equal(width.after.history.length, 0);
    // Six long hard lines: two physical rows at both widths, but different break offsets.
    const longBefore = text(width.before.grid[0]) + text(width.before.grid[1]);
    const longAfter = text(width.after.grid[0]) + text(width.after.grid[1]);
    assert.equal(longBefore, longAfter);
    assert.equal(longBefore.length, 140 + 70);
    assert.notEqual(text(width.before.grid[0]), text(width.after.grid[0]));
    assert.notEqual(text(width.before.grid[1]), text(width.after.grid[1]));
    // The parked cursor sits inside the first line's wrapped continuation and moves right by the removed columns.
    assert.deepEqual([width.before.row, width.before.col], [1, 46]);
    assert.deepEqual([width.after.row, width.after.col], [1, 53]);
    // Unwritten tails stay unwritten at both widths.
    assert.ok(width.before.grid.slice(-6).every(row => text(row) === ''));
    assert.ok(width.after.grid.slice(-6).every(row => text(row) === ''));
});

test('width restoration rejoins every hard line to the original layout', () => {
    const [width] = makeReflowCases(40, 140);
    assert.deepEqual(width.restored, width.before);
    const original = width.before.grid.map(text).filter(Boolean);
    const rejoined = width.restored.grid.map(text).filter(Boolean);
    assert.deepEqual(rejoined, original);
});

test('the bold+underline probe keeps its stored attributes through the whole width round trip', () => {
    const [width] = makeReflowCases(40, 140);
    // The probe line (logical index 1, RFs000) sits at physical row 2: after the first long line's two wrapped rows.
    for (const state of [width.before, width.after, width.restored]) {
        const row = state.grid[2];
        assert.deepEqual(row.slice(0, 5).map(cell => [cell.flags, cell.underlineStyle]),
            Array.from({ length: 5 }, () => [777, 1]));
        assert.ok(row.slice(5).every(cell => cell.flags === 768 && cell.underlineStyle === 0));
        assert.equal(text(row).startsWith('RFs000'), true);
        assert.equal(row[6].text === ' ' && row[11].text === ' ' && row[7].text !== ' ', true);
    }
});

test('history round trip displaces complete ordered rows and preserves every hard line', () => {
    const [, history] = makeReflowCases(40, 140);
    assert.deepEqual(history.target, { rows: 36, cols: 133 });
    assert.equal(history.after.history.length, 44);
    const physical = [...history.after.history, ...history.after.grid.map(text)];
    for (let index = 0; index < 40; index++) {
        assert.equal(physical[index * 2] + physical[index * 2 + 1], text(history.before.grid[index]));
    }
    // The cursor follows the last line's end: last viewport row at the smaller grid, column of the continuation.
    assert.deepEqual([history.after.row, history.after.col], [35, 5]);
});

test('history restoration follows the documented top-anchor policy with bottom padding', () => {
    const [, history] = makeReflowCases(40, 140);
    assert.equal(history.restored.history.length, 22);
    assert.equal(text(history.restored.grid[0]), text(history.before.grid[22]));
    assert.ok(history.restored.grid.slice(-22).every(row => text(row) === ''));
    assert.deepEqual([history.restored.row, history.restored.col], [17, 138]);
    const original = history.before.grid.map(text).filter(Boolean);
    const retained = [...history.restored.history, ...history.restored.grid.map(text)].filter(Boolean);
    assert.deepEqual(retained, original);
});

test('authored spaces are single, between ink, and no row tail is ever seeded', () => {
    for (const item of makeReflowCases(40, 140)) {
        assert.ok(!item.setup.includes('  '), 'No double space may appear in a fixture.');
        for (const state of [item.before, item.after, item.restored]) {
            for (const row of state.grid) assert.ok(!text(row).endsWith(' '), 'No expectation may depend on a trailing authored space.');
        }
        assert.ok(item.setup.startsWith(asciiReflowReset));
    }
    const [width] = makeReflowCases(40, 140);
    assert.ok(width.before.grid[2][6].text === ' ' && width.before.grid[2][11].text === ' ' && width.before.grid[2][7].text !== ' ',
        'The probe line carries neutral authored spaces between ink.');
});

test('the model state is distinguishable from unwritten cells only where ink pins it', () => {
    const [width] = makeReflowCases(40, 140);
    // A blank expected row is all-default cells: the projection cannot say whether a space was authored there,
    // which is why no fixture row ever ends with one.
    assert.ok(width.before.grid.at(-1).every(cell => cell.text === ' ' && cell.flags === 768));
});

test('out of budget grids are refused with the typed unavailable reason', () => {
    for (const [rows, cols] of [[11, 140], [53, 140], [40, 54], [40, 257], [23, 140], [52, 256]]) {
        assert.throws(() => makeReflowCases(rows, cols), error => error.code === 'asciiReflowGeometryUnavailable');
    }
});

test('the known 40x140 baseline fits every read budget', () => {
    const cases = makeReflowCases(40, 140);
    assert.equal(cases.length, 2);
    assert.ok(cases.every(item => item.after.history.length <= 64));
});

/** A synthetic stored screen equal to a model state, for grading refusals without an App. */
function screenFromState(state) {
    const rows = state.grid.length, cols = state.grid[0].length;
    const runs = [];
    for (const row of state.grid) {
        for (const value of row) {
            const { text, ...values } = value, last = runs.at(-1);
            if (last && JSON.stringify(last.values) === JSON.stringify(values)) last.count++;
            else runs.push({ count: 1, values });
        }
    }
    return { rows, cols, lines: state.grid.map(row => text(row)), cursorRow: state.row, cursorCol: state.col,
        cursorVisible: true,
        bufferState: { scrollTop: 0, scrollBottom: rows - 1, originMode: false, autoWrap: true, insertMode: false,
            pendingWrap: false, alternateScreen: false, scrollbackLines: state.history.length },
        history: { startLine: 0, totalLines: state.history.length, lines: [...state.history] },
        cursorMode: { style: 2, blink: true, isDefault: true },
        cellDetails: runs.map(({ count, values }) => ({ count, ...values })) };
}

const stage = (state, geometry) => ({ screen: screenFromState(state), geometry,
    cursor: [state.row, state.col], historyCount: state.history.length, matches: true });

function stagesFor(rows, cols, overrides = {}) {
    const [width, history] = makeReflowCases(rows, cols);
    const original = { rows, cols };
    const result = {
        width: { initial: stage(width.before, original), shrunk: stage(width.after, width.target), restored: stage(width.restored, original) },
        history: { initial: stage(history.before, original), shrunk: stage(history.after, history.target), restored: stage(history.restored, original) },
    };
    for (const [path, patch] of Object.entries(overrides)) {
        const [experiment, name] = path.split('.');
        Object.assign(result[experiment][name], patch);
    }
    return result;
}

test('the effect contract is supported when every stage matches its independent model', () => {
    assert.deepEqual(gradeReflowCase(stagesFor(40, 140)), { outcome: 'supported' });
});

test('a missing or wrong geometry transition blocks the verdict as inconclusive', () => {
    const noWidth = gradeReflowCase(stagesFor(40, 140, { 'width.shrunk': { geometry: { rows: 40, cols: 140 } } }));
    assert.equal(noWidth.outcome, 'inconclusive');
    const noRestore = gradeReflowCase(stagesFor(40, 140, { 'history.restored': { geometry: { rows: 36, cols: 133 } } }));
    assert.equal(noRestore.outcome, 'inconclusive');
});

for (const [path, note] of [
    ['width.initial', 'prepared width fixture'],
    ['width.shrunk', 'independent character-slicing model'],
    ['width.restored', 'round trip did not preserve'],
    ['history.initial', 'prepared history fixture'],
    ['history.shrunk', 'history rows were lost'],
    ['history.restored', 'viewport-anchor mapping'],
]) {
    test(`a differing ${path} stage is refused as unsupported`, () => {
        const [experiment, name] = path.split('.');
        const result = gradeReflowCase(stagesFor(40, 140, { [path]: { matches: false } }));
        assert.equal(result.outcome, 'unsupported');
        assert.ok(result.note.includes(note), result.note);
    });
}

test('the grader refuses to grade mutated content: lost, duplicated, reordered, merged, wrong cursor or truncated history', () => {
    const [width, history] = makeReflowCases(40, 140);
    const mutated = screenFromState(width.after);
    mutated.lines[0] = mutated.lines[0].slice(1);
    assert.equal(matchesReflowState(mutated, width.after), false, 'A lost character must break the exact grid.');
    const duplicated = screenFromState(width.after);
    duplicated.lines[2] = duplicated.lines[1];
    assert.equal(matchesReflowState(duplicated, width.after), false, 'A duplicated row must break the exact grid.');
    const reordered = screenFromState(width.after);
    [reordered.lines[1], reordered.lines[3]] = [reordered.lines[3], reordered.lines[1]];
    assert.equal(matchesReflowState(reordered, width.after), false, 'Reordered content must break the exact grid.');
    const merged = screenFromState(history.after);
    merged.lines[5] = merged.lines[5] + merged.lines[6];
    merged.lines.splice(6, 1);
    merged.lines.push('');
    merged.history.lines = [...merged.history.lines.slice(0, -1), merged.history.lines.at(-1) + 'X'];
    merged.bufferState.scrollbackLines = merged.history.lines.length;
    assert.equal(matchesReflowState(merged, history.after), false, 'A merged hard line must break the model.');
    const wrongCursor = screenFromState(width.after);
    wrongCursor.cursorCol = (wrongCursor.cursorCol + 1) % width.after.grid[0].length;
    assert.equal(matchesReflowState(wrongCursor, width.after), false, 'A wrong cursor mapping must break the model.');
    const truncated = screenFromState(history.after);
    truncated.history.lines = truncated.history.lines.slice(2);
    truncated.history.totalLines = truncated.history.lines.length;
    truncated.bufferState.scrollbackLines = truncated.history.lines.length;
    assert.equal(matchesReflowState(truncated, history.after), false, 'Truncated history must break the model.');
    const wrongWidth = screenFromState(width.after);
    wrongWidth.cols = 132;
    assert.throws(() => matchesReflowState(wrongWidth, width.after), undefined, 'Wrong-width evidence cannot grade.');
});
