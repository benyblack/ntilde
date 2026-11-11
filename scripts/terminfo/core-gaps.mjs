// Core evidence supplement. The terminal receives writes only through the owned fence;
// the parent observes cells and parser icon state independently. No host-icon claim.
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { OwnedCellObserver, screenSnapshot } from './agent-observer.mjs';
import { validateGradedJournal } from './graded-plan.mjs';
import { scrollbackReset, scrollbackView } from './scrollback-contracts.mjs';
import { sgrAttrsCellAt } from './sgr-attrs-contracts.mjs';

export const coreGapsSuiteId = 'ntilde-owned-core-gaps-v1';
export const coreGapsAdapterId = 'ntilde-owned-core-gaps-grading-v1';
export const coreGapsIds = Object.freeze(['text.reverse-index-scroll', 'reset.method', 'device.env-term', 'extensions.osc1-icon']);
export const backendResetBytes = '\x1b[?1049l\x1bc\x1b[?6l\x1b[?7h\x1b[4l\x1b[?25h\x1b[r\x1b[0m\x1b[2J\x1b[3J\x1b[H';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const check = (condition, note) => { if (!condition) throw new Error('Core gaps: ' + note); };
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const pinnedBytes = readFileSync(new URL('./core-gaps-pinned.json', import.meta.url));
check(hash(pinnedBytes) === '917d1920da1d08eba5c81b88eb093105161f249a1ce55d544383e6f98b62f30c', 'pinned definitions changed');
export const coreGapsPinned = Object.freeze(JSON.parse(pinnedBytes));

/** Independent mirror of the tiny pinned parser-state result helper. */
function parserStateResult(pass, expected, state, note) {
    const response = JSON.stringify(state);
    let outcome = 'inconclusive';
    if (pass !== null) outcome = pass ? 'supported' : 'unsupported';
    return { pass: pass === true, response, ...(note && { note }),
        observation: { outcome, ...(pass === null && { reason: 'insufficient-evidence' }), evidence: 'parser-state', ...(note && { note }) },
        ...(pass !== null && { assertions: [{ kind: pass ? 'positive' : 'negative', expected, observed: response }] }) };
}

function replayReverseIndex(ctx) {
    const rows = ctx.getScrollback().screenLines;
    if (!Number.isSafeInteger(rows) || rows < 5) return { pass: false,
        observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'none' } };
    try {
        ctx.feed('\x1b[1;5r'); ctx.feed('\x1b[H'); ctx.feed('MARKER');
        const before = ctx.getCell(0, 0);
        ctx.feed('\x1b[H'); ctx.feed('\x1bM');
        const cell = ctx.getCell(1, 0);
        let pass = null, note = 'MARKER control was not measured';
        if (before.char === 'M') { pass = cell.char === 'M'; note = undefined; }
        return parserStateResult(pass, 'RI moves the measured MARKER to row 2', { before, cell }, note);
    } finally { ctx.feed('\x1b[r'); }
}

function replayReset(ctx) {
    ctx.feed('Hello World');
    const before = { ...ctx.getCursor(), text: ctx.getText() };
    ctx.reset();
    const after = { ...ctx.getCursor(), text: ctx.getText() };
    const expected = 'Reset method returns the cursor home and clears measured text';
    if (!before.text.includes('Hello World') || before.x === 0)
        return parserStateResult(null, expected, { before, after }, 'Reset-method control state was not measured');
    return parserStateResult(after.x === 0 && after.y === 0 && !after.text.includes('Hello World'), expected, { before, after });
}

/** No captured code executes parent-side. The worker still runs the untouched pinned
 * callback; this separate implementation must reproduce every result field. */
export function replayCoreGap(id, ctx) {
    check(Object.hasOwn(coreGapsPinned, id), 'unknown pinned id');
    if (id === 'text.reverse-index-scroll') return { probeId: id, ...replayReverseIndex(ctx) };
    if (id === 'reset.method') return { probeId: id, ...replayReset(ctx) };
    const before = ctx.getTitle(); ctx.feed('\x1b]1;test-icon\x07'); const after = ctx.getTitle();
    return { probeId: id, pass: false, response: JSON.stringify({ before, after }), note: 'icon-name state',
        notTested: { reason: 'no-semantic-observable', noObservable: 'icon-name state' } };
}

/** Adds exports only; all original probe bodies and fixture bytes remain unchanged. */
export function adaptCoreGapsCollector(input) {
    const source = String(input);
    check(source.includes('export const ntildeLrMarginsAdapter = Object.freeze({'), 'missing predecessor');
    for (const text of Object.values(coreGapsPinned)) check(source.split(text).length === 2, 'pinned definition changed');
    return source + '\nexport function ntildeRunCoreGap(id, ctx) { init_src(); const p = ALL_PROBES.find(p => p.id === id); if (!p || !p.termless) throw new Error("Unknown Core gap"); return { probeId: id, ...p.termless(ctx) }; }\n';
}

/** Explicit asynchronous desktop backend API. reset() promises a clean main screen,
 * empty history, default rendition/full margins, origin/insert off, wrap on, visible home cursor. Host metadata
 * is outside that promise. This method, rather than runner preparation, is measured. */
export function createCoreBackend(fence, calls) {
    return { async reset() { calls.push({ method: 'reset', bytes: backendResetBytes }); return fence.write(backendResetBytes); } };
}

/** One authoritative ordered plan, including exact pinned writes and separate controls. */
export function coreGapsPlan(rows, termOutput) {
    const plan = [];
    const add = (feed, role = null, api = null) => plan.push({ feed, role, api });
    add(scrollbackReset);
    add('\x1b[1;5r'); add('\x1b[H'); add('MARKER', 'riBefore');
    add('\x1b[H'); add('\x1bM', 'riAfter'); add('\x1b[r', 'riMarginsRestored');
    const seed = '\x1b[1;1HABOVE\x1b[2;1HSECOND\x1b[3;1HTHIRD\x1b[4;1HFOURTH\x1b[5;1HBOTTOM\x1b[6;1HOUTSIDE';
    add(scrollbackReset + seed + '\x1b[2;5r\x1b[2;3H', 'regionBefore');
    add('\x1bM', 'regionAfter');
    add('\x1b[4;3H', 'moveBefore'); add('\x1bM', 'moveAfter');
    add('\x1b[r', 'regionRestored');
    add(scrollbackReset); add('Hello World', 'resetBefore');
    add(backendResetBytes, 'resetAfter', 'reset');
    const dirtyHistory = Array.from({ length: rows + 3 }, (_, i) => 'DIRTY-' + i + '\r\n').join('');
    const dirtyMain = '\x1b[3;9r\x1b[?6h\x1b[?7l\x1b[4h\x1b[?25l\x1b[1;3;7;42mMAIN-DIRTY';
    add(dirtyHistory, 'dirtyHistory');
    add(dirtyMain, 'dirtyMain');
    add(backendResetBytes, 'cleanMainReset', 'reset');
    add(dirtyHistory, 'dirtyHistoryForAlt');
    add(dirtyMain, 'dirtyMainForAlt');
    add('\x1b[?1049h\x1b[3;9r\x1b[?6h\x1b[?7l\x1b[4h\x1b[?25l\x1b[1;3;7;42mDIRTY', 'dirtyModes');
    add(backendResetBytes, 'cleanReset', 'reset');
    add('X', 'resetPen'); add(scrollbackReset);
    add(termOutput, 'termChild'); add(scrollbackReset);
    // Save the actual pre-suite title/icon. Restore it with the stack after controls.
    add('\x1b[22;0t\x1b]0;core-baseline\x07MARK', 'iconBefore');
    add('\x1b]1;test-icon\x07', 'iconPinned');
    add('\x1b]2;title-decoy\x07', 'iconTitleOnly');
    add('\x1b]1;core-distinct-icon\x07', 'iconDistinct');
    add('\x1b[22;1t\x1b]1;temporary-icon\x07', 'iconTemporary');
    add('\x1b[23;1t', 'iconStackRestored');
    add('\x1b[23;0t' + scrollbackReset, 'iconRestored');
    return plan;
}

function iconFact(raw, paneId) {
    check(raw?.paneId === paneId && typeof raw.buffer?.requestedIconName === 'string'
        && raw.buffer.requestedIconName.length <= 4096 && typeof raw.oscTitle === 'string'
        && raw.oscTitle.length <= 4096 && typeof raw.presentation?.tabText === 'string'
        && raw.presentation.tabText.length <= 4096, 'missing bounded icon/title readback');
    return { requestedIconName: raw.buffer.requestedIconName, oscTitle: raw.oscTitle, tabText: raw.presentation.tabText };
}

/** Complete paired reads; state disagreement is retained as a failed attempt, never graded. */
export class CoreGapsObserver extends OwnedCellObserver {
    async capture(budget) {
        const before = await super.capture(budget);
        const left = iconFact(await this.request('readPaneMetadata', { paneId: this.paneId }, budget), this.paneId);
        const after = await super.capture(budget);
        const right = iconFact(await this.request('readPaneMetadata', { paneId: this.paneId }, budget), this.paneId);
        this.lastAttempt = { before, after, left, right };
        check(same(before, after) && same(left, right), 'paired observation disagrees');
        this.facts = { before: left, after: right };
        return before;
    }
    async handle(kind, budget, barrier) {
        const result = await super.handle(kind, budget, barrier);
        Object.assign(this.frames.at(-1), { facts: this.facts });
        return { ...result, facts: this.facts };
    }
}

/** Pinned callbacks are synchronous, so replay them over the independently captured
 * states of their exact writes; reset() consumes its separately recorded API call. */
export function coreGapContext(id, roles) {
    let feeds = 0, resetCalls = 0;
    const writes = {
        'text.reverse-index-scroll': ['\x1b[1;5r', '\x1b[H', 'MARKER', '\x1b[H', '\x1bM', '\x1b[r'],
        'reset.method': ['Hello World'], 'extensions.osc1-icon': ['\x1b]1;test-icon\x07'],
    }[id];
    check(writes, 'unknown recording context');
    const current = () => {
        if (id === 'text.reverse-index-scroll') return roles[feeds >= 5 ? 'riAfter' : 'riBefore'];
        if (id === 'reset.method') return roles[resetCalls ? 'resetAfter' : 'resetBefore'];
        return roles[feeds ? 'iconPinned' : 'iconBefore'];
    };
    return {
        feed(value) { check(value === writes[feeds++], 'pinned feed mismatch'); },
        reset() { check(id === 'reset.method' && resetCalls++ === 0, 'unexpected reset API replay'); },
        getCursor() { const s = current().screen; return { x: s.cursorCol, y: s.cursorRow, visible: s.cursorVisible }; },
        getText() { return current().screen.lines.join('\n'); },
        getCell(row, col) { return sgrAttrsCellAt(current().screen, row, col); },
        getScrollback() { return { screenLines: current().screen.rows }; },
        getTitle() { return current().facts.before.oscTitle; },
        assertComplete() { check(feeds === writes.length && resetCalls === (id === 'reset.method' ? 1 : 0), 'incomplete pinned API replay'); },
    };
}

/** Each write/API call and its observation finish before the next step starts. */
export function executeCorePlan(plan, { backend, fence, owner, records, roles }) {
    return plan.reduce(async (previous, step) => {
        await previous;
        const barrier = step.api ? await backend.reset() : await fence.write(step.feed);
        records.push({ ...step, barrier });
        if (step.role) roles[step.role] = await owner('cg-read', barrier);
    }, Promise.resolve());
}

/** The actual child emits its environment onto the qualified terminal. This is not
 * an operator label or the collector parent's environment. The marker binds to runId. */
export async function collectCoreGaps({ config, fence, owner, gradingModule }) {
    const term = process.env.TERM ?? null;
    check(typeof term === 'string' && /^[A-Za-z0-9_.+-]{1,80}$/.test(term), 'child TERM unavailable');
    const termOutput = 'TERM-' + config.runId + '=' + term;
    const calls = [], backend = createCoreBackend(fence, calls), records = [], roles = {};
    const initial = await owner('cg-start');
    let primary;
    try {
        await owner('cg-bind', await fence.write('\x1b[?1049h' + scrollbackReset + 'NTILDE_CG_' + config.runId));
        await executeCorePlan(coreGapsPlan(initial.screen.rows, termOutput), { backend, fence, owner, records, roles });
    } catch (error) { primary = error; }
    try { await owner('cg-finish', await fence.write(scrollbackReset)); }
    catch (error) {
        if (primary) throw new AggregateError([primary, error], 'Core-gap collection and restoration failed.');
        throw error;
    }
    if (primary) throw primary;
    // Worker receives screen reads; parent alone retains icon facts. A small title
    // projection returned by cg-read serves only the pinned diagnostic callback.
    const pinned = [];
    for (const id of ['text.reverse-index-scroll', 'reset.method', 'extensions.osc1-icon']) {
        const ctx = coreGapContext(id, roles);
        pinned.push(gradingModule.ntildeRunCoreGap(id, ctx)); ctx.assertComplete();
    }
    return { schemaVersion: 1, suiteId: coreGapsSuiteId, runId: config.runId, records, calls,
        child: { pid: process.pid, term, output: termOutput }, pinned };
}

/** A valid dirty control precedes every decision. Missing or contradictory evidence
 * refuses the report; a stable measured mismatch is a failure, never a timeout pass. */
export function gradeCoreGapEffects(roles, run) {
    const view = name => scrollbackView(roles[name].screen);
    const a = view('riBefore'), b = view('riAfter'), rb = view('regionBefore'), ra = view('regionAfter');
    check(a.lines[0] === 'MARKER' && same(a.cursor, [0, 6]) && a.top === 0 && a.bottom === 4, 'RI seed invalid');
    check(same(rb.lines.slice(0, 6), ['ABOVE', 'SECOND', 'THIRD', 'FOURTH', 'BOTTOM', 'OUTSIDE'])
        && same(rb.cursor, [1, 2]) && rb.top === 1 && rb.bottom === 4, 'region control invalid');
    const riExpected = [...a.lines]; riExpected.splice(0, 0, ''); riExpected.splice(5, 1);
    const regionExpected = [...rb.lines]; regionExpected.splice(1, 0, ''); regionExpected.splice(5, 1);
    const mb = view('moveBefore'), ma = view('moveAfter');
    const ri = same(b.lines, riExpected) && same(b.cursor, [0, 0]) && same(ra.lines, regionExpected)
        && same(ra.cursor, [1, 2]) && same(ma.lines, mb.lines) && same(ma.cursor, [2, 2])
        && [a, b, rb, ra, ma].every(s => s.count === 0)
        && view('regionRestored').top === 0 && view('regionRestored').bottom === a.rows - 1;
    const before = view('resetBefore'), after = view('resetAfter'), history = view('dirtyHistory'), dirty = view('dirtyModes');
    check(before.lines[0] === 'Hello World' && same(before.cursor, [0, 11]), 'reset pinned seed invalid');
    check(history.count > 0 && dirty.alternate && dirty.origin && dirty.insert && !dirty.autoWrap && !dirty.cursorVisible
        && dirty.lines.some(line => line.includes('DIRTY')), 'non-default reset setup invalid');
    for (const role of ['dirtyMain', 'dirtyMainForAlt']) {
        const main = view(role);
        check(!main.alternate && main.origin && main.insert && !main.autoWrap && !main.cursorVisible
            && main.top === 2 && main.bottom === 8 && main.count > 0 && !main.storedDefault
            && main.lines.some(line => line.includes('MAIN-DIRTY')), 'non-default main reset setup invalid');
    }
    const clean = view('cleanReset'), pen = view('resetPen');
    const pristine = s => s.lines.every(line => line === '') && same(s.cursor, [0, 0]) && s.count === 0 && !s.alternate
        && !s.origin && !s.insert && !s.pending && s.autoWrap && s.cursorVisible && s.top === 0 && s.bottom === s.rows - 1 && s.storedDefault;
    const reset = pristine(after) && pristine(view('cleanMainReset')) && pristine(clean)
        && pen.lines[0] === 'X' && pen.storedDefault && same(pen.cursor, [0, 1]);
    const icon = name => roles[name].facts.before;
    const base = icon('iconBefore');
    check(base.requestedIconName === 'core-baseline' && base.oscTitle === 'core-baseline', 'OSC setup invalid');
    const expectations = { iconPinned: ['test-icon', 'core-baseline'], iconTitleOnly: ['test-icon', 'title-decoy'],
        iconDistinct: ['core-distinct-icon', 'title-decoy'], iconTemporary: ['temporary-icon', 'title-decoy'],
        iconStackRestored: ['core-distinct-icon', 'title-decoy'] };
    const osc = Object.entries(expectations).every(([role, values]) => {
        const fact = icon(role);
        return same([fact.requestedIconName, fact.oscTitle], values) && same(roles[role].screen, roles.iconBefore.screen);
    });
    check(view('termChild').lines[0] === run.child.output && run.child.output === 'TERM-' + run.runId + '=' + run.child.term,
        'child environment output was not observed');
    const term = run.child.term === 'xterm-256color';
    const notes = {
        'device.env-term': 'Observed ' + run.child.term + ' in the retained disposable direct-local profile; expected compatible xterm-256color.',
        'extensions.osc1-icon': 'Icon-name state and independent title/stack controls only; no host icon presentation.',
        'reset.method': 'Actual asynchronous desktop backend reset() call; sequence implementation is retained separately from runner preparation.',
        'text.reverse-index-scroll': 'Exact pinned top-boundary fixture and a separately seeded interior region; below-boundary cursor-only control.',
    };
    return Object.fromEntries(coreGapsIds.map((id, i) => [id, { outcome: [ri, reset, term, osc][i] ? 'supported' : 'unsupported',
        layer: ['stored-state/cursor/margins/history', 'desktop-backend-reset-api/stored-state', 'actual-session-child-output', 'parser-icon-state/title-independence'][i],
        note: notes[id] }]));
}

export function buildCoreGapsReport(frames, run, runId, adapter, replayModule = null) {
    check(run.runId === runId && run.suiteId === coreGapsSuiteId && adapter.id === coreGapsAdapterId, 'identity mismatch');
    check(Number.isSafeInteger(run.child?.pid) && run.child.pid > 0 && typeof run.child.term === 'string', 'child identity missing');
    const plan = coreGapsPlan(frames[0].screen.rows, run.child.output);
    check(run.records.length === plan.length && run.records.every((r, i) => same({ feed: r.feed, role: r.role, api: r.api }, plan[i])
        && /^\x1b\[\?[\d;]+c$/.test(r.barrier)), 'plan changed');
    check(same(run.calls, Array.from({ length: 3 }, () => ({ method: 'reset', bytes: backendResetBytes }))), 'reset API was not called');
    validateGradedJournal(frames, { frameCount: plan.filter(s => s.role).length + 3, prefix: 'cg', noncePrefix: 'NTILDE_CG_', runId,
        label: 'Core gaps', screenOf: s => screenSnapshot(s, true, true, false, true, true, true) });
    const roles = {}; let index = 2;
    for (const step of run.records) if (step.role) {
        const frame = frames[index++];
        check(frame.barrier === step.barrier && same(frame.facts.before, frame.facts.after), 'unpaired metadata or barrier');
        check(iconFact({ paneId: frame.paneId, buffer: frame.facts.before, oscTitle: frame.facts.before.oscTitle,
            presentation: { tabText: frame.facts.before.tabText } }, frame.paneId), 'invalid icon fact');
        roles[step.role] = frame;
    }
    check(same(roles.iconRestored.facts.before, frames[0].facts.before), 'title/icon restoration failed');
    const effects = gradeCoreGapEffects(roles, run);
    check(run.pinned.length === 3, 'pinned results missing');
    for (const [i, id] of ['text.reverse-index-scroll', 'reset.method', 'extensions.osc1-icon'].entries()) {
        const pinned = run.pinned[i];
        check(pinned.probeId === id, 'pinned order mismatch');
        const independentContext = coreGapContext(id, roles);
        check(same(replayCoreGap(id, independentContext), pinned), 'pinned result differs from journal replay'); independentContext.assertComplete();
        if (replayModule) { const ctx = coreGapContext(id, roles); check(same(replayModule.ntildeRunCoreGap(id, ctx), pinned), 'pinned replay differs'); ctx.assertComplete(); }
        // The icon callback is diagnostic by construction; never promote its verdict.
        check(id === 'extensions.osc1-icon' ? pinned.notTested?.reason === 'no-semantic-observable'
            : !(pinned.observation.outcome === 'unsupported' && effects[id].outcome === 'supported'), 'pinned/effect mismatch');
    }
    return { schemaVersion: 1, suiteId: coreGapsSuiteId, runId, adapter, child: run.child,
        cases: coreGapsIds.map(id => ({ caseId: id, pinned: run.pinned.find(p => p.probeId === id) ?? null,
            effectContract: { id: 'ntilde-core-gaps-effect-v1', ...effects[id] } })), snapshots: frames };
}
