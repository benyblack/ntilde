import assert from 'node:assert/strict';
import { test } from 'node:test';
import { inputModeReplyHex, validateInputAction } from './input-state.mjs';
import { inputPlanV5 } from './input-plan.mjs';
import { inputPaneId, inputRunId, initialInputScreen, syntheticInputJournal } from './fixtures/input-journal.mjs';

const geometry = { rows: 30, cols: 120 };
const journal = syntheticInputJournal(inputRunId, { initial: initialInputScreen(30, 120), plan: inputPlanV5 });
const cases = inputPlanV5.make(30, 120);
const pixelCase = cases.find(item => item.id === 'input.pxm.press.left.mid'), cellCase = cases.find(item => item.id === 'input.pxm.control.sgr.press');
const actionOf = item => structuredClone(journal.actions.find(action => action.caseId === item.id).observed);
const validate = (action, item) => validateInputAction(action, item, inputPaneId, inputRunId, geometry, 'fixture');

test('the v5 reply profile appends the DECRQM 1016 state after the 1005 and 1015 replies; earlier profiles never carry it', () => {
    const modes = mousePixel => ({ applicationKeypad: false, mouseTracking: 1000, mouseSgr: false, kittyKeyboardFlags: 0, modifyOtherKeysLevel: 0, mouseUtf8: false, mouseUrxvt: false, mousePixel });
    const text = value => Buffer.from(value, 'hex').toString('latin1');
    assert.ok(text(inputModeReplyHex(modes(true))).endsWith('\x1b[?1005;2$y\x1b[?1015;2$y\x1b[?1016;1$y'));
    assert.ok(text(inputModeReplyHex(modes(false))).endsWith('\x1b[?1016;2$y'));
    const { mousePixel, ...v4 } = modes(false);
    assert.ok(text(inputModeReplyHex(v4)).endsWith('\x1b[?1015;2$y'), 'a v4 profile has no 1016 reply');
});

test('a pixel-declared action validates with the cell the App derived, and a cell action still needs its echoed cell', () => {
    assert.doesNotThrow(() => validate(actionOf(pixelCase), pixelCase));
    assert.doesNotThrow(() => validate(actionOf(cellCase), cellCase));
});

for (const [label, change] of [
    ['a request that also names a cell', action => { action.request.column = 3; }],
    ['a request echo with another pixelX', action => { action.request.pixelX += 1; }],
    ['a request echo without pixelY', action => { delete action.request.pixelY; }],
    ['a derived column outside the grid', action => { action.app.column = geometry.cols + 1; }],
    ['a derived row of zero', action => { action.app.row = 0; }],
    ['a fractional derived cell', action => { action.app.column = 2.5; }],
    ['a missing routed DIP position', action => { action.app.y = null; }],
    ['a pixel state the plan did not declare', action => { action.app.mousePixel = false; }],
]) test('a pixel action with ' + label + ' is refused', () => {
    const action = actionOf(pixelCase); change(action);
    assert.throws(() => validate(action, pixelCase));
});

test('a cell action echoing another cell is refused and a pixel plan item never accepts a cell echo of its own', () => {
    const action = actionOf(cellCase); action.app.row = 4;
    assert.throws(() => validate(action, cellCase));
});
