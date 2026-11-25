import { keyboardModes, verifyKeyboardRestoration, verifyKeyboardStep, withKeyboardRestore } from './modern-x11-keyboard.mjs';
import { before, exchange, need } from './modern-x11-mouse.mjs';

export const id = 'modes.focus-tracking';
const hex = value => Buffer.from(value, 'ascii').toString('hex');
const set = '\x1b[?1004h', reset = '\x1b[?1004l', query = '\x1b[?1004$p';
const status = '\x1b[5n', statusReply = '\x1b[0n';
const delivery = '61', focusIn = '1b5b49', focusOut = '1b5b4f';
const phases = [
    { name: 'on', writeHex: hex(set + query + status), tag: 'zf' },
    { name: 'off', writeHex: hex(reset + query + status), tag: 'zg' },
];
export const modePlan = {
    incoming: keyboardModes(0),
    restore: { writeHex: hex(reset + status), replyHex: hex(statusReply) },
};

export async function observeFocus(record, modes, action) {
    record.incoming = await modes('incoming');
    const trial = { id };
    record.cases.push(trial);
    await withKeyboardRestore(record, modes, async () => {
        for (const { name, tag, writeHex } of phases) {
            const step = trial[name] = {};
            await action(step, id + '/' + name, tag, writeHex); // NOSONAR: the mode phases share a pane and must run in order.
        }
    });
}

function verifyCheckpoint(step, key, writeHex) {
    const { checkpoint, arm } = step;
    need(checkpoint?.key === key && checkpoint.writeHex === writeHex
        && before(arm?.armed, checkpoint.started) && before(checkpoint.started, checkpoint.finished)
        && BigInt(checkpoint.finished) - BigInt(checkpoint.started) >= 200000000n, 'unarmed or stale mode write');
    const chunks = checkpoint.chunks;
    need(Array.isArray(chunks) && chunks.every((chunk, index) =>
        typeof chunk.hex === 'string' && /^(?:[a-f\d]{2})+$/.test(chunk.hex)
        && before(chunks[index - 1]?.at ?? checkpoint.started, chunk.at)
        && before(chunk.at, checkpoint.finished)), 'malformed mode checkpoint');
    const raw = Buffer.from(chunks.map(chunk => chunk.hex).join(''), 'hex').toString('latin1');
    const tokens = raw.match(/\x1b\[(?:0n|\?1004;[0-4]\$y|[IO])/g) ?? [];
    const decrpm = tokens.filter(token => token.startsWith('\x1b[?'));
    const reports = tokens.filter(token => token === '\x1b[I' || token === '\x1b[O');
    return {
        valid: raw.length <= 1024 && tokens.join('') === raw
            && tokens.filter(token => token === statusReply).length === 1 && decrpm.length <= 1,
        decrpmHex: decrpm.map(hex), setupReports: reports.map(hex),
    };
}

function verifyFocusReads(step, identity) {
    const { checkpoint, focus, native } = step;
    const window = identity.window.id, root = identity.root;
    need(focus?.serverPid === identity.serverPid && focus.windowId === window && focus.root === root
        && focus.out?.target === root && focus.back?.target === window
        && before(checkpoint.finished, focus.out.requested) && before(focus.out.requested, focus.out.at)
        && before(focus.out.at, focus.back.requested) && before(focus.back.requested, focus.back.at)
        && before(focus.back.at, native?.started), 'stale or unowned X11 focus observations');
    if (focus.back.observed !== window) {
        const { recovery } = focus;
        need(recovery?.target === window && recovery.observed === window && before(focus.back.at, recovery.requested)
            && before(recovery.requested, recovery.at) && before(recovery.at, native.started), 'unverified X11 focus restoration');
    } else need(focus.recovery === null, 'unexpected X11 focus recovery');
    return focus.out.observed === root && focus.back.observed === window;
}

function byteReadAfter(chunks, offset, event) {
    let end = 0;
    const chunk = chunks.find(chunk => {
        end += chunk.hex.length / 2;
        return end > offset;
    });
    return before(event, chunk?.at);
}

function replayPhase(step, phase, previous, identity) {
    const key = id + '/' + phase.name;
    const checkpoint = verifyCheckpoint(step, key, phase.writeHex);
    const focusVerified = verifyFocusReads(step, identity);
    const { sealed, raw } = verifyKeyboardStep(step, key, {
        chord: 'a', keys: ['a'], tag: phase.tag, firstInput: step.focus.out.requested,
    }, previous, identity);
    const chunks = step.capture.chunks;
    const delivered = raw.endsWith(delivery)
        && byteReadAfter(chunks, raw.length / 2 - 1, step.native.events[0].at);
    const focusInOrdered = raw !== focusOut + focusIn + delivery
        || byteReadAfter(chunks, focusOut.length / 2, step.focus.back.requested);
    return { ...checkpoint, focusVerified, delivered, focusInOrdered, sealed, raw };
}

const invalid = reason => ({ status: 'probe-invalid', conclusion: 'inconclusive', reason });

function gradeFocus(on, off) {
    if (!on.focusVerified || !off.focusVerified) return invalid('An X11 focus readback differed from the requested window.');
    if (!on.valid || !off.valid || on.setupReports.length > 1) return invalid('The armed mode checkpoint did not isolate an optional enable-time report.');
    if (!on.delivered || !off.delivered) return invalid('The XTEST delivery key was missing from a fresh raw PTY read.');
    if (off.setupReports.length || off.raw !== delivery) return invalid('Unexpected bytes appeared in the mode-off control.');
    if (on.raw === focusOut + focusIn + delivery && on.focusInOrdered) {
        return { status: 'supported', conclusion: 'pass', reason: 'The mode-on read contained CSI O then CSI I before the key. The mode-off read contained only the key.' };
    }
    if (on.raw === delivery) {
        return { status: 'unsupported', conclusion: 'fail', reason: 'Neither focus report arrived in the mode-on read despite verified X11 transitions and key delivery.' };
    }
    return invalid('The mode-on read did not contain a classified sequence of reports before the key.');
}

export function replayFocus(record) {
    const { identity, cases } = record;
    need(identity?.pane?.kind === 'local' && identity.pane.isActive === true && cases?.length === 1
        && cases[0].id === id && Number.isSafeInteger(identity.root) && identity.root > 1
        && identity.root !== identity.window.id, 'expected one direct-local focus trial with a distinct X11 root');
    const incoming = exchange(record.incoming, modePlan.incoming, identity.admitted);
    const trial = cases[0];
    const on = replayPhase(trial.on, phases[0], incoming, identity);
    const off = replayPhase(trial.off, phases[1], on.sealed, identity);
    verifyKeyboardRestoration(record, modePlan.restore, off.sealed);
    return [{ id, ...gradeFocus(on, off), modeOnHex: on.raw, modeOffHex: off.raw,
        enableTimeHex: on.setupReports.length <= 1 ? on.setupReports[0] ?? null : null,
        decrpm: { on: on.decrpmHex, off: off.decrpmHex },
        focus: { on: trial.on.focus, off: trial.off.focus } }];
}
