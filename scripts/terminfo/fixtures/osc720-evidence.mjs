// Synthetic, fully valid OSC 720 evidence for portable tests. Every read is built from a small model App (never a collected observation): by default it ignores OSC 720 like Ntilde and honors the
// wheel and ED 3 like the controls need; `behavior: 'rxvt'` makes it follow the rxvt text instead. The fixture exercises the journal, report and receipt plumbing and the sensitivity of the
// graders to tampering, not the oracle itself; the frozen real-App publication is the evidence.
import { screenSnapshot, snapshotHash } from '../agent-observer.mjs';
import { planFeeds } from '../graded-plan.mjs';
import { copyMetadataStatus, copyPaneMetadata } from '../metadata-state.mjs';
import { metadataProjection } from '../metadata.mjs';
import { exposeOsc720Facts } from '../osc720-collector.mjs';
import { osc720AdapterId } from '../osc720-grading-collector.mjs';
import { replayOsc720TerminalStatic, replayOsc720TermlessStatic } from '../osc720-grading.mjs';
import { osc720ActionDeltas, osc720CaseId, osc720CaseIds, osc720Experiments, osc720Fixture, osc720NonceRow } from '../osc720-contracts.mjs';
import { colEditRows, paletteBinding, syntheticColEditScreen } from './col-edit-evidence.mjs';

export const osc720PaneId = '0f0e0d0c-0b0a-4009-8008-700600500400';
const barrier = '\x1b[?1;2c';
/** The retained history of the fixed fill on the synthetic grid, and the rows one wheel scroll moves the view. */
export const syntheticHistory = 30;
export const syntheticWheelRows = 3;

function blankScreen() {
    const { attributeRuns, ...screen } = syntheticColEditScreen([], [0, 0], undefined, [], paletteBinding());
    return screenSnapshot({ ...screen, cursorMode: { style: 2, blink: true, isDefault: true } }, false, true, true, true, true, true);
}

/** One observed state of the model App. */
function stateScreen(base, state) {
    const screen = structuredClone(base);
    screen.lines = Array.from({ length: colEditRows }, (_, row) => state.lines[row] ?? '');
    screen.cursorRow = state.cursor[0]; screen.cursorCol = state.cursor[1];
    screen.bufferState = { ...screen.bufferState, scrollbackLines: state.scrollback };
    if (state.cursorStyle !== undefined) screen.cursorMode = { ...screen.cursorMode, style: state.cursorStyle };
    screen.history = { startLine: 0, totalLines: state.scrollback, lines: Array.from({ length: state.scrollback }, (_, index) => `scroll-${index}`) };
    return screen;
}
function wire(screen, state) {
    return { paneId: osc720PaneId, oscTitle: null, workingDirectory: null, displayTitle: 'Owned fixture',
        buffer: { rows: screen.rows, cols: screen.cols, cursorRow: screen.cursorRow, cursorCol: screen.cursorCol, alternateScreen: false, scrollbackLines: state.scrollback,
            historyGeneration: state.generation, rowsEvicted: 0, acceptingCommandInput: false, commandStart: null, outputStart: null, links: [] },
        viewport: { scrollOffset: state.offset, maxScrollOffset: state.scrollback },
        presentation: { windowActive: false, windowVisible: true, paneActive: true, focusMode: false, notificationsEnabled: true, progressEnabled: true,
            tabTextVisible: true, tabText: 'Owned fixture', progressOwner: null, progressKind: null, progressPercent: null, progressBar: null,
            toastVisible: false, toastTitle: '', toastMessage: '' } };
}
const statusOf = () => ({ paneId: osc720PaneId, status: 'awaitingInput', confidence: 'precise', currentCommand: null,
    exitCode: null, statusSinceMs: 100, lastOutputAtMs: 101, isStalled: false, stallThresholdSeconds: 30, idleThresholdSeconds: 60 });

function frameOf(base, index, kind, state) {
    const screen = stateScreen(base, state), metadata = copyPaneMetadata(wire(screen, state)), rawStatus = statusOf(), status = copyMetadataStatus(rawStatus, osc720PaneId);
    const before = { screen, metadata, status, rawStatus }, sha256 = snapshotHash(metadataProjection(before));
    return { index, kind, paneId: osc720PaneId, barrier: index === 0 ? null : barrier, before, sha256, after: structuredClone(before), afterSha256: sha256, caseId: null, stageId: null };
}

const wheelRecord = action => ({ action, ok: true, ownerPid: 4242, notches: [{ request: { kind: 'mouseWheel', wheelDelta: osc720ActionDeltas[action], column: 1, row: 1, paneId: osc720PaneId }, eventDispatched: true, handled: true,
    boundary: 'avalonia-routed', observationAvailable: true, postVerified: true, observedAt: '2026-11-17T12:00:00.000Z' }], startedAt: '2026-11-17T12:00:00.000Z', finishedAt: '2026-11-17T12:00:01.500Z' });

/** The model App applying one probe. `behavior` is 'ignore' (Ntilde) or 'rxvt' (the reference text). */
function applyProbe(contract, state, behavior) {
    const next = { ...state };
    if (contract.probe === '\x1b[3J') { next.scrollback = 0; next.offset = 0; next.generation += 1; return next; }
    if (behavior !== 'rxvt' || contract.expected.kind === 'none') return next;
    if (contract.expected.kind === 'erase-saved') { next.scrollback = 0; next.offset = 0; next.generation += 1; return next; }
    next.offset = Math.min(state.offset + contract.expected.count, state.scrollback);
    return next;
}

/** A complete, valid synthetic run: frames, worker run log and adapter facts. `adjust(contract, label, state)` may change the model App's answer to one read. */
export function syntheticOsc720Evidence({ runId = 'a'.repeat(32), behavior = 'ignore', adjust = null, bundleSha256 = 'f'.repeat(64) } = {}) {
    const fixture = osc720Fixture(runId), contracts = osc720Experiments(runId), base = blankScreen();
    const blank = { lines: [], cursor: [0, 0], scrollback: 0, offset: 0, generation: 4 };
    const bound = { ...blank, lines: Array.from({ length: osc720NonceRow + 1 }, (_, row) => (row === osc720NonceRow ? fixture.nonce : '')), cursor: [osc720NonceRow, fixture.nonce.length] };
    const frames = [frameOf(base, 0, 'ovu-start', blank), frameOf(base, 1, 'ovu-bind', bound)];
    const run = { schemaVersion: 1, adapterId: osc720AdapterId, probeIds: [...osc720CaseIds], experiments: [], pinned: null };
    let generation = 4;
    for (const contract of contracts) {
        const reads = [], actions = [];
        // Each experiment resets (ED 3 erases the history, so the history generation rises) and refills the fixed history.
        generation += 1;
        let state = { lines: ['scroll-28', 'scroll-29'], cursor: [colEditRows - 1, 0], scrollback: syntheticHistory, offset: 0, generation };
        for (const step of contract.plan.filter(item => item.action !== undefined)) actions.push({ action: step.action, response: wheelRecord(step.action) });
        const pushRead = (step, current) => {
            const shown = adjust ? adjust(contract, step.read, structuredClone(current)) : current;
            const frame = frameOf(base, frames.length, 'ovu-read', shown);
            frames.push(frame);
            reads.push({ kind: step.read, cursor: [shown.cursor[0], shown.cursor[1]], facts: exposeOsc720Facts(frame.before) });
        };
        const steps = contract.plan.filter(step => step.read !== undefined);
        if (contract.probe === null) {
            pushRead(steps[0], state);
            state = { ...state, offset: syntheticWheelRows };
            pushRead(steps[1], state);
        } else {
            if (contract.setup.scrolled) state = { ...state, offset: syntheticWheelRows };
            pushRead(steps[0], state);
            state = applyProbe(contract, state, behavior);
            pushRead(steps[1], state);
        }
        generation = state.generation;
        run.experiments.push({ caseId: contract.caseId, feeds: planFeeds(contract), barriers: planFeeds(contract).map(() => barrier), reads, ...(actions.length ? { actions } : {}) });
    }
    const bare = run.experiments.find(item => item.caseId === `${osc720CaseId}/pinned-bare`);
    if (bare === undefined) throw new Error('The synthetic run has no pinned-bare experiment.');
    const [before, after] = bare.reads.map(entry => entry.facts);
    run.pinned = { caseId: osc720CaseId, termless: replayOsc720TermlessStatic(before.rows, before, after), terminal: replayOsc720TerminalStatic() };
    frames.push(frameOf(base, frames.length, 'ovu-finish', { ...blank, generation: generation + 1 }));
    const actions = run.experiments.flatMap(item => item.actions ?? []).map(({ response: { ok, ownerPid, ...record } }) => record);
    return { frames, run, runId, actions, adapter: { id: osc720AdapterId, derivedFromAdapterId: 'ntilde-owned-left-right-margins-grading-v1',
        bundleSha256, upstreamBundleSha256: 'a'.repeat(64) } };
}
