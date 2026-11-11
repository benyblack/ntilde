import { screenSnapshot } from './agent-observer.mjs';
import { scrollbackView } from './scrollback-contracts.mjs';
import { validateGradedJournal } from './graded-plan.mjs';
import { validateCursorReport } from './cursor-report.mjs';
import { validateWrapGeometry } from './core-wrap-geometry.mjs';

// Independent expectations for the frozen Core 80-column wrap fixture.
// Each boundary is a separate experiment: a fence between characters could clear
// the terminal's delayed-wrap latch. No conclusion is a receipt by itself.
export const specimen = '0123456789'.repeat(8) + 'ABCDE';
export const wrapCases = Object.freeze([
    { role: 'enabled-80', enabled: true, text: specimen.slice(0, 80), lines: [specimen.slice(0, 80), '', 'GUARD'], cursor: [0, 79] },
    { role: 'enabled-81', enabled: true, text: specimen.slice(0, 81), lines: [specimen.slice(0, 80), 'A', 'GUARD'], cursor: [1, 1] },
    { role: 'enabled-85', enabled: true, text: specimen, lines: [specimen.slice(0, 80), 'ABCDE', 'GUARD'], cursor: [1, 5] },
    { role: 'disabled-85', enabled: false, text: specimen, lines: [specimen.slice(0, 79) + 'E', '', 'GUARD'], cursor: [0, 79] },
]);
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const fail = message => { throw new Error('Core wrap: ' + message); };
const setup = enabled => '\x1b[?6l\x1b[r\x1b[?69l\x1b[4l\x1b[0m\x1b[2J\x1b[3;1HGUARD\x1b[H' + (enabled ? '\x1b[?7h' : '\x1b[?7l');
export function validateWrapFeeds(feeds) {
    const expected = wrapPlan().filter(step => step.feed).map(step => step.feed);
    if (!same(feeds, expected)) fail('fixture bytes changed');
}
export function wrapPlan() {
    return wrapCases.flatMap(c => [
        { feed: setup(c.enabled) }, { read: c.role + ':before' },
        { feed: c.text }, { read: c.role + ':after' },
    ]);
}
// Inputs are validated, independently observed screen projections. Restoration
// and source/geometry provenance must also be checked by the owning receipt.
export function gradeWrap(roles) {
    const results = {};
    for (const c of wrapCases) {
        const before = roles[c.role + ':before'], after = roles[c.role + ':after'];
        if (!before || !after) fail('missing observation: ' + c.role);
        const valid = v => Number.isInteger(v.rows) && v.rows >= 3 && v.cols === 80
            && Array.isArray(v.lines) && v.lines.length === v.rows
            && v.alternate === true && v.origin === false && v.insert === false
            && v.top === 0 && v.bottom === v.rows - 1 && v.left === 0 && v.right === 79
            && v.leftRightMarginMode === false && v.historyCount === 0 && Array.isArray(v.history) && v.history.length === 0
            && v.cursorVisible === true && v.storedDefault === true;
        if (!valid(before) || !valid(after) || before.rows !== after.rows) fail('invalid geometry or unrelated state: ' + c.role);
        const initial = Array(before.rows).fill(''); initial[2] = 'GUARD';
        if (!same(before.lines, initial) || !same(before.cursor, [0, 0]) || before.autoWrap !== c.enabled)
            fail('invalid setup: ' + c.role);
        const expected = Array(before.rows).fill(''); c.lines.forEach((line, i) => { expected[i] = line; });
        results[c.role] = { outcome: same(after.lines, expected) && same(after.cursor, c.cursor)
            && after.autoWrap === c.enabled ? 'supported' : 'unsupported',
        expected: { lines: expected, cursor: c.cursor }, observed: { lines: after.lines, cursor: after.cursor, autoWrap: after.autoWrap, pendingWrap: after.pendingWrap } };
    }
    return results;
}

/** Re-derive a fixed-geometry journal; the owning receipt must separately prove
 * the App and PTY dimensions, source identity, and resize/restoration actions. */
export function buildWrapReport(frames, run, runId) {
    if (run?.suiteId !== 'ntilde-core-wrap-v1' || run.runId !== runId) fail('identity mismatch');
    validateGradedJournal(frames, { frameCount: wrapCases.length * 2 + 3, prefix: 'cw',
        noncePrefix: 'NTILDE_CW_', runId, label: 'Core wrap',
        screenOf: s => screenSnapshot(s, true, true, false, true, true, true, false, false, true) });
    validateWrapFeeds(run.feeds);
    const plan = wrapPlan(), reads = plan.filter(step => step.read);
    if (!Array.isArray(run.barriers) || run.barriers.length !== run.feeds.length
        || run.barriers.some(x => !/^\x1b\[\?[\d;]+c$/.test(x))
        || !Array.isArray(run.reads) || run.reads.length !== reads.length) fail('incomplete capture');
    const roles = {};
    for (const [index, step] of reads.entries()) {
        const frame = frames[index + 2], record = run.reads[index];
        if (record?.role !== step.read || !same(record.cpr, frame.cpr)) fail('read order or CPR changed');
        const cpr = validateCursorReport(frame.cpr);
        if (cpr.barrier !== frame.barrier || cpr.reply === null
            || cpr.reply.row !== frame.screen.cursorRow + 1 || cpr.reply.col !== frame.screen.cursorCol + 1)
            fail('CPR disagrees with physical cursor');
        const view = scrollbackView(frame.screen), margins = frame.screen.horizontalMargins;
        roles[step.read] = { ...view, left: margins.leftMargin, right: margins.rightMargin,
            leftRightMarginMode: margins.leftRightMarginMode, historyCount: view.count, pendingWrap: view.pending };
    }
    return { schemaVersion: 1, suiteId: run.suiteId, runId, effects: gradeWrap(roles), snapshots: frames };
}

/** Re-derive the phase only after the enclosing owned receipt has verified
 * executable, source, settings, worker and PTY identities. */
export function deriveWrapEvidence(frames, run, runId, geometry) {
    const report = buildWrapReport(frames, run, runId);
    validateWrapGeometry({ ...geometry, runId, frames });
    for (const screen of [geometry.before, geometry.after]) {
        const validated = screenSnapshot(screen, true, true, false, true, true, true, false, false, true);
        if (!same(validated, screen)) fail('invalid original screen projection');
    }
    const initialMode = geometry.before?.bufferState?.autoWrap;
    if (typeof initialMode !== 'boolean' || frames[0].screen.bufferState.autoWrap !== initialMode
        || frames.at(-1).screen.bufferState.autoWrap !== initialMode) fail('original mode was not restored');
    return { ...report, geometry };
}
