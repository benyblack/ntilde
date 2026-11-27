// Fixed generated-event plan (v5) for the SGR pixel-coordinate mouse benchmark layer: input.pixel-mouse (?1016). v5 keeps every v4 event
// verbatim (their modes only gain the pixel profile key, held at false), so the routed-input, modifyOtherKeys and extended-mouse layers keep
// re-deriving from the same journal, and appends the pixel events. A pixel event is declared by an exact device-pixel position in the
// pane's text area (`pixelX`, `pixelY`, zero-based), never by a cell, so the expected report is a literal protocol string written from
// xterm's control-sequence documentation and button.c (EmitMousePosition: pixel offset + 1, SGR format, release keeps the button with
// final `m`); the independent oracle in pxm-contracts.mjs must agree with each literal, and neither is ever derived from the App's encoder.
// v1 (input-cases.mjs) .. v4 (mex-cases.mjs) stay frozen for their publications.
import { inputReset } from './input-cases.mjs';
import { makeMexCases, mexQueries, mexReset } from './mex-cases.mjs';

export const pxmPlanId = 'v5';
export const pxmSuiteId = 'ntilde-owned-routed-input-v5';
export const ownedPxmPolicy = 'one declared Avalonia event at a time in the proved disposable local pane; raw child stdin plus a post-action FIFO ordering reply; '
    + 'every control capture proves the negotiated modifyOtherKeys level and the DECRQM state of the mouse tracking selectors and of the UTF-8 (1005), SGR (1006), urxvt (1015) '
    + 'and SGR-pixel (1016) coordinate encodings before any dispatch, event bytes are recorded and compared with literal protocol expectations; '
    + 'modes and the original negotiation state restored before teardown';
export const pxmBenchmarkIds = Object.freeze(['input.pixel-mouse']);
const P = pxmBenchmarkIds[0];

/** v5 events clear the pixel coordinate encoding in addition to everything the earlier plans reset. */
export const pxmReset = mexReset + '\x1b[?1016l';
/** The DECRQM query v5 appends after the v4 queries; the reply follows the query order. */
export const pxmQueries = mexQueries + '\x1b[?1016$p';
/** The DECRQM query that proves the pixel selector in every control capture. */
export const pixelStateQuery = '\x1b[?1016$p';
/** Smallest grid the plan is admitted for: every planned pixel must lie inside a text area of at least 4 x 8 device pixels per cell. */
export const minimumPxmColumns = 97;
export const minimumPxmRows = 24;
const minimumCellPixels = Object.freeze({ width: 4, height: 8 });
const hex = value => Buffer.from(value, 'latin1').toString('hex');

/** The pixel positions the plan uses (device pixels, zero-based): the first pixel, two small ones, a mid-cell one, and a far and a near-limit one (the latter two still inside the smallest admitted text area). */
export const pixelPositions = Object.freeze({
    origin: Object.freeze({ pixelX: 0, pixelY: 0 }),
    small: Object.freeze({ pixelX: 3, pixelY: 11 }),
    mid: Object.freeze({ pixelX: 143, pixelY: 57 }),
    neighbour: Object.freeze({ pixelX: 144, pixelY: 57 }),
    below: Object.freeze({ pixelX: 144, pixelY: 58 }),
    far: Object.freeze({ pixelX: 361, pixelY: 171 }),
    edge: Object.freeze({ pixelX: 387, pixelY: 191 }),
});

/** The v5 mode profile (key order is part of the journal): the v4 profile plus the pixel encoding state. */
const profile = (tracking, encoding) => ({ applicationKeypad: false, mouseTracking: tracking, mouseSgr: encoding === 'sgr', kittyKeyboardFlags: 0,
    modifyOtherKeysLevel: 0, mouseUtf8: encoding === 'utf8', mouseUrxvt: encoding === 'urxvt', mousePixel: encoding === 'pixel' });
const encodingOf = { 1005: 'utf8', 1006: 'sgr', 1015: 'urxvt', 1016: 'pixel' };
const sgrPixel = (code, position, final = 'M') => `\x1b[<${code};${position.pixelX + 1};${position.pixelY + 1}${final}`;

/** The v4 events (their modes extended with the pixel state, false) plus the pixel-encoding events. */
export function makePxmCases(rows, cols, ignoredLevel) {
    const cases = makeMexCases(rows, cols, ignoredLevel).map(item => ({ ...item, modes: { ...item.modes, mousePixel: false } }));
    const add = (id, setup, event, expected, modes, roles) => cases.push({ id: 'input.pxm.' + id,
        setup: pxmReset + setup, event: { modifiers: 0, ...event }, expectedHex: hex(expected), modes, roles });
    const selects = numbers => numbers.map(number => `\x1b[?${number}h`).join('');
    const control = { [P]: 'control' }, required = { [P]: 'required' };
    // One pointer experiment: a tracking selector plus the coordinate-encoding selects the setup performs, in order. An event is declared by a
    // cell (`column`/`row`) or by exact pixels (`pixelX`/`pixelY`, the mid position unless the event names one), never both.
    const mouse = (id, tracking, encodings, event, expected, roles, modes) => add(id, `\x1b[?${tracking}h` + selects(encodings),
        event.column === undefined ? { ...pixelPositions.mid, ...event } : event, expected, modes, roles);
    const pixel = (id, event, expected, { tracking = 1000, encodings = [1016], roles = required } = {}) => mouse(id, tracking, encodings, event, expected, roles,
        profile(tracking, 'pixel'));
    const cell = { column: 96, row: 3 };
    const press = (button, extra = {}) => ({ kind: 'mousePress', button, ...extra });

    // --- the pinned probe's own state: the mode set alone proves the selector without any report ---------------------------------------------
    // A pointer event is refused while no tracking selector is active, so the selector-only case carries a key-up (which reports nothing); the
    // mode profile proved by the control capture is the observation, and the coordinate encoding alone never reports an event.
    add('alone.set', '\x1b[?1016h', { kind: 'keyUp', key: 'a' }, '', profile(0, 'pixel'), required);
    // Controls: the same cell-centre press without an extended encoding and under SGR (cell coordinates) must still behave as before.
    add('control.legacy.press', '\x1b[?1000h', { ...cell, kind: 'mousePress', button: 'left' }, '\x1b[M \x80#', profile(1000, ''), control);
    mouse('control.sgr.press', 1000, [1006], { ...cell, ...press('left') }, '\x1b[<0;96;3M', control, profile(1000, 'sgr'));

    // --- SGR-Pixels reports ----------------------------------------------------------------------------------------------------------------
    pixel('press.left.origin', { ...press('left'), ...pixelPositions.origin }, sgrPixel(0, pixelPositions.origin));
    pixel('press.left.small', { ...press('left'), ...pixelPositions.small }, sgrPixel(0, pixelPositions.small));
    pixel('press.left.mid', press('left'), sgrPixel(0, pixelPositions.mid));
    pixel('press.middle.mid', press('middle'), sgrPixel(1, pixelPositions.mid));
    pixel('press.right.mid', press('right'), sgrPixel(2, pixelPositions.mid));
    // The generated-event bit mask is shift=1, ctrl=2, alt=4, meta=8; xterm's SGR code adds shift 4, meta/alt 8 and control 16.
    pixel('press.shift', press('left', { modifiers: 1 }), sgrPixel(4, pixelPositions.mid));
    pixel('press.alt', press('left', { modifiers: 4 }), sgrPixel(8, pixelPositions.mid));
    pixel('press.ctrl', press('left', { modifiers: 2 }), sgrPixel(16, pixelPositions.mid));
    pixel('press.mods7', press('left', { modifiers: 7 }), sgrPixel(28, pixelPositions.mid));
    pixel('press.far', { ...press('left'), ...pixelPositions.far }, sgrPixel(0, pixelPositions.far));
    pixel('press.edge', { ...press('left'), ...pixelPositions.edge }, sgrPixel(0, pixelPositions.edge));
    pixel('release.left', { kind: 'mouseRelease', button: 'left' }, sgrPixel(0, pixelPositions.mid, 'm'));
    pixel('release.right', { kind: 'mouseRelease', button: 'right' }, sgrPixel(2, pixelPositions.mid, 'm'));
    pixel('wheel.up', { kind: 'mouseWheel', wheelDelta: 1 }, sgrPixel(64, pixelPositions.mid));
    pixel('wheel.down', { kind: 'mouseWheel', wheelDelta: -1 }, sgrPixel(65, pixelPositions.mid));
    // Motion is reported whenever the pixel position changes, not once per cell: xterm compares the pixel offsets it just computed. Each motion
    // experiment of the plan uses a position that differs from the preceding reported motion (the v4 plan's last one is a cell-mode move).
    pixel('motion.held', { kind: 'mouseMove', button: 'left' }, sgrPixel(32, pixelPositions.mid), { tracking: 1002 });
    pixel('motion.next-pixel', { kind: 'mouseMove', button: 'left', ...pixelPositions.neighbour }, sgrPixel(32, pixelPositions.neighbour), { tracking: 1002 });
    pixel('motion.next-row', { kind: 'mouseMove', button: 'left', ...pixelPositions.below }, sgrPixel(32, pixelPositions.below), { tracking: 1002 });
    pixel('hover.any', { kind: 'mouseMove', button: 'none', ...pixelPositions.origin }, sgrPixel(35, pixelPositions.origin), { tracking: 1003 });
    // The same pixel again under unchanged any-event tracking changes nothing and sends nothing (xterm's changed=False branch).
    pixel('hover.same-pixel', { kind: 'mouseMove', button: 'none', ...pixelPositions.origin }, '', { tracking: 1003 });
    // X10 compatibility tracking never carries modifier bits: the press with every modifier set is the plain left press in the SGR layout.
    pixel('x10.press', press('left', { modifiers: 7 }), sgrPixel(0, pixelPositions.mid), { tracking: 9 });

    // --- single-slot selection (xterm extend_coords): a later select replaces the earlier encoding; a reset of an inactive one changes nothing -
    for (const earlier of [1005, 1006, 1015]) {
        mouse(`slot.pixel.replaces.${earlier}`, 1000, [earlier, 1016], press('left'), sgrPixel(0, pixelPositions.mid), required, profile(1000, 'pixel'));
    }
    mouse('slot.sgr.replaces', 1000, [1016, 1006], { ...cell, ...press('left') }, '\x1b[<0;96;3M', required, profile(1000, 'sgr'));
    mouse('slot.urxvt.replaces', 1000, [1016, 1015], { ...cell, ...press('left') }, '\x1b[32;96;3M', required, profile(1000, 'urxvt'));
    mouse('slot.utf8.replaces', 1000, [1016, 1005], { ...cell, ...press('left') }, '\x1b[M \xc2\x80#', required, profile(1000, 'utf8'));
    add('slot.reset.inactive', '\x1b[?1000h\x1b[?1006h\x1b[?1016l', { ...cell, kind: 'mousePress', button: 'left' }, '\x1b[<0;96;3M', profile(1000, 'sgr'), required);
    add('slot.reset.active', '\x1b[?1000h\x1b[?1016h\x1b[?1016l', { ...cell, kind: 'mousePress', button: 'left' }, '\x1b[M \x80#', profile(1000, ''), required);

    // Every planned pixel lies inside the smallest text area the plan is admitted for (the App refuses an out-of-area pixel after earlier events ran).
    if (!Number.isSafeInteger(cols) || !Number.isSafeInteger(rows) || cols < minimumPxmColumns || rows < minimumPxmRows) {
        throw Object.assign(new Error(`SGR-pixel qualification requires an observed grid of at least ${minimumPxmColumns} columns and ${minimumPxmRows} rows.`),
            { code: 'inputGeometryUnavailable' });
    }
    const width = cols * minimumCellPixels.width, height = rows * minimumCellPixels.height;
    for (const item of cases) {
        if (item.event.pixelX >= width || item.event.pixelY >= height) {
            throw Object.assign(new Error(`SGR-pixel event ${item.id} lies outside the smallest admitted text area.`), { code: 'inputGeometryUnavailable' });
        }
    }
    return cases;
}

export { inputReset };
export const pixelEncodingOf = encodingOf;
