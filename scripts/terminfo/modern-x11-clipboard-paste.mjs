import { randomBytes, randomUUID } from 'node:crypto';
import { verifyKeyboardRestoration, verifyKeyboardStep } from './modern-x11-keyboard.mjs';
import { before, exchange, need } from './modern-x11-mouse.mjs';
import { reading, sameOwner, seed } from './modern-x11-osc52-write.mjs';

export const id = 'extensions.clipboard-paste';
const shortcut = 'Ctrl+V';
const selection = 'CLIPBOARD';
const csi = '\x1b[';
const hex = text => Buffer.from(text, 'ascii').toString('hex');
const query = csi + '?2004$p';
const read = { kind: 'read' };
const control = { chord: 'a', keys: ['a'], tag: 'zh' };
const phases = [
    { name: 'off-before', enabled: false, tag: 'zi' },
    { name: 'on', enabled: true, tag: 'zj' },
    { name: 'off-after', enabled: false, tag: 'zk' },
];

function mode(enabled, write = true) {
    const command = enabled ? 'h' : 'l';
    const change = write ? csi + '?2004' + command : '';
    return {
        writeHex: hex(change + query),
        replyHex: hex(csi + '?2004;' + (enabled ? '1' : '2') + '$y'),
    };
}

function incomingMode(sample, identity) {
    const enabled = sample?.replyHex === mode(true, false).replyHex;
    return { enabled, at: exchange(sample, mode(enabled, false), identity.admitted) };
}

function checkedRead(sample, record, at, seeded, text) {
    const finished = reading(sample, record, at, [seeded.ownerPid]);
    need(sameOwner(sample.after, seeded) && sample.result.text === text, 'clipboard readback or owner changed');
    return finished;
}

function plainKey(step, record, at) {
    const result = verifyKeyboardStep(step, id + '/control', control, at, record.identity);
    need(result.raw === '61', 'plain XTEST a did not reach the child');
    return result;
}

function pasteStep(step, phase, at, identity) {
    const result = verifyKeyboardStep(step, id + '/' + phase.name, {
        chord: 'ctrl+v', keys: ['Control_L', 'v'], tag: phase.tag,
    }, at, identity);
    need(BigInt(step.native.fence.started) - BigInt(step.native.events.at(-1).at) >= 1_000_000_000n,
        'paste delivery window ended before the one-second budget');
    return result;
}

async function prepareSelection(record, trial, peer) {
    await peer(trial.seed, { kind: 'seed', text: trial.baseline }, [null]);
    const seededAt = seed(trial.seed, record, record.incoming.finished, trial.baseline, [null]);
    await peer(trial.baselineRead, read, [trial.seed.process.pid]);
    return checkedRead(trial.baselineRead, record, seededAt, trial.seed.after, trial.baseline);
}

async function capturePastes(record, trial, ops) {
    const baselineAt = await prepareSelection(record, trial, ops.peer);
    await ops.action(trial.key, control.chord, id + '/control', control.tag);
    const key = plainKey(trial.key, record, baselineAt);
    const baselineOwners = [trial.seed.process.pid];
    await ops.peer(trial.nonceSeed, { kind: 'seed', text: trial.nonce }, baselineOwners);
    const nonceAt = seed(trial.nonceSeed, record, key.sealed, trial.nonce, baselineOwners);
    need(sameOwner(trial.nonceSeed.before, trial.seed.after), 'baseline lost ownership before the nonce seed');
    const nonceOwners = [trial.nonceSeed.process.pid];
    await ops.peer(trial.nonceRead, read, nonceOwners);
    checkedRead(trial.nonceRead, record, nonceAt, trial.nonceSeed.after, trial.nonce);
    for (const phase of phases) {
        const step = { name: phase.name, action: {}, read: {} };
        trial.phases.push(step);
        step.mode = await ops.mode(phase.name, mode(phase.enabled)); // NOSONAR: each mode precedes its own paste window.
        await ops.action(step.action, 'ctrl+v', id + '/' + phase.name, phase.tag); // NOSONAR: one raw PTY window at a time.
        await ops.peer(step.read, read, nonceOwners); // NOSONAR: verify the same owner and nonce after each paste.
        checkedRead(step.read, record, step.action.capture.sealed, trial.nonceSeed.after, trial.nonce);
    }
}

function restorationOwners(record, trial) {
    return [null, record.identity.appPid, trial.seed.process?.pid, trial.nonceSeed.process?.pid];
}

async function restoreSelection(record, trial, peer) {
    if (!trial.nonceSeed.dispatched) return;
    const owners = restorationOwners(record, trial);
    const started = process.hrtime.bigint().toString();
    await peer(trial.restoreSeed, { kind: 'seed', text: trial.baseline }, owners, true);
    const restored = seed(trial.restoreSeed, record, started, trial.baseline, owners);
    await peer(trial.restoreRead, read, [trial.restoreSeed.process.pid], true);
    checkedRead(trial.restoreRead, record, restored, trial.restoreSeed.after, trial.baseline);
}

export async function observePaste(record, ops) {
    const plan = { ...mode(false, false), alternateReplyHex: mode(true, false).replyHex };
    record.incoming = await ops.mode('incoming', plan);
    const incoming = incomingMode(record.incoming, record.identity);
    const trial = { id, baseline: 'ntilde-paste-baseline-' + randomBytes(16).toString('hex'),
        nonce: 'terminfo-paste-' + randomUUID(), seed: {}, baselineRead: {}, key: {},
        nonceSeed: {}, nonceRead: {}, phases: [], restoreSeed: {}, restoreRead: {} };
    record.cases.push(trial);
    record.restore = {};
    const errors = [];
    try { await capturePastes(record, trial, ops); }
    catch (error) { errors.push(error); }
    try { record.restore.modes = await ops.mode('restore', mode(incoming.enabled), true); }
    catch (error) { errors.push(error); }
    try { await restoreSelection(record, trial, ops.peer); }
    catch (error) { errors.push(error); }
    if (errors.length) throw new AggregateError(errors, errors.map(error => error.message).join(' / '));
}

export function pastePeerSteps(trial) {
    return [trial.seed, trial.baselineRead, trial.nonceSeed, trial.nonceRead,
        ...trial.phases.map(phase => phase.read), trial.restoreSeed, trial.restoreRead];
}

function grade(nonce, [offBefore, on, offAfter]) {
    const raw = hex(nonce);
    need(offBefore === raw && offAfter === raw, 'invalid paste run: both off phases must deliver the raw nonce');
    if (on === hex(csi + '200~' + nonce + csi + '201~')) {
        return { conclusion: 'pass', status: 'supported', reason: 'Ctrl+V delivered the nonce raw with mode 2004 off, bracketed with it on, and raw again with it off.' };
    }
    need(on === raw, 'invalid paste run: mode-on paste was missing or had unexpected bytes');
    return { conclusion: 'fail', status: 'unsupported', reason: 'Ctrl+V delivered the raw nonce with mode 2004 on after valid delivery and restoration controls.' };
}

export function replayPaste(record) {
    const { identity, cases, restore } = record;
    need(!Object.keys(record.settings).some(name => name.toLowerCase() === 'keybindings'), 'paste run has custom shortcut settings');
    const [trial] = cases;
    need(identity?.pane?.kind === 'local' && identity.pane.isActive === true && cases.length === 1
        && trial.id === id && /^ntilde-paste-baseline-[a-f\d]{32}$/.test(trial.baseline)
        && /^terminfo-paste-[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/.test(trial.nonce), 'wrong direct-local clipboard paste trial');
    const incoming = incomingMode(record.incoming, identity);
    const baseline = seed(trial.seed, record, incoming.at, trial.baseline, [null]);
    const checked = checkedRead(trial.baselineRead, record, baseline, trial.seed.after, trial.baseline);
    const key = plainKey(trial.key, record, checked);
    const nonce = seed(trial.nonceSeed, record, key.sealed, trial.nonce, [trial.seed.process.pid]);
    need(sameOwner(trial.nonceSeed.before, trial.seed.after), 'nonce seed did not replace the recorded baseline owner');
    let previous = checkedRead(trial.nonceRead, record, nonce, trial.nonceSeed.after, trial.nonce);
    need(trial.phases.length === phases.length, 'incomplete off/on/off paste phases');
    const reads = phases.map((phase, index) => {
        const step = trial.phases[index];
        need(step.name === phase.name, 'wrong paste phase order');
        const modeAt = exchange(step.mode, mode(phase.enabled), previous);
        const action = pasteStep(step.action, phase, modeAt, identity);
        previous = checkedRead(step.read, record, action.sealed, trial.nonceSeed.after, trial.nonce);
        return { phase: phase.name, rawHex: action.raw, mode: step.mode };
    });
    const restoredAt = exchange(restore?.modes, mode(incoming.enabled), previous);
    need(sameOwner(trial.restoreSeed.before, trial.nonceSeed.after), 'clipboard seed changed before restoration');
    const selectionAt = seed(trial.restoreSeed, record, restoredAt, trial.baseline, [trial.nonceSeed.process.pid]);
    const restored = checkedRead(trial.restoreRead, record, selectionAt, trial.restoreSeed.after, trial.baseline);
    const peers = pastePeerSteps(trial).map(step => step.process.pid);
    need(new Set([...peers, record.coordinatorPid, identity.serverPid, identity.appPid, record.lineage.child.pid]).size === peers.length + 4,
        'clipboard peers are not independent processes');
    verifyKeyboardRestoration(record, mode(incoming.enabled), previous);
    need(before(restored, record.terminal.at), 'selection was not restored before PTY cleanup');
    return [{ id, shortcut, selection, keyHex: key.raw, incomingMode: incoming.enabled, reads, baseline: trial.baseline,
        nonce: trial.nonce, restoredSelection: trial.restoreRead.result.text, restoredMode: incoming.enabled,
        ...grade(trial.nonce, reads.map(step => step.rawHex)) }];
}
