// Exact Core default-tab-stop fixtures (`text.tab`, `unicode.tab-stops`). Coordinates are physical and
// zero-based; CPR replies are one-based. The expected positions come from the specification model below
// ("a stop at every eighth column, never column 0"), not from the production tab-stop implementation.
// A stop installed with HTS is never evidence of the default configuration: the target specimens are written
// only after a documented full reset (RIS) that follows a measured custom-stop state.
import { scrollbackReset, scrollbackView } from './scrollback-contracts.mjs';
import { validateGradedJournal, planFeed, planRead } from './graded-plan.mjs';
import { screenSnapshot, OwnedCellObserver } from './agent-observer.mjs';
import { validateCursorReport } from './cursor-report.mjs';

export const tabStopsSuiteId = 'ntilde-core-tab-stops-v1';
export const tabStopsNonce = 'NTILDE_TS_';
export const tabStopsIds = Object.freeze(['text.tab', 'unicode.tab-stops']);
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const check = (condition, message) => { if (!condition) throw new Error('Core tab stops: ' + message); };

const reset = scrollbackReset.replace('\x1b[?1049l', '') + '\x1b[?69l\x1b[?7h\x1b[?25h';
const guardRow = 9;
const marker = '\x1b[10;1HGUARD\x1b[H';
const setup = reset + marker;
/** RIS (ESC c): the full reset to the power-up state. DECSTR (CSI ! p) is deliberately not used; no claim is made about it. */
export const fullReset = '\x1bc';
/** The deliberately custom configuration: TBC 3 clears every stop, then HTS at one-based columns 5 and 21 (zero-based 4 and 20). */
export const customStopColumns = Object.freeze([4, 20]);
const customStops = '\x1b[3g\x1b[1;5H\x1bH\x1b[1;21H\x1bH\x1b[H';
const seedText = 'ABCDEFGHIJ';

/** Specification model of the power-up configuration: every eighth column, never column 0, within the measured width. */
export function defaultStopColumns(cols) {
    const stops = [];
    for (let col = 8; col < cols; col += 8) stops.push(col);
    return stops;
}
/** The next stop strictly right of `col`, or the last column when none remains. */
export function nextStop(stops, col, cols) {
    return stops.find(stop => stop > col) ?? cols - 1;
}
/** A walk of every default stop: TAB then a marker at each stop that leaves room for the marker, then one clamping TAB. */
export function defaultWalk(cols) {
    const stops = defaultStopColumns(cols), cells = new Array(cols).fill(' ');
    let feed = '', col = 0;
    for (;;) {
        const next = nextStop(stops, col, cols);
        if (next >= cols - 1) break;
        feed += '\t*';
        cells[next] = '*';
        col = next + 1;
    }
    feed += '\t';
    return { feed, line: cells.join('').trimEnd(), cursor: [0, nextStop(stops, col, cols)] };
}

/** Each entry: role, feed, expected first rows and cursor. Every feed is one fenced write followed by one observation. */
export function tabStopsSteps(cols) {
    const walk = defaultWalk(cols);
    const spaces = ' '.repeat(8);
    const steps = [
        // Initial state as found: no tab-stop write of this suite precedes it. Corroboration only; it is not the baseline.
        { role: 'found:walk', feed: setup + walk.feed, lines: [walk.line], cursor: walk.cursor },
        // Measured custom state: a stop at a different column, and cleared default stops.
        { role: 'taint:set', feed: setup + customStops, lines: [], cursor: [0, 0] },
        { role: 'taint:ht1', feed: '\t', lines: [], cursor: [0, customStopColumns[0]] },
        { role: 'taint:ht2', feed: '\t', lines: [], cursor: [0, customStopColumns[1]] },
        { role: 'taint:ht3', feed: '\t', lines: [], cursor: [0, cols - 1] },
        // The documented reset path, then independent proof that every default stop is back.
        { role: 'reset:ris', feed: fullReset + '\x1b[?47h' + setup, lines: [], cursor: [0, 0] },
        { role: 'reset:walk', feed: setup + walk.feed, lines: [walk.line], cursor: walk.cursor },
        // Target specimens, each on a freshly cleared row with an independent setup observation.
        { role: 'ht:setup', feed: setup, lines: [], cursor: [0, 0] },
        { role: 'ht:result', feed: '\t', lines: [], cursor: [0, 8] },
        { role: 'tab:setup', feed: setup, lines: [], cursor: [0, 0] },
        { role: 'tab:result', feed: '\tX', lines: [spaces + 'X'], cursor: [0, 9] },
        // Controls that reject false positives: X without TAB, spaces versus movement on seeded cells.
        { role: 'ctl:x:setup', feed: setup, lines: [], cursor: [0, 0] },
        { role: 'ctl:x', feed: 'X', lines: ['X'], cursor: [0, 1] },
        { role: 'ctl:spaces:setup', feed: setup, lines: [], cursor: [0, 0] },
        { role: 'ctl:spaces', feed: spaces + 'X', lines: [spaces + 'X'], cursor: [0, 9] },
        { role: 'ctl:tab-seed', feed: setup + seedText + '\x1b[H', lines: [seedText], cursor: [0, 0] },
        { role: 'ctl:tab-preserves', feed: '\tX', lines: ['ABCDEFGHXJ'], cursor: [0, 9] },
        { role: 'ctl:spaces-seed', feed: setup + seedText + '\x1b[H', lines: [seedText], cursor: [0, 0] },
        { role: 'ctl:spaces-overwrite', feed: spaces + 'X', lines: [spaces + 'XJ'], cursor: [0, 9] },
        // No control or target changed the default stops.
        { role: 'final:walk', feed: setup + walk.feed, lines: [walk.line], cursor: walk.cursor },
    ];
    return steps;
}

export function tabStopsPlan(cols) {
    return tabStopsSteps(cols).flatMap(step => [planFeed(step.feed), { ...planRead('state'), role: step.role }]);
}

/** The stable terminal state every observation must share: nothing that could change HT addressing. */
function stable(v, margins) {
    return v.alternate && !v.origin && !v.insert && v.autoWrap && !v.pending && v.cursorVisible
        && v.top === 0 && v.bottom === v.rows - 1 && v.count === 0 && v.history.length === 0 && v.storedDefault
        && margins?.leftRightMarginMode === false && margins.leftMargin === 0 && margins.rightMargin === v.cols - 1;
}
function expectedLines(rows, first) {
    const lines = new Array(rows).fill('');
    first.forEach((line, i) => { lines[i] = line; });
    lines[guardRow] = 'GUARD';
    return lines;
}
function matches(frame, step) {
    const v = scrollbackView(frame.screen);
    return stable(v, frame.screen.horizontalMargins) && same(v.lines, expectedLines(v.rows, step.lines)) && same(v.cursor, step.cursor);
}
/** The stored cells of the first row before the cursor target stay blank (never written). */
function blankPrefix(frame, count) {
    return frame.screen.lines[0].slice(0, count) === ' '.repeat(count);
}

/** Grade from complete, ordered observations. A missing or reordered role is an invalid journal, never a verdict. */
export function gradeTabStops(roles, cols) {
    const steps = Object.fromEntries(tabStopsSteps(cols).map(step => [step.role, step]));
    for (const role of Object.keys(steps)) check(roles[role], 'missing observation: ' + role);
    const ok = role => matches(roles[role], steps[role]);
    const setups = ['taint:set', 'reset:ris', 'ht:setup', 'tab:setup', 'ctl:x:setup', 'ctl:spaces:setup', 'ctl:tab-seed', 'ctl:spaces-seed'];
    for (const role of setups) check(ok(role), 'setup observation invalid: ' + role);
    // The custom-stop controls prove the observation path distinguishes a measured non-default state.
    const tainted = ['taint:ht1', 'taint:ht2', 'taint:ht3'].every(ok);
    // After RIS every default stop must be back, and still be there after all controls.
    const restored = ok('reset:walk');
    const intact = ok('final:walk');
    const controls = ['ctl:x', 'ctl:spaces', 'ctl:tab-preserves', 'ctl:spaces-overwrite'].every(ok);
    const asFound = ok('found:walk');
    const provenance = { strategy: 'documented-reset-restoration', reset: { sequence: 'ESC c (RIS)', bytes: fullReset },
        customStatePrecededReset: tainted, defaultStopsRestoredAfterReset: restored, defaultStopsIntactAfterControls: intact,
        asFoundWalkMatchesDefault: asFound };
    const proof = tainted && restored && intact;
    const verdict = (valid, effect) => {
        if (!proof) return { outcome: 'inconclusive', reason: 'default-state-not-established' };
        if (!valid) return { outcome: 'inconclusive', reason: 'observation-controls-invalid' };
        return { outcome: effect ? 'supported' : 'unsupported' };
    };
    const ht = ok('ht:result');
    const tab = ok('tab:result') && blankPrefix(roles['tab:result'], 8) && roles['tab:result'].screen.lines[0][8] === 'X';
    return {
        provenance,
        effects: {
            'text.tab': { ...verdict(controls, tab), id: 'text.tab', layer: 'stored physical cells and cursor' },
            'unicode.tab-stops': { ...verdict(true, ht), id: 'unicode.tab-stops', layer: 'physical cursor and stored cells' },
        },
    };
}

const specimenFor = {
    'text.tab': { fixture: '\tX', expected: { lines: [' '.repeat(8) + 'X'], cursor: [0, 9] }, before: 'tab:setup', after: 'tab:result' },
    'unicode.tab-stops': { fixture: '\t', expected: { lines: [], cursor: [0, 8] }, before: 'ht:setup', after: 'ht:result' },
};
export const tabStopsSpecimens = Object.freeze(specimenFor);

const observedFrame = frame => ({ lines: frame.screen.lines.slice(0, 2), guard: frame.screen.lines[guardRow],
    cursor: [frame.screen.cursorRow, frame.screen.cursorCol], cpr: frame.cpr.reply });

/** Re-derive from the trusted plan and parent-journaled observations, never the worker's reported grade. */
export function buildTabStopsReport(frames, run, runId) {
    check(run?.suiteId === tabStopsSuiteId && run.runId === runId, 'identity mismatch');
    check(Array.isArray(frames) && frames.length > 0, 'incomplete journal');
    const cols = frames[0].screen.cols;
    check(Number.isSafeInteger(cols) && cols >= 48 && cols <= 256, 'unsupported geometry');
    const plan = tabStopsPlan(cols), reads = plan.filter(step => step.read);
    validateGradedJournal(frames, { frameCount: reads.length + 3, prefix: 'ts', noncePrefix: tabStopsNonce,
        runId, label: 'Core tab stops', screenOf: s => screenSnapshot(s, true, true, false, true, true, true, false, false, true) });
    check(same(run.feeds, plan.filter(step => step.feed !== undefined).map(step => step.feed)), 'fixture bytes changed');
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
    const { provenance, effects } = gradeTabStops(roles, cols);
    for (const id of tabStopsIds) {
        const specimen = specimenFor[id];
        Object.assign(effects[id], { fixture: specimen.fixture, expected: specimen.expected,
            observed: { before: observedFrame(roles[specimen.before]), after: observedFrame(roles[specimen.after]) } });
    }
    const controls = Object.fromEntries(['found:walk', 'taint:ht1', 'taint:ht2', 'taint:ht3', 'reset:walk', 'final:walk', 'ctl:x', 'ctl:spaces',
        'ctl:tab-preserves', 'ctl:spaces-overwrite'].map(role => [role, observedFrame(roles[role])]));
    return { schemaVersion: 1, suiteId: tabStopsSuiteId, runId, cols, provenance, effects, controls, snapshots: frames };
}

/** Execute every write and its observation in order; restoration (a full reset) is attempted on every path. */
export async function collectTabStops({ config, fence, owner }) {
    const feeds = [], barriers = [], reads = [];
    const started = await owner('ts-start');
    check(started.screen.rows >= 12 && started.screen.cols >= 48, 'geometry too small');
    let primary;
    try {
        await owner('ts-bind', await fence.write('\x1b[?47h' + reset + tabStopsNonce + config.runId));
        for (const step of tabStopsPlan(started.screen.cols)) {
            if (step.feed !== undefined) {
                feeds.push(step.feed);
                barriers.push(await fence.write(step.feed)); // NOSONAR: fixture writes and reads must remain serial.
            } else {
                const cpr = await fence.cursorReport(); // NOSONAR: each CPR orders after its fixture write.
                await owner('ts-read', cpr.barrier, { cpr }); // NOSONAR: observation must finish before the next write.
                reads.push({ role: step.role, cpr });
            }
        }
    } catch (error) { primary = error; }
    try {
        // RIS first: even an aborted run must not leave custom stops behind for the following suites.
        await owner('ts-finish', await fence.write(fullReset + '\x1b[?47h' + reset + '\x1b7\x1b[?47l'));
    } catch (error) {
        if (primary) throw new AggregateError([primary, error], 'Core tab stops collection and restoration failed.');
        throw error;
    }
    if (primary) throw primary;
    return { schemaVersion: 1, suiteId: tabStopsSuiteId, runId: config.runId, feeds, barriers, reads };
}

/** Two complete reads must agree before a frame can enter the journal. */
export class TabStopsObserver extends OwnedCellObserver {
    async capture(budget) {
        const first = await super.capture(budget);
        const second = await super.capture(budget);
        this.lastAttempt = { first, second };
        check(same(first, second), 'paired reads disagree');
        return first;
    }
    async handleRequest(request, budget) {
        if (request.kind === 'ts-read') {
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
