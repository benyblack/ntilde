// Fixed generated-event plan (v2) for the routed-input benchmark layer: input.x10-mouse, input.button-event-mouse and
// modes.application-keypad. Every expectation is a literal protocol byte string; the independent oracle in
// routed-input-contracts.mjs must agree with each literal, and neither is ever derived from the App's encoder.
// v1 (input-cases.mjs) stays frozen for the historical 39-case publication.
import { inputReset } from './input-cases.mjs';

export const routedInputPlanId = 'v2';
export const routedInputSuiteId = 'ntilde-owned-routed-input-v2';
export const ownedRoutedInputPolicy = 'one declared Avalonia event at a time in the proved disposable local pane; raw child stdin plus a post-action FIFO ordering reply; '
    + 'mode controls must match exactly before dispatch, event bytes are recorded and compared with literal protocol expectations; modes restored before teardown';
const hex = value => Buffer.from(value, 'latin1').toString('hex');

export const minimumRoutedInputColumns = 97;
export const benchmarkIds = Object.freeze(['input.x10-mouse', 'input.button-event-mouse', 'modes.application-keypad']);
const X10 = benchmarkIds[0], BUTTON = benchmarkIds[1], KEYPAD = benchmarkIds[2];

const noKeypad = { applicationKeypad: false, kittyKeyboardFlags: 0 };

/** The 30 keypad and 30 mouse experiments, in dispatch order. Roles: required (graded), control (must hold before a
 * mismatch can be attributed) and label (reported, never graded). */
export function makeRoutedInputCases(rows, cols) {
    // The widest planned pointer cell is column 97 (a held-button motion in a fresh cell), so a grid narrower than that cannot run the plan.
    if (!Number.isSafeInteger(rows) || rows < 6 || rows > 128
        || !Number.isSafeInteger(cols) || cols < minimumRoutedInputColumns || cols > 256 || rows * cols > 8192) {
        throw Object.assign(new Error(`Routed-input qualification requires an observed grid with at least ${minimumRoutedInputColumns} columns.`), { code: 'inputGeometryUnavailable' });
    }
    const cases = [];
    const add = (id, setup, event, expected, modes, roles, reset = true) => cases.push({ id: 'input.' + id,
        setup: (reset ? inputReset : '') + setup, event: { modifiers: 0, ...event }, expectedHex: hex(expected), modes, roles });
    const keypad = applicationKeypad => ({ ...noKeypad, applicationKeypad, mouseTracking: 0, mouseSgr: false });

    // --- modes.application-keypad: numeric controls, the pinned ESC = / ESC > pair, then the keypad table ---------------------
    add('keypad.normal.keyDown', '', { kind: 'keyDown', key: '7' }, '', keypad(false), { [KEYPAD]: 'control' });
    add('keypad.normal.text', '', { kind: 'text', text: '7' }, '7', keypad(false), { [KEYPAD]: 'control' });
    add('keypad.normal.keyUp', '', { kind: 'keyUp', key: '7' }, '', keypad(false), { [KEYPAD]: 'control' });
    add('keypad.normal.enter', '', { kind: 'keyDown', key: 'enter' }, '\r', keypad(false), { [KEYPAD]: 'control' });
    // Numeric mode: the key-down is unhandled, so the backend's text for it is the one delivery (a key-down carrying text raises that
    // TextInput right after it in the same dispatch).
    add('keypad.normal.7.withText', '', { kind: 'keyDown', key: '7', text: '7' }, '7', keypad(false), { [KEYPAD]: 'control' });
    // The next case's setup is exactly the pinned probe's first feed (ESC =) and the one after the 17-key table is its second (ESC >).
    const table = [['0', 'p'], ['1', 'q'], ['2', 'r'], ['3', 's'], ['4', 't'], ['5', 'u'], ['6', 'v'], ['7', 'w'], ['8', 'x'], ['9', 'y'],
        ['decimal', 'n'], ['separator', 'l'], ['add', 'k'], ['subtract', 'm'], ['multiply', 'j'], ['divide', 'o'], ['enter', 'M']];
    const keypadText = { 7: '7', add: '+' };
    let selected = false;
    for (const [key, final] of table) {
        add('keypad.application.' + key, selected ? '' : '\x1b=', { kind: 'keyDown', key }, '\x1bO' + final, keypad(true), { [KEYPAD]: 'required' }, false);
        selected = true;
        if (key === '7' || key === 'add') {
            // A handled keypad key reserves one TextInput callback: the backend's text for the same key-down must not duplicate it.
            add(`keypad.application.${key}.withText`, '', { kind: 'keyDown', key, text: keypadText[key] }, '\x1bO' + final, keypad(true), { [KEYPAD]: 'required' }, false);
        }
        if (key === '7') {
            add('keypad.application.7.keyUp', '', { kind: 'keyUp', key: '7' }, '', keypad(true), { [KEYPAD]: 'required' }, false);
            // Separate calls never share a decision: a standalone text commit after the release is ordinary text, not a keypad sequence.
            add('keypad.application.7.text.standalone', '', { kind: 'text', text: '7' }, '7', keypad(true), { [KEYPAD]: 'required' }, false);
        }
    }
    add('keypad.application.enter.keyUp', '', { kind: 'keyUp', key: 'enter' }, '', keypad(true), { [KEYPAD]: 'required' }, false);
    add('keypad.restore.deckpnm.enter', '\x1b>', { kind: 'keyDown', key: 'enter' }, '\r', keypad(false), { [KEYPAD]: 'required' }, false);
    add('keypad.decnkm.set.enter', '\x1b[?66h', { kind: 'keyDown', key: 'enter' }, '\x1bOM', keypad(true), { [KEYPAD]: 'required' }, false);
    add('keypad.decnkm.reset.enter', '\x1b[?66l', { kind: 'keyDown', key: 'enter' }, '\r', keypad(false), { [KEYPAD]: 'required' }, false);

    // --- mouse: one tracking selector per case, legacy bytes unless the id says sgr --------------------------------------------
    const mouse = (id, tracking, sgr, event, expected, roles) => add('mouse.' + id,
        `\x1b[?${tracking}h` + (sgr ? '\x1b[?1006h' : ''),
        { column: 96, row: 3, ...event }, expected,
        { applicationKeypad: false, mouseTracking: tracking, mouseSgr: sgr, kittyKeyboardFlags: 0 }, roles);
    const press = (id, button, modifiers, expected, roles, tracking = 9, sgr = false) => mouse(id, tracking, sgr, { kind: 'mousePress', button, modifiers }, expected, roles);
    const x10 = { [X10]: 'required' }, x10Control = { [X10]: 'control' }, button = { [BUTTON]: 'required' }, buttonControl = { [BUTTON]: 'control' };

    // input.x10-mouse (DECSET 9): ordinary-button presses only, never modifier bits; high bytes stay single octets (96 -> 0x80).
    press('x10.left.nomod.raw', 'left', 0, '\x1b[M \x80#', x10);
    press('x10.left.shift.raw', 'left', 1, '\x1b[M \x80#', x10);
    press('x10.left.ctrl.raw', 'left', 2, '\x1b[M \x80#', x10);
    press('x10.left.alt.raw', 'left', 4, '\x1b[M \x80#', x10);
    press('x10.left.meta.raw', 'left', 8, '\x1b[M \x80#', x10);
    press('x10.left.allmods.raw', 'left', 15, '\x1b[M \x80#', x10);
    press('x10.left.raw', 'left', 7, '\x1b[M \x80#', x10);
    press('x10.middle.raw', 'middle', 7, '\x1b[M!\x80#', x10);
    press('x10.right.raw', 'right', 7, '\x1b[M"\x80#', x10);
    press('x10.left.sgr', 'left', 7, '\x1b[<0;96;3M', x10, 9, true);
    // Coordinate encoding is independent of the tracking selector: resetting 1006 alone returns to legacy bytes with mode 9 still selected.
    add('mouse.x10.left.raw.afterSgrReset', '\x1b[?1006l', { column: 96, row: 3, kind: 'mousePress', button: 'left', modifiers: 7 }, '\x1b[M \x80#',
        { applicationKeypad: false, mouseTracking: 9, mouseSgr: false, kittyKeyboardFlags: 0 }, x10, false);
    mouse('x10.release.suppressed', 9, false, { kind: 'mouseRelease', button: 'left' }, '', x10);
    mouse('x10.move.suppressed', 9, false, { kind: 'mouseMove', button: 'left' }, '', x10);
    mouse('x10.hover.suppressed', 9, false, { kind: 'mouseMove', button: 'none' }, '', x10);
    mouse('x10.wheel.suppressed', 9, false, { kind: 'mouseWheel', wheelDelta: 1 }, '', x10);
    mouse('x10.wheel.down.suppressed', 9, false, { kind: 'mouseWheel', wheelDelta: -1 }, '', x10);
    // Controls: the same events under normal / button-event / any-event tracking must differ from X10.
    press('normal.press.mods7.raw.control', 'left', 7, '\x1b[M<\x80#', x10Control, 1000);
    mouse('normal.release.raw.control', 1000, false, { kind: 'mouseRelease', button: 'left' }, '\x1b[M#\x80#', x10Control);
    mouse('normal.wheel.up.raw.control', 1000, false, { kind: 'mouseWheel', wheelDelta: 1 }, '\x1b[M`\x80#', x10Control);
    mouse('normal.move.suppressed', 1000, false, { kind: 'mouseMove', button: 'left' }, '', { [X10]: 'control', [BUTTON]: 'control' });
    mouse('button.move.left.raw', 1002, false, { kind: 'mouseMove', button: 'left' }, '\x1b[M@\x80#', { [X10]: 'control', [BUTTON]: 'required' });

    // input.button-event-mouse (DECSET 1002): press, held-button motion and release; motion with no button is suppressed.
    press('button.press.left.raw', 'left', 0, '\x1b[M \x80#', button, 1002);
    press('button.press.right.raw', 'right', 0, '\x1b[M"\x80#', button, 1002);
    // A different cell than the preceding held motion: the App reports held-button motion once per cell change.
    mouse('button.move.middle.shiftalt.raw', 1002, false, { kind: 'mouseMove', button: 'middle', modifiers: 5, column: 97 }, '\x1b[MM\x81#', button);
    mouse('button.release.left.raw', 1002, false, { kind: 'mouseRelease', button: 'left' }, '\x1b[M#\x80#', button);
    mouse('button.hover.suppressed', 1002, false, { kind: 'mouseMove', button: 'none', column: 94 }, '', button);
    mouse('any.hover.raw.control', 1003, false, { kind: 'mouseMove', button: 'none', column: 94 }, '\x1b[MC~#', buttonControl);
    press('button.press.sgr', 'left', 0, '\x1b[<0;96;3M', { [BUTTON]: 'label' }, 1002, true);
    mouse('button.move.sgr', 1002, true, { kind: 'mouseMove', button: 'left' }, '\x1b[<32;96;3M', { [BUTTON]: 'label' });
    mouse('button.release.sgr', 1002, true, { kind: 'mouseRelease', button: 'left' }, '\x1b[<0;96;3m', { [BUTTON]: 'label' });
    // Every planned pointer cell lies inside the grid it was admitted for (the App refuses an out-of-grid cell after earlier events already ran).
    for (const item of cases) {
        if (item.event.column > cols || item.event.row > rows) throw Object.assign(new Error(`Routed-input event ${item.id} lies outside the observed grid.`), { code: 'inputGeometryUnavailable' });
    }
    return cases;
}
