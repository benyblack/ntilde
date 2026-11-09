import assert from 'node:assert/strict';
import { test } from 'node:test';
import { cursorQualCaseIds, cursorQualMismatch, cursorQualReset, cursorReportMismatch, expectedCursorReport, hideCleanup, hideClear, hideSeed, makeCursorQualContracts,
    requestedCursorMode, cursorScene, cursorQualView } from './cursor-qual-contracts.mjs';
import { planFeeds, planReads } from './graded-plan.mjs';
import { SimTerminal, cursorRows, cursorCols } from './fixtures/cursor-qual-evidence.mjs';

const contracts = makeCursorQualContracts(cursorRows, cursorCols);
const byId = Object.fromEntries(contracts.map(contract => [contract.caseId, contract]));
const group = (caseId, id) => byId[caseId].groups.find(entry => entry.id === id);

test('the contracts cover exactly the four pinned ids, in order, and refuse unmeasurable geometry', () => {
    assert.deepEqual(contracts.map(contract => contract.caseId), [...cursorQualCaseIds]);
    assert.deepEqual([...cursorQualCaseIds], ['cursor.cup-scroll-region', 'cursor.hide', 'cursor.shape', 'cursor.reverse-wrap']);
    for (const [rows, cols] of [[14, 60], [24, 47], [129, 60], [24, 257], [1.5, 60], [undefined, undefined]]) {
        assert.throws(() => makeCursorQualContracts(rows, cols), /at least 15 rows and 48 columns/, `${rows}x${cols}`);
    }
});

test('cup-scroll-region replays the exact pinned bytes: termless fixture, app fixture with CUD 999, and the same finally cleanup', () => {
    const termless = group('cursor.cup-scroll-region', 'termless'), app = group('cursor.cup-scroll-region', 'app');
    assert.deepEqual(termless.pinnedFeeds, ['\x1b[5;15r', '\x1b[?6h', '\x1b[1;1H', '\x1b[?6l', '\x1b[r']);
    assert.deepEqual(app.pinnedFeeds, ['\x1b[5;15r', '\x1b[?6h', '\x1b[1;1H', '\x1b[2;1H', '\x1b[999B', '\x1b[?6l', '\x1b[r']);
    assert.equal(termless.tailCount, 2); assert.equal(app.tailCount, 2);
    assert.deepEqual(termless.pinnedReadAt, { 3: 0 });
    assert.deepEqual(app.reads.map(read => read.kind), ['cpr', 'cpr', 'cpr', 'state']);
    // CUD target is max(999, rows + 1), so a tall screen changes the pinned bytes.
    assert.equal(group('cursor.cup-scroll-region', 'app').pinnedFeeds[4], '\x1b[999B');
    assert.equal(makeCursorQualContracts(128, 60)[0].groups[1].pinnedFeeds[4], '\x1b[999B');
});

test('every group starts from the measured reset and the plan counts are consistent', () => {
    for (const contract of contracts) {
        assert.equal(planFeeds(contract)[0], cursorQualReset);
        assert.equal(planReads(contract), contract.groups.reduce((sum, entry) => sum + entry.reads.length, 0));
        for (const entry of contract.groups) {
            assert.equal(entry.plan[0].feed, cursorQualReset, `${contract.caseId}/${entry.id}`);
            assert.ok(entry.reads.length >= 2);
        }
    }
    assert.ok(cursorQualReset.includes('\x1b[?45l') && cursorQualReset.includes('\x1b[?6l') && cursorQualReset.includes('\x1b[r'));
    assert.ok(cursorQualReset.endsWith('\x1b[?25h\x1b[0 q'));
});

test('hide replays the pinned DECTCEM sequence and the pinned capture fixture byte for byte', () => {
    const termless = group('cursor.hide', 'termless'), app = group('cursor.hide', 'app');
    assert.deepEqual(termless.pinnedFeeds, ['\x1b[?25h', '\x1b[?25l', '\x1b[?25h']);
    assert.deepEqual(termless.pinnedReadAt, { 0: 0, 1: 1, 2: 2, 3: 3 });
    assert.deepEqual(app.pinnedFeeds, ['\x1b[0m\x1b[2J\x1b[H', '\x1b[1;1HCURSOR\x1b[2;1HL  R\x1b[2;3H\x1b[2 q\x1b[?25h', '\x1b[?25l', '\x1b[0m\x1b[2J\x1b[H\x1b[?25h\x1b[0 q']);
    assert.equal(hideClear, app.pinnedFeeds[0]); assert.equal(hideSeed, app.pinnedFeeds[1]); assert.equal(hideCleanup, app.pinnedFeeds[3]);
    assert.deepEqual(app.pinnedCaptureAt, [0, 1]);
    assert.deepEqual(app.reads.filter(read => read.pinnedCapture !== undefined || read.kind === 'capture').map(read => read.label).slice(0, 2),
        ['Shown steady block cursor requested at row 2 column 3', 'Hidden cursor requested at the same cell']);
    assert.equal(app.reads[3].kind, 'capture', 'the control scene is rendered again after restoration');
    assert.deepEqual(byId['cursor.hide'].pixelChecks.map(([id]) => id), ['hide.control-cursor-visible', 'hide.target-cell-blank', 'hide.unrelated-pixels-unchanged', 'hide.rerender-matches-control']);
});

test('shape replays CSI 6 SP q (termless) and CSI 5 SP q with captures at both phases (app), and keeps the shape references separate', () => {
    const termless = group('cursor.shape', 'termless'), app = group('cursor.shape', 'app'), shapes = group('cursor.shape', 'shapes');
    assert.deepEqual(termless.pinnedFeeds, ['\x1b[6 q']);
    assert.deepEqual(app.pinnedFeeds, ['\x1b[5 q', '\x1b[0 q']);
    assert.equal(app.kind, 'pinned-app-repaired', 'the shape callback has the same undeclared-geometry defect once a capture adapter exists');
    assert.deepEqual(app.reads.filter(read => read.kind === 'capture').map(read => read.timeMs), [0, 0, 530]);
    assert.deepEqual(shapes.reads.filter(read => read.kind === 'capture').map(read => read.expect.mode ?? 'default'), [requestedCursorMode(2), requestedCursorMode(4), requestedCursorMode(6), requestedCursorMode(6)]);
    assert.equal(shapes.reads[3].expect.visible, false);
    assert.deepEqual(requestedCursorMode(0), 'default');
    assert.deepEqual(requestedCursorMode(5), { style: 1, blink: true });
    assert.deepEqual(requestedCursorMode(6), { style: 1, blink: false });
    assert.deepEqual(requestedCursorMode(3), { style: 2, blink: true });
    assert.deepEqual(requestedCursorMode(1), { style: 0, blink: true });
    assert.deepEqual(requestedCursorMode(2), { style: 0, blink: false });
    assert.ok(cursorScene.endsWith('\x1b[2;3H'));
});

test('reverse-wrap replays the pinned wrap fixture at the measured width and a mode-off control with the same bytes', () => {
    const termless = group('cursor.reverse-wrap', 'termless'), app = group('cursor.reverse-wrap', 'app'), off = group('cursor.reverse-wrap', 'mode-off');
    const wrap = 'A'.repeat(cursorCols) + 'B';
    assert.deepEqual(termless.pinnedFeeds, ['\x1b[?7h', '\x1b[?45h', '\x1b[H', wrap, '\b\b', '\x1b[?45l', '\x1b[?7h']);
    assert.deepEqual(termless.pinnedReadAt, { 0: 0, 3: 1, 4: 2, 5: 3 });
    assert.deepEqual(app.pinnedFeeds, ['\x1b[?45h', '\x1b[?7h', '\x1b[H', wrap, '\b\b', '\x1b[?45l']);
    assert.equal(app.kind, 'pinned-app-repaired');
    assert.deepEqual(app.reads[3].expect.cursor, [0, cursorCols - 1]);
    assert.deepEqual(planFeeds({ plan: off.plan }).slice(1, 5), ['\x1b[?7h', '\x1b[H', wrap, '\b\b'].slice(0, 4));
    assert.ok(!planFeeds({ plan: off.plan }).includes('\x1b[?45h'), 'the control never sets reverse-wrap');
    assert.deepEqual(off.reads[1].expect.cursor, [1, 0], 'without ?45 the second backspace stays at column 1 of row 2');
    const wide = makeCursorQualContracts(cursorRows, 100)[3];
    assert.equal(wide.groups[0].pinnedFeeds[3], 'A'.repeat(100) + 'B');
});

test('the CPR a conforming terminal sends is region-relative under DECOM and absolute otherwise, judged on the observed physical cursor', () => {
    assert.deepEqual(expectedCursorReport({ cursor: [4, 0], top: 4, origin: true }), [1, 1]);
    assert.deepEqual(expectedCursorReport({ cursor: [14, 0], top: 4, origin: true }), [11, 1]);
    assert.deepEqual(expectedCursorReport({ cursor: [4, 0], top: 4, origin: false }), [5, 1]);
    const view = { cursor: [4, 0], top: 4, origin: true };
    assert.equal(cursorReportMismatch({ raw: '\x1b[1;1R', row: 1, col: 1 }, view), null);
    // A region-relative reply must not be mistaken for the absolute physical row (and an absolute one is flagged when relative is due).
    assert.equal(cursorReportMismatch({ raw: '\x1b[5;1R', row: 5, col: 1 }, view).part, 'cpr');
    assert.match(cursorReportMismatch({ raw: '\x1b[5;1R', row: 5, col: 1 }, view).note, /region-relative report 1;1/);
    assert.equal(cursorReportMismatch(null, view).part, 'cpr-missing');
    assert.equal(cursorReportMismatch({ raw: '\x1b[5;1R', row: 5, col: 1 }, { ...view, origin: false }), null);
});

function viewOf(setup) {
    const sim = new SimTerminal();
    sim.feed('\x1b[?1049h'); sim.feed(cursorQualReset); sim.feed(setup);
    return cursorQualView(sim.screen());
}
const baseline = cursorQualView(new SimTerminal().screen());

test('state expectations name the first drifted part: screen, margins, origin, modes, visibility, mode, viewport, attributes and cursor', () => {
    const view = viewOf('\x1b[5;15r\x1b[?6h\x1b[1;1H');
    const expect = { cursor: [4, 0], margins: [4, 14], origin: true };
    assert.equal(cursorQualMismatch(view, expect, baseline), null);
    assert.equal(cursorQualMismatch({ ...view, alternate: false }, expect, baseline).part, 'screen');
    assert.equal(cursorQualMismatch({ ...view, bottom: 15 }, expect, baseline).part, 'margins');
    assert.equal(cursorQualMismatch({ ...view, origin: false }, expect, baseline).part, 'origin');
    assert.equal(cursorQualMismatch({ ...view, autoWrap: false }, expect, baseline).part, 'modes');
    assert.equal(cursorQualMismatch({ ...view, pending: true }, expect, baseline).part, 'modes');
    assert.equal(cursorQualMismatch({ ...view, visible: false }, expect, baseline).part, 'visibility');
    assert.equal(cursorQualMismatch({ ...view, mode: { style: 1, blink: false, isDefault: false } }, expect, baseline).part, 'cursor-mode');
    assert.equal(cursorQualMismatch({ ...view, lines: ['x', ...view.lines.slice(1)] }, expect, baseline).part, 'viewport');
    assert.equal(cursorQualMismatch({ ...view, storedDefault: false }, expect, baseline).part, 'attributes');
    assert.equal(cursorQualMismatch({ ...view, cursor: [5, 0] }, expect, baseline).part, 'cursor');
});

test('default-versus-explicit intent: a host-default expectation needs isDefault and the baseline shape, an explicit one forbids isDefault', () => {
    const base = viewOf('');
    assert.equal(cursorQualMismatch(base, { cursor: [0, 0] }, baseline), null);
    const explicit = { ...base, mode: { ...base.mode, isDefault: false } };
    assert.equal(cursorQualMismatch(explicit, { cursor: [0, 0] }, baseline).part, 'cursor-mode', 'CSI 0 q must leave host-default intent, not an explicit copy of it');
    assert.equal(cursorQualMismatch(explicit, { cursor: [0, 0], mode: { style: base.mode.style, blink: base.mode.blink } }, baseline), null);
    assert.equal(cursorQualMismatch(base, { cursor: [0, 0], mode: { style: base.mode.style, blink: base.mode.blink } }, baseline).part, 'cursor-mode');
});

test('relative expectations compare with an earlier read of the same group (restoration keeps the measured cursor and text)', () => {
    const wrapped = viewOf('A'.repeat(cursorCols) + 'B');
    assert.equal(cursorQualMismatch(wrapped, { cursorSameAs: 0, textSameAs: 0 }, baseline, [wrapped]), null);
    assert.equal(cursorQualMismatch({ ...wrapped, cursor: [0, 3] }, { cursorSameAs: 0, textSameAs: 0 }, baseline, [wrapped]).part, 'cursor');
    assert.equal(cursorQualMismatch({ ...wrapped, lines: ['', ''] }, { cursorSameAs: 0, textSameAs: 0 }, baseline, [wrapped]).part, 'viewport');
});

test('the simulated terminal used as the test oracle behaves like the protocol for the fixture bytes', () => {
    const sim = new SimTerminal();
    sim.feed('\x1b[?1049h'); sim.feed(cursorQualReset);
    sim.feed('\x1b[5;15r\x1b[?6h\x1b[1;1H');
    assert.deepEqual([sim.row, sim.col, sim.top, sim.bottom, sim.origin], [4, 0, 4, 14, true]);
    assert.deepEqual(sim.report(), [1, 1]);
    sim.feed('\x1b[999B');
    assert.deepEqual([sim.row, sim.report()], [14, [11, 1]]);
    sim.feed('\x1b[?6l\x1b[r');
    assert.deepEqual([sim.row, sim.origin, sim.top, sim.bottom], [0, false, 0, cursorRows - 1]);
    sim.feed(cursorQualReset); sim.feed('\x1b[?45h\x1b[H' + 'A'.repeat(cursorCols) + 'B');
    assert.deepEqual([sim.row, sim.col], [1, 1]);
    sim.feed('\b\b');
    assert.deepEqual([sim.row, sim.col], [0, cursorCols - 1]);
});
