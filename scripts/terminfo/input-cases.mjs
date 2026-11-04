// Independent protocol expectations; never derived from App encoder responses.
export const inputSuiteId = 'ntilde-owned-routed-input-v1';
export const ownedInputPolicy = 'one declared Avalonia event at a time in the proved disposable local pane; raw child stdin plus a post-action FIFO ordering reply; modes restored before teardown';
const hex = value => Buffer.from(value, 'latin1').toString('hex');
export const inputReset = '\x1b>\x1b[=0u\x1b[>4;0m\x1b[?1;9;1000;1002;1003;1006l';

export function makeInputCases(rows, cols) {
    if (!Number.isSafeInteger(rows) || rows < 6 || rows > 128
        || !Number.isSafeInteger(cols) || cols < 96 || cols > 256 || rows * cols > 8192) {
        throw Object.assign(new Error('Input qualification requires an observed grid with at least96 columns.'), { code: 'inputGeometryUnavailable' });
    }
    const cases = [];
    const add = (id, setup, event, expected, modes) => cases.push({ id: 'input.' + id,
        setup: inputReset + setup, event: { modifiers: 0, ...event }, expectedHex: hex(expected), modes });
    const keypad = applicationKeypad => ({ applicationKeypad, mouseTracking: 0, mouseSgr: false, kittyKeyboardFlags: 0 });
    add('keypad.normal.keyDown', '', { kind: 'keyDown', key: '7' }, '', keypad(false));
    add('keypad.normal.text', '', { kind: 'text', text: '7' }, '7', keypad(false));
    add('keypad.normal.keyUp', '', { kind: 'keyUp', key: '7' }, '', keypad(false));
    // VT100/VT220 unmodified application keypad, not OS NumLock remapping.
    for (const [key, final] of [
        ['0', 'p'], ['1', 'q'], ['2', 'r'], ['3', 's'], ['4', 't'],
        ['5', 'u'], ['6', 'v'], ['7', 'w'], ['8', 'x'], ['9', 'y'],
        ['decimal', 'n'], ['separator', 'l'], ['add', 'k'], ['subtract', 'm'],
        ['multiply', 'j'], ['divide', 'o'], ['enter', 'M'],
    ]) add('keypad.application.' + key, '\x1b=', { kind: 'keyDown', key }, '\x1bO' + final, keypad(true));
    add('keypad.application.keyUp', '\x1b=', { kind: 'keyUp', key: 'enter' }, '', keypad(true));

    const mouse = (id, tracking, sgr, event, expected) => add('mouse.' + id,
        `\x1b[?${tracking}h` + (sgr ? '\x1b[?1006h' : ''),
        { column: 96, row: 3, ...event }, expected,
        { applicationKeypad: false, mouseTracking: tracking, mouseSgr: sgr, kittyKeyboardFlags: 0 });
    mouse('x10.left.raw', 9, false, { kind: 'mousePress', button: 'left', modifiers: 7 }, '\x1b[M \x80#');
    mouse('x10.middle.raw', 9, false, { kind: 'mousePress', button: 'middle', modifiers: 7 }, '\x1b[M!\x80#');
    mouse('x10.right.raw', 9, false, { kind: 'mousePress', button: 'right', modifiers: 7 }, '\x1b[M"\x80#');
    mouse('x10.left.sgr', 9, true, { kind: 'mousePress', button: 'left', modifiers: 7 }, '\x1b[<0;96;3M');
    mouse('x10.release.suppressed', 9, false, { kind: 'mouseRelease', button: 'left' }, '');
    mouse('x10.move.suppressed', 9, false, { kind: 'mouseMove', button: 'left' }, '');
    mouse('x10.wheel.suppressed', 9, false, { kind: 'mouseWheel', wheelDelta: 1 }, '');
    mouse('normal.press.raw.control', 1000, false, { kind: 'mousePress', button: 'left', modifiers: 2 }, '\x1b[M0\x80#');
    mouse('normal.release.raw.control', 1000, false, { kind: 'mouseRelease', button: 'left', modifiers: 2 }, '\x1b[M3\x80#');
    mouse('normal.move.suppressed', 1000, false, { kind: 'mouseMove', button: 'left' }, '');
    mouse('normal.press.sgr.control', 1000, true, { kind: 'mousePress', button: 'right', modifiers: 2 }, '\x1b[<18;96;3M');
    mouse('normal.release.sgr.control', 1000, true, { kind: 'mouseRelease', button: 'right', modifiers: 2 }, '\x1b[<18;96;3m');
    mouse('normal.wheel.up.sgr.control', 1000, true, { kind: 'mouseWheel', wheelDelta: 1, modifiers: 2 }, '\x1b[<80;96;3M');
    mouse('normal.wheel.down.sgr.control', 1000, true, { kind: 'mouseWheel', wheelDelta: -1, modifiers: 2 }, '\x1b[<81;96;3M');
    mouse('button.move.raw.control', 1002, false, { kind: 'mouseMove', button: 'left', modifiers: 2, column: 95 }, '\x1b[MP\x7f#');
    mouse('button.hover.suppressed', 1002, false, { kind: 'mouseMove', button: 'none', column: 94 }, '');
    mouse('any.hover.sgr.shift', 1003, true, { kind: 'mouseMove', button: 'none', modifiers: 1 }, '\x1b[<39;96;3M');
    // Keep the same mode and location: this explicitly observes motion coalescing.
    const repeated = { ...cases.at(-1), event: { ...cases.at(-1).event }, id: 'input.mouse.any.hover.coalesced', setup: '', expectedHex: '' };
    cases.push(repeated);
    return cases;
}
