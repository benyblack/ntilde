import assert from 'node:assert/strict';
import { test } from 'node:test';
import { inputAttemptCount, inputFrameCount, inputPlanForPolicy, inputPlanV4, inputPlanV5, inputPlanV6, inputPlanForSuiteId, inputPlans, currentInputPlan, pxmFirstPolicy } from './input-plan.mjs';
import { inputModeReplyHex } from './input-state.mjs';
import { makeMexCases } from './mex-cases.mjs';
import { makePxmCases, minimumPxmColumns, minimumPxmRows, pixelPositions, pxmBenchmarkIds, pxmQueries, pxmReset, pxmSuiteId } from './pxm-cases.mjs';
import { queryPriorCounts } from './queries.mjs';

const cases = makePxmCases(30, 140), v4 = makeMexCases(30, 140), pxmOnly = cases.slice(v4.length);
const B = pxmBenchmarkIds[0];

test('v5 keeps every v4 event verbatim, its modes only gaining the neutral pixel state, and appends the pixel-encoding events', () => {
    assert.equal(cases.length, 196);
    assert.equal(pxmOnly.length, 32);
    v4.forEach((item, index) => {
        const { modes, ...rest } = cases[index], { modes: v4Modes, ...v4Rest } = item;
        assert.deepEqual(rest, v4Rest, item.id);
        assert.deepEqual(modes, { ...v4Modes, mousePixel: false }, item.id);
    });
    assert.ok(pxmOnly.every(item => item.id.startsWith('input.pxm.')));
    assert.equal(new Set(cases.map(item => item.id)).size, cases.length);
});

test('current collection probes the first unimplemented level and retains the original v5 journal contract', () => {
    assert.equal(currentInputPlan, inputPlanV6);
    assert.equal(inputPlanForSuiteId(inputPlanV6.suiteId), inputPlanV6);
    const current = inputPlanV6.make(30, 140);
    const index = cases.findIndex(item => item.id === 'input.mok.trans.l3.ignored');
    assert.ok(index >= 0);
    assert.deepEqual(current[index], { ...cases[index], id: 'input.mok.trans.l4.ignored',
        setup: cases[index].setup.replace('\x1b[>4;3m', '\x1b[>4;4m') });
    assert.deepEqual(current.filter((_, i) => i !== index), cases.filter((_, i) => i !== index));
});

test('the plan identity, reset, queries, budgets and policy selection', () => {
    assert.equal(inputPlanV5.id, 'v5'); assert.equal(inputPlanV5.suiteId, pxmSuiteId);
    assert.equal(inputPlanForSuiteId(pxmSuiteId), inputPlanV5);
    assert.equal(inputPlans.at(-1), currentInputPlan);
    assert.equal(inputPlanForPolicy(`readonly-owned-source-graph-v${pxmFirstPolicy}`), inputPlanV5);
    assert.equal(inputPlanForPolicy(`readonly-owned-source-graph-v${pxmFirstPolicy - 1}`), inputPlanV4, 'earlier policies keep the plan they executed');
    assert.ok(pxmReset.endsWith('\x1b[?1005;1015l\x1b[?1016l'));
    assert.equal(inputPlanV5.reset, pxmReset);
    assert.ok(inputPlanV5.queries.endsWith(pxmQueries) && pxmQueries.endsWith('\x1b[?1005$p\x1b[?1015$p\x1b[?1016$p'));
    assert.ok(inputPlanV5.queries.includes('\x1b[?4m'), 'the XTQMODKEYS query of v3 stays');
    assert.deepEqual(inputPlanV5.finalModes, { applicationKeypad: false, mouseTracking: 0, mouseSgr: false, kittyKeyboardFlags: 0, modifyOtherKeysLevel: 0, mouseUtf8: false, mouseUrxvt: false, mousePixel: false });
    assert.ok(inputAttemptCount(cases.length) <= inputPlanV5.captureBudget, 'the raw capture budget covers bind, two captures per event and the restoration');
    assert.ok(cases.length + 3 <= inputPlanV5.readerBudget, 'each reader stays inside its snapshot budget');
    assert.equal(inputFrameCount(cases.length), 395);
    assert.equal(queryPriorCounts.at(-1), inputAttemptCount(cases.length) + 30, 'later suites owe the v5 capture count');
});

test('every new event resets the encodings, selects exactly one encoding and its mode profile equals the DECRQM state the setup produces', () => {
    const names = { 1005: 'utf8', 1006: 'sgr', 1015: 'urxvt', 1016: 'pixel' };
    for (const item of pxmOnly) {
        assert.ok(item.setup.startsWith(pxmReset), item.id);
        const selected = [item.modes.mouseSgr, item.modes.mouseUtf8, item.modes.mouseUrxvt, item.modes.mousePixel].filter(Boolean);
        assert.ok(selected.length <= 1, item.id);
        assert.deepEqual([item.modes.modifyOtherKeysLevel, item.modes.kittyKeyboardFlags, item.modes.applicationKeypad], [0, 0, false]);
        // Replay the setup's DEC private mode writes with xterm's single-slot rule and compare with the declared profile.
        const state = { tracking: 0, encoding: '' };
        for (const [, mode, final] of item.setup.slice(pxmReset.length).matchAll(/\x1b\[\?(\d+)([hl])/g)) {
            const number = Number(mode);
            if (names[number]) {
                if (final === 'h') state.encoding = names[number]; else if (state.encoding === names[number]) state.encoding = '';
            } else state.tracking = final === 'h' ? number : 0;
        }
        const encoding = ['sgr', 'utf8', 'urxvt', 'pixel'].find(name => item.modes['mouse' + { sgr: 'Sgr', utf8: 'Utf8', urxvt: 'Urxvt', pixel: 'Pixel' }[name]]) ?? '';
        assert.deepEqual([item.modes.mouseTracking, encoding], [state.tracking, state.encoding], item.id);
        assert.match(Buffer.from(inputModeReplyHex(item.modes), 'hex').toString('latin1'), /\x1b\[\?1005;[12]\$y\x1b\[\?1015;[12]\$y\x1b\[\?1016;[12]\$y$/);
    }
});

test('pixel events carry exact pixels and no cell, cell events carry no pixels, and every pixel lies inside the smallest admitted text area', () => {
    const pixels = cases.filter(item => item.event.pixelX !== undefined);
    assert.equal(pixels.length, 24);
    for (const item of pixels) {
        assert.equal(item.event.column, undefined, item.id); assert.equal(item.event.row, undefined, item.id);
        assert.equal(item.event.pixelY === undefined, false, item.id);
        assert.ok(Number.isSafeInteger(item.event.pixelX) && item.event.pixelX >= 0 && item.event.pixelX < 140 * 6, item.id);
        assert.ok(Number.isSafeInteger(item.event.pixelY) && item.event.pixelY >= 0 && item.event.pixelY < 30 * 12, item.id);
    }
    for (const item of cases.filter(entry => entry.event.column !== undefined)) assert.equal(item.event.pixelX, undefined, item.id);
    assert.ok(pixelPositions.far.pixelX > 223 && pixelPositions.edge.pixelX > 223, 'the far pixels exceed what legacy X10 coordinates could carry');
});

test('roles: the benchmark has required experiments and exactly the two cell-coordinate controls', () => {
    const roles = cases.map(item => item.roles[B]).filter(Boolean);
    assert.equal(roles.filter(role => role === 'control').length, 2);
    assert.equal(roles.filter(role => role === 'required').length, 30);
    assert.deepEqual(pxmOnly.filter(item => item.roles[B] === 'control').map(item => item.id), ['input.pxm.control.legacy.press', 'input.pxm.control.sgr.press']);
    assert.ok(cases.slice(0, v4.length).every(item => item.roles[B] === undefined));
});

test('the pinned probe state case is a key-up that proves the selector alone, and pixel motion differs from the previous reported pixel except the deliberate repeat', () => {
    const alone = cases.find(item => item.id === 'input.pxm.alone.set');
    assert.deepEqual([alone.setup, alone.event.kind, alone.expectedHex, alone.modes.mousePixel], [pxmReset + '\x1b[?1016h', 'keyUp', '', true]);
    const reported = cases.filter(item => item.event.kind === 'mouseMove' && item.event.pixelX !== undefined);
    reported.slice(1).forEach((item, index) => {
        const previous = reported[index], same = previous.event.pixelX === item.event.pixelX && previous.event.pixelY === item.event.pixelY;
        assert.equal(same, item.id === 'input.pxm.hover.same-pixel', item.id);
        assert.equal(item.expectedHex === '', same, item.id);
    });
});

test('a grid smaller than the plan needs is refused as typed unavailability', () => {
    assert.deepEqual([minimumPxmColumns, minimumPxmRows], [97, 24]);
    for (const [rows, cols] of [[30, 96], [23, 140], [3, 120], [30, 40]]) assert.throws(() => makePxmCases(rows, cols), error => error.code === 'inputGeometryUnavailable', `${rows}x${cols}`);
    assert.doesNotThrow(() => makePxmCases(24, 97));
});
