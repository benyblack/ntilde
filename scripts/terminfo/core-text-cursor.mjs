// Exact live-page Core fixtures. Coordinates in this module are physical and
// zero-based; CPR replies are one-based. No production parser is used as oracle.
import { scrollbackReset, scrollbackView } from './scrollback-contracts.mjs';
import { validateGradedJournal, planFeed, planRead } from './graded-plan.mjs';
import { screenSnapshot, OwnedCellObserver } from './agent-observer.mjs';
import { validateCursorReport } from './cursor-report.mjs';

export const textCursorSuiteId = 'ntilde-core-text-cursor-v1';
export const textCursorNonce = 'NTILDE_TC_';
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const check = (ok, message) => { if (!ok) throw new Error('Core text/cursor: ' + message); };
const cases = [
    ['text.cr', 'AB\rC', ['CB'], [0, 1]],
    ['text.newline', 'A\r\nB', ['A', 'B'], [1, 1]],
    ['text.backspace', 'AB\x08C', ['AC'], [0, 2]],
    ['text.index', 'A\x1bD', ['A'], [1, 1]],
    ['text.next-line', 'ABC\x1bE', ['ABC'], [1, 0]],
    ['cursor.move.up', '\x1b[5B\x1b[2A', [], [3, 0]],
    // The requirement's 5;5 denotes the transmitted one-based CUP parameters,
    // as recorded in the versioned rationale. The observed physical point is 4,4.
    ['cursor.save-restore', 'AB\x1b7\x1b[5;5H\x1b8', ['AB'], [0, 2]],
    ['cursor.hpa', 'ABCDEFGH\x1b[5`', ['ABCDEFGH'], [0, 4]],
];
export const textCursorIds = Object.freeze(cases.map(c => c[0]));
const reset = scrollbackReset.replace('\x1b[?1049l', '') + '\x1b[?69l\x1b[?7h\x1b[?25h';
const marker = '\x1b[10;1HGUARD\x1b[H';

/** Each fixture is a single fenced write. Controls use independent resets. */
export function textCursorPlan() {
    const plan = [];
    const add = (feed, role) => { plan.push(planFeed(feed)); if (role) plan.push({ ...planRead('state'), role }); };
    for (const [id, fixture] of cases) {
        add(reset + marker, id + ':setup');
        add(fixture, id + ':result');
    }
    // Separate witnesses establish displacement and distinguish IND, NEL and CR.
    for (const [role, fixture] of [
        ['save:displaced', 'AB\x1b7\x1b[5;5H'],
        ['save:restored', '\x1b8'],
    ]) {
        if (role === 'save:displaced') add(reset + marker, 'save:setup');
        add(fixture, role);
    }
    for (const [role, fixture] of [['ind', 'ABC\x1bD'], ['nel', 'ABC\x1bE'], ['cr', 'ABC\r']]) {
        add(reset + marker, 'control:' + role + ':setup');
        add(fixture, 'control:' + role);
    }
    add(reset + marker + '\x1b[3;4H', 'control:ind-interior:setup');
    add('\x1bD', 'control:ind-interior');
    return plan;
}

function expectedLines(rows, firstRows) {
    const lines = Array(rows).fill('');
    firstRows.forEach((line, i) => { lines[i] = line; });
    lines[9] = 'GUARD';
    return lines;
}
function stable(v) {
    return v.alternate && !v.origin && !v.insert && v.autoWrap && !v.pending && v.cursorVisible
        && v.top === 0 && v.bottom === v.rows - 1 && v.count === 0 && v.storedDefault;
}
function view(frame) { return scrollbackView(frame.screen); }
function isState(frame, lines, cursor) {
    const v = view(frame);
    return stable(v) && frame.screen.horizontalMargins?.leftRightMarginMode === false && same(v.lines, expectedLines(v.rows, lines)) && same(v.cursor, cursor);
}

/** Validate setup and controls before deciding any effects. A mismatched target is
 * unsupported only after a complete, nonce-bound journal and restoration. */
export function gradeTextCursor(roles) {
    for (const id of textCursorIds) check(isState(roles[id + ':setup'], [], [0, 0]), id + ' setup invalid');
    for (const role of ['save:setup', 'control:ind:setup', 'control:nel:setup', 'control:cr:setup'])
        check(isState(roles[role], [], [0, 0]), role + ' invalid');
    check(isState(roles['save:displaced'], ['AB'], [4, 4]), 'save displacement invalid');
    check(isState(roles['control:ind-interior:setup'], [], [2, 3]), 'IND interior setup invalid');
    return Object.fromEntries(cases.map(([id, fixture, lines, cursor]) => [id, {
        outcome: isState(roles[id + ':result'], lines, cursor)
            && (id !== 'cursor.save-restore' || isState(roles['save:restored'], ['AB'], [0, 2]))
            && (id !== 'text.index' || (isState(roles['control:ind'], ['ABC'], [1, 3])
                && isState(roles['control:ind-interior'], [], [3, 3])))
            && (id !== 'text.next-line' || isState(roles['control:nel'], ['ABC'], [1, 0]))
            && (id !== 'text.cr' || isState(roles['control:cr'], ['ABC'], [0, 0])) ? 'supported' : 'unsupported',
        id: 'ntilde-core-text-cursor-effect-v1', fixture, expected: { lines, cursor }, layer: 'stored physical cells and cursor',
    }]));
}

/** Re-derive from trusted plan and parent-journaled observations, never the
 * worker's reported grade. Each post-fixture CPR is retained separately. */
export function buildTextCursorReport(frames, run, runId) {
    const plan = textCursorPlan(), reads = plan.filter(s => s.read);
    check(run?.suiteId === textCursorSuiteId && run.runId === runId, 'identity mismatch');
    validateGradedJournal(frames, { frameCount: reads.length + 3, prefix: 'tc', noncePrefix: textCursorNonce,
        runId, label: 'Core text/cursor', screenOf: s => screenSnapshot(s, true, true, false, true, true, true, false, false, true) });
    check(same(run.feeds, plan.filter(s => s.feed).map(s => s.feed)), 'fixture bytes changed');
    check(Array.isArray(run.barriers) && run.barriers.length === run.feeds.length
        && run.barriers.every(b => /^\x1b\[\?[\d;]+c$/.test(b)), 'missing write barriers');
    check(Array.isArray(run.reads) && run.reads.length === reads.length, 'incomplete reads');
    const roles = {};
    for (let i = 0; i < reads.length; i++) {
        const frame = frames[i + 2], record = run.reads[i];
        check(record.role === reads[i].role && same(record.cpr, frame.cpr), 'read order or CPR changed');
        const cpr = validateCursorReport(frame.cpr);
        check(cpr.barrier === frame.barrier && cpr.reply !== null, 'missing ordered CPR');
        check(cpr.reply.row === frame.screen.cursorRow + 1 && cpr.reply.col === frame.screen.cursorCol + 1,
            'CPR disagrees with physical cursor');
        roles[record.role] = frame;
    }
    const effects = gradeTextCursor(roles);
    for (const id of textCursorIds) {
        const frame = roles[id + ':result'];
        effects[id].observed = { lines: frame.screen.lines.slice(0, 2), cursor: [frame.screen.cursorRow, frame.screen.cursorCol],
            guard: frame.screen.lines[9], cpr: frame.cpr.reply };
    }
    return { schemaVersion: 1, suiteId: textCursorSuiteId, runId, cases: textCursorIds.map(id => ({ caseId: id, effectContract: effects[id] })), snapshots: frames };
}

/** Execute every write and its observation in order; restoration is attempted on
 * every path. The fixture itself remains one write before any CPR or DA fence. */
export async function collectTextCursor({ config, fence, owner }) {
    const feeds = [], barriers = [], reads = [];
    const started = await owner('tc-start');
    check(started.screen.rows >= 12 && started.screen.cols >= 48, 'geometry too small');
    let primary;
    try {
        await owner('tc-bind', await fence.write('\x1b[?47h' + reset + textCursorNonce + config.runId));
        for (const step of textCursorPlan()) {
            if (step.feed) {
                feeds.push(step.feed);
                barriers.push(await fence.write(step.feed));
            } else {
                const cpr = await fence.cursorReport();
                await owner('tc-read', cpr.barrier, { cpr });
                reads.push({ role: step.role, cpr });
            }
        }
    } catch (error) { primary = error; }
    try { await owner('tc-finish', await fence.write(reset + '\x1b7\x1b[?47l')); }
    catch (error) {
        if (primary) throw new AggregateError([primary, error], 'Core text/cursor collection and restoration failed.');
        throw error;
    }
    if (primary) throw primary;
    return { schemaVersion: 1, suiteId: textCursorSuiteId, runId: config.runId, feeds, barriers, reads };
}

/** Two complete reads must agree before a frame can enter the journal. */
export class TextCursorObserver extends OwnedCellObserver {
    async capture(budget) {
        const first = await super.capture(budget);
        const second = await super.capture(budget);
        this.lastAttempt = { first, second };
        check(same(first, second), 'paired reads disagree');
        return first;
    }
    async handleRequest(request, budget) {
        if (request.kind === 'tc-read') {
            const cpr = validateCursorReport(request.cpr);
            check(cpr.barrier === request.barrier, 'CPR barrier mismatch');
            const result = await super.handle(request.kind, budget, request.barrier);
            this.frames.at(-1).cpr = cpr;
            return result;
        }
        check(request.cpr === undefined, 'unexpected CPR');
        return super.handle(request.kind, budget, request.barrier);
    }
}
