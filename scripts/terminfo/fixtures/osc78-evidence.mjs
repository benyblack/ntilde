// Synthetic, fully valid OSC 7 / OSC 8 evidence for portable tests. Every read is built from the suite's own oracle (the model App agrees with it by
// construction), so this fixture exercises the journal, report and receipt plumbing and the sensitivity of the graders to tampering, not the oracle itself:
// the oracle is checked against the real parser separately, and the frozen real-App publication is the evidence.
import { screenSnapshot, snapshotHash } from '../agent-observer.mjs';
import { planFeeds } from '../graded-plan.mjs';
import { copyMetadataStatus, copyPaneMetadata } from '../metadata-state.mjs';
import { metadataProjection } from '../metadata.mjs';
import { expectedOsc78Screen, makeOsc78Experiments, osc78CaseIds, osc78Fixture, osc78NonceRow, osc78Pinned, osc78Prefix, replayOsc78Pinned, simulateOsc78 } from '../osc78-contracts.mjs';
import { osc78AdapterId } from '../osc78-grading-collector.mjs';
import { paletteBinding, syntheticColEditScreen } from './col-edit-evidence.mjs';

export const osc78PaneId = '0f0e0d0c-0b0a-4009-8008-700600500400';
const barrier = '\x1b[?1;2c';

/** A valid pristine initial main screen with the given theme binding, in the shape the metadata observer retains (cell details, cursor mode, buffer state, colors). */
function blankScreen(binding) {
    const { attributeRuns, ...screen } = syntheticColEditScreen([], [0, 0], undefined, [], binding);
    return screenSnapshot({ ...screen, cursorMode: { style: 2, blink: true, isDefault: true } }, false, true, true, true, true, true);
}
const withOverride = binding => ({ ...binding, paletteOverrideIndices: [7] });

function wire(runId, screen, state, { initial = false } = {}) {
    return { paneId: osc78PaneId, oscTitle: initial ? null : state.title, workingDirectory: initial ? null : state.cwd, displayTitle: initial ? 'Owned fixture' : state.title,
        buffer: { rows: screen.rows, cols: screen.cols, cursorRow: screen.cursorRow, cursorCol: screen.cursorCol, alternateScreen: false, scrollbackLines: 0,
            historyGeneration: 4, rowsEvicted: 0, acceptingCommandInput: false, links: state.runs.map(run => ({ ...run })) },
        presentation: { windowActive: false, windowVisible: true, paneActive: true, focusMode: false, notificationsEnabled: true, progressEnabled: true,
            tabTextVisible: true, tabText: initial ? 'Owned fixture' : state.title, progressOwner: null, progressKind: null, progressPercent: null, progressBar: null,
            toastVisible: false, toastTitle: '', toastMessage: '' } };
}
function statusOf({ initial = false } = {}) {
    return { paneId: osc78PaneId, status: 'awaitingInput', confidence: initial ? 'heuristic' : 'precise', currentCommand: null,
        exitCode: null, statusSinceMs: 100, lastOutputAtMs: 101, isStalled: false, stallThresholdSeconds: 30, idleThresholdSeconds: 60 };
}

/** A paired frame of `screen` and the oracle `state`. */
function frameOf(runId, index, kind, screen, state, options = {}) {
    const metadata = copyPaneMetadata(wire(runId, screen, state, options)), rawStatus = statusOf(options), status = copyMetadataStatus(rawStatus, osc78PaneId);
    const before = { screen, metadata, status, rawStatus }, sha256 = snapshotHash(metadataProjection(before));
    return { index, kind, paneId: osc78PaneId, barrier: index === 0 ? null : barrier, before, sha256, after: structuredClone(before), afterSha256: sha256, caseId: null, stageId: null };
}

/** A complete, valid synthetic run: frames, worker run log and adapter facts. `adjust(experimentId, label, state)` may change the model App's answer to one read. */
export function syntheticOsc78Evidence({ runId = 'a'.repeat(32), adjust = null, bundleSha256 = 'f'.repeat(64) } = {}) {
    const fixture = osc78Fixture(runId), experiments = makeOsc78Experiments(runId);
    const initialBinding = paletteBinding(), boundBinding = withOverride(initialBinding);
    const blank = blankScreen(initialBinding);
    const baseline = simulateOsc78([fixture.stage], fixture, runId);
    const bound = expectedOsc78Screen(blankScreen(boundBinding), baseline);
    const frames = [frameOf(runId, 0, `${osc78Prefix}-start`, blank, baseline, { initial: true }), frameOf(runId, 1, `${osc78Prefix}-bind`, bound, baseline)];
    const run = { schemaVersion: 1, adapterId: osc78AdapterId, probeIds: [...osc78CaseIds], experiments: [], pinned: [] };
    const stock = new Map();
    for (const contract of experiments) {
        const feeds = [], reads = [];
        for (const step of contract.plan) {
            if (step.feed !== undefined) { feeds.push(step.feed); continue; }
            const state = structuredClone(simulateOsc78(feeds, fixture, runId));
            adjust?.(contract.caseId, step.read, state);
            const screen = expectedOsc78Screen(bound, state);
            frames.push(frameOf(runId, frames.length, `${osc78Prefix}-read`, screen, state));
            reads.push({ kind: step.read, cursor: [screen.cursorRow, screen.cursorCol] });
            if (contract.name === 'stock') stock.set(contract.owner, { screen, links: state.runs });
        }
        run.experiments.push({ caseId: contract.caseId, feeds: planFeeds(contract), barriers: planFeeds(contract).map(() => barrier), reads });
    }
    frames.push(frameOf(runId, frames.length, `${osc78Prefix}-finish`, blank, baseline));
    // The finish frame restores the prepared title/cwd baseline: the hidden initial null metadata is not restored.
    for (const caseId of osc78CaseIds) {
        const replay = replayOsc78Pinned(caseId, stock.get(caseId).screen, stock.get(caseId).links);
        run.pinned.push({ caseId, termless: { feeds: [...osc78Pinned[caseId].termlessFeeds], result: replay.termless },
            terminal: { writes: [...osc78Pinned[caseId].terminalWrites], result: replay.terminal } });
    }
    const adapter = { id: osc78AdapterId, derivedFromAdapterId: 'ntilde-owned-left-right-margins-grading-v1', bundleSha256,
        upstreamBundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6' };
    return { frames, run, runId, adapter, nonceRow: osc78NonceRow };
}
