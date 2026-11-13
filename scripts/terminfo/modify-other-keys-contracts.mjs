// Independent contracts for the modifyOtherKeys benchmark layer (input.modify-other-keys, levels 1 and 2).
// The oracle below is written from xterm's control-sequence documentation (XTMODKEYS/XTQMODKEYS and the
// modifyOtherKeys resource in xterm's input tables) and from the documented Ntilde policy deviations; it never
// calls the App's encoder. Each plan case carries a literal expectation that must equal the oracle, and the
// child's raw bytes must equal the literal. Levels 1 and 2 are subcases of the single benchmark id;
// input.modify-other-keys-3 (level 3) is out of scope and is never claimed.
import { makeModifyOtherKeysCases, modifyOtherKeysBenchmarkIds, xtgettcapNotAdvertised } from './modify-other-keys-cases.mjs';

export const modifyOtherKeysGradedSuiteId = 'ntilde-owned-modify-other-keys-benchmark-graded-v1';
export const modifyOtherKeysContractsVersion = 1;
export const modifyOtherKeysCaseIds = Object.freeze([...modifyOtherKeysBenchmarkIds]);
export const modifyOtherKeysEffectContracts = Object.freeze({
    'input.modify-other-keys': 'ntilde-modify-other-keys-effect-v1',
});

const hex = value => Buffer.from(value, 'latin1').toString('hex');

/** US-layout characters of every declared ordinary key: [unshifted, shifted]; null for keys that compose no text. */
const ordinaryChars = Object.freeze({
    a: ['a', 'A'], b: ['b', 'B'], c: ['c', 'C'], d: ['d', 'D'], e: ['e', 'E'], f: ['f', 'F'], g: ['g', 'G'],
    h: ['h', 'H'], i: ['i', 'I'], j: ['j', 'J'], k: ['k', 'K'], l: ['l', 'L'], m: ['m', 'M'], n: ['n', 'N'],
    o: ['o', 'O'], p: ['p', 'P'], q: ['q', 'Q'], r: ['r', 'R'], s: ['s', 'S'], t: ['t', 'T'], u: ['u', 'U'],
    v: ['v', 'V'], w: ['w', 'W'], x: ['x', 'X'], y: ['y', 'Y'], z: ['z', 'Z'],
    digit0: ['0', ')'], digit1: ['1', '!'], digit2: ['2', '@'], digit3: ['3', '#'], digit4: ['4', '$'],
    digit5: ['5', '%'], digit6: ['6', '^'], digit7: ['7', '&'], digit8: ['8', '*'], digit9: ['9', '('],
    grave: ['`', '~'], minus: ['-', '_'], equal: ['=', '+'], leftBracket: ['[', '{'], rightBracket: [']', '}'],
    backslash: ['\\', '|'], semicolon: [';', ':'], apostrophe: ["'", '"'], comma: [',', '<'], period: ['.', '>'],
    slash: ['/', '?'], space: [' ', ' '], return: null, tab: null, backspace: null, escape: null,
});
const specialCode = Object.freeze({ return: 13, tab: 9, backspace: 127, escape: 27 });

/** Xlib's XLookupString Control translation: the C0 byte a legacy Ctrl chord sends, or null when the chord has none. */
function legacyControlByte(key, shift) {
    const chars = ordinaryChars[key];
    if (chars === undefined || chars === null) return null;
    const code = (shift ? chars[1] : chars[0]).charCodeAt(0);
    if (key === 'space') return 0;
    if (code >= 'a'.charCodeAt(0) && code <= 'z'.charCodeAt(0)) return code & 0x1f;
    if (code >= 'A'.charCodeAt(0) && code <= 'Z'.charCodeAt(0)) return code & 0x1f;
    if (code >= 0x40 && code < 0x7f) return code & 0x1f; // @[\]^_ and the shifted punctuation row
    if (key === 'slash') return 0x1f;
    if (key === 'digit2' && !shift) return 0x00;
    if (key === 'digit3' && !shift) return 0x1b;
    if (key === 'digit4' && !shift) return 0x1c;
    if (key === 'digit5' && !shift) return 0x1d;
    if (key === 'digit6' && !shift) return 0x1e;
    if (key === 'digit7' && !shift) return 0x1f;
    if (key === 'digit8' && !shift) return 0x7f;
    return null;
}

/** xterm's metaSendsEscape legacy form: ESC prefixed printable, or null when the chord has no legacy encoding. */
function legacyAltBytes(key, shift) {
    const chars = ordinaryChars[key];
    if (chars === undefined) return null;
    if (key === 'backspace') return '\x1b\x7f';
    if (key === 'return') return '\x1b\r';
    if (chars === null) return null;
    if (key === 'period' && !shift) return '\x1b.';
    if (key.startsWith('digit') && shift) return null;
    return '\x1b' + (shift ? chars[1] : chars[0]);
}

/** Legacy recovery keys without protocols: Enter CR, Tab/Backtab, DEL, ESC; printable keys deliver through TextInput instead. */
function legacyPlainBytes(key, shift) {
    if (key === 'return') return '\r';
    if (key === 'tab') return shift ? '\x1b[Z' : '\t';
    if (key === 'backspace') return '\x7f';
    if (key === 'escape') return '\x1b';
    return '';
}

const formatOtherKey = (code, m) => `\x1b[27;${m};${code}~`;

/**
 * The documented outcome of one declared ordinary key event at a modifyOtherKeys level, in bytes.
 * Deliberate Ntilde policy deviations are implemented exactly as documented: Shift-only printables stay on
 * TextInput, Ctrl+Alt (AltGr) and Meta never encode and carry no bytes of their own, keypad/cursor/editing
 * keys are other-key-exempt (out of the declared vocabulary here), and only the CSI 27 ; m ; code ~ form exists.
 * An active kitty disambiguate stack (flag 1) wins over the level, exactly as the routing contract documents.
 */
export function modifyOtherKeysReport({ level = 0, kind, key, modifiers = 0, kittyKeyboardFlags = 0, text, reserved = false }) {
    if (kind === 'keyUp') return '';
    if (kind === 'text') return reserved ? '' : (text ?? '');
    const chars = ordinaryChars[key];
    if (chars === undefined) return '';
    const shift = (modifiers & 1) !== 0, ctrl = (modifiers & 2) !== 0, alt = (modifiers & 4) !== 0, meta = (modifiers & 8) !== 0;
    // Policy exclusions first: Meta/Super stays with the UI; Ctrl+Alt is how AltGr arrives and composes text.
    if (meta || (ctrl && alt)) return '';
    // xterm's modifier parameter weights differ from the declared event bits: shift=1, alt=2, ctrl=4.
    const m = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);
    if (level > 0 && (kittyKeyboardFlags & 1) !== 0) {
        // The kitty stack replaces the encodings below for the chords it claims; Ctrl+letter is CSI base;5u.
        if (ctrl && !shift && !alt && chars !== null && chars !== undefined) return `\x1b[${chars[0].charCodeAt(0)};${m}u`;
        return '';
    }
    if (level === 0) {
        // Recovery keys keep their bytes whatever the modifiers; then the Xlib Control table, then Alt-sends-ESC.
        if (chars === null) return legacyPlainBytes(key, shift);
        if (ctrl) { const byte = legacyControlByte(key, shift); return byte === null ? '' : String.fromCharCode(byte); }
        if (alt) { const bytes = legacyAltBytes(key, shift); return bytes ?? ''; }
        return legacyPlainBytes(key, shift);
    }
    const code = chars === null ? specialCode[key] : (shift ? chars[1] : chars[0]).charCodeAt(0);
    if (level === 1) {
        // Alt never participates at level 1; the X11 control aliases keep their C0 bytes; Enter and Ctrl+Tab encode.
        if (alt) return legacyAltBytes(key, shift) ?? legacyPlainBytes(key, shift);
        if (key === 'return') return formatOtherKey(13, m);
        if (key === 'tab') return ctrl && !shift ? formatOtherKey(9, m) : legacyPlainBytes('tab', shift);
        if (key === 'backspace' || key === 'escape') return legacyPlainBytes(key, shift);
        if (ctrl) {
            const alias = legacyControlByte(key, shift);
            return alias === null ? formatOtherKey(code, m) : String.fromCharCode(alias);
        }
        return legacyPlainBytes(key, shift);
    }
    // Level 2: every chord with Ctrl and/or Alt encodes, including the X11 aliases; Shift-only printables stay on
    // TextInput (their shifted character is its own text); Shift+Tab stays Backtab; plain Ctrl+Backspace stays DEL.
    if (key === 'tab' && shift && !ctrl && !alt) return '\x1b[Z';
    if (key === 'backspace') {
        if (ctrl) return shift ? formatOtherKey(8, m) : '\x7f';
        return formatOtherKey(127, m);
    }
    if (key === 'space' || chars === null || ctrl || alt) return formatOtherKey(code, m);
    return ''; // Shift alone on a printable: the shifted character is its own text, delivered through TextInput.
}

/** The oracle's expectation for one plan case: the bytes its declared event must deliver to the child. */
export function oracleHex(item) {
    const { event, modes } = item;
    const down = modifyOtherKeysReport({ level: modes.modifyOtherKeysLevel, kind: event.kind, key: event.key,
        modifiers: event.modifiers, kittyKeyboardFlags: modes.kittyKeyboardFlags, text: event.text });
    if (event.kind !== 'keyDown' || event.text === undefined) return hex(down);
    // A key-down that carries text also raises the backend's TextInput: a handled key reserved it (suppressed), an
    // unhandled one did not, so the text is the one delivery. Never fabricated through an unrelated text commit.
    const reserved = down !== '';
    return hex(down + modifyOtherKeysReport({ kind: 'text', text: event.text, reserved }));
}

/** Every modifyOtherKeys plan literal must equal the independent oracle; returns the mismatching ids (empty when coherent). */
export function oracleDisagreements(cases = makeModifyOtherKeysCases(40, 140)) {
    return cases.filter(item => item.roles[modifyOtherKeysCaseIds[0]] !== undefined)
        .flatMap(item => oracleHex(item) === item.expectedHex ? [] : [item.id]);
}

const absentEventNote = 'No generated input event and encoded report were validated';
/** The pinned definitions (terminfo.dev@4.0.0, probe-defs/src/input.ts), documented for the publication. */
export const modifyOtherKeysPinned = Object.freeze({
    'input.modify-other-keys': Object.freeze({
        criterion: 'probe("input.modify-other-keys", (ctx) => ({ pass: false, response: JSON.stringify({ declaredModifyOtherKeys: '
            + 'ctx.capabilities.extensions.has("modifyOtherKeys") }), note: absentEventNote, observation: { outcome: "inconclusive", '
            + 'reason: "insufficient-evidence", evidence: "legacy", note: absentEventNote } }), () => Promise.resolve({ pass: false, '
            + 'note: absentEventNote, observation: { outcome: "inconclusive", reason: "insufficient-evidence", evidence: "none", '
            + 'note: absentEventNote } })): reads only the capability declaration, feeds nothing and looks at no event, and is inconclusive '
            + 'for any value; the terminal path returns inconclusive without touching the terminal',
        feeds: Object.freeze([]), declaration: 'modifyOtherKeys', note: absentEventNote, evidence: 'legacy',
    }),
});

/** The pinned termless result, key order included, over the served declaration value. */
export function replayModifyOtherKeysPinned(served) {
    return { pass: false, response: JSON.stringify({ declaredModifyOtherKeys: served.declaredModifyOtherKeys === true }),
        note: absentEventNote,
        observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'legacy', note: absentEventNote } };
}

/** The pinned outcome a result carries: only inconclusive is ever possible for this probe. */
export function pinnedOutcome(result) {
    return result?.observation?.outcome === 'inconclusive' ? 'inconclusive' : 'invalid';
}

/**
 * Serve the pinned code with the child's XTGETTCAP refusal/advertisement for the modifyOtherKeys capability name,
 * refusing any feed or read the contract does not name: the probe feeds nothing, reads no mode, no cell, no cursor.
 */
export function modifyOtherKeysRecordingContext(served) {
    let reads = 0;
    return {
        get readCount() { return reads; },
        feed: () => { throw new Error('Pinned input.modify-other-keys must not feed.'); },
        getMode: () => { throw new Error('Pinned input.modify-other-keys reads no mode.'); },
        getCell: () => { throw new Error('Pinned input.modify-other-keys must not read cells.'); },
        getCursor: () => { throw new Error('Pinned input.modify-other-keys must not read the cursor.'); },
        capabilities: { extensions: { has: name => {
            reads++;
            if (name !== 'modifyOtherKeys') throw new Error('Pinned input.modify-other-keys reads an uncontracted capability.');
            return served.declaredModifyOtherKeys === true;
        } } },
    };
}

/** The declaration the child itself answered, from the retained XTGETTCAP control capture (the reply is its exact prefix). */
export function servedDeclaration(controlHex) {
    const text = Buffer.from(controlHex ?? '', 'hex').toString('latin1');
    if (!text.startsWith(xtgettcapNotAdvertised)) {
        if (!text.startsWith('\x1bP1+r')) throw new Error('The child answered no XTGETTCAP declaration capture.');
        return { declaredModifyOtherKeys: true, source: 'child-xtgettcap-modifyOtherKeys' };
    }
    return { declaredModifyOtherKeys: false, source: 'child-xtgettcap-modifyOtherKeys' };
}

const requiredStatuses = Object.freeze(['required', 'control', 'label']);

/** Experiments of the benchmark in plan order, with the role the benchmark assigns. */
export function contractExperiments(caseId, cases = makeModifyOtherKeysCases(40, 140)) {
    return cases.filter(item => item.roles[caseId]).map(item => ({ id: item.id, role: item.roles[caseId] }));
}

/**
 * Grade the benchmark from its measured experiments ({id, role, matched}). A mismatching required experiment establishes
 * unsupported only when every control held; an invalid control leaves the benchmark inconclusive; labels never grade.
 */
export function gradeModifyOtherKeys(caseId, experiments, version = modifyOtherKeysContractsVersion) {
    if (version !== modifyOtherKeysContractsVersion) throw new Error('Unknown modifyOtherKeys contracts version.');
    const planned = contractExperiments(caseId);
    if (experiments.length !== planned.length || planned.some((item, index) => experiments[index].id !== item.id || experiments[index].role !== item.role
        || typeof experiments[index].matched !== 'boolean')) throw new Error(`modifyOtherKeys experiments differ from the plan for ${caseId}.`);
    const failedControls = experiments.filter(item => item.role === 'control' && !item.matched).map(item => item.id);
    const failedRequired = experiments.filter(item => item.role === 'required' && !item.matched).map(item => item.id);
    if (failedControls.length) return { outcome: 'inconclusive', reason: 'control-mismatch', failedControls, failedRequired };
    if (failedRequired.length) return { outcome: 'unsupported', failedRequired };
    return { outcome: 'supported' };
}
