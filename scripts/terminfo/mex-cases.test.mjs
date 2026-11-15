import assert from 'node:assert/strict';
import { test } from 'node:test';
import { inputAttemptCount, inputFrameCount, inputPlanForPolicy, inputPlanV3, inputPlanV4, inputPlanForSuiteId, inputPlans, currentInputPlan, mexFirstPolicy } from './input-plan.mjs';
import { inputModeReplyHex } from './input-state.mjs';
import { makeMexCases, mexBenchmarkIds, mexQueries, mexReset, mexSuiteId, minimumMexColumns, utf8Octets, utf8PinnedCases, utf8PinnedFeeds } from './mex-cases.mjs';
import { makeModifyOtherKeysCases } from './modify-other-keys-cases.mjs';
import { queryPriorCounts } from './queries.mjs';

const cases = makeMexCases(12, 120), v3 = makeModifyOtherKeysCases(12, 120);
const mexOnly = cases.slice(v3.length);
const hex = value => Buffer.from(value, 'latin1').toString('hex');

test('v4 keeps every v3 event verbatim, its modes only gaining the two neutral encoding states, and appends the extended-encoding events', () => {
    assert.equal(cases.length, 164);
    assert.equal(mexOnly.length, 46);
    v3.forEach((item, index) => {
        const { modes, ...rest } = cases[index], { modes: v3Modes, ...v3Rest } = item;
        assert.deepEqual(rest, v3Rest, item.id);
        assert.deepEqual(modes, { ...v3Modes, mouseUtf8: false, mouseUrxvt: false }, item.id);
    });
    assert.ok(mexOnly.every(item => item.id.startsWith('input.mex.')));
    assert.equal(new Set(cases.map(item => item.id)).size, cases.length);
});

test('the plan identity, reset, queries, budgets and policy selection', () => {
    assert.equal(inputPlanV4.id, 'v4'); assert.equal(inputPlanV4.suiteId, mexSuiteId); assert.equal(currentInputPlan, inputPlanV4);
    assert.equal(inputPlanForSuiteId(mexSuiteId), inputPlanV4);
    assert.equal(inputPlans.at(-1), inputPlanV4);
    assert.equal(inputPlanForPolicy(`readonly-owned-source-graph-v${mexFirstPolicy}`), inputPlanV4);
    assert.equal(inputPlanForPolicy(`readonly-owned-source-graph-v${mexFirstPolicy - 1}`), inputPlanV3, 'earlier policies keep the plan they executed');
    assert.ok(mexReset.endsWith('\x1b[?1005;1015l'));
    assert.equal(inputPlanV4.reset, mexReset);
    assert.ok(inputPlanV4.queries.endsWith(mexQueries) && mexQueries === '\x1b[?1005$p\x1b[?1015$p');
    assert.ok(inputPlanV4.queries.includes('\x1b[?4m'), 'the XTQMODKEYS query of v3 stays');
    assert.deepEqual(inputPlanV4.finalModes, { applicationKeypad: false, mouseTracking: 0, mouseSgr: false, kittyKeyboardFlags: 0, modifyOtherKeysLevel: 0, mouseUtf8: false, mouseUrxvt: false });
    assert.ok(inputAttemptCount(cases.length) <= inputPlanV4.captureBudget, 'the raw capture budget covers bind, two captures per event and the restoration');
    assert.ok(cases.length + 3 <= inputPlanV4.readerBudget, 'each reader stays inside its snapshot budget');
    assert.equal(inputFrameCount(cases.length), 331);
    assert.equal(queryPriorCounts.at(-1), inputAttemptCount(cases.length) + 30, 'later suites owe the v4 capture count');
});

test('every new event resets the encodings, selects exactly one encoding and its mode profile equals the DECRQM state the setup produces', () => {
    for (const item of mexOnly) {
        const resets = item.setup.startsWith(mexReset);
        if (!resets) assert.ok(utf8PinnedFeeds.includes(item.setup), `${item.id} continues the state of the previous case only for the pinned feeds`);
        const selected = [item.modes.mouseSgr, item.modes.mouseUtf8, item.modes.mouseUrxvt].filter(Boolean);
        assert.ok(selected.length <= 1, item.id);
        assert.equal(item.modes.modifyOtherKeysLevel, 0); assert.equal(item.modes.kittyKeyboardFlags, 0); assert.equal(item.modes.applicationKeypad, false);
        // Replay the setup's DEC private mode writes with xterm's single-slot rule and compare with the declared profile.
        const state = { tracking: 0, encoding: '' };
        let carried = mexOnly[mexOnly.indexOf(item) - 1];
        if (!resets) state.tracking = carried.modes.mouseTracking, state.encoding = [['sgr', carried.modes.mouseSgr], ['utf8', carried.modes.mouseUtf8], ['urxvt', carried.modes.mouseUrxvt]].find(entry => entry[1])?.[0] ?? '';
        const names = { 1005: 'utf8', 1006: 'sgr', 1015: 'urxvt' };
        for (const [, mode, final] of item.setup.slice(resets ? mexReset.length : 0).matchAll(new RegExp(String.raw`\x1b\[\?(\d+)([hl])`, 'g'))) {
            const number = Number(mode);
            if (names[number]) {
                if (final === 'h') state.encoding = names[number]; else if (state.encoding === names[number]) state.encoding = '';
            } else state.tracking = final === 'h' ? number : 0;
        }
        assert.deepEqual([item.modes.mouseTracking, [item.modes.mouseSgr ? 'sgr' : item.modes.mouseUtf8 ? 'utf8' : item.modes.mouseUrxvt ? 'urxvt' : ''][0]], [state.tracking, state.encoding], item.id);
        assert.match(Buffer.from(inputModeReplyHex(item.modes), 'hex').toString('latin1'), new RegExp(String.raw`\x1b\[\?1005;[12]\$y\x1b\[\?1015;[12]\$y$`));
    }
});

test('the pinned probe feeds are exactly two consecutive plan cases that start from a proved all-off state and carry a key-up (the App refuses a pointer event while no tracking selector is active)', () => {
    const set = cases.find(item => item.id === utf8PinnedCases.set), reset = cases.find(item => item.id === utf8PinnedCases.reset);
    assert.equal(set.setup, '\x1b[?1005h'); assert.equal(reset.setup, '\x1b[?1005l');
    assert.equal(cases.indexOf(reset), cases.indexOf(set) + 1);
    const before = cases[cases.indexOf(set) - 1];
    assert.deepEqual([before.modes.mouseTracking, before.modes.mouseSgr, before.modes.mouseUtf8, before.modes.mouseUrxvt], [0, false, false, false]);
    for (const item of [set, reset]) { assert.equal(item.expectedHex, ''); assert.equal(item.event.kind, 'keyUp'); assert.equal(item.roles['modes.utf8-mouse-1005'], 'required'); }
    assert.equal(set.modes.mouseUtf8, true); assert.equal(reset.modes.mouseUtf8, false);
});

test('roles: both benchmarks have required experiments, shared controls and exactly one labeled rightmost-column event each', () => {
    for (const id of mexBenchmarkIds) {
        const roles = cases.map(item => item.roles[id]).filter(Boolean);
        assert.ok(roles.includes('required') && roles.includes('control'), id);
        assert.equal(roles.filter(role => role === 'label').length, 1, id);
    }
    assert.deepEqual(mexOnly.filter(item => item.roles['modes.utf8-mouse-1005'] && item.roles['input.urxvt-mouse']).map(item => item.id),
        ['input.mex.baseline.reset', 'input.mex.control.legacy.press', 'input.mex.control.sgr.press']);
    const rightmost = cases.filter(item => item.id.endsWith('.rightmost'));
    assert.equal(rightmost.length, 2);
    assert.equal(rightmost[0].event.column, 120); assert.equal(rightmost[0].expectedHex, hex('\x1b[M ' + utf8Octets(120) + '#'));
    assert.equal(rightmost[1].expectedHex, hex('\x1b[32;120;3M'));
});

test('reported motion events never repeat the cell of the preceding reported motion and every planned cell lies inside the grid', () => {
    const reported = cases.filter(item => item.event.kind === 'mouseMove' && item.expectedHex !== '');
    const lastRouted = reported.filter(item => !item.id.startsWith('input.mex.')).at(-1);
    assert.equal(lastRouted.id, 'input.mouse.button.move.sgr');
    reported.slice(1).forEach((item, index) => assert.notDeepEqual([item.event.column, item.event.row], [reported[index].event.column, reported[index].event.row], item.id));
    for (const item of cases) if (item.event.kind.startsWith('mouse')) assert.ok(item.event.column <= 120 && item.event.row <= 12, item.id);
    assert.ok(utf8Octets(95).length === 1 && utf8Octets(96).length === 2 && hex(utf8Octets(96)) === 'c280' && hex(utf8Octets(2015)) === 'dfbf');
});

test('a grid narrower than the plan needs is refused as typed unavailability, the plan itself needs 97 columns', () => {
    assert.equal(minimumMexColumns, 97);
    for (const cols of [96, 40]) assert.throws(() => makeMexCases(12, cols), error => error.code === 'inputGeometryUnavailable');
    assert.throws(() => makeMexCases(3, 120), error => error.code === 'inputGeometryUnavailable');
    assert.doesNotThrow(() => makeMexCases(12, 97));
});
