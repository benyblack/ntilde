import { randomBytes, randomUUID } from 'node:crypto';
import { isDeepStrictEqual as same } from 'node:util';
import { before, need } from './modern-x11-mouse.mjs';
import { budget, reading, sameOwner, seed } from './modern-x11-osc52-write.mjs';

export const id = 'extensions.osc52-read';
export const queryHex = '1b5d35323b633b3f07';
export const uuidPattern = /^[a-f\d]{8}-[a-f\d]{4}-4[a-f\d]{3}-[89ab][a-f\d]{3}-[a-f\d]{12}$/;
const baselinePattern = /^ntilde-osc52-baseline-[a-f\d]{32}$/;
const frame = /^\x1b\]52;c;([A-Za-z0-9+/=]*)(?:\x07|\x1b\\)$/;
const read = { kind: 'read' };
const hex = text => Buffer.from(text, 'latin1').toString('hex');

function queryPlan(marker) {
    return { writeHex: queryHex + hex('\x1b[6n'), cprHex: hex('\x1b[1;' + (marker.length + 1) + 'R'), fullWindow: true };
}

export function peerSteps(trial) {
    return [trial.seed, trial.baselineRead, trial.querySeed, trial.beforeRead, trial.afterRead, trial.restoreSeed, trial.restoreRead];
}

function fixtureRead(step, record, previous, source, text) {
    const checked = reading(step, record, previous, [source.process.pid]);
    need(sameOwner(step.before, source.after) && step.result.text === text, 'clipboard readback value or owner differs from the seed');
    return checked;
}

function classify(rawHex, cprHex, trial) {
    const raw = Buffer.from(rawHex, 'hex').toString('latin1');
    const cpr = Buffer.from(cprHex, 'hex').toString('latin1');
    const parts = raw.split(cpr);
    if (parts.length !== 2) return { kind: 'delivery-failure' };

    // Do not splice a frame around the fence. Keep extraneous and duplicate bytes visible.
    const reply = parts.join('');
    const replyHex = hex(reply);
    if (!reply) return { kind: 'silence', replyHex };
    const match = frame.exec(reply);
    if ((parts[0] && parts[1]) || !match || match[0] !== reply) return { kind: 'malformed', replyHex };
    const encoded = match[1];
    const decoded = Buffer.from(encoded, 'base64');
    if (decoded.toString('base64') !== encoded) return { kind: 'malformed', replyHex };
    if (!encoded) return { kind: 'empty-denial', replyHex };
    if (decoded.equals(Buffer.from(trial.nonce))) return { kind: 'seeded-nonce', replyHex };
    if (decoded.equals(Buffer.from(trial.baseline))) return { kind: 'stale-data', replyHex };
    return { kind: 'other-value', replyHex };
}

function verifyExchange(record, trial, previous) {
    const { before: incoming, after, exchange } = trial.query;
    const owner = trial.querySeed.after;
    need([incoming, after].every(state =>
        ['selection', 'peerPid', 'peerWindow', 'serverPid', 'appPid', 'windowId', 'focus'].every(key => state[key] === owner[key])
        && sameOwner(state, owner)), 'clipboard ownership, focus or identity changed in the query window');

    const { rawHex, lastReadAt, started, finished, writeHex } = exchange;
    const plan = queryPlan(record.identity.marker);
    need(writeHex === plan.writeHex && typeof rawHex === 'string' && /^(?:[a-f\d]{2}){0,1024}$/.test(rawHex)
        && before(previous, incoming.at) && before(incoming.at, started)
        && before(started, finished) && before(finished, after.at), 'stale or incomplete OSC 52 read window');
    need(rawHex ? before(started, lastReadAt) && before(lastReadAt, finished) : lastReadAt === null,
        'missing or stale OSC 52 reply timestamp');
    const deadline = BigInt(started) + budget;
    need(BigInt(finished) >= deadline, 'OSC 52 read window closed before the capture deadline');
    const result = classify(rawHex, plan.cprHex, trial);
    const kind = lastReadAt && BigInt(lastReadAt) >= deadline ? 'late-reply' : result.kind;
    return { ...result, kind, queryHex, writeHex, rawHex, cprHex: plan.cprHex };
}

async function observeQuery(record, trial, ops) {
    const baselineSet = seed(trial.seed, record, record.identity.admitted, trial.baseline, [null]);
    await ops.peer(trial.baselineRead, read, [trial.seed.process.pid]);
    const baselineRead = fixtureRead(trial.baselineRead, record, baselineSet, trial.seed, trial.baseline);

    await ops.peer(trial.querySeed, { kind: 'seed', text: trial.nonce }, [trial.seed.process.pid]);
    const seeded = seed(trial.querySeed, record, baselineRead, trial.nonce, [trial.seed.process.pid]);
    await ops.peer(trial.beforeRead, read, [trial.querySeed.process.pid]);
    fixtureRead(trial.beforeRead, record, seeded, trial.querySeed, trial.nonce);

    await ops.query(trial.query, trial.querySeed.after, queryPlan(record.identity.marker));
    await ops.peer(trial.afterRead, read, [trial.querySeed.process.pid]);
    fixtureRead(trial.afterRead, record, trial.query.after.at, trial.querySeed, trial.nonce);
}

export async function observeClipboard(record, ops) {
    const trial = {
        id, baseline: 'ntilde-osc52-baseline-' + randomBytes(16).toString('hex'), uuid: randomUUID(),
        seed: {}, baselineRead: {}, querySeed: {}, beforeRead: {}, query: {}, afterRead: {}, restoreSeed: {}, restoreRead: {},
    };
    trial.nonce = 'terminfo-osc52-' + trial.uuid;
    record.cases.push(trial);

    const baseline = { kind: 'seed', text: trial.baseline };
    let failure;
    try {
        await ops.peer(trial.seed, baseline, [null]);
        await observeQuery(record, trial, ops);
    } catch (error) { failure = error; }
    if (!trial.seed.dispatched) throw failure;

    try {
        const owners = [null, trial.seed.process.pid];
        if (trial.querySeed.process) owners.push(trial.querySeed.process.pid);
        await ops.peer(trial.restoreSeed, baseline, owners, true);
        await ops.peer(trial.restoreRead, read, [trial.restoreSeed.process.pid], true);
        fixtureRead(trial.restoreRead, record, trial.restoreSeed.after.at, trial.restoreSeed, trial.baseline);
    } catch (error) {
        throw failure ? new AggregateError([failure, error], 'OSC 52 read and clipboard restoration failed') : error;
    }
    if (failure) throw failure;
    record.restore = {};
}

export function replayClipboard(record) {
    const { identity, cases, restore, terminal } = record;
    need(identity?.pane?.kind === 'local' && identity.pane.isActive === true && cases?.length === 1
        && cases[0].id === id, 'expected one direct-local OSC 52 read trial');
    const [trial] = cases;
    need(baselinePattern.test(trial.baseline) && uuidPattern.test(trial.uuid)
        && trial.nonce === 'terminfo-osc52-' + trial.uuid, 'wrong synthetic OSC 52 fixtures');
    const peers = peerSteps(trial).map(step => step.process.pid);
    need(new Set([...peers, record.coordinatorPid, identity.serverPid, identity.appPid, record.lineage.child.pid]).size === peers.length + 4,
        'clipboard peers are not independent processes');

    const baselineSet = seed(trial.seed, record, identity.admitted, trial.baseline, [null]);
    const baselineRead = fixtureRead(trial.baselineRead, record, baselineSet, trial.seed, trial.baseline);
    const seeded = seed(trial.querySeed, record, baselineRead, trial.nonce, [trial.seed.process.pid]);
    need(sameOwner(trial.querySeed.before, trial.seed.after), 'baseline owner changed before the independent seed');
    const preQuery = fixtureRead(trial.beforeRead, record, seeded, trial.querySeed, trial.nonce);
    const exchange = verifyExchange(record, trial, preQuery);
    const postQuery = fixtureRead(trial.afterRead, record, trial.query.after.at, trial.querySeed, trial.nonce);

    const restored = seed(trial.restoreSeed, record, postQuery, trial.baseline, [trial.querySeed.process.pid]);
    need(sameOwner(trial.restoreSeed.before, trial.querySeed.after), 'seed owner changed before restoration');
    const verified = fixtureRead(trial.restoreRead, record, restored, trial.restoreSeed, trial.baseline);
    need(restore?.paneId === identity.pane.paneId && restore.marker === identity.marker
        && terminal?.original?.length === 7 && same(terminal.original, terminal.restored)
        && before(verified, terminal.at), 'unverified pane or kernel terminal restoration');

    return [{ id, exchange, baseline: trial.baselineRead.result.text, before: trial.beforeRead.result.text,
        after: trial.afterRead.result.text, restored: trial.restoreRead.result.text }];
}
