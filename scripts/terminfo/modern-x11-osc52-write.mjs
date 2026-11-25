import { randomBytes } from 'node:crypto';
import { setTimeout as delay } from 'node:timers/promises';
import { isDeepStrictEqual as same } from 'node:util';
import { verifyKeyboardStep } from './modern-x11-keyboard.mjs';
import { before, exchange, need } from './modern-x11-mouse.mjs';

export const id = 'extensions.osc52-write';
export const budget = 1_000_000_000n;
export const pollMs = 25;
const baselinePattern = /^ntilde-osc52-baseline-[a-f\d]{32}$/;
const noncePattern = /^ntilde-osc52-probe-[a-f\d]{32}$/;
const keyPlan = { chord: 'a', keys: ['a'], tag: 'zh' };
const hex = text => Buffer.from(text, 'ascii').toString('hex');
const now = () => process.hrtime.bigint().toString();

function writePlan(nonce, marker) {
    const osc52Hex = hex('\x1b]52;c;' + Buffer.from(nonce, 'ascii').toString('base64') + '\x07');
    return { osc52Hex, writeHex: osc52Hex + hex('\x1b[6n'), replyHex: hex('\x1b[1;' + (marker.length + 1) + 'R') };
}

function verifyPeer(step, record, previous) {
    const { process, before: incoming, result, after } = step;
    const { identity, settings, coordinatorPid, uid, python } = record;
    const scratch = settings.Profiles[0].StartingDirectory;
    need(process.platform === 'linux' && process.executable === python && process.ppid === coordinatorPid
        && process.uids.length === 4 && process.uids.every(value => value === uid)
        && same(process.argv.slice(0, 6), [python, '-E', '-s', '-B', scratch + '/sources/peer-linux.py', 'text'])
        && process.argv.length === 7 && /^\d+$/.test(process.argv[6].slice((scratch + '/gtk-').length))
        && process.argv[6].startsWith(scratch + '/gtk-'), 'wrong retained GTK peer process');
    need([incoming, after].every(state => state.selection === 'CLIPBOARD' && state.peerPid === process.pid
        && state.serverPid === identity.serverPid && state.appPid === identity.appPid
        && state.windowId === identity.window.id && state.focus === identity.window.id
        && Number.isSafeInteger(state.peerWindow) && state.peerWindow > 1 && state.peerWindow !== identity.window.id
        && Number.isSafeInteger(state.ownerWindow)
        && (state.ownerPid === null ? state.ownerWindow === 0 : state.ownerWindow > 1))
        && incoming.peerWindow === after.peerWindow, 'wrong retained X11 clipboard authority');
    need(typeof result?.text === 'string' && !result.error && before(previous, incoming.at)
        && before(incoming.at, step.dispatched) && before(step.dispatched, result.started) && before(result.started, result.finished)
        && before(result.finished, after.at), 'stale or incomplete GTK clipboard reading');
    return after.at;
}

export function reading(step, record, previous, owners) {
    const at = verifyPeer(step, record, previous);
    need(owners.includes(step.before.ownerPid) && step.after.ownerPid === step.before.ownerPid
        && step.after.ownerWindow === step.before.ownerWindow, 'unattributed CLIPBOARD read');
    return at;
}

export function seed(step, record, previous, baseline, owners) {
    const at = verifyPeer(step, record, previous);
    need(owners.includes(step.before.ownerPid) && step.result.text === baseline
        && step.after.ownerPid === step.process.pid, 'unverified CLIPBOARD seed');
    return at;
}

function delivery(record, trial, previous) {
    const result = verifyKeyboardStep(trial.key, trial.id, keyPlan, previous, record.identity);
    need(result.raw === '61', 'XTEST a did not reach the armed child as 61');
    return result;
}

export async function observeClipboard(record, ops, roundTrip) {
    const baseline = 'ntilde-osc52-baseline-' + randomBytes(16).toString('hex');
    const nonce = 'ntilde-osc52-probe-' + randomBytes(16).toString('hex');
    const trial = { id: roundTrip?.id ?? id, baseline, nonce, seed: {}, baselineRead: {}, key: {}, reads: [], restoreSeed: {}, restoreRead: {} };
    record.cases.push(trial);
    const request = { kind: 'seed', text: baseline };
    const read = { kind: 'read' };
    let failure;
    try {
        await ops.peer(trial.seed, request, [null]);
        const seeded = seed(trial.seed, record, record.identity.admitted, baseline, [null]);
        const seedPid = trial.seed.process.pid;
        await ops.peer(trial.baselineRead, read, [seedPid]);
        const admitted = reading(trial.baselineRead, record, seeded, [seedPid]);
        need(trial.baselineRead.result.text === baseline, 'the independent peer did not read back the baseline');
        await ops.key(trial.key);
        delivery(record, trial, admitted);
        trial.write = await ops.write(writePlan(nonce, record.identity.marker));
        await pollTarget(record, trial, ops.peer, seedPid);
        if (roundTrip && grade(trial, record.identity).conclusion === 'pass') {
            await roundTrip.observeRead(record, trial, ops);
        }
    } catch (error) { failure = error; }
    if (!trial.seed.dispatched) throw failure;
    try {
        const restoringAt = now();
        const restoreOwners = [null, ...restoreOwnerPids(record, trial)];
        await ops.peer(trial.restoreSeed, request, restoreOwners, true);
        await ops.peer(trial.restoreRead, read, [trial.restoreSeed.process.pid], true);
        verifyRestoration(trial, record, restoringAt, restoreOwners);
    } catch (error) {
        throw failure ? new AggregateError([failure, error], 'clipboard observation and restoration failed') : error;
    }
    if (failure) throw failure;
    record.restore = {};
}

export function sameOwner(left, right) {
    return left.ownerPid === right.ownerPid && left.ownerWindow === right.ownerWindow;
}

function targetReading(step, record, trial, previous, appOwner) {
    const at = verifyPeer(step, record, previous);
    const fromSeed = sameOwner(step.before, trial.seed.after);
    const fromApp = step.before.ownerPid === record.identity.appPid;
    const transitioning = fromSeed && step.after.ownerPid === record.identity.appPid;
    need((fromSeed || fromApp) && (!appOwner || sameOwner(step.before, appOwner))
        && (sameOwner(step.before, step.after) || transitioning), 'unattributed CLIPBOARD read');
    return { at, transitioning, unchanged: fromSeed && !transitioning && step.result.text === trial.baseline };
}

function pollingRequired(sample, classification, pollStarted) {
    return classification.transitioning || (classification.unchanged
        && BigInt(sample.result.started) - BigInt(pollStarted) < budget);
}

async function pollTarget(record, trial, peer, seedPid) {
    trial.pollStarted = now();
    let at = trial.pollStarted, appOwner;
    for (let index = 0; index < 64; index++) {
        const sample = {};
        trial.reads.push(sample);
        const owners = appOwner ? [record.identity.appPid] : [seedPid, record.identity.appPid];
        await peer(sample, { kind: 'read' }, owners); // NOSONAR: serialized reads of one X11 selection.
        const classification = targetReading(sample, record, trial, at, appOwner);
        if (!pollingRequired(sample, classification, trial.pollStarted)) return;
        at = classification.at;
        if (classification.transitioning) appOwner = sample.after;
        await delay(pollMs); // NOSONAR: wait between serial clipboard reads.
    }
    throw new Error('clipboard polling exhausted its observation limit');
}

function verifyTarget(trial, record, plan, previous) {
    const processed = exchange(trial.write, plan, previous);
    need(before(processed, trial.pollStarted) && trial.reads.length > 0 && trial.reads.length <= 64,
        'missing post-CPR clipboard observations');
    let at = trial.pollStarted, appOwner;
    for (const [index, sample] of trial.reads.entries()) {
        const classification = targetReading(sample, record, trial, at, appOwner);
        at = classification.at;
        if (classification.transitioning) appOwner = sample.after;
        const moreSamples = index < trial.reads.length - 1;
        need(moreSamples === pollingRequired(sample, classification, trial.pollStarted), 'incomplete or extra settled CLIPBOARD reads');
    }
    return at;
}

function grade(trial, identity) {
    const sample = trial.reads.at(-1);
    if (sample.result.text === trial.nonce && sample.after.ownerPid === identity.appPid) {
        return { status: 'supported', conclusion: 'pass', reason: 'The independent GTK peer read the exact nonce from the App-owned X11 CLIPBOARD.' };
    }
    if (sameOwner(sample.after, trial.seed.after) && sample.result.text === trial.baseline
        && BigInt(sample.result.started) - BigInt(trial.pollStarted) >= budget) {
        return { status: 'unsupported', conclusion: 'fail', reason: 'The original baseline owner and text held through the post-CPR deadline.' };
    }
    return { status: 'probe-invalid', conclusion: 'inconclusive', reason: 'The clipboard readback did not establish either frozen App outcome.' };
}

function verifyRestoration(trial, record, previous, owners) {
    const restored = seed(trial.restoreSeed, record, previous, trial.baseline, owners);
    const verified = reading(trial.restoreRead, record, restored, [trial.restoreSeed.process.pid]);
    need(trial.restoreRead.result.text === trial.baseline && trial.restoreRead.after.ownerWindow === trial.restoreSeed.after.ownerWindow,
        'baseline restoration did not read back from the restoring peer');
    return verified;
}

function restoreOwnerPids(record, trial) {
    const owners = [trial.seed.process?.pid, record.identity.appPid];
    if (trial.querySeed?.process) owners.push(trial.querySeed.process.pid);
    return owners;
}

export function clipboardPeerSteps(trial) {
    const steps = [trial.seed, trial.baselineRead, ...trial.reads];
    if (trial.querySeed) steps.push(trial.querySeed, trial.querySeedRead);
    return [...steps, trial.restoreSeed, trial.restoreRead];
}

export function replayClipboard(record, roundTrip) {
    const { identity, cases, restore, terminal } = record;
    need(identity?.pane?.kind === 'local' && identity.pane.isActive === true && cases?.length === 1
        && cases[0].id === (roundTrip?.id ?? id), 'expected one direct-local clipboard trial');
    const [trial] = cases;
    need(baselinePattern.test(trial.baseline) && noncePattern.test(trial.nonce), 'wrong synthetic clipboard fixtures');
    const seedPid = trial.seed.process.pid;
    const seeded = seed(trial.seed, record, identity.admitted, trial.baseline, [null]);
    const checked = reading(trial.baselineRead, record, seeded, [seedPid]);
    need(trial.baselineRead.result.text === trial.baseline && trial.baselineRead.after.ownerWindow === trial.seed.after.ownerWindow,
        'baseline did not read back from the original owner');
    const key = delivery(record, trial, checked);
    const plan = writePlan(trial.nonce, identity.marker);
    const written = verifyTarget(trial, record, plan, key.sealed);
    const write = grade(trial, identity);
    const { observedAt, read } = roundTrip?.replayRead(record, trial, write, written) ?? { observedAt: written };
    const verified = verifyRestoration(trial, record, observedAt, restoreOwnerPids(record, trial));
    need(restore?.paneId === identity.pane.paneId && restore.marker === identity.marker
        && terminal?.original?.length === 7 && same(terminal.original, terminal.restored)
        && before(verified, terminal.at), 'unverified pane or kernel terminal restoration');
    const peers = clipboardPeerSteps(trial).map(step => step.process.pid);
    need(new Set([...peers, record.coordinatorPid, identity.serverPid, identity.appPid, record.lineage.child.pid]).size === peers.length + 4,
        'clipboard peers are not independent processes');
    const result = { id: trial.id, ...write, osc52Hex: plan.osc52Hex, keyHex: key.raw,
        cprHex: trial.write.replyHex, baseline: trial.baselineRead.result.text, nonce: trial.nonce,
        reads: trial.reads.map(({ result, after }) => ({ text: result.text, ownerPid: after.ownerPid })),
        restored: trial.restoreRead.result.text };
    return [roundTrip ? roundTrip.combine(result, read) : result];
}
