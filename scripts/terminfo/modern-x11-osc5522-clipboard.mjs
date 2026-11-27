import { randomBytes } from 'node:crypto';
import { isDeepStrictEqual as same } from 'node:util';
import { verifyKeyboardStep } from './modern-x11-keyboard.mjs';
import { before, need } from './modern-x11-mouse.mjs';
import { sameOwner, verifyPeer } from './modern-x11-osc52-write.mjs';

export const id = 'extensions.osc5522-clipboard';
export const query = '\x1b[?5522$p';
export const sentinel = '\x1b[c';
const bracketedQuery = '\x1b[?2004$p';
const osc52 = '\x1b]52;c;?\x07';
const st = '\x1b\\';
const osc = '\x1b]5522;';
const utf8 = 'text/plain;charset=utf-8';
const html = 'text/html';
const plain = 'text/plain';
const hex = value => Buffer.from(value).toString('hex');
const base64 = value => Buffer.from(value).toString('base64');
const text = value => Buffer.from(value, 'base64').toString('utf8');
const mode = (number, set) => '\x1b[?' + number + (set ? 'h' : 'l');
const report = (number, value) => '\x1b[?' + number + ';' + value + '$y';
const read = { kind: 'read' };

function fixture(nonce) {
    return { [utf8]: base64(nonce), [html]: base64('<b>' + nonce + '</b>') };
}

function verifiedFixture(values, phase) {
    const nonce = text(values[utf8]);
    need(new RegExp('^ntilde-5522-' + phase + '-[a-f0-9]{32}$').test(nonce)
        && same(values, fixture(nonce)), 'invalid synthetic clipboard values');
    return nonce;
}

function exchanges(step, key, write, previous) {
    const { arm, checkpoint, capture, dialog } = step;
    need([arm, checkpoint, capture].every(sample => sample.key === key)
        && checkpoint.writeHex === hex(write)
        && before(previous, arm.armed) && before(arm.armed, checkpoint.started)
        && before(checkpoint.started, checkpoint.finished) && before(checkpoint.finished, capture.sealed)
        && BigInt(checkpoint.finished) - BigInt(checkpoint.started) >= 200_000_000n,
    'unbound or incomplete OSC 5522 exchange ' + key);
    function retained(chunks, start, end) {
        need(chunks.every((chunk, index) => /^(?:[a-f0-9]{2})+$/.test(chunk.hex)
            && before(chunks[index - 1]?.at ?? start, chunk.at) && before(chunk.at, end)), 'stale OSC 5522 reply chunks');
        return Buffer.from(chunks.map(chunk => chunk.hex).join(''), 'hex').toString('latin1');
    }
    const early = retained(checkpoint.chunks, checkpoint.started, checkpoint.finished);
    const late = retained(capture.chunks, checkpoint.finished, capture.sealed);
    need(dialog || late === '', 'late or duplicate OSC 5522 exchange bytes');
    return { at: capture.sealed, raw: early + late, early, late };
}

function modeReply(sample) {
    const match = /^(\x1b\[\?5522;([0-4])\$y)(\x1b\[\?62;4;22c)$/.exec(sample.raw);
    need(match?.[0] === sample.raw, 'missing or malformed OSC 5522 DECRPM and independent DA1 fence');
    return Number(match[2]);
}

function checkedPeer(step, record, previous, values, owners, seed = false) {
    const at = verifyPeer(step, record, previous, 'mime');
    need(owners.includes(step.before.ownerPid) && same(step.result.values, values)
        && (seed ? step.after.ownerPid === step.process.pid : sameOwner(step.before, step.after)),
    'MIME seed or independent clipboard readback differs');
    return at;
}

function readRequest(requestId, token) {
    const name = base64('Ntilde disposable clipboard probe');
    return osc + 'type=read:id=' + requestId + ':name=' + name + ':pw=' + base64(token)
        + ';' + base64(plain + ' ' + html) + st;
}

const response = (status, id = '', metadata = '', payload = '') =>
    osc + 'type=read:status=' + status + (id ? ':id=' + id : '') + metadata + (payload ? ';' + payload : '') + st;
const frameData = (id, mime, value) => response('DATA', id, ':mime=' + base64(mime), value);

function notification(raw) {
    // Strict framing across the entire retained window excludes any ordinary or duplicate paste.
    const frames = raw.split(st);
    const ok = osc + 'type=read:status=OK:pw=';
    const data = osc + 'type=read:status=DATA:mime=Lg==;';
    need(frames.length === 4 && frames[0].startsWith(ok) && frames[1].startsWith(data)
        && frames[2] + st === response('DONE') && frames[3] === '',
    'expected exactly one MIME paste notification without ordinary paste bytes: ' + hex(raw));
    const encodedToken = frames[0].slice(ok.length);
    const encodedMimes = frames[1].slice(data.length);
    const token = text(encodedToken);
    const mimes = text(encodedMimes).split(' ');
    need(/^[0-9A-F]{64}$/.test(token) && base64(token) === encodedToken && base64(mimes.join(' ')) === encodedMimes
        && mimes.includes(plain) && mimes.includes(html) && new Set(mimes).size === mimes.length,
    'invalid MIME list or paste token');
    return { token, mimes };
}

function paste(step, phase, tag, previous, identity) {
    const action = verifyKeyboardStep(step, id + '/' + phase, { chord: 'ctrl+v', keys: ['Control_L', 'v'], tag }, previous, identity);
    need(BigInt(step.native.fence.started) - BigInt(step.native.events.at(-1).at) >= 1_000_000_000n,
        'paste capture ended before the app delivery window');
    return action;
}

function checkDismissal(step, exchange, identity) {
    const d = step.dialog;
    const owner = identity.window.id;
    need(d.title === 'Clipboard access' && Number.isSafeInteger(d.dialog) && d.dialog > 1 && d.dialog !== owner
        && d.owner === owner && d.appPid === identity.appPid && d.serverPid === identity.serverPid
        && d.focus.target === owner && d.focus.observed === owner && exchange.early === ''
        && before(step.checkpoint.finished, d.started) && before(d.started, d.sent) && before(d.sent, d.focus.requested)
        && before(d.focus.requested, d.focus.at) && before(d.focus.at, d.finished)
        && before(d.finished, exchange.at) && before(d.sent, step.capture.chunks[0]?.at),
    'token reuse did not require fresh explicit consent');
}

const modeSteps = [
    ['off-set', mode(5522, false), 0], ['off-query', query + sentinel, 2],
    ['on-set', mode(5522, true), 0], ['on-query', query + sentinel, 1],
    ['off-reset', mode(5522, false), 0], ['off-again', query + sentinel, 2],
];

function modeControl(trial, previous) {
    let at = previous;
    const controls = modeSteps.map(([key, write, value]) => {
        const result = exchanges(trial[key], key, write, at);
        at = result.at;
        need(value ? modeReply(result) === value : result.raw === '', 'OSC 5522 control differs for ' + key);
        return { key, writeHex: hex(write), replyHex: hex(result.raw) };
    });
    return { at, controls };
}

async function transact(trial, ops, key, write, dismiss = false) {
    trial[key] = {};
    await ops.exchange(trial[key], key, hex(write), dismiss);
}

async function prepare(trial, ops, phase, owners, restoring = false) {
    const values = phase === 'restore' ? trial.baseline : trial[phase];
    await ops.peer(trial[phase + 'Seed'], { kind: 'seed', values }, owners, restoring);
    await ops.peer(trial[phase + 'Read'], read, [trial[phase + 'Seed'].process.pid], restoring);
}

async function effects(trial, ops, identity) {
    await prepare(trial, ops, 'baseline', [null]);
    await prepare(trial, ops, 'paste', [trial.baselineSeed.process.pid]);
    await ops.action(trial.off, 'ctrl+v', id + '/off', 'zi');
    await transact(trial, ops, 'paste-enable', mode(5522, true) + query + sentinel);
    await ops.action(trial.on, 'ctrl+v', id + '/on', 'zj');
    const capture = paste(trial.on, 'on', 'zj', trial['paste-enable'].capture.sealed, identity);
    const { token } = notification(Buffer.from(capture.raw, 'hex').toString('latin1'));
    await prepare(trial, ops, 'later', [trial.pasteSeed.process.pid]);
    await transact(trial, ops, 'read', readRequest('capture', token));
    await transact(trial, ops, 'reuse', readRequest('reuse', token), true);
    await transact(trial, ops, 'osc52', osc52 + sentinel);
}

export async function observe(record, ops) {
    const trial = { id, off: {}, on: {} };
    for (const phase of ['baseline', 'paste', 'later']) {
        trial[phase] = fixture('ntilde-5522-' + phase + '-' + randomBytes(16).toString('hex'));
        trial[phase + 'Seed'] = {};
        trial[phase + 'Read'] = {};
    }
    trial.restoreSeed = {};
    trial.restoreRead = {};
    record.cases.push(trial);
    await transact(trial, ops, 'pinned', query + sentinel);
    const incoming = modeReply(exchanges(trial.pinned, 'pinned', query + sentinel, record.identity.admitted));
    need(incoming === 1 || incoming === 2, 'mode is unavailable for the independent paste experiment');
    await transact(trial, ops, 'bracketed', bracketedQuery);
    let failure;
    try {
        for (const [key, write] of modeSteps) await transact(trial, ops, key, write); // NOSONAR: sequenced writes on one PTY
        modeControl(trial, trial.bracketed.capture.sealed);
        await effects(trial, ops, record.identity);
    } catch (error) { failure = error; }
    try {
        await transact(trial, ops, 'restore', mode(5522, incoming === 1) + query + sentinel + bracketedQuery);
    } catch (error) { failure = failure ? new AggregateError([failure, error], 'clipboard run and mode restore failed') : error; }
    try {
        if (trial.baselineSeed.dispatched) {
            const owners = [null, ...['baselineSeed', 'pasteSeed', 'laterSeed'].map(key => trial[key].process?.pid)];
            await prepare(trial, ops, 'restore', owners, true);
        }
    } catch (error) { failure = failure ? new AggregateError([failure, error], 'clipboard run and seed restore failed') : error; }
    if (failure) throw failure;
    record.restore = {};
}

export const peerSteps = trial => ['baseline', 'paste', 'later', 'restore'].flatMap(phase =>
    [trial[phase + 'Seed'], trial[phase + 'Read']]);

function verifySeed(trial, record, phase, previous, prior) {
    const seed = trial[phase + 'Seed'], sample = trial[phase + 'Read'];
    const values = phase === 'restore' ? trial.baseline : trial[phase];
    const seeded = checkedPeer(seed, record, previous, values, [prior?.ownerPid ?? null], true);
    need((!prior || sameOwner(seed.before, prior)) && sameOwner(sample.before, seed.after), 'clipboard owner changed between peers');
    return checkedPeer(sample, record, seeded, values, [seed.process.pid]);
}

function verifyPasteEffect(trial, record, previous) {
    const { identity } = record;
    const baseline = verifySeed(trial, record, 'baseline', previous);
    const seeded = verifySeed(trial, record, 'paste', baseline, trial.baselineSeed.after);
    const off = paste(trial.off, 'off', 'zi', seeded, identity);
    const enabled = exchanges(trial['paste-enable'], 'paste-enable', mode(5522, true) + query + sentinel, off.sealed);
    need(modeReply(enabled) === 1, 'paste did not enter mode 5522');
    const on = paste(trial.on, 'on', 'zj', enabled.at, identity);
    const { token, mimes } = notification(Buffer.from(on.raw, 'hex').toString('latin1'));
    const changed = verifySeed(trial, record, 'later', on.sealed, trial.pasteSeed.after);
    const readback = exchanges(trial.read, 'read', readRequest('capture', token), changed);
    const data = [frameData('capture', plain, trial.paste[utf8]), frameData('capture', html, trial.paste[html])];
    need(off.raw === hex(text(trial.paste[utf8])) && [data, data.toReversed()].some(order =>
        readback.raw === response('OK', 'capture') + order.join('') + response('DONE', 'capture')),
    'ordinary paste control or captured values differ');
    const reused = exchanges(trial.reuse, 'reuse', readRequest('reuse', token), readback.at);
    checkDismissal(trial.reuse, reused, identity);
    need(reused.raw === response('EPERM', 'reuse')
        && BigInt(trial.reuse.checkpoint.started) - BigInt(trial.on.native.events[0].at) < 15_000_000_000n,
    'paste credential reuse was not denied inside its validity window');
    const legacy = exchanges(trial.osc52, 'osc52', osc52 + sentinel, reused.at);
    const da1 = '\x1b[?62;4;22c';
    need(['\x07', st].some(end => legacy.raw === '\x1b]52;c;' + end + da1), 'OSC 52 read did not return empty denial');
    return { at: legacy.at, offHex: off.raw, notificationHex: on.raw, mimes, readHex: hex(readback.raw),
        reuseHex: hex(reused.raw), osc52Hex: hex(legacy.raw) };
}

export function replay(record) {
    const { identity, cases, settings } = record;
    const [trial] = cases;
    need(cases.length === 1 && trial.id === id && identity.pane.kind === 'local' && identity.pane.isActive
        && settings.AllowOsc52ClipboardWrite === false
        && !Object.keys(settings).some(key => key.toLowerCase() === 'keybindings'), 'wrong X11 OSC 5522 settings or pane');
    for (const phase of ['baseline', 'paste', 'later']) verifiedFixture(trial[phase], phase);
    need(new Set(['baseline', 'paste', 'later'].map(phase => trial[phase][utf8])).size === 3, 'clipboard controls share the same seed');
    const incoming = exchanges(trial.pinned, 'pinned', query + sentinel, identity.admitted);
    const incomingMode = modeReply(incoming);
    need(incomingMode === 2, 'direct disposable pane did not start with mode 5522 off');
    const bracketed = exchanges(trial.bracketed, 'bracketed', bracketedQuery, incoming.at);
    need(bracketed.raw === report(2004, 2), 'bracketed paste was not off at entry');
    const controls = modeControl(trial, bracketed.at);
    const { at, ...effect } = verifyPasteEffect(trial, record, controls.at);
    const restore = exchanges(trial.restore, 'restore', mode(5522, false) + query + sentinel + bracketedQuery, at);
    need(restore.raw === incoming.raw + bracketed.raw, 'mode readback did not restore the incoming state');
    const restored = verifySeed(trial, record, 'restore', restore.at, trial.laterSeed.after);
    const peers = peerSteps(trial).map(step => step.process.pid);
    need(new Set([...peers, record.coordinatorPid, identity.serverPid, identity.appPid, record.lineage.child.pid]).size === peers.length + 4,
        'GTK peers are not independent of each other and the App');
    need(record.restore.paneId === identity.pane.paneId && record.restore.marker === identity.marker
        && before(restored, record.terminal.at) && same(record.terminal.original, record.terminal.restored),
    'owned pane or raw PTY was not restored');
    return [{ id, pinnedWriteHex: trial.pinned.checkpoint.writeHex, pinnedRawHex: hex(incoming.raw),
        controls: controls.controls, restoreHex: hex(restore.raw), effect: { ...effect, outcome: 'supported' } }];
}
