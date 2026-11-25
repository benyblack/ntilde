import { randomBytes } from 'node:crypto';
import { setTimeout as delay } from 'node:timers/promises';
import { isDeepStrictEqual as same } from 'node:util';
import { verifyKeyboardStep } from './modern-x11-keyboard.mjs';
import { before, exchange, need } from './modern-x11-mouse.mjs';

export const id = 'extensions.osc52-write';
const budget = 1_000_000_000n;
const pollMs = 25;
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

function reading(step, record, previous, owners) {
    const at = verifyPeer(step, record, previous);
    need(owners.includes(step.before.ownerPid) && step.after.ownerPid === step.before.ownerPid
        && step.after.ownerWindow === step.before.ownerWindow, 'unattributed CLIPBOARD read');
    return at;
}

function seed(step, record, previous, baseline, owners) {
    const at = verifyPeer(step, record, previous);
    need(owners.includes(step.before.ownerPid) && step.result.text === baseline
        && step.after.ownerPid === step.process.pid, 'unverified CLIPBOARD seed');
    return at;
}

function delivery(record, step, previous) {
    const result = verifyKeyboardStep(step, id, keyPlan, previous, record.identity);
    need(result.raw === '61', 'XTEST a did not reach the armed child as 61');
    return result;
}

export async function observeClipboard(record, ops) {
    const baseline = 'ntilde-osc52-baseline-' + randomBytes(16).toString('hex');
    const nonce = 'ntilde-osc52-probe-' + randomBytes(16).toString('hex');
    const trial = { id, baseline, nonce, seed: {}, baselineRead: {}, key: {}, reads: [], restoreSeed: {}, restoreRead: {} };
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
        delivery(record, trial.key, admitted);
        trial.write = await ops.write(writePlan(nonce, record.identity.marker));
        trial.pollStarted = now();
        for (;;) {
            const sample = {};
            trial.reads.push(sample);
            await ops.peer(sample, read, [seedPid, record.identity.appPid]); // NOSONAR: serialized reads of one X11 selection.
            if (sample.result.text !== baseline || BigInt(sample.result.started) - BigInt(trial.pollStarted) >= budget) break;
            await delay(pollMs); // NOSONAR: wait between serial clipboard reads.
        }
    } catch (error) { failure = error; }
    if (!trial.seed.dispatched) throw failure;
    try {
        const restoringAt = now();
        const restoreOwners = [null, trial.seed.process?.pid, record.identity.appPid];
        await ops.peer(trial.restoreSeed, request, restoreOwners, true);
        await ops.peer(trial.restoreRead, read, [trial.restoreSeed.process.pid], true);
        verifyRestoration(trial, record, restoringAt, restoreOwners);
    } catch (error) {
        throw failure ? new AggregateError([failure, error], 'clipboard observation and restoration failed') : error;
    }
    if (failure) throw failure;
    record.restore = {};
}

function verifyTarget(trial, record, plan, previous, owners) {
    const processed = exchange(trial.write, plan, previous);
    need(before(processed, trial.pollStarted) && trial.reads.length > 0 && trial.reads.length <= 64,
        'missing post-CPR clipboard observations');
    const seedPid = trial.seed.process.pid;
    let at = trial.pollStarted;
    for (const [index, sample] of trial.reads.entries()) {
        at = reading(sample, record, at, owners);
        const unchanged = sample.result.text === trial.baseline;
        if (unchanged) need(sample.after.ownerPid === seedPid && sample.after.ownerWindow === trial.seed.after.ownerWindow,
            'clipboard baseline came from a different owner');
        if (index < trial.reads.length - 1) need(unchanged
            && BigInt(sample.result.started) - BigInt(trial.pollStarted) < budget, 'clipboard polling continued after a decisive sample');
    }
    return at;
}

function grade(trial, identity) {
    const sample = trial.reads.at(-1);
    if (sample.result.text === trial.nonce && sample.after.ownerPid === identity.appPid) {
        return { status: 'supported', conclusion: 'pass', reason: 'The independent GTK peer read the exact nonce from the App-owned X11 CLIPBOARD.' };
    }
    if (sample.result.text === trial.baseline && BigInt(sample.result.started) - BigInt(trial.pollStarted) >= budget) {
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

export function replayClipboard(record) {
    const { identity, cases, restore, terminal } = record;
    need(identity?.pane?.kind === 'local' && identity.pane.isActive === true && cases?.length === 1
        && cases[0].id === id, 'expected one direct-local clipboard trial');
    const [trial] = cases;
    need(baselinePattern.test(trial.baseline) && noncePattern.test(trial.nonce), 'wrong synthetic clipboard fixtures');
    const seedPid = trial.seed.process.pid;
    const seeded = seed(trial.seed, record, identity.admitted, trial.baseline, [null]);
    const checked = reading(trial.baselineRead, record, seeded, [seedPid]);
    need(trial.baselineRead.result.text === trial.baseline && trial.baselineRead.after.ownerWindow === trial.seed.after.ownerWindow,
        'baseline did not read back from the original owner');
    const key = delivery(record, trial.key, checked);
    const plan = writePlan(trial.nonce, identity.marker);
    const owners = [seedPid, identity.appPid];
    const observed = verifyTarget(trial, record, plan, key.sealed, owners);
    const verified = verifyRestoration(trial, record, observed, owners);
    need(restore?.paneId === identity.pane.paneId && restore.marker === identity.marker
        && terminal?.original?.length === 7 && same(terminal.original, terminal.restored)
        && before(verified, terminal.at), 'unverified pane or kernel terminal restoration');
    const peers = [trial.seed, trial.baselineRead, ...trial.reads, trial.restoreSeed, trial.restoreRead].map(step => step.process.pid);
    need(new Set([...peers, record.coordinatorPid, identity.serverPid, identity.appPid, record.lineage.child.pid]).size === peers.length + 4,
        'clipboard peers are not independent processes');
    return [{ id, ...grade(trial, identity), osc52Hex: plan.osc52Hex, keyHex: key.raw,
        cprHex: trial.write.replyHex, baseline: trial.baselineRead.result.text, nonce: trial.nonce,
        reads: trial.reads.map(({ result, after }) => ({ text: result.text, ownerPid: after.ownerPid })),
        restored: trial.restoreRead.result.text }];
}
