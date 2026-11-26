import { keyboardModes, verifyKeyboardRestoration, verifyKeyboardStep, withKeyboardRestore } from './modern-x11-keyboard.mjs';
import { exchange, need } from './modern-x11-mouse.mjs';

const csi = '\x1b[', family = 'extensions.kitty-keyboard';
const pop = csi + '<u';
const code = (key, extra = '') => csi + key + extra + 'u';
const hex = value => Buffer.from(value, 'ascii').toString('hex');
const entries = [
    { name: 'disambiguate', mask: 1, chord: 'ctrl+i', keys: ['Control_L', 'i'], tag: 'zm', expected: code(105, ';5') },
    { name: 'report-events', mask: 3, chord: 'escape-hold', keys: ['Escape'], tag: 'zn' },
    { name: 'report-alternate', mask: 5, chord: 'ctrl+shift+a', keys: ['Control_L', 'Shift_L', 'a'], tag: 'zo', expected: code('97:65', ';6') },
    { name: 'report-all-keys', mask: 9, chord: 'a', keys: ['a'], tag: 'zp', expected: code(97) },
    { name: 'report-text', mask: 25, chord: 'a', keys: ['a'], tag: 'zq', expected: code(97, ';1;97') },
];
export const plans = Object.freeze(entries.map(item => Object.freeze({ ...item, id: family + '.' + item.name })));
const controls = [
    { name: 'plain-a', chord: 'a', keys: ['a'], tag: 'zl', expected: '61' },
    { name: 'legacy-ctrl-i', chord: 'ctrl+i', keys: ['Control_L', 'i'], tag: 'zr', expected: '09' },
];

export const modePlan = Object.freeze({
    incoming: keyboardModes(0),
    restore: keyboardModes(0, pop),
    ...Object.fromEntries(plans.flatMap(({ name, mask }) => [
        [name + '/push', keyboardModes(0, csi + '>' + mask + 'u', undefined, mask)],
        [name + '/pop', keyboardModes(0, pop)],
    ])),
});

function fenceBytes({ mask, tag }) {
    if (!(mask & 8)) return undefined;
    const sequence = Array.from(tag, letter => {
        const point = letter.codePointAt(0);
        return code(point, mask & 16 ? ';1;' + point : '');
    }).join('');
    return Buffer.from(sequence, 'ascii');
}

function verifyAction(step, key, plan, previous, identity) {
    const result = verifyKeyboardStep(step, key, { ...plan, fenceBytes: fenceBytes(plan) }, previous, identity);
    if (plan.name === 'report-events') {
        const [press, release] = step.native.events;
        need(BigInt(release.at) - BigInt(press.at) >= 850000000n, 'Escape was not held for the native repeat window');
    }
    if (plan.name === 'report-alternate') {
        const { mapping, events } = step.native;
        need(mapping?.unmodifiedKeysym === 97 && mapping.shiftedKeysym === 65 && mapping.code === events[2].code,
            'the native layout did not expose a/A on the same physical key');
    }
    return result;
}

const escapeRepeat = code(27, ';1:2');

function gradeTrial(plan, childHex, controlsValid) {
    const observed = { id: plan.id, mask: plan.mask, chord: plan.chord, childHex };
    if (plan.name !== 'report-events') {
        const matches = childHex === hex(plan.expected);
        return { ...observed, effect: effectVerdict(controlsValid, matches), repeats: null };
    }
    const raw = Buffer.from(childHex, 'hex').toString('latin1');
    const repeats = raw.split(escapeRepeat).length - 1;
    // Without a repeat frame, the recorded key hold alone cannot prove that
    // X11 delivered an autorepeat event to the App.
    if (repeats === 0) {
        return { ...observed, effect: 'inconclusive', repeats, reason: 'The child observed no Escape autorepeat frame.' };
    }
    const expected = code(27) + escapeRepeat.repeat(repeats) + code(27, ';1:3');
    return { ...observed, effect: effectVerdict(controlsValid, raw === expected), repeats };
}

function effectVerdict(valid, matches) {
    if (!valid) return 'inconclusive';
    return matches ? 'supported' : 'unsupported';
}

export async function observeKeyboard(record, modes, action) {
    const capture = (step, plan, key) => action(step, { kind: 'keyboard', chord: plan.chord }, key, plan.tag);
    record.incoming = await modes('incoming');
    record.controls = [];
    await withKeyboardRestore(record, modes, async () => {
        for (const plan of controls) {
            const step = {};
            record.controls.push(step);
            await capture(step, plan, plan.name); // NOSONAR: arm one owned window at a time.
        }
        for (const plan of plans) {
            const trial = { id: plan.id, target: {} };
            record.cases.push(trial);
            trial.push = await modes(plan.name + '/push'); // NOSONAR: establish a flag before injecting its chord.
            await capture(trial.target, plan, plan.id); // NOSONAR: await child capture before popping.
            trial.pop = await modes(plan.name + '/pop'); // NOSONAR: restore the stack before the next trial.
        }
    });
}

export function replayKeyboard(record) {
    const { identity } = record;
    need(identity?.pane?.kind === 'local' && identity.pane.isActive === true && record.cases?.length === plans.length
        && record.controls?.length === controls.length, 'expected five owned Kitty trials and two baseline controls');
    let previous = exchange(record.incoming, modePlan.incoming, identity.admitted);
    const baselines = controls.map((plan, index) => {
        const measured = verifyAction(record.controls[index], plan.name, plan, previous, identity);
        previous = measured.sealed;
        return { name: plan.name, expected: plan.expected, childHex: measured.raw };
    });
    const controlsValid = baselines.every(control => control.childHex === control.expected);
    const rows = plans.map((plan, index) => {
        const trial = record.cases[index];
        need(trial.id === plan.id, 'wrong Kitty flag trial order');
        const enabled = exchange(trial.push, modePlan[plan.name + '/push'], previous);
        const result = verifyAction(trial.target, plan.id, plan, enabled, identity);
        previous = exchange(trial.pop, modePlan[plan.name + '/pop'], result.sealed);
        return { ...gradeTrial(plan, result.raw, controlsValid), negotiation: {
            pushedReplyHex: trial.push.replyHex, poppedReplyHex: trial.pop.replyHex },
        };
    });
    verifyKeyboardRestoration(record, modePlan.restore, previous);
    return { controls: baselines, rows };
}
