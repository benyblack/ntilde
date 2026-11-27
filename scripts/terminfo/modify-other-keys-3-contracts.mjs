// Literal child-byte oracle, specified before editing VT or App. xterm patch 412,
// input.c: ModifyOtherKeys(case 3), modifyOtherKey(format_keys == 0). Ntilde's
// printable key table uses the existing US logical-key policy with Shift applied.
import { defaultInputModes, inputModeQueries } from './input-plan.mjs';
import { inputReset } from './input-cases.mjs';
import { modifyOtherKeysXtgettcapQuery, xtgettcapNotAdvertised, xtqmodkeysQuery } from './modify-other-keys-cases.mjs';

export const caseIds = ['input.modify-other-keys-3'];
export const contractsVersion = 'ntilde-modify-other-keys-3-effect-v1';
export const suiteId = 'ntilde-owned-modify-other-keys-3-v1';
export const scope = { id: suiteId, persistence: 'Off', deadlineMs: 90000 };
export const baselineScope = { ...scope, id: suiteId + '-baseline' };
const hex = bytes => Buffer.from(bytes, 'ascii').toString('hex');
const set = level => '\x1b[>4;' + level + 'm';
const control = 'control', effect = 'required';

function row(id, level, event, bytes, handled, role = control, { setup = set(level), kittyKeyboardFlags = 0 } = {}) {
    return { id, setup: inputReset + setup, event: { modifiers: 0, ...event }, expectedHex: hex(bytes), handled, role,
        modes: { ...defaultInputModes, kittyKeyboardFlags, modifyOtherKeysLevel: level } };
}

const down = (key, modifiers, text) => ({ kind: 'keyDown', key, modifiers, ...(text === undefined ? {} : { text }) });
const declaration = { ...row('declaration', 0, { kind: 'keyUp', key: 'a' }, '', false, control, { setup: modifyOtherKeysXtgettcapQuery }),
    controlPrefixHex: hex(xtgettcapNotAdvertised) };
const plain = [
    ['a', 'a', '\x1b[27;1;97~'],
    ['digit2', '2', '\x1b[27;1;50~'],
    ['semicolon', ';', '\x1b[27;1;59~'],
    ['space', ' ', '\x1b[27;1;32~'],
];
const restored = row('reset-to-text', 0, down('a', 0, 'a'), 'a', false, control, { setup: set(3) + '\x1b[>4m' });

function experiments(baseline) {
    if (baseline) return [
        row('level2-text', 2, down('a', 0, 'a'), 'a', false),
        row('request3-over2', 2, down('a', 0, 'a'), 'a', false, control, { setup: set(2) + set(3) }),
        row('request3-over2-ctrl', 2, down('a', 2), '\x1b[27;5;97~', true, control, { setup: set(2) + set(3) }),
        restored,
    ];
    return [
        declaration,
        row('level0-ctrl-a', 0, down('a', 2), '\x01', true),
        row('level1-ctrl-a', 1, down('a', 2), '\x01', true),
        ...plain.map(([key, text]) => row('level2-' + key, 2, down(key, 0, text), text, false)),
        ...plain.map(([key, text, frame]) => row('level3-' + key, 3, down(key, 0, text), frame, true, effect)),
        row('level3-key-only', 3, down('a', 0), '\x1b[27;1;97~', true, effect),
        row('level3-shift-a', 3, down('a', 1, 'A'), '\x1b[27;2;65~', true, effect),
        row('level3-ctrl-a', 3, down('a', 2), '\x1b[27;5;97~', true, effect),
        row('level3-alt-a', 3, down('a', 4), '\x1b[27;3;97~', true, effect),
        row('level3-standalone-text', 3, { kind: 'text', text: '7' }, '7', true),
        row('level3-key-up', 3, { kind: 'keyUp', key: 'a' }, '', false),
        row('level3-return', 3, down('return', 0), '\r', true),
        row('level3-kitty-ctrl-a', 3, down('a', 2), '\x1b[97;5u', true, control, { setup: set(3) + '\x1b[>1u', kittyKeyboardFlags: 1 }),
        row('level3-kitty-pop', 3, down('a', 0, 'a'), '\x1b[27;1;97~', true, control, { setup: set(3) + '\x1b[>1u\x1b[<u' }),
        row('return-to-level2', 2, down('a', 0, 'a'), 'a', false, control, { setup: set(3) + set(2) }),
        restored,
    ];
}

export function planFor(id) {
    if (![suiteId, baselineScope.id].includes(id)) throw new Error('Unknown modifyOtherKeys measurement.');
    return { suiteId: id, make: () => experiments(id === baselineScope.id), exactEvents: false,
        queries: inputModeQueries + xtqmodkeysQuery, finalModes: { ...defaultInputModes, modifyOtherKeysLevel: 0 },
        scope: () => 'Generated Avalonia input and raw direct-local child stdin' };
}
