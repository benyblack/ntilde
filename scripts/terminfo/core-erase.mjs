// Exact Core erase specimens. CUP and CHA use one-based parameters; observed
// physical cursor positions are zero-based. Historical pinned probes are separate.
import { scrollbackReset, scrollbackView } from './scrollback-contracts.mjs';
import { validateGradedJournal, planFeed, planRead } from './graded-plan.mjs';
import { screenSnapshot, OwnedCellObserver } from './agent-observer.mjs';
import { validateCursorReport } from './cursor-report.mjs';
import { expandCellDetails } from './cell-details.mjs';

export const eraseSuiteId = 'ntilde-core-erase-v1';
export const eraseNonce = 'NTILDE_CE_';
export const eraseIds = Object.freeze(['erase.line.right', 'erase.screen.above', 'erase.screen.below']);
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const check = (condition, message) => { if (!condition) throw new Error('Core erase: ' + message); };
const reset = scrollbackReset.replace('\x1b[?1049l', '') + '\x1b[?69l\x1b[?7h\x1b[?25h';
// The frozen descriptions say only “three lines”; use distinguishable concrete
// text, recorded here rather than pretending the pinned middle-row probe matches.
const seed = 'AAAAA\r\nBBBBB\r\nCCCCC';
export const specimens = Object.freeze([
    { role: 'el-home', id: eraseIds[0], seed: 'XXXXX', position: '\x1b[1G', operation: '\x1b[K', before: ['XXXXX', 'KEEP'], after: ['', 'KEEP'], cursor: [0, 0], guard: true },
    { role: 'el-interior', id: eraseIds[0], seed: 'ABCDE', position: '\x1b[3G', operation: '\x1b[K', before: ['ABCDE', 'KEEP'], after: ['AB', 'KEEP'], cursor: [0, 2], guard: true },
    // The live page uses row 3 col 2; catalogue uses row 2 col 3. Both
    // positions are expressed as one-based CUP and remain separately recorded.
    { role: 'ed1-site', id: eraseIds[1], seed, position: '\x1b[3;2H', operation: '\x1b[1J', before: ['AAAAA', 'BBBBB', 'CCCCC'], after: ['', '', '  CCC'], cursor: [2, 1] },
    { role: 'ed1-catalogue', id: eraseIds[1], seed, position: '\x1b[2;3H', operation: '\x1b[1J', before: ['AAAAA', 'BBBBB', 'CCCCC'], after: ['', '   BB', 'CCCCC'], cursor: [1, 2] },
    { role: 'ed0-home', id: eraseIds[2], seed, position: '\x1b[H', operation: '\x1b[J', before: ['AAAAA', 'BBBBB', 'CCCCC'], after: [], cursor: [0, 0] },
    { role: 'ed0-interior', id: eraseIds[2], seed, position: '\x1b[2;3H', operation: '\x1b[J', before: ['AAAAA', 'BBBBB', 'CCCCC'], after: ['AAAAA', 'BB'], cursor: [1, 2] },
]);

export function erasePlan() {
    const plan = [];
    const add = (bytes, role) => { plan.push(planFeed(bytes)); if (role) plan.push({ ...planRead('state'), role }); };
    for (const s of specimens) {
        // EL controls have a neighboring row that must survive. Setup and erase
        // are separate writes with a measured physical state between them.
        const guard = s.guard ? '\x1b[2;1HKEEP\x1b[H' : '';
        add(reset + guard + s.seed + s.position, s.role + ':before');
        add(s.operation, s.role + ':after');
    }
    return plan;
}
function matches(frame, lines, cursor, seedState) {
    const v = scrollbackView(frame.screen);
    const expected = Array(v.rows).fill('');
    lines.forEach((line, i) => { expected[i] = line; });
    return v.alternate && !v.origin && !v.insert && v.autoWrap && !v.pending && v.cursorVisible
        && v.top === 0 && v.bottom === v.rows - 1 && v.count === 0 && v.history.length === 0
        && frame.screen.horizontalMargins?.leftRightMarginMode === false
        && frame.screen.horizontalMargins?.leftMargin === 0 && frame.screen.horizontalMargins?.rightMargin === v.cols - 1
        && (!seedState || v.storedDefault) && same(v.lines, expected) && same(v.cursor, cursor);
}
function retainedDetailsMatch(before, after, specimen) {
    const { rows, cols } = before.screen;
    if (after.screen.rows !== rows || after.screen.cols !== cols) return false;
    const a = expandCellDetails(before.screen.cellDetails, rows * cols, true);
    const b = expandCellDetails(after.screen.cellDetails, rows * cols, true);
    const [cr, cc] = specimen.cursor;
    return a.every((cell, index) => {
        const row = Math.floor(index / cols), col = index % cols;
        const erased = specimen.role.startsWith('el-') ? row === cr && col >= cc
            : specimen.role.startsWith('ed1-') ? row < cr || (row === cr && col <= cc)
                : row > cr || (row === cr && col >= cc);
        return erased || same(cell, b[index]);
    });
}
export function gradeErase(roles) {
    const outcomes = {};
    for (const s of specimens) {
        check(roles[s.role + ':before'] && roles[s.role + ':after'], 'missing frame: ' + s.role);
        check(matches(roles[s.role + ':before'], s.before, s.cursor, true), 'invalid seed or cursor: ' + s.role);
        const outcome = matches(roles[s.role + ':after'], s.after, s.cursor, false)
            && retainedDetailsMatch(roles[s.role + ':before'], roles[s.role + ':after'], s) ? 'supported' : 'unsupported';
        outcomes[s.role] = { id: s.id, outcome, fixture: { seed: s.seed, position: s.position, operation: s.operation },
            expected: { before: s.before, after: s.after, cursor: s.cursor } };
    }
    return outcomes;
}
export function buildEraseReport(frames, run, runId) {
    const plan = erasePlan(), reads = plan.filter(s => s.read);
    check(run?.suiteId === eraseSuiteId && run.runId === runId, 'identity mismatch');
    validateGradedJournal(frames, { frameCount: reads.length + 3, prefix: 'ce', noncePrefix: eraseNonce,
        runId, label: 'Core erase', screenOf: s => screenSnapshot(s, true, true, false, true, true, true, false, false, true) });
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
        check(cpr.reply.row === frame.screen.cursorRow + 1 && cpr.reply.col === frame.screen.cursorCol + 1, 'CPR disagrees with cursor');
        roles[record.role] = frame;
    }
    const effects = gradeErase(roles);
    for (const s of specimens) {
        effects[s.role].observed = Object.fromEntries(['before', 'after'].map(part => {
            const f = roles[s.role + ':' + part];
            return [part, { lines: f.screen.lines, cursor: [f.screen.cursorRow, f.screen.cursorCol], cpr: f.cpr.reply }];
        }));
    }
    return { schemaVersion: 1, suiteId: eraseSuiteId, runId, effects, snapshots: frames };
}
export async function collectErase({ config, fence, owner }) {
    const feeds = [], barriers = [], reads = [];
    const started = await owner('ce-start');
    check(started.screen.rows >= 12 && started.screen.cols >= 48, 'geometry too small');
    let primary;
    try {
        await owner('ce-bind', await fence.write('\x1b[?47h' + reset + eraseNonce + config.runId));
        for (const step of erasePlan()) {
            if (step.feed) { feeds.push(step.feed); barriers.push(await fence.write(step.feed)); }
            else {
                const cpr = await fence.cursorReport();
                await owner('ce-read', cpr.barrier, { cpr });
                reads.push({ role: step.role, cpr });
            }
        }
    } catch (error) { primary = error; }
    try { await owner('ce-finish', await fence.write(reset + '\x1b7\x1b[?47l')); }
    catch (error) { if (primary) throw new AggregateError([primary, error], 'Core erase collection and restoration failed'); throw error; }
    if (primary) throw primary;
    return { schemaVersion: 1, suiteId: eraseSuiteId, runId: config.runId, feeds, barriers, reads };
}
export class EraseObserver extends OwnedCellObserver {
    async capture(budget) {
        const first = await super.capture(budget);
        const second = await super.capture(budget);
        this.lastAttempt = { first, second };
        check(same(first, second), 'paired reads disagree');
        return first;
    }
    async handleRequest(request, budget) {
        if (request.kind !== 'ce-read') {
            check(request.cpr === undefined, 'unexpected CPR');
            return super.handle(request.kind, budget, request.barrier);
        }
        return this.captureWithCursorReport(request, budget);
    }
    async captureWithCursorReport(request, budget) {
        const cpr = validateCursorReport(request.cpr);
        check(cpr.barrier === request.barrier, 'CPR barrier mismatch');
        const result = await super.handle(request.kind, budget, request.barrier);
        this.frames.at(-1).cpr = cpr;
        return result;
    }
}
