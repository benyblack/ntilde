import { isDeepStrictEqual as equal } from 'node:util';
import { keyboardModes, verifyKeyboardStep } from './modern-x11-keyboard.mjs';
import { before, exchange, need } from './modern-x11-mouse.mjs';

export const id = 'modes.application-keypad';
const deckpam = '\x1b=', deckpnm = '\x1b>';
const competingDecModes = [1, 1049];
export const modePlan = {
    incoming: keyboardModes(0),
    application: keyboardModes(0, deckpam, competingDecModes),
    numeric: keyboardModes(0, deckpnm, competingDecModes),
    restore: keyboardModes(0, deckpnm),
};

const plan = {
    control: { chord: 'a', keys: ['a'], tag: 'zb' },
    application: { chord: 'KP_Add', keys: ['KP_Add'], tag: 'zd' },
    numeric: { chord: 'KP_Add', keys: ['KP_Add'], tag: 'ze' },
};

export function gradeKeypad(controlHex, applicationHex, numericHex) {
    if (controlHex !== '61') return { conclusion: 'inconclusive', reason: 'Probe invalid: plain a did not arrive as 61.' };
    if (applicationHex === '1b4f6b' && numericHex === '2b') {
        return { conclusion: 'pass', reason: 'KP_Add produced SS3 k under DECKPAM and + under DECKPNM.' };
    }
    if (applicationHex === '2b' && numericHex === '2b') {
        return { conclusion: 'fail', reason: 'Unsupported: KP_Add produced + in both modes after valid plain-a delivery.' };
    }
    return { conclusion: 'inconclusive', reason: 'Probe invalid: the two KP_Add byte streams did not match a frozen outcome.' };
}

function verifyStep(step, phase, previous, identity) {
    return verifyKeyboardStep(step, id + '/' + phase, plan[phase], previous, identity);
}

function verifyMapping(native) {
    const mapping = native.mapping;
    need(mapping?.name === 'KP_Add' && mapping.keysym === 0xffab && mapping.code === native.events[0].code
        && Array.isArray(mapping.levels) && mapping.levels.length <= 32 && mapping.levels.includes(mapping.keysym)
        && mapping.levels.every(level => Number.isInteger(level) && level >= 0 && level <= 0xffffffff)
        && Number.isInteger(mapping.numLockMask) && mapping.numLockMask > 0 && mapping.numLockMask <= 255
        && mapping.unmodifiedKeysym === mapping.keysym && mapping.numLockKeysym === mapping.keysym, 'missing or incorrect KP_Add mapping');
    return mapping;
}

export async function observeKeyboard(record, modes, action) {
    record.incoming = await modes('incoming');
    const trial = { id };
    record.cases.push(trial);
    async function capture(phase) {
        const { chord, tag } = plan[phase];
        const step = trial[phase] = {};
        await action(step, { kind: 'keyboard', chord }, id + '/' + phase, tag);
    }
    let failure;
    try {
        await capture('control');
        const control = verifyStep(trial.control, 'control', record.incoming.finished, record.identity);
        if (control.raw === '61') {
            trial.enabled = await modes('application');
            await capture('application');
            trial.disabled = await modes('numeric');
            await capture('numeric');
        }
    } catch (error) { failure = error; }
    try { record.restore = { modes: await modes('restore') }; }
    catch (error) { throw failure ? new AggregateError([failure, error], 'keypad capture and restoration failed') : error; }
    if (failure) throw failure;
}

export function replayKeyboard(record) {
    const { identity, cases, restore, terminal } = record;
    need(identity?.pane?.kind === 'local' && identity.pane.isActive === true && cases?.length === 1
        && cases[0].id === id, 'expected one direct-local keypad trial');
    const incoming = exchange(record.incoming, modePlan.incoming, identity.admitted);
    const trial = cases[0];
    const control = verifyStep(trial.control, 'control', incoming, identity);
    let previous = control.sealed, applicationHex = null, numericHex = null, mapping = null;
    if (control.raw === '61') {
        const enabled = exchange(trial.enabled, modePlan.application, previous);
        const application = verifyStep(trial.application, 'application', enabled, identity);
        const disabled = exchange(trial.disabled, modePlan.numeric, application.sealed);
        const numeric = verifyStep(trial.numeric, 'numeric', disabled, identity);
        mapping = verifyMapping(trial.application.native);
        need(equal(verifyMapping(trial.numeric.native), mapping), 'KP_Add mapping changed between modes');
        applicationHex = application.raw;
        numericHex = numeric.raw;
        previous = numeric.sealed;
    } else {
        need(['enabled', 'application', 'disabled', 'numeric'].every(phase => !Object.hasOwn(trial, phase)),
            'KP_Add dispatched after failed plain-a control');
    }
    const restored = exchange(restore?.modes, modePlan.restore, previous);
    need(restore.paneId === identity.pane.paneId && restore.marker === identity.marker
        && terminal?.original?.length === 7 && equal(terminal.original, terminal.restored)
        && before(restored, terminal.at), 'incomplete keypad, pane or kernel terminal restoration');
    return [{ id, controlHex: control.raw, applicationHex, numericHex, mapping, ...gradeKeypad(control.raw, applicationHex, numericHex) }];
}
