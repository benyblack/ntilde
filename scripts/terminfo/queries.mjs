// Exact queries use independently retained bytes and exposed state; parent process proofs remain mandatory.
import { createHash } from 'node:crypto';
import { inputPriorCaptureCap } from './input-plan.mjs';
import { screenSnapshot, snapshotHash } from './agent-observer.mjs';
import { pristineEditing } from './editing-capture.mjs';
import { queryWitnessPlan } from './query-witness-cases.mjs';
import { inspectQueryWitness } from './query-witness-frame.mjs';
import { validateOutputGeometry } from './terminal-output.mjs';

export const querySuiteId = 'ntilde-owned-exact-queries-v1';
/** v1 is every run before DECRQSS s reported the margins: its horizontal-margins witness keeps the negative reply. v2 (the policy new
 * runs record) owes the positive one; the policy is archived in the receipt context, the run marker and the report, so re-derivation
 * reads the version from the run itself and a flipped policy cannot re-derive (the derived report would differ). */
export const legacyQueryPolicy = 'owned-exact-query-paired-raw-state-v1';
export const marginsQueryPolicy = 'owned-exact-query-paired-raw-state-v2';
/** v3 (the policy new runs record) additionally owes the OSC 99 capability reply to q07.extensions.osc99-kitty-notify, which the App answers since it
 * implements the kitty notification title/body subset; every v1 and v2 run keeps the silence expectation. */
export const ownedQueryPolicy = 'owned-exact-query-paired-raw-state-v3';
export const queryPolicies = Object.freeze([legacyQueryPolicy, marginsQueryPolicy, ownedQueryPolicy]);
/** Earlier challenges a query suite owes: every raw input capture plus the 30 image captures. 110 is the frozen 39-event input plan (80 captures);
 * 152 is the 60-event benchmark plan (122 captures); 268 is the 118-event v3 plan (238 captures). Any other count is not a plan this suite follows. */
export const queryPriorCounts = Object.freeze([110, 152, inputPriorCaptureCap + 30]);
export const pristineQuery = screen => typeof screen.cursorVisible === 'boolean' && pristineEditing({ ...screen, cursorVisible: true });
export const checkQueries = value => { if (!value) throw new Error('Invalid or incomplete owned query evidence.'); };
const check = checkQueries;
const hash = value => createHash('sha256').update(value).digest('hex');
const fixed = queryWitnessPlan({ rows: 8, cols: 60, top: 1, bottom: 8 });
const planned = [{ kind: 'bind', reader: 0 }];
fixed.forEach((item, index) => {
    if (item.id === 'q12.decrqss.vertical-margins') planned.push({ kind: 'margins', reader: 0 });
    planned.push({ kind: 'query', id: item.id, reader: Math.floor(index / 20) });
    if (item.id === 'q12.decrqss.vertical-margins') planned.push({ kind: 'full', reader: 0 });
});
planned.push({ kind: 'finish', reader: 1 });
export const queryPlan = Object.freeze(planned.map((value, index) => Object.freeze({ index, ...value })));

export function queryBinding(input) {
    const keys = ['runId', 'ownerPid', 'workerPid', 'uid', 'appExecutable', 'workerExecutable', 'tty', 'profileName', 'paneId'];
    check(input && Object.keys(input).length === keys.length && keys.every(key => Object.hasOwn(input, key)));
    const b = Object.fromEntries(keys.map(key => [key, input[key]]));
    check(typeof b.runId === 'string' && /^[a-f\d]{32}$/.test(b.runId)
        && [b.ownerPid, b.workerPid].every(pid => Number.isSafeInteger(pid) && pid > 1) && b.ownerPid !== b.workerPid
        && Number.isSafeInteger(b.uid) && b.uid >= 0 && typeof b.tty === 'string' && /^\/dev\/ttys\d+$/.test(b.tty)
        && [b.appExecutable, b.workerExecutable].every(path => typeof path === 'string' && path.startsWith('/') && path.length <= 4096)
        && typeof b.profileName === 'string' && b.profileName.length > 0 && b.profileName.length <= 128
        && typeof b.paneId === 'string' && /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i.test(b.paneId));
    return Object.freeze(b);
}
export function queryPrior(input) {
    check(Array.isArray(input));
    const length = input.length, pairs = [];
    check(Number.isSafeInteger(length) && length >= 0 && length <= Math.max(...queryPriorCounts));
    for (let index = 0; index < length; index++) {
        check(Object.hasOwn(input, index)); const value = input[index];
        check(typeof value === 'string');
        const parts = value.split(',').map(Number);
        check(parts.length === 2 && parts.every(n => Number.isSafeInteger(n) && n >= 32768 && n <= 65535)
            && parts[0] !== parts[1] && parts.join(',') === value);
        pairs.push(value);
    }
    check(new Set(pairs).size === pairs.length); return Object.freeze(pairs);
}
export function queryAction(entry, binding, cursorVisible = true) {
    const nonce = 'NTILDE_QRY_' + binding.runId;
    let command;
    if (entry.kind === 'bind') command = '\x1bc\x1b[?1049h' + nonce;
    else if (entry.kind === 'margins') command = '\x1b[2;6r\x1b[?6h';
    else if (entry.kind === 'full') command = '\x1b[?6l\x1b[r\x1b[1;' + (nonce.length + 1) + 'H';
    else if (entry.kind === 'finish') { check(typeof cursorVisible === 'boolean'); command = '\x1bc\x1b[?25' + (cursorVisible ? 'h' : 'l'); }
    else command = fixed.find(value => value.id === entry.id)?.request;
    check(typeof command === 'string' && command.length <= 64 && [...command].every(c => c.codePointAt(0) <= 127));
    return Object.freeze({ index: entry.index, kind: entry.kind, id: entry.id ?? null, reader: entry.reader,
        commandHex: Buffer.from(command, 'ascii').toString('hex'), commandSha256: hash(command) });
}
export function queryScreen(value) {
    const copy = structuredClone(value);
    return screenSnapshot(copy, true, true, true, true, true);
}
export function queryBound(initial, binding, margins = false) {
    const result = structuredClone(initial);
    result.lines[0] = 'NTILDE_QRY_' + binding.runId;
    result.cursorVisible = true; result.cursorRow = margins ? 1 : 0; result.cursorCol = margins ? 0 : result.lines[0].length;
    Object.assign(result.bufferState, { alternateScreen: true, scrollTop: margins ? 1 : 0,
        scrollBottom: margins ? 5 : result.rows - 1, originMode: margins });
    return result;
}
export function validateQueryOwnership(value, binding, screen) {
    const fact = structuredClone(value);
    check(fact && typeof fact.at === 'string' && new Date(fact.at).toISOString() === fact.at);
    for (const [side, pid, executable] of [['app', binding.ownerPid, binding.appExecutable], ['worker', binding.workerPid, binding.workerExecutable]]) {
        check(fact[side]?.pid === pid && fact[side].uid === binding.uid && fact[side].executable === executable
            && Number.isSafeInteger(fact[side].ppid) && fact[side].ppid > 1);
    }
    check(fact.worker.ppid === binding.ownerPid && fact.worker.tty === binding.tty.slice(5)
        && fact.tty === binding.tty && typeof fact.appCommand === 'string' && fact.appCommand.length <= 16384
        && typeof fact.workerCommand === 'string' && fact.workerCommand.length <= 16384
        && fact.appCommand.split(/\s+/).includes('--ntilde-terminfo-owner=' + binding.runId)
        && fact.workerCommand.split(/\s+/).includes('--ntilde-terminfo-worker=' + binding.runId)
        && fact.terminalOutput?.tool === '/bin/stty' && fact.terminalOutput.outputProcessing === false);
    validateOutputGeometry(fact.terminalOutput.description, screen.rows, screen.cols); return fact;
}
function validateSample(source, binding, original, clock) {
    const screen = queryScreen(source.screen);
    check(source.sha256 === snapshotHash(screen));
    for (const fact of [source.beforeOwnership, source.afterOwnership]) {
        validateQueryOwnership(fact, binding, screen);
        const time = Date.parse(fact.at); check(time >= clock.time); clock.time = time;
        for (const side of ['app', 'worker']) check(snapshotHash(fact[side]) === snapshotHash((original ?? source.beforeOwnership)[side]));
    }
    return screen;
}
function inspectCapture(capture, context, id, used) {
    const result = inspectQueryWitness(context, id, capture), key = result.orderingNonces.join(',');
    check(!used.has(key)); used.add(key); return result;
}
/** The witness context of one query frame; `checksumReport` only when the run turned the off-by-default DECRQCRA report on and
 * `marginsReport` only under the v2 and v3 query policies and `noticeReport` only under v3. */
export const queryContext = (screen, checksumReport = false, marginsReport = false, noticeReport = false) => ({ rows: screen.rows, cols: screen.cols, top: screen.bufferState.scrollTop + 1,
    bottom: screen.bufferState.scrollBottom + 1, ...(checksumReport ? { checksumReport: true } : {}), ...(marginsReport ? { marginsReport: true } : {}), ...(noticeReport ? { noticeReport: true } : {}) });
export function buildQueryReport(input) {
    const { binding: supplied, priorChallenges, initial, actions, frames, seal, checksumReport = false, marginsReport = false, noticeReport = false } = structuredClone(input);
    check(typeof checksumReport === 'boolean' && typeof marginsReport === 'boolean' && typeof noticeReport === 'boolean' && (!noticeReport || marginsReport));
    const binding = queryBinding(supplied), prior = queryPrior(priorChallenges), clock = { time: -Infinity };
    check(queryPriorCounts.includes(prior.length) && actions?.length === 44 && frames?.length === 44);
    const baseline = validateSample(initial.before, binding, null, clock), original = initial.before.beforeOwnership;
    check(snapshotHash(validateSample(initial.after, binding, original, clock)) === snapshotHash(baseline));
    check(pristineQuery(baseline) && baseline.rows >= 8);
    const used = new Set(prior), observations = [], counts = [2, 0]; let expected = baseline;
    for (const entry of queryPlan) {
        const frame = frames[entry.index], action = actions[entry.index]; counts[entry.reader] += 2;
        check(frame.index === entry.index && snapshotHash(action) === snapshotHash(queryAction(entry, binding, baseline.cursorVisible)));
        const before = validateSample(frame.before, binding, original, clock);
        const after = validateSample(frame.after, binding, original, clock);
        check(snapshotHash(before) === snapshotHash(expected));
        const comparison = inspectCapture(frame.capture, queryContext(before, checksumReport, marginsReport, noticeReport), entry.id ?? fixed[0].id, used);
        if (entry.kind === 'query') observations.push(comparison);
        else {
            check(frame.capture.byteLength === 0);
            expected = entry.kind === 'finish' ? baseline : queryBound(baseline, binding, entry.kind === 'margins');
        }
        check(snapshotHash(after) === snapshotHash(expected));
    }
    check(snapshotHash(counts) === snapshotHash([48, 42]) && observations.length === 40);
    const final = frames.at(-1).capture, status = seal?.status;
    validateQueryOwnership(seal.ownership, binding, baseline);
    for (const side of ['app', 'worker']) check(snapshotHash(seal.ownership[side]) === snapshotHash(original[side]));
    check(Date.parse(seal.ownership.at) >= clock.time && status?.failed === false && status.disposed === true && status.attempts === 44
        && status.lastAttempt?.completed === true && status.lastAttempt.fenceRequested === true && status.lastAttempt.error === null
        && status.lastAttempt.hex === final.hex + Buffer.from(final.barrier).toString('hex')
        && snapshotHash(status.lastAttempt.nonces) === snapshotHash(final.nonces));
    return { schemaVersion: 1, suiteId: querySuiteId, policy: noticeReport ? ownedQueryPolicy : marginsReport ? marginsQueryPolicy : legacyQueryPolicy, runId: binding.runId, binding, priorChallenges: prior, readerCounts: counts,
        ...(checksumReport ? { checksumReport: 'AllowRectangleChecksumReport on in the initial settings: q07.editing.decrqcra owes the DECRQCRA reply' } : {}),
        ...(marginsReport ? { marginsReport: 'query policy v2: q12.decrqss.horizontal-margins owes the DECRQSS s reply, ESC P 1 $ r 1 ; cols s ESC \\' } : {}),
        ...(noticeReport ? { noticeReport: 'query policy v3: q07.extensions.osc99-kitty-notify owes the OSC 99 capability reply, ESC ] 99 ; i=ntilde-query-audit:p=? ; o=always:p=title,body ESC \\' } : {}),
        unobservedState: ['reverseVideo'],
        scope: 'Exact original query spellings and exposed cells/attributes/cursor/modes/margins/history. Reverse-video memory and appearance are unobserved; no preservation claim. Contract matches include explicit negative replies and observed silence; no feature-support score, hidden parser/title/native-state or physical-geometry claim.',
        observations, initial, actions, frames, seal };
}
