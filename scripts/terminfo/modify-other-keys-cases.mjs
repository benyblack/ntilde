// Fixed generated-event plan (v3) for the routed-input benchmark layer: the routed-input benchmarks of v2 (kept verbatim, so their
// layer keeps re-deriving from the same journal) plus the modifyOtherKeys qualification (levels 1 and 2, subcases of the single pinned
// benchmark id input.modify-other-keys). Every expectation is a literal protocol byte string; the independent oracle in
// modify-other-keys-contracts.mjs must agree with each literal, and neither is ever derived from the App's encoder.
// v1 (input-cases.mjs) and v2 (routed-input-cases.mjs) stay frozen for their historical publications.
import { inputReset } from './input-cases.mjs';
import { makeRoutedInputCases } from './routed-input-cases.mjs';

export const modifyOtherKeysPlanId = 'v3';
export const routedInputV3SuiteId = 'ntilde-owned-routed-input-v3';
export const ownedModifyOtherKeysPolicy = 'one declared Avalonia event at a time in the proved disposable local pane; raw child stdin plus a post-action FIFO ordering reply; '
    + 'every control capture proves the negotiated modifyOtherKeys level through the child XTQMODKEYS reply (and the neutral kitty and DECRQM modes) before any dispatch, '
    + 'event bytes are recorded and compared with literal protocol expectations; modes and the original negotiation state restored before teardown';
export const modifyOtherKeysBenchmarkIds = Object.freeze(['input.modify-other-keys']);
const MOK = modifyOtherKeysBenchmarkIds[0];

/** The XTQMODKEYS query every v3 control appends after the shared mode queries. */
export const xtqmodkeysQuery = '\x1b[?4m';
/** XTGETTCAP request for the modifyOtherKeys capability name, hex-encoded (the pinned probe's response embeds the declaration). */
export const modifyOtherKeysXtgettcapQuery = '\x1bP+q6d6f646966794f746865724b657973\x1b\\';
/** xterm's bare refusal for a name the terminal does not advertise. */
export const xtgettcapNotAdvertised = '\x1bP0+r\x1b\\';

export const minimumRoutedInputColumns = 97;
const hex = value => Buffer.from(value, 'latin1').toString('hex');

/** One modifyOtherKeys level (0 disabled, 1, 2) plus the neutral competing protocols the plan always proves. */
const mok = (level, kittyKeyboardFlags = 0) => ({ applicationKeypad: false, mouseTracking: 0, mouseSgr: false, kittyKeyboardFlags, modifyOtherKeysLevel: level });
const required = { [MOK]: 'required' }, control = { [MOK]: 'control' }, label = { [MOK]: 'label' };

/** The 60 v2 events (modes extended with the negotiated level, which inputReset holds at 0) plus 57 modifyOtherKeys events. */
export function makeModifyOtherKeysCases(rows, cols) {
    const routed = makeRoutedInputCases(rows, cols);
    const cases = routed.map(item => ({ ...item, modes: { ...item.modes, modifyOtherKeysLevel: 0 } }));
    const add = (id, setup, event, expected, modes, roles, reset = true) => cases.push({ id: 'input.mok.' + id,
        setup: (reset ? inputReset : '') + setup, event: { modifiers: 0, ...event }, expectedHex: hex(expected), modes, roles });

    // --- The pinned probe's declaration input: the child's own XTGETTCAP answer for the modifyOtherKeys capability name ------------------
    // The refusal reply rides ahead of the mode replies, so the control capture proves it and the mode state in one ordered read.
    cases.push({ id: 'input.mok.declaration.xtgettcap', setup: inputReset + modifyOtherKeysXtgettcapQuery,
        event: { kind: 'keyUp', key: 'a', modifiers: 0 }, expectedHex: '', modes: mok(0), roles: label,
        controlPrefixHex: hex(xtgettcapNotAdvertised) });

    // --- Disabled-mode legacy controls (level 0; the control captures prove XTQMODKEYS >4;0m) ---------------------------------------------
    add('disabled.ctrl.a', '', { kind: 'keyDown', key: 'a', modifiers: 2 }, '\x01', mok(0), control);
    add('disabled.alt.a', '', { kind: 'keyDown', key: 'a', modifiers: 4 }, '\x1ba', mok(0), control);
    add('disabled.enter.shift', '', { kind: 'keyDown', key: 'return', modifiers: 1 }, '\r', mok(0), control);
    add('disabled.tab.shift', '', { kind: 'keyDown', key: 'tab', modifiers: 1 }, '\x1b[Z', mok(0), control);
    add('disabled.backspace.shift', '', { kind: 'keyDown', key: 'backspace', modifiers: 1 }, '\x7f', mok(0), control);
    add('disabled.escape.ctrl', '', { kind: 'keyDown', key: 'escape', modifiers: 2 }, '\x1b', mok(0), control);

    // --- Level 1: the X11 control aliases and Alt keep their legacy bytes; chords without well-known legacy behavior encode ----------------
    // Every case re-selects its own level: the child shell's prompt signal resets the negotiated level (documented transient-reset
    // policy), so no case may assume a level that a previous case set.
    add('l1.ctrl.a.alias', '\x1b[>4;1m', { kind: 'keyDown', key: 'a', modifiers: 2 }, '\x01', mok(1), required);
    add('l1.ctrl.leftbracket.alias', '\x1b[>4;1m', { kind: 'keyDown', key: 'leftBracket', modifiers: 2 }, '\x1b', mok(1), required);
    add('l1.ctrl.digit2.alias', '\x1b[>4;1m', { kind: 'keyDown', key: 'digit2', modifiers: 2 }, '\x00', mok(1), required);
    add('l1.ctrl.slash.alias', '\x1b[>4;1m', { kind: 'keyDown', key: 'slash', modifiers: 2 }, '\x1f', mok(1), required);
    add('l1.ctrlshift.digit2.alias', '\x1b[>4;1m', { kind: 'keyDown', key: 'digit2', modifiers: 3 }, '\x00', mok(1), required);
    add('l1.ctrl.digit1', '\x1b[>4;1m', { kind: 'keyDown', key: 'digit1', modifiers: 2 }, '\x1b[27;5;49~', mok(1), required);
    add('l1.ctrlshift.digit1', '\x1b[>4;1m', { kind: 'keyDown', key: 'digit1', modifiers: 3 }, '\x1b[27;6;33~', mok(1), required);
    add('l1.ctrl.semicolon', '\x1b[>4;1m', { kind: 'keyDown', key: 'semicolon', modifiers: 2 }, '\x1b[27;5;59~', mok(1), required);
    add('l1.alt.a.legacy', '\x1b[>4;1m', { kind: 'keyDown', key: 'a', modifiers: 4 }, '\x1ba', mok(1), required);
    add('l1.enter.shift', '\x1b[>4;1m', { kind: 'keyDown', key: 'return', modifiers: 1 }, '\x1b[27;2;13~', mok(1), required);
    add('l1.enter.ctrl', '\x1b[>4;1m', { kind: 'keyDown', key: 'return', modifiers: 2 }, '\x1b[27;5;13~', mok(1), required);
    add('l1.tab.ctrl', '\x1b[>4;1m', { kind: 'keyDown', key: 'tab', modifiers: 2 }, '\x1b[27;5;9~', mok(1), required);
    add('l1.tab.shift.backtab', '\x1b[>4;1m', { kind: 'keyDown', key: 'tab', modifiers: 1 }, '\x1b[Z', mok(1), required);
    add('l1.escape.shift.legacy', '\x1b[>4;1m', { kind: 'keyDown', key: 'escape', modifiers: 1 }, '\x1b', mok(1), required);
    add('l1.backspace.shift.legacy', '\x1b[>4;1m', { kind: 'keyDown', key: 'backspace', modifiers: 1 }, '\x7f', mok(1), required);

    // --- Transitions between disabled, level 1 and level 2, including the reset and disable forms and the ignored level 3 ------------------
    // Each transition runs inside one case from the reset baseline: the setup performs the sequence, the control capture proves the
    // resulting level through the child's XTQMODKEYS reply, and only then is the event dispatched.
    add('trans.set2.from1', '\x1b[>4;1m\x1b[>4;2m', { kind: 'keyDown', key: 'a', modifiers: 2 }, '\x1b[27;5;97~', mok(2), required);
    add('trans.reset.form', '\x1b[>4;2m\x1b[>4m', { kind: 'keyDown', key: 'a', modifiers: 2 }, '\x01', mok(0), required);
    add('trans.set1.from0', '\x1b[>4;1m', { kind: 'keyDown', key: 'digit1', modifiers: 2 }, '\x1b[27;5;49~', mok(1), required);
    add('trans.disable.form', '\x1b[>4;2m\x1b[>4n', { kind: 'keyDown', key: 'a', modifiers: 2 }, '\x01', mok(0), required);
    add('trans.l3.ignored', '\x1b[>4;2m\x1b[>4;3m', { kind: 'keyDown', key: 'a', modifiers: 2 }, '\x1b[27;5;97~', mok(2), required);

    // --- Level 2: representative Ctrl, Ctrl+Shift and Alt chords of ordinary keys and the Enter/Tab/Backspace/Escape table ------------------
    add('l2.ctrl.a', '\x1b[>4;2m', { kind: 'keyDown', key: 'a', modifiers: 2 }, '\x1b[27;5;97~', mok(2), required);
    add('l2.ctrlshift.a', '\x1b[>4;2m', { kind: 'keyDown', key: 'a', modifiers: 3 }, '\x1b[27;6;65~', mok(2), required);
    add('l2.alt.a', '\x1b[>4;2m', { kind: 'keyDown', key: 'a', modifiers: 4 }, '\x1b[27;3;97~', mok(2), required);
    add('l2.altshift.a', '\x1b[>4;2m', { kind: 'keyDown', key: 'a', modifiers: 5 }, '\x1b[27;4;65~', mok(2), required);
    add('l2.ctrl.i', '\x1b[>4;2m', { kind: 'keyDown', key: 'i', modifiers: 2 }, '\x1b[27;5;105~', mok(2), required);
    add('l2.ctrl.digit2.alias', '\x1b[>4;2m', { kind: 'keyDown', key: 'digit2', modifiers: 2 }, '\x1b[27;5;50~', mok(2), required);
    add('l2.ctrl.leftbracket.alias', '\x1b[>4;2m', { kind: 'keyDown', key: 'leftBracket', modifiers: 2 }, '\x1b[27;5;91~', mok(2), required);
    add('l2.ctrlshift.digit1', '\x1b[>4;2m', { kind: 'keyDown', key: 'digit1', modifiers: 3 }, '\x1b[27;6;33~', mok(2), required);
    add('l2.alt.digit1', '\x1b[>4;2m', { kind: 'keyDown', key: 'digit1', modifiers: 4 }, '\x1b[27;3;49~', mok(2), required);
    add('l2.ctrl.semicolon', '\x1b[>4;2m', { kind: 'keyDown', key: 'semicolon', modifiers: 2 }, '\x1b[27;5;59~', mok(2), required);
    add('l2.ctrlshift.slash', '\x1b[>4;2m', { kind: 'keyDown', key: 'slash', modifiers: 3 }, '\x1b[27;6;63~', mok(2), required);
    add('l2.enter.shift', '\x1b[>4;2m', { kind: 'keyDown', key: 'return', modifiers: 1 }, '\x1b[27;2;13~', mok(2), required);
    add('l2.enter.alt', '\x1b[>4;2m', { kind: 'keyDown', key: 'return', modifiers: 4 }, '\x1b[27;3;13~', mok(2), required);
    add('l2.tab.ctrl', '\x1b[>4;2m', { kind: 'keyDown', key: 'tab', modifiers: 2 }, '\x1b[27;5;9~', mok(2), required);
    add('l2.tab.alt', '\x1b[>4;2m', { kind: 'keyDown', key: 'tab', modifiers: 4 }, '\x1b[27;3;9~', mok(2), required);
    add('l2.tab.shift.backtab', '\x1b[>4;2m', { kind: 'keyDown', key: 'tab', modifiers: 1 }, '\x1b[Z', mok(2), required);
    add('l2.escape.ctrl', '\x1b[>4;2m', { kind: 'keyDown', key: 'escape', modifiers: 2 }, '\x1b[27;5;27~', mok(2), required);
    add('l2.escape.alt', '\x1b[>4;2m', { kind: 'keyDown', key: 'escape', modifiers: 4 }, '\x1b[27;3;27~', mok(2), required);
    add('l2.backspace.shift', '\x1b[>4;2m', { kind: 'keyDown', key: 'backspace', modifiers: 1 }, '\x1b[27;2;127~', mok(2), required);
    add('l2.backspace.alt', '\x1b[>4;2m', { kind: 'keyDown', key: 'backspace', modifiers: 4 }, '\x1b[27;3;127~', mok(2), required);
    add('l2.backspace.ctrl.legacy', '\x1b[>4;2m', { kind: 'keyDown', key: 'backspace', modifiers: 2 }, '\x7f', mok(2), required);
    add('l2.backspace.ctrlshift.bs', '\x1b[>4;2m', { kind: 'keyDown', key: 'backspace', modifiers: 3 }, '\x1b[27;6;8~', mok(2), required);

    // --- Paired key-down/backend-text handling, the text policy controls and the labeled policy exclusions ---------------------------------
    // Shift+Space is handled at level 2: the frame goes once and the backend's own text for the same key is suppressed (textHandled).
    add('l2.space.shift.withText', '\x1b[>4;2m', { kind: 'keyDown', key: 'space', modifiers: 1, text: ' ' }, '\x1b[27;2;32~', mok(2), required);
    // A Shift-only printable stays on TextInput by documented policy (not xterm's level-2 byte): the key-down is unhandled and the
    // paired backend text is the one delivery - the positive control that proves the fixture delivers text.
    add('l2.shift.a.withText', '\x1b[>4;2m', { kind: 'keyDown', key: 'a', modifiers: 1, text: 'A' }, 'A', mok(2), required);
    add('l2.normal.a.withText', '\x1b[>4;2m', { kind: 'keyDown', key: 'a', text: 'a' }, 'a', mok(2), control);
    // A standalone text commit after the paired events is ordinary text: no suppression leaks across dispatches.
    add('l2.text.recovery', '\x1b[>4;2m', { kind: 'text', text: '7' }, '7', mok(2), control);
    // Deliberate policy exclusions, labeled: Meta chords stay with the UI and Ctrl+Alt is AltGr - neither encodes, neither invents bytes.
    add('l2.meta.a.unhandled', '\x1b[>4;2m', { kind: 'keyDown', key: 'a', modifiers: 8 }, '', mok(2), label);
    add('l2.ctrlalt.a.unhandled', '\x1b[>4;2m', { kind: 'keyDown', key: 'a', modifiers: 6 }, '', mok(2), label);
    // Key-up sends nothing at every level.
    add('l2.keyup.ctrl.a', '\x1b[>4;2m', { kind: 'keyUp', key: 'a', modifiers: 2 }, '', mok(2), required);

    // --- Bounded Kitty-precedence control: an active kitty stack wins over modifyOtherKeys, and popping it restores the level ---------------
    add('l2.kitty.wins.control', '\x1b[>4;2m\x1b[>1u', { kind: 'keyDown', key: 'a', modifiers: 2 }, '\x1b[97;5u', mok(2, 1), control);
    add('l2.kitty.pop.restores', '\x1b[>4;2m\x1b[>1u\x1b[<u', { kind: 'keyDown', key: 'a', modifiers: 2 }, '\x1b[27;5;97~', mok(2, 0), required);

    // Every mok event is keyboard-only; the routed events above already validated their own pointer cells against the grid.
    for (const item of cases) {
        if (item.event.kind === 'keyDown' && item.event.text !== undefined && item.event.key === undefined) {
            throw Object.assign(new Error(`Paired text event ${item.id} must name its key.`), { code: 'inputPlanInvalid' });
        }
    }
    return cases;
}