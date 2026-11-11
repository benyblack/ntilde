// Synthetic, fully valid OSC 133 evidence for portable tests. Every read is built from the suite's own lifecycle oracle (the model App agrees with it by
// construction), so this fixture exercises the journal, report and receipt plumbing and the sensitivity of the graders to tampering, not the oracle itself:
// the oracle is checked against the real parser separately, and the frozen real-App publication is the evidence.
import { screenSnapshot, snapshotHash } from '../agent-observer.mjs';
import { planFeeds } from '../graded-plan.mjs';
import { copyMetadataStatus, copyPaneMetadata } from '../metadata-state.mjs';
import { metadataProjection } from '../metadata.mjs';
import { expectedOsc133Screen, makeOsc133Experiments, osc133CaseIds, osc133Fixture, osc133NonceRow, osc133Pinned, osc133Prefix, replayOsc133Pinned, simulateOsc133 } from '../osc133-contracts.mjs';
import { osc133AdapterId } from '../osc133-grading-collector.mjs';
import { colEditCols, colEditRows, paletteBinding, syntheticColEditScreen } from './col-edit-evidence.mjs';

export const osc133PaneId = '0f0e0d0c-0b0a-4009-8008-700600500400';
const barrier = '\x1b[?1;2c';
const rows = colEditRows, cols = colEditCols;

/** A valid pristine initial main screen with the given theme binding, in the shape the metadata observer retains (cell details, cursor mode, buffer state, colors). */
function blankScreen(binding) {
    const { attributeRuns, ...screen } = syntheticColEditScreen([], [0, 0], undefined, [], binding);
    return screenSnapshot({ ...screen, cursorMode: { style: 2, blink: true, isDefault: true } }, false, true, true, true, true, true);
}
const withOverride = binding => ({ ...binding, paletteOverrideIndices: [7] });

const mark = (value, generation) => value === null ? null : { row: value[0], col: value[1], absoluteRow: value[0], generation, alternateScreen: false };

function wire(runId, screen, state, { initial = false } = {}) {
    const fixture = osc133Fixture(runId), title = initial ? null : fixture.title;
    return { paneId: osc133PaneId, oscTitle: title, workingDirectory: initial ? null : fixture.cwd, displayTitle: initial ? 'Owned fixture' : fixture.title,
        buffer: { rows: screen.rows, cols: screen.cols, cursorRow: screen.cursorRow, cursorCol: screen.cursorCol, alternateScreen: false, scrollbackLines: 0,
            historyGeneration: 4, rowsEvicted: 0, acceptingCommandInput: state.accepting, commandStart: mark(state.commandStart, 4), outputStart: mark(state.outputStart, 4), links: [] },
        presentation: { windowActive: false, windowVisible: true, paneActive: true, focusMode: false, notificationsEnabled: true, progressEnabled: true,
            tabTextVisible: true, tabText: title ?? 'Owned fixture', progressOwner: null, progressKind: null, progressPercent: null, progressBar: null,
            toastVisible: false, toastTitle: '', toastMessage: '' } };
}
function statusOf(state, { initial = false } = {}) {
    return { paneId: osc133PaneId, status: state.status === 'running' ? 'running' : 'awaitingInput', confidence: initial ? 'heuristic' : 'precise', currentCommand: state.command,
        exitCode: null, statusSinceMs: 100, lastOutputAtMs: 101, isStalled: false, stallThresholdSeconds: 30, idleThresholdSeconds: 60 };
}

/** A paired frame of `screen` and the oracle `state`. */
function frameOf(runId, index, kind, screen, state, options = {}) {
    const metadata = copyPaneMetadata(wire(runId, screen, state, options)), rawStatus = statusOf(state, options), status = copyMetadataStatus(rawStatus, osc133PaneId);
    const before = { screen, metadata, status, rawStatus }, sha256 = snapshotHash(metadataProjection(before));
    return { index, kind, paneId: osc133PaneId, barrier: index === 0 ? null : barrier, before, sha256, after: structuredClone(before), afterSha256: sha256, caseId: null, stageId: null };
}

/** A complete, valid synthetic run: frames, worker run log and adapter facts. `adjust(experimentId, label, state)` may change the model App's answer to one read. */
export function syntheticOsc133Evidence({ runId = 'a'.repeat(32), adjust = null, bundleSha256 = 'f'.repeat(64) } = {}) {
    const fixture = osc133Fixture(runId), experiments = makeOsc133Experiments(runId);
    const initialBinding = paletteBinding(), boundBinding = withOverride(initialBinding);
    const blank = blankScreen(initialBinding);
    const baseline = simulateOsc133([fixture.stage], fixture);
    const bound = expectedOsc133Screen(blankScreen(boundBinding), baseline);
    const frames = [frameOf(runId, 0, `${osc133Prefix}-start`, blank, baseline, { initial: true }), frameOf(runId, 1, `${osc133Prefix}-bind`, bound, baseline)];
    const run = { schemaVersion: 1, adapterId: osc133AdapterId, probeIds: [...osc133CaseIds], experiments: [], pinned: [] };
    const stockScreens = new Map();
    for (const contract of experiments) {
        const feeds = [], reads = [];
        for (const step of contract.plan) {
            if (step.feed !== undefined) { feeds.push(step.feed); continue; }
            const state = structuredClone(simulateOsc133(feeds, fixture));
            adjust?.(contract.caseId, step.read, state);
            const screen = expectedOsc133Screen(bound, state);
            frames.push(frameOf(runId, frames.length, `${osc133Prefix}-read`, screen, state));
            reads.push({ kind: step.read, cursor: [screen.cursorRow, screen.cursorCol] });
            if (contract.name === 'stock') stockScreens.set(contract.owner, screen);
        }
        run.experiments.push({ caseId: contract.caseId, feeds: planFeeds(contract), barriers: planFeeds(contract).map(() => barrier), reads });
    }
    frames.push(frameOf(runId, frames.length, `${osc133Prefix}-finish`, blank, baseline));
    // The finish frame restores the prepared title/cwd baseline: the hidden initial null metadata is not restored.
    for (const caseId of osc133CaseIds) {
        const replay = replayOsc133Pinned(caseId, stockScreens.get(caseId));
        run.pinned.push({ caseId, termless: { feeds: [...osc133Pinned[caseId].termlessFeeds], result: replay.termless },
            terminal: { writes: [...osc133Pinned[caseId].terminalWrites], result: replay.terminal } });
    }
    const adapter = { id: osc133AdapterId, derivedFromAdapterId: 'ntilde-owned-left-right-margins-grading-v1', bundleSha256,
        upstreamBundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6' };
    return { frames, run, runId, adapter, nonceRow: osc133NonceRow };
}
