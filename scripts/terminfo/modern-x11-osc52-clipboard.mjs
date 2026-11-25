import { randomBytes } from 'node:crypto';
import { setTimeout as delay } from 'node:timers/promises';
import { before, need } from './modern-x11-mouse.mjs';
import {
    budget, observeClipboard as observeWriteFixture, pollMs, reading, replayClipboard as replayWriteFixture, sameOwner, seed,
} from './modern-x11-osc52-write.mjs';

export const id = 'extensions.osc52-clipboard';
const seedPattern = /^ntilde-osc52-seed-[a-f\d]{32}$/;
const osc52Reply = /^\x1b\]52;c;([A-Za-z0-9+/=]*)(?:\x07|\x1b\\)$/;
const queryHex = '1b5d35323b633b3f07';
const now = () => process.hrtime.bigint().toString();
const hex = value => Buffer.from(value, 'ascii').toString('hex');

function queryPlan(marker) {
    return { writeHex: queryHex + hex('\x1b[6n'), cprHex: hex('\x1b[1;' + (marker.length + 1) + 'R') };
}

function verifySeed(record, trial, previous) {
    need(seedPattern.test(trial.queryNonce), 'wrong synthetic read nonce');
    const writeOwner = trial.reads.at(-1).after;
    const seeded = seed(trial.querySeed, record, previous, trial.queryNonce, [record.identity.appPid]);
    need(sameOwner(trial.querySeed.before, writeOwner), 'OSC 52 write owner changed before the independent seed');
    const checked = reading(trial.querySeedRead, record, seeded, [trial.querySeed.process.pid]);
    need(sameOwner(trial.querySeedRead.after, trial.querySeed.after)
        && trial.querySeedRead.result.text === trial.queryNonce, 'independent read nonce or owner did not read back');
    return checked;
}

function classifyReply(rawHex, plan, trial, elapsed) {
    const raw = Buffer.from(rawHex, 'hex').toString('latin1');
    const cpr = Buffer.from(plan.cprHex, 'hex').toString('latin1');
    const parts = raw.split(cpr);
    if (parts.length !== 2) return { outcome: 'unfenced' };
    // A complete OSC frame can arrive on either side of the CPR, in its own bytes.
    const reply = parts.join('');
    const replyHex = Buffer.from(reply, 'latin1').toString('hex');
    if (!replyHex && elapsed >= budget) return { outcome: 'silence', replyHex };
    const match = parts[0] && parts[1] ? null : osc52Reply.exec(reply);
    if (!match || match[0].length * 2 !== replyHex.length) return { outcome: 'invalid-reply', replyHex };
    const encoded = match[1], decoded = Buffer.from(encoded, 'base64');
    if (decoded.toString('base64') !== encoded) return { outcome: 'invalid-reply', replyHex };
    if (!encoded) return { outcome: 'empty-denial', replyHex };
    if (decoded.equals(Buffer.from(trial.nonce))) return { outcome: 'own-write', replyHex };
    if (decoded.equals(Buffer.from(trial.queryNonce))) return { outcome: 'seeded-nonce', replyHex };
    return { outcome: 'other-value', replyHex };
}

function verifyQuery(sample, trial, plan, previous) {
    const { before: incoming, after, exchange } = sample;
    const owner = trial.querySeed.after;
    need([incoming, after].every(state =>
        ['selection', 'peerPid', 'peerWindow', 'serverPid', 'appPid', 'windowId', 'focus'].every(key => state[key] === owner[key])
        && sameOwner(state, owner)), 'independent seed lost X11 ownership or focus during the OSC 52 query');
    need(exchange.writeHex === plan.writeHex && /^(?:[a-f\d]{2}){0,1024}$/.test(exchange.rawHex)
        && before(previous, incoming.at) && before(incoming.at, exchange.started)
        && before(exchange.started, exchange.finished) && before(exchange.finished, after.at)
        && before(after.at, sample.decidedAt), 'stale or incomplete OSC 52 query window');
    const elapsed = BigInt(exchange.finished) - BigInt(exchange.started);
    return { rawHex: exchange.rawHex, ...classifyReply(exchange.rawHex, plan, trial, elapsed) };
}

function retryRequired(outcome, startedAt, decidedAt) {
    return outcome === 'own-write' && BigInt(decidedAt) - BigInt(startedAt) < budget;
}

async function observeRead(record, trial, ops) {
    trial.queryNonce = 'ntilde-osc52-seed-' + randomBytes(16).toString('hex');
    trial.querySeed = {};
    trial.querySeedRead = {};
    await ops.peer(trial.querySeed, { kind: 'seed', text: trial.queryNonce }, [record.identity.appPid]);
    await ops.peer(trial.querySeedRead, { kind: 'read' }, [trial.querySeed.process.pid]);
    const checked = verifySeed(record, trial, trial.reads.at(-1).after.at);
    const plan = queryPlan(record.identity.marker);
    trial.queries = [];
    trial.queryStarted = now();
    need(before(checked, trial.queryStarted), 'query started before verifying the seed');
    let previous = trial.queryStarted;
    for (;;) {
        const sample = {};
        trial.queries.push(sample);
        await ops.query(sample, trial.querySeed.after, plan); // NOSONAR: each reply decides whether to issue another request.
        sample.decidedAt = now();
        const { outcome } = verifyQuery(sample, trial, plan, previous);
        if (!retryRequired(outcome, trial.queryStarted, sample.decidedAt)) return;
        previous = sample.decidedAt;
        await delay(pollMs); // NOSONAR: serialize retries of the same clipboard query.
    }
}

function replayRead(record, trial, write, written) {
    if (write.conclusion !== 'pass') {
        need(trial.querySeed === undefined && trial.queries === undefined, 'query followed an unverified OSC 52 write');
        return { observedAt: written, read: { outcome: 'not-run' } };
    }
    const checked = verifySeed(record, trial, written);
    need(before(checked, trial.queryStarted) && Array.isArray(trial.queries)
        && trial.queries.length > 0 && trial.queries.length <= 41, 'missing or excessive OSC 52 query attempts');
    const plan = queryPlan(record.identity.marker);
    let previous = trial.queryStarted;
    const attempts = trial.queries.map((sample, index) => {
        const result = verifyQuery(sample, trial, plan, previous);
        const retry = retryRequired(result.outcome, trial.queryStarted, sample.decidedAt);
        need(retry === (index + 1 < trial.queries.length), 'missing or extra OSC 52 stale-nonce retry');
        if (index) need(BigInt(sample.exchange.started) - BigInt(previous) >= BigInt(pollMs) * 1_000_000n,
            'OSC 52 retry ran before the poll interval');
        previous = sample.decidedAt;
        return result;
    });
    return { observedAt: previous, read: { outcome: attempts.at(-1).outcome, nonce: trial.queryNonce,
        ownerPid: trial.querySeed.process.pid, queryHex, cprHex: plan.cprHex, attempts } };
}

const readReasons = {
    'not-run': 'The OSC 52 write could not be verified, so the read leg was not run.',
    'empty-denial': 'OSC 52 c returned a complete empty-payload denial frame. The independently seeded nonce was absent from the reply.',
    'own-write': 'OSC 52 c still returned the earlier OSC 52 write nonce when the retry deadline elapsed.',
    'other-value': 'OSC 52 c replied with a value other than the independently seeded nonce.',
    silence: 'The cursor-position control answered, and OSC 52 c returned no bytes within the query window.',
    'invalid-reply': 'OSC 52 c did not return one complete, canonical reply.',
    unfenced: 'The OSC 52 query stream lacked a unique cursor-position control.',
    'seeded-nonce': 'The independent GTK peer verified the OSC 52 write, and OSC 52 c returned the independently seeded nonce.',
};

const roundTrip = {
    id, observeRead, replayRead,
    combine({ conclusion: writeConclusion, status: writeStatus, reason: writeReason, ...fixture }, read) {
        const pass = read.outcome === 'seeded-nonce';
        return { ...fixture, status: pass ? 'supported' : 'inconclusive', conclusion: pass ? 'pass' : 'inconclusive',
            reason: readReasons[read.outcome], write: { conclusion: writeConclusion, status: writeStatus, reason: writeReason }, read };
    },
};

export const observeClipboard = (record, ops) => observeWriteFixture(record, ops, roundTrip);
export const replayClipboard = record => replayWriteFixture(record, roundTrip);
