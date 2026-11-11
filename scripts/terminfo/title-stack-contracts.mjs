// Versioned probe contract for the XTWINOPS title/icon stack qualification batch: device.xtwinops-22 (push) and device.xtwinops-23 (pop).
//
// Pinned terminfo.dev@4.0.0 definitions (read from the retained bundle, never inferred from the names). Both cases register the SAME termless body:
//   termless:  feed(OSC 2 ; pushed-title) ; original = getTitle() ; feed(CSI 22;0t) ; feed(OSC 2 ; new-title) ; changed = getTitle() ; feed(CSI 23;0t) ;
//              restored = getTitle() ; parserStateResult(original === "pushed-title" && changed === "new-title" && restored === "pushed-title" ? true : null, ...)
// and differ in the terminal path, which can never decide:
//   xtwinops-22: write(OSC 2 ; pushed-title) ; write(CSI 22;0t) ; write(OSC 2 ; new-title) ; DSR  -> "title stack was not measured" (inconclusive, consumed)
//   xtwinops-23: write(CSI 22;0t) ; write(CSI 23;0t) ; DSR                                           -> "restored title was not measured" (inconclusive, consumed)
// The termless path can only return supported (all three titles as expected) or inconclusive; it never returns unsupported. The pinned verdict of each case is
// the termless result replayed over the owned observations; the terminal path is replayed beside it and is inconclusive by design.
//
// The oracle below models the documented title-stack contract (docs/qa/terminfo/osc-title-payloads.md, xterm's xtermPushTitle/xtermPopTitle) over the exact
// bytes the contracts emit. It is written independently of the production implementation: it does not call it, and it carries its own bounded-depth,
// one-sided-copy and reset lifecycle rules. Each experiment starts from a fresh stage whose own pops drain the stack and prove it empty by behavior.
//
// Roles: stock (the exact pinned fixture on the prepared baseline), terminal (the exact pinned terminal-path bytes), effect and required (owned effect
// experiments graded in full), limit (the documented bounded-depth policy, a deliberate xterm-compatible implementation limit, reported separately) and
// boundary (stack retention across alternate screens, DECSTR and RIS, supplemental, reported separately).
import { planFeeds, planReads } from './graded-plan.mjs';

/** Contracts v1 is the first measured semantics of this batch. */
export const titleStackContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const titleStackSuiteId = 'ntilde-owned-title-stack-graded-v1';
/** The separately identified effect contracts, one per pinned case. */
export const titleStackPushContractId = 'ntilde-xtwinops-title-push-effect-v1';
export const titleStackPopContractId = 'ntilde-xtwinops-title-pop-effect-v1';
/** The nonce prefix binding worker output to this suite. */
export const titleStackNoncePrefix = 'NTILDE_TS_';
/** Owner-protocol prefix of this suite's observation requests. */
export const titleStackPrefix = 'tst';
/** The two pinned cases, in report order. */
export const titleStackCaseIds = Object.freeze(['device.xtwinops-22', 'device.xtwinops-23']);
export const titleStackRoles = Object.freeze({
    'device.xtwinops-22': { role: 'push (save title/icon state)', contractId: titleStackPushContractId, criterion: 'XTWINOPS 22 preserves the old title on its stack after a different title is set' },
    'device.xtwinops-23': { role: 'pop (restore title/icon state)', contractId: titleStackPopContractId, criterion: 'XTWINOPS 23 restores the title saved before a different title was set' },
});
/** The geometry the fixtures need. The nonce row sits below every fixture row. */
export const titleStackMinimum = Object.freeze({ rows: 12, cols: 48 });
export const titleStackNonceRow = 9;
/** The documented depth of the stack (xterm's MAX_SAVED_TITLES). */
export const titleStackDepth = 10;
/** Enough pops to empty a full stack twice over; extra pops on an empty stack are documented no-ops. */
const drainPops = 12;

const osc = body => `\x1b]${body}\x07`;
const at = (row, col) => `\x1b[${row + 1};${col + 1}H`;
const pushSeq = selector => `\x1b[22;${selector}t`;
const popSeq = selector => `\x1b[23;${selector}t`;

/** The pinned fixture bytes, byte for byte. */
const pushedTitle = osc('2;pushed-title'), newTitle = osc('2;new-title');
export const titleStackPinned = Object.freeze({
    'device.xtwinops-22': Object.freeze({ termlessFeeds: Object.freeze([pushedTitle, pushSeq(0), newTitle, popSeq(0)]),
        terminalWrites: Object.freeze([pushedTitle, pushSeq(0), newTitle]), reads: Object.freeze([['pinned-original', 1], ['pinned-changed', 3], ['pinned-restored', 4]]),
        expected: 'XTWINOPS 22 preserves the old title on its stack after a different title is set', terminalNote: 'title push' }),
    'device.xtwinops-23': Object.freeze({ termlessFeeds: Object.freeze([pushedTitle, pushSeq(0), newTitle, popSeq(0)]),
        terminalWrites: Object.freeze([pushSeq(0), popSeq(0)]), reads: Object.freeze([['pinned-original', 1], ['pinned-changed', 3], ['pinned-restored', 4]]),
        expected: 'XTWINOPS 23 restores the title saved before a different title was set', terminalNote: 'title pop' }),
});

/** The measured baseline every experiment starts from. Title and icon are the prepared fixture title; the stack is drained by the stage's own pops. */
export function titleStackFixture(runId) {
    if (!/^[a-f\d]{32}$/.test(runId ?? '')) throw new Error('Invalid title-stack fixture run identity.');
    const title = `TS-base-${runId.slice(0, 8)}`, cwd = '/ntilde-qa/title-stack-base', nonce = titleStackNoncePrefix + runId;
    const reset = osc('8;;') + osc('9;4;0') + osc('133;D;0') + osc('133;A')
        + '\x1b[?1049l\x1b[!p\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[H\x1b[128M\x1b[2J\x1b[H' + popSeq(0).repeat(drainPops) + osc(`0;${title}`) + osc(`7;file://localhost${cwd}`);
    const stage = reset + at(titleStackNonceRow, 0) + nonce;
    // A runtime palette override proves the title operations preserve runtime colors; it is withdrawn at the end. The finish write ends with one more pop on
    // the (drained) stack: the prepared title and icon must survive it, which shows the stack is empty again by behavior.
    return { title, cwd, nonce, reset, stage, bind: stage + osc('4;7;rgb:12/34/56'), finish: reset + osc('104;7') + popSeq(0) };
}

// ---------- the independent oracle ----------

const refusal = () => new Error('Title-stack oracle does not understand a contracted feed.');

/** A fresh measured baseline after the stage bytes. */
function baselineState(fixture) {
    return { title: fixture.title, icon: fixture.title, stack: [], rows: { [titleStackNonceRow]: fixture.nonce }, cursor: [titleStackNonceRow, fixture.nonce.length], cleared: false };
}

/** An OSC 0, 1 or 2 title request starting at `index` and its BEL terminator. */
function oscToken(feed, index) {
    const end = feed.indexOf('\x07', index + 2);
    if (end < 0) throw refusal();
    const body = feed.slice(index + 2, end), separator = body.indexOf(';'), code = body.slice(0, separator);
    if (separator < 0 || !['0', '1', '2'].includes(code)) throw refusal();
    return { token: { type: 'osc', code: Number(code), text: body.slice(separator + 1) }, next: end + 1 };
}

/** A CSI sequence starting at `index`: CSI 22/23 ; selector t, an alternate-screen switch or DECSTR. */
function csiToken(feed, index) {
    let end = index + 2;
    while (end < feed.length && /[\d;?!]/.test(feed[end])) end++;
    const params = feed.slice(index + 2, end), final = feed[end];
    if (final === 't' && /^2[23];[012]$/.test(params)) {
        const [operation, selector] = params.split(';').map(Number);
        return { token: { type: operation === 22 ? 'push' : 'pop', selector }, next: end + 1 };
    }
    if (params === '?1049' && 'hl'.includes(final ?? '')) return { token: { type: 'alternate' }, next: end + 1 };
    if (params === '!' && final === 'p') return { token: { type: 'decstr' }, next: end + 1 };
    throw refusal();
}

/** One token starting at `index`: OSC 0/1/2 title text, CSI 22/23 ; selector t, alternate screen, DECSTR or RIS; anything else is refused. */
function token(feed, index) {
    if (feed[index] !== '\x1b') throw refusal();
    const next = feed[index + 1];
    if (next === 'c') return { token: { type: 'ris' }, next: index + 2 };
    if (next === ']') return oscToken(feed, index);
    if (next === '[') return csiToken(feed, index);
    throw refusal();
}

/** Push: selector 0 saves both halves, 1 the icon, 2 the title. A one-sided push takes the other half from the entry below (none: that half is absent). */
function pushState(state, selector) {
    const below = state.stack.at(-1) ?? null;
    state.stack.push({ icon: selector === 2 ? below?.icon ?? null : state.icon, title: selector === 1 ? below?.title ?? null : state.title });
    if (state.stack.length > titleStackDepth) state.stack.shift();
}

/** Pop: an empty stack changes nothing. Otherwise the whole top entry is consumed and only the selected, present halves are restored. */
function popState(state, selector) {
    const entry = state.stack.pop();
    if (entry === undefined) return;
    if (selector !== 2 && entry.icon !== null) state.icon = entry.icon;
    if (selector !== 1 && entry.title !== null) state.title = entry.title;
}

/** Apply one token to the oracle state. Alternate screens and DECSTR leave the title, icon and stack alone and change no cell the contracts wrote. */
function applyToken(state, item) {
    if (item.type === 'osc') {
        if (item.code !== 1) state.title = item.text;
        if (item.code !== 2) state.icon = item.text;
    } else if (item.type === 'push') pushState(state, item.selector);
    else if (item.type === 'pop') popState(state, item.selector);
    else if (item.type === 'ris') Object.assign(state, { stack: [], rows: {}, cursor: [0, 0], cleared: true });
}

/** Interpret feeds over the oracle state. Only the bytes the contracts emit are understood; anything else is refused. */
export function simulateTitleStack(feeds, fixture) {
    let state = baselineState(fixture);
    for (const feed of feeds) {
        if (feed === fixture.stage) {
            state = baselineState(fixture);
            continue;
        }
        for (let index = 0; index < feed.length;) {
            const step = token(feed, index);
            index = step.next;
            applyToken(state, step.token);
        }
    }
    return state;
}

/** The expected whole screen: the bound baseline with the oracle's rows and cursor; every other stored fact is the baseline's. */
export function expectedTitleStackScreen(boundScreen, state) {
    const screen = structuredClone(boundScreen);
    screen.lines = Array.from({ length: boundScreen.rows }, (_, row) => (state.rows[row] ?? '').trimEnd());
    [screen.cursorRow, screen.cursorCol] = state.cursor;
    return screen;
}

// ---------- the plan ----------

const shortId = runId => runId.slice(0, 8);
const read = (label, kind = 'setup', scope = 'strict') => ({ read: label, kind, scope });

/** One experiment: the shared stage, a distinct prepared baseline whose last byte is an empty pop (the baseline read proves it left the values alone), then steps. */
function experiment(fixture, runId, spec, build) {
    const { owner, name, role, tag } = spec;
    const id = n => ({ title: `${shortId(runId)}-${tag}-t${n}`, icon: `${shortId(runId)}-${tag}-i${n}` });
    const set = n => osc(`2;${id(n).title}`) + osc(`1;${id(n).icon}`);
    const plan = [{ feed: fixture.stage }, { feed: set(0) + popSeq(0) }];
    if (!spec.unread) plan.push(read('base'));
    for (const step of build({ set, push: pushSeq, pop: popSeq, id })) plan.push(typeof step === 'string' ? { feed: step } : step);
    return { caseId: `${owner}/${name}`, owner, name, role, tag, plan };
}

const alt = '\x1b[?1049h', main = '\x1b[?1049l';

/** The fixed experiment plan, in execution order. */
export function makeTitleStackExperiments(runId) {
    const fixture = titleStackFixture(runId), push = 'device.xtwinops-22', pop = 'device.xtwinops-23';
    const e = (owner, name, role, tag, build, extra = {}) => experiment(fixture, runId, { owner, name, role, tag, ...extra }, build);
    const pinned = owner => () => {
        const [fed1, fed2, fed3, fed4] = titleStackPinned[owner].termlessFeeds;
        return [fed1, read('pinned-original'), fed2, fed3, read('pinned-changed'), fed4, read('pinned-restored', 'effect')];
    };
    return [
        e(push, 'stock', 'stock', 's2', pinned(push)),
        e(pop, 'stock', 'stock', 's3', pinned(pop)),
        e(pop, 'terminal-path', 'terminal', 'tp', () => [...titleStackPinned[pop].terminalWrites, read('pinned-terminal')], { unread: true }),

        e(push, 'effect', 'effect', 'e2', ({ set, push: p, pop: q }) => [p(0), read('pushed', 'effect'), set(1), read('changed'), q(0), read('popped', 'effect')]),
        e(push, 'selectors', 'required', 'q2', ({ set, push: p, pop: q }) => [p(2), set(1), read('changed-1'), q(0), read('title-restored', 'effect'),
            p(1), set(3), q(0), read('icon-restored', 'effect')]),
        e(push, 'nested', 'required', 'n2', ({ set, push: p, pop: q }) => [p(0), set(1), p(0), set(2), read('changed-2'), q(0), read('popped-1', 'effect'), q(0), read('popped-2', 'effect')]),
        e(push, 'overflow', 'limit', 'o2', ({ set, push: p, pop: q }) => [p(0), ...Array.from({ length: 11 }, (_, index) => set(index + 1) + p(0)), set(12), read('changed'),
            q(0), read('pop-1', 'effect'), q(0).repeat(8), q(0), read('pop-10', 'effect'), q(0), read('pop-11', 'effect')]),

        e(pop, 'effect', 'effect', 'e3', ({ set, push: p, pop: q }) => [p(0), set(1), read('changed'), q(0), read('restored', 'effect')]),
        e(pop, 'empty', 'required', 'z3', ({ set, pop: q }) => [set(1), read('changed'), q(0) + q(1) + q(2), read('empty-pop', 'effect')]),
        e(pop, 'selectors', 'required', 'q3', ({ set, push: p, pop: q }) => [p(0), set(1), read('changed-1'), q(1), read('icon-popped', 'effect'), q(2), read('title-after-consumed', 'effect'),
            p(0), set(3), q(2), read('title-popped', 'effect'), q(1), read('icon-after-consumed', 'effect')]),
        e(pop, 'lifo', 'required', 'l3', ({ set, push: p, pop: q }) => [p(0), set(1), p(0), set(2), read('changed-2'), q(0), read('popped-1', 'effect'), q(0), read('popped-2', 'effect'), q(0), read('popped-3', 'effect')]),
        e(pop, 'mixed', 'required', 'm3', ({ set, push: p, pop: q }) => [p(2), set(1), p(1), set(2), read('changed-2'), q(0), read('popped-1', 'effect'), q(0), read('popped-2', 'effect')]),

        e(push, 'alternate-screen', 'boundary', 'a2', ({ set, push: p, pop: q }) => [p(0), alt, set(1), main, read('changed-1'), q(0), read('popped-1', 'effect'),
            alt, p(0), main, set(2), read('changed-2'), alt, q(0), main, read('popped-2', 'effect')]),
        e(push, 'decstr', 'boundary', 'd2', ({ set, push: p, pop: q }) => [p(0), set(1), '\x1b[!p', read('changed', 'setup', 'preserved'), q(0), read('popped', 'effect', 'preserved')]),
        e(pop, 'ris', 'boundary', 'r3', ({ set, push: p, pop: q }) => [p(0), set(1), '\x1bc', read('after-ris', 'effect', 'cleared'), q(0), read('after-pop', 'effect', 'cleared')]),
    ];
}

/** Every experiment is a graded-plan contract: its plan names feeds and reads. */
export const titleStackExperimentCount = 15;
/** 58 reads + start, bind and finish = 61 of the observer's 64 captures: three captures stay free for settling retries of a transiently disagreeing pair. */
export const titleStackReadCount = 58;
export function countTitleStackReads(runId) { return makeTitleStackExperiments(runId).reduce((sum, item) => sum + planReads(item), 0); }
export function countTitleStackFeeds(runId) { return makeTitleStackExperiments(runId).reduce((sum, item) => sum + planFeeds(item).length, 0); }

// ---------- pinned replay ----------

/** The pinned parserStateResult(pass, expected, state, note), key order included. */
function parserStateResult(pass, expected, state, note) {
    const response = JSON.stringify(state);
    let outcome = 'inconclusive';
    if (pass !== null) outcome = pass ? 'supported' : 'unsupported';
    return { pass: pass === true, response, ...(note && { note }),
        observation: { outcome, ...(pass === null && { reason: 'insufficient-evidence' }), evidence: 'parser-state', ...(note && { note }) },
        ...(pass !== null && { assertions: [{ kind: pass ? 'positive' : 'negative', expected, observed: response }] }) };
}

/** The result the pinned termless code returns over the three title observations (of the exact pinned feeds), keyed by the label of the read that observed each. */
export function replayTitleStackTermless(caseId, titles) {
    const original = titles['pinned-original'], changed = titles['pinned-changed'], restored = titles['pinned-restored'];
    const ok = original === 'pushed-title' && changed === 'new-title' && restored === 'pushed-title';
    let note;
    if (original !== 'pushed-title' || changed !== 'new-title') note = 'Title setup was not observable';
    else if (restored !== 'pushed-title') note = 'Title push/pop did not restore the old title; the failing operation is unknown';
    return parserStateResult(ok ? true : null, titleStackPinned[caseId].expected, { original, changed, restored }, note);
}

/** The result the pinned terminal-path code returns once the cursor answered (or did not). */
export function replayTitleStackTerminal(caseId, cursorAnswered) {
    const what = titleStackPinned[caseId].terminalNote, restored = caseId === 'device.xtwinops-22' ? 'title stack was not measured' : 'restored title was not measured';
    return { pass: false, note: cursorAnswered ? `Cursor answered after ${what}; ${restored}` : `No DSR response after ${what}`,
        observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'consumed' } };
}

/** The contexts the pinned code is replayed in. Both refuse any write, read or feed outside the pinned fixture. */
export function titleStackTermlessContext(caseId, titles) {
    const pinned = titleStackPinned[caseId];
    let fed = 0, reads = 0;
    return { get fedCount() { return fed; }, get readCount() { return reads; },
        feed: text => {
            if (fed >= pinned.termlessFeeds.length || text !== pinned.termlessFeeds[fed]) throw new Error(`Pinned ${caseId} feed differs from the contract.`);
            fed++;
        },
        getTitle: () => {
            const [label, after] = pinned.reads[reads] ?? [];
            if (label === undefined || fed !== after) throw new Error(`Pinned ${caseId} reads the title outside its contracted points.`);
            reads++;
            return titles[label];
        } };
}
export function titleStackTerminalContext(caseId, cursor) {
    const pinned = titleStackPinned[caseId];
    let written = 0;
    return { get writtenCount() { return written; },
        write: text => {
            if (written >= pinned.terminalWrites.length || text !== pinned.terminalWrites[written]) throw new Error(`Pinned ${caseId} write differs from the contract.`);
            written++;
        },
        queryCursorPosition: () => {
            if (written !== pinned.terminalWrites.length) throw new Error(`Pinned ${caseId} queried the cursor early.`);
            return Promise.resolve({ row: cursor[0] + 1, col: cursor[1] + 1 });
        } };
}
