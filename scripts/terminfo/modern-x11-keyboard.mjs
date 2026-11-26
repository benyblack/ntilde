import { isDeepStrictEqual as equal } from 'node:util';
import { actionWindow, before, exchange, need } from './modern-x11-mouse.mjs';

export const id = 'input.modify-other-keys';
const csi = '\x1b[';
const hex = value => Buffer.from(value, 'ascii').toString('hex');
const decModes = [1, 66, 1049];
export function keyboardModes(level, write = '', modes = decModes, kittyFlags = 0) {
    const query = modes.map(mode => csi + '?' + mode + '$p').join('') + csi + '?u' + csi + '?4m';
    const reply = modes.map(mode => csi + '?' + mode + ';2$y').join('') + csi + '?' + kittyFlags + 'u' + csi + '>4;' + level + 'm';
    return { writeHex: hex(write + query), replyHex: hex(reply) };
}
export const modePlan = {
    incoming: keyboardModes(0),
    enable: keyboardModes(2, csi + '>4;2m'),
    restore: keyboardModes(0, csi + '>4;0m'),
};
const plan = {
    control: { chord: 'a', keys: ['a'], tag: 'zb' },
    target: { chord: 'ctrl+i', keys: ['Control_L', 'i'], tag: 'zc' },
};

export function gradeKeyboard(controlHex, targetHex) {
    if (controlHex !== '61') return { conclusion: 'inconclusive', reason: 'Plain a did not arrive as 61.' };
    if ([hex(csi + '27;5;105~'), hex(csi + '105;5u')].includes(targetHex)) {
        return { conclusion: 'pass', reason: 'Ctrl+I produced a frozen level-2 encoding.' };
    }
    if (targetHex === '09') return { conclusion: 'fail', reason: 'Ctrl+I arrived as a literal Tab under level 2.' };
    return { conclusion: 'inconclusive', reason: 'The Ctrl+I byte stream did not match a classified frozen outcome.' };
}

function verifyChord(events, keys, start, end) {
    const sequence = [...keys, ...keys.toReversed()];
    need(Array.isArray(events) && events.length === sequence.length, 'incomplete XTEST chord');
    const held = new Map();
    for (const [index, event] of events.entries()) {
        const down = index < keys.length;
        need(event.name === sequence[index] && event.down === down
            && Number.isInteger(event.code) && event.code >= 8 && event.code <= 255
            && before(start, event.at) && before(event.at, end), 'invalid XTEST key transition');
        if (down) {
            need(![...held.values()].includes(event.code), 'duplicate XTEST keycode');
            held.set(event.name, event.code);
        } else {
            need(held.get(event.name) === event.code, 'wrong XTEST release keycode');
            held.delete(event.name);
        }
        need(equal(event.pressed, [...held.values()].toSorted((a, b) => a - b))
            && event.mask === (Number(held.has('Control_L')) * 4 + Number(held.has('Shift_L'))), 'incorrect XTEST key or modifier state');
        start = event.at;
    }
}

export function verifyKeyboardStep(step, key, { chord, keys, tag, firstInput, fenceBytes }, previous, identity) {
    const native = step?.native;
    need(native?.xtest === true && native.serverPid === identity.serverPid && native.appPid === identity.appPid
        && native.windowId === identity.window.id && native.focus === identity.window.id
        && equal(native.initialKeys, []) && equal(native.finalKeys, []) && native.finalMask === 0
        && equal(native.request, { kind: 'keyboard', chord }), 'wrong XTEST chord, focus or key restoration');
    verifyChord(native.events, keys, native.started, native.fence.started);
    const events = native.fence.events;
    need(events?.length === 4 && equal(native.fence.keycodes, [events[0].code, events[2].code]), 'invalid keyboard delimiter');
    verifyChord(events.slice(0, 2), [tag[0]], native.fence.started, events[2].at);
    verifyChord(events.slice(2), [tag[1]], events[1].at, native.finished);
    return actionWindow(step, previous, key, tag, firstInput ?? native.events[keys.length - 1].at, fenceBytes);
}

const verifyStep = (step, phase, previous, identity) => verifyKeyboardStep(step, id + '/' + phase, plan[phase], previous, identity);

export async function withKeyboardRestore(record, modes, observe) {
    let failure;
    try { await observe(); }
    catch (error) { failure = error; }
    try { record.restore = { modes: await modes('restore') }; }
    catch (error) { throw failure ? new AggregateError([failure, error], 'keyboard capture and restoration failed') : error; }
    if (failure) throw failure;
}

export function verifyKeyboardRestoration(record, expected, previous) {
    const { identity, restore, terminal } = record;
    const restored = exchange(restore?.modes, expected, previous);
    need(restore.paneId === identity.pane.paneId && restore.marker === identity.marker
        && terminal?.original?.length === 7 && equal(terminal.original, terminal.restored)
        && before(restored, terminal.at), 'incomplete keyboard, pane or kernel terminal restoration');
}

export async function observeKeyboard(record, modes, action) {
    record.incoming = await modes('incoming');
    const trial = { id, control: {} };
    record.cases.push(trial);
    await withKeyboardRestore(record, modes, async () => {
        trial.enabled = await modes('enable');
        for (const [phase, { chord, tag }] of Object.entries(plan)) {
            const step = trial[phase] ??= {};
            await action(step, { kind: 'keyboard', chord }, id + '/' + phase, tag); // NOSONAR: each chord needs its own armed PTY window.
            if (phase === 'control' && verifyStep(step, phase, trial.enabled.finished, record.identity).raw !== '61') break;
        }
    });
}

export function replayKeyboard(record) {
    const { identity, cases } = record;
    need(identity?.pane?.kind === 'local' && identity.pane.isActive === true && cases?.length === 1
        && cases[0].id === id, 'expected one direct-local keyboard trial');
    const incoming = exchange(record.incoming, modePlan.incoming, identity.admitted);
    const trial = cases[0];
    const enabled = exchange(trial.enabled, modePlan.enable, incoming);
    const control = verifyStep(trial.control, 'control', enabled, identity);
    let target;
    if (control.raw === '61') target = verifyStep(trial.target, 'target', control.sealed, identity);
    else need(!trial.target, 'Ctrl+I dispatched after failed plain-a control');
    verifyKeyboardRestoration(record, modePlan.restore, target?.sealed ?? control.sealed);
    const targetHex = target?.raw ?? null;
    return [{ id, controlHex: control.raw, targetHex, ...gradeKeyboard(control.raw, targetHex) }];
}
