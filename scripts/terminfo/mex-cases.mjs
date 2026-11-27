// Fixed generated-event plan (v4) for the extended-mouse-encoding benchmark layer: modes.utf8-mouse-1005 (UTF-8 coordinates) and
// input.urxvt-mouse (decimal coordinates, mode 1015). v4 keeps every v3 event verbatim (their modes only gain the two new profile
// keys, held at false), so the routed-input and modifyOtherKeys layers keep re-deriving from the same journal, and appends the
// extended-encoding events. Every expectation is a literal protocol byte string written from xterm's control-sequence documentation
// and button.c; the independent oracle in mex-contracts.mjs must agree with each literal, and neither is ever derived from the App's
// encoder. v1 (input-cases.mjs), v2 (routed-input-cases.mjs) and v3 (modify-other-keys-cases.mjs) stay frozen for their publications.
import { inputReset } from './input-cases.mjs';
import { makeModifyOtherKeysCases } from './modify-other-keys-cases.mjs';

export const mexPlanId = 'v4';
export const mexSuiteId = 'ntilde-owned-routed-input-v4';
export const ownedMexPolicy = 'one declared Avalonia event at a time in the proved disposable local pane; raw child stdin plus a post-action FIFO ordering reply; '
    + 'every control capture proves the negotiated modifyOtherKeys level and the DECRQM state of the mouse tracking selectors and of the UTF-8 (1005), SGR (1006) and urxvt (1015) '
    + 'coordinate encodings before any dispatch, event bytes are recorded and compared with literal protocol expectations; modes and the original negotiation state restored before teardown';
export const mexBenchmarkIds = Object.freeze(['modes.utf8-mouse-1005', 'input.urxvt-mouse']);
const U = mexBenchmarkIds[0], R = mexBenchmarkIds[1];

/** v4 events clear the two extended coordinate encodings in addition to everything the earlier plans reset. */
export const mexReset = inputReset + '\x1b[?1005;1015l';
/** The DECRQM queries v4 appends after the shared mode queries and XTQMODKEYS; the replies follow the query order. */
export const mexQueries = '\x1b[?1005$p\x1b[?1015$p';
/** The pinned probe's own DECRQM request (its feedCapture argument). */
export const utf8PinnedQuery = '\x1b[?1005$p';
export const minimumMexColumns = 97;
const hex = value => Buffer.from(value, 'latin1').toString('hex');

/** The UTF-8 octets of one coordinate/button value under mode 1005: the code point value + 32, as a latin1 string of octets. */
export const utf8Octets = value => Buffer.from(String.fromCodePoint(32 + value), 'utf8').toString('latin1');

/** The v4 mode profile (key order is part of the journal): the v3 profile plus the two extended-encoding states. */
const profile = (tracking, encoding) => ({ applicationKeypad: false, mouseTracking: tracking, mouseSgr: encoding === 'sgr', kittyKeyboardFlags: 0,
    modifyOtherKeysLevel: 0, mouseUtf8: encoding === 'utf8', mouseUrxvt: encoding === 'urxvt' });
const both = { [U]: 'control', [R]: 'control' };
const pinnedFeeds = Object.freeze(['\x1b[?1005h', '\x1b[?1005l']);
export const utf8PinnedFeeds = pinnedFeeds;
export const utf8PinnedCases = Object.freeze({ set: 'input.mex.u.pinned.set', reset: 'input.mex.u.pinned.reset' });

/** The v3 events (their modes extended with both new states, false) plus 44 extended-encoding events. */
export function makeMexCases(rows, cols, ignoredLevel) {
    const cases = makeModifyOtherKeysCases(rows, cols, ignoredLevel).map(item => ({ ...item, modes: { ...item.modes, mouseUtf8: false, mouseUrxvt: false } }));
    const add = (id, setup, event, expected, modes, roles, reset = true) => cases.push({ id: 'input.mex.' + id,
        setup: (reset ? mexReset : '') + setup, event: { modifiers: 0, ...event }, expectedHex: hex(expected), modes, roles });
    // One mouse experiment: a tracking selector plus the coordinate-encoding selects that the setup performs, in order.
    const mouse = (id, tracking, encodings, event, expected, roles, modes) => add(id,
        `\x1b[?${tracking}h` + encodings.map(number => `\x1b[?${number}h`).join(''),
        { column: 96, row: 3, ...event }, expected, modes, roles);
    const encodingOf = { 1005: 'utf8', 1006: 'sgr', 1015: 'urxvt' };
    const press = (id, button, expected, roles, { tracking = 1000, encoding = 1005, modifiers = 0, ...cell } = {}) => mouse(id, tracking, encoding ? [encoding] : [],
        { kind: 'mousePress', button, modifiers, ...cell }, expected, roles, profile(tracking, encodingOf[encoding]));
    const motion = (id, tracking, encoding, event, expected, roles) => mouse(id, tracking, [encoding], { kind: 'mouseMove', ...event }, expected, roles,
        profile(tracking, encodingOf[encoding]));
    const u = { [U]: 'required' }, r = { [R]: 'required' };

    // --- the proved all-off baseline, then the pinned probe's own feeds, then the shared controls -----------------------------------------
    // The first case carries no tracking selector at all, so the pinned feeds that follow it start from a verified all-off state.
    add('baseline.reset', '', { kind: 'keyUp', key: 'a' }, '', profile(0, ''), both);
    // The pinned probe feeds ?1005h, asks DECRQM, then feeds ?1005l: the next two cases are exactly those feeds. The App refuses a generated
    // pointer event while no tracking selector is active, so each case carries a key-up (which reports nothing); the mode profile proved
    // by the control capture is the observation, and the coordinate encoding alone never reports an event.
    add('u.pinned.set', pinnedFeeds[0], { kind: 'keyUp', key: 'a' }, '', profile(0, 'utf8'), u, false);
    add('u.pinned.reset', pinnedFeeds[1], { kind: 'keyUp', key: 'a' }, '', profile(0, ''), u, false);
    // Shared controls: the same press without an extended encoding and under SGR.
    add('control.legacy.press', '\x1b[?1000h', { column: 96, row: 3, kind: 'mousePress', button: 'left' }, '\x1b[M \x80#', profile(1000, ''), both);
    press('control.sgr.press', 'left', '\x1b[<0;96;3M', both, { encoding: 1006 });

    // --- modes.utf8-mouse-1005 -------------------------------------------------------------------------------------------------------------
    press('u.press.left', 'left', '\x1b[M \xc2\x80#', u);
    press('u.press.middle', 'middle', '\x1b[M!\xc2\x80#', u);
    press('u.press.right', 'right', '\x1b[M"\xc2\x80#', u);
    press('u.press.col95', 'left', '\x1b[M \x7f#', u, { column: 95 });
    press('u.press.col97', 'left', '\x1b[M \xc2\x81#', u, { column: 97 });
    press('u.press.col1row1', 'left', '\x1b[M !!', u, { column: 1, row: 1 });
    press('u.press.mods7', 'left', '\x1b[M<\xc2\x80#', u, { modifiers: 7 });
    mouse('u.release.left', 1000, [1005], { kind: 'mouseRelease', button: 'left' }, '\x1b[M#\xc2\x80#', u, profile(1000, 'utf8'));
    mouse('u.wheel.up', 1000, [1005], { kind: 'mouseWheel', wheelDelta: 1 }, '\x1b[M`\xc2\x80#', u, profile(1000, 'utf8'));
    mouse('u.wheel.down', 1000, [1005], { kind: 'mouseWheel', wheelDelta: -1 }, '\x1b[Ma\xc2\x80#', u, profile(1000, 'utf8'));
    // Held-button and any-event motion are reported once per cell change: every motion experiment of the plan uses a cell that differs
    // from the preceding reported motion (the routed plan's last one is column 96 row 3).
    motion('u.motion.left', 1002, 1005, { button: 'left', column: 97, row: 4 }, '\x1b[M@\xc2\x81$', u);
    motion('u.hover.any', 1003, 1005, { button: 'none', column: 96, row: 4 }, '\x1b[MC\xc2\x80$', u);
    // X10 tracking never carries modifier bits: the press with every modifier set is the plain left press, coordinates UTF-8 encoded.
    press('u.x10.press', 'left', '\x1b[M \xc2\x80#', u, { tracking: 9, modifiers: 7 });
    // Single-slot selection (xterm extend_coords): a later select replaces the earlier encoding; a reset of an inactive one changes nothing.
    mouse('u.slot.sgr.replaces', 1000, [1005, 1006], { kind: 'mousePress', button: 'left' }, '\x1b[<0;96;3M', u, profile(1000, 'sgr'));
    mouse('u.slot.utf8.replaces', 1000, [1006, 1005], { kind: 'mousePress', button: 'left' }, '\x1b[M \xc2\x80#', u, profile(1000, 'utf8'));
    mouse('u.slot.urxvt.replaced', 1000, [1015, 1005], { kind: 'mousePress', button: 'left' }, '\x1b[M \xc2\x80#', u, profile(1000, 'utf8'));
    add('u.slot.reset.inactive', '\x1b[?1000h\x1b[?1006h\x1b[?1005l', { column: 96, row: 3, kind: 'mousePress', button: 'left' }, '\x1b[<0;96;3M',
        profile(1000, 'sgr'), u);
    add('u.slot.reset.active', '\x1b[?1000h\x1b[?1005h\x1b[?1005l', { column: 96, row: 3, kind: 'mousePress', button: 'left' }, '\x1b[M \x80#',
        profile(1000, ''), u);
    // The rightmost grid column: the one grid-dependent experiment, so its literal is computed from the code-point rule (not graded).
    press('u.press.rightmost', 'left', '\x1b[M ' + utf8Octets(cols) + '#', { [U]: 'label' }, { column: cols });

    // --- input.urxvt-mouse -----------------------------------------------------------------------------------------------------------------
    const urxvt = { encoding: 1015 };
    add('r.alone.set', '\x1b[?1015h', { kind: 'keyUp', key: 'a' }, '', profile(0, 'urxvt'), r);
    press('r.press.left', 'left', '\x1b[32;96;3M', r, urxvt);
    press('r.press.middle', 'middle', '\x1b[33;96;3M', r, urxvt);
    press('r.press.right', 'right', '\x1b[34;96;3M', r, urxvt);
    press('r.press.col1row1', 'left', '\x1b[32;1;1M', r, { ...urxvt, column: 1, row: 1 });
    press('r.press.mods7', 'left', '\x1b[60;96;3M', r, { ...urxvt, modifiers: 7 });
    press('r.press.shift', 'left', '\x1b[36;96;3M', r, { ...urxvt, modifiers: 1 });
    press('r.press.alt', 'left', '\x1b[40;96;3M', r, { ...urxvt, modifiers: 4 });
    press('r.press.ctrl', 'left', '\x1b[48;96;3M', r, { ...urxvt, modifiers: 2 });
    mouse('r.release.left', 1000, [1015], { kind: 'mouseRelease', button: 'left' }, '\x1b[35;96;3M', r, profile(1000, 'urxvt'));
    mouse('r.wheel.up', 1000, [1015], { kind: 'mouseWheel', wheelDelta: 1 }, '\x1b[96;96;3M', r, profile(1000, 'urxvt'));
    mouse('r.wheel.down', 1000, [1015], { kind: 'mouseWheel', wheelDelta: -1 }, '\x1b[97;96;3M', r, profile(1000, 'urxvt'));
    motion('r.motion.left', 1002, 1015, { button: 'left', column: 97, row: 4 }, '\x1b[64;97;4M', r);
    motion('r.hover.any', 1003, 1015, { button: 'none', column: 96, row: 4 }, '\x1b[67;96;4M', r);
    press('r.x10.press', 'left', '\x1b[32;96;3M', r, { ...urxvt, tracking: 9 });
    press('r.x10.press.mods7', 'left', '\x1b[32;96;3M', r, { ...urxvt, tracking: 9, modifiers: 7 });
    mouse('r.slot.urxvt.replaces', 1000, [1006, 1015], { kind: 'mousePress', button: 'left' }, '\x1b[32;96;3M', r, profile(1000, 'urxvt'));
    mouse('r.slot.sgr.replaces', 1000, [1015, 1006], { kind: 'mousePress', button: 'left' }, '\x1b[<0;96;3M', r, profile(1000, 'sgr'));
    mouse('r.slot.utf8.replaced', 1000, [1005, 1015], { kind: 'mousePress', button: 'left' }, '\x1b[32;96;3M', r, profile(1000, 'urxvt'));
    add('r.slot.reset.inactive', '\x1b[?1000h\x1b[?1006h\x1b[?1015l', { column: 96, row: 3, kind: 'mousePress', button: 'left' }, '\x1b[<0;96;3M',
        profile(1000, 'sgr'), r);
    add('r.slot.reset.active', '\x1b[?1000h\x1b[?1015h\x1b[?1015l', { column: 96, row: 3, kind: 'mousePress', button: 'left' }, '\x1b[M \x80#',
        profile(1000, ''), r);
    press('r.press.rightmost', 'left', `\x1b[32;${cols};3M`, { [R]: 'label' }, { ...urxvt, column: cols });

    // Every planned pointer cell lies inside the grid it was admitted for (the App refuses an out-of-grid cell after earlier events already ran).
    if (!Number.isSafeInteger(cols) || cols < minimumMexColumns) {
        throw Object.assign(new Error(`Extended-mouse qualification requires an observed grid with at least ${minimumMexColumns} columns.`), { code: 'inputGeometryUnavailable' });
    }
    for (const item of cases) {
        if (item.event.column > cols || item.event.row > rows) throw Object.assign(new Error(`Extended-mouse event ${item.id} lies outside the observed grid.`), { code: 'inputGeometryUnavailable' });
    }
    return cases;
}
