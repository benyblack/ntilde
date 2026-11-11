// Synthetic, fully valid title-stack evidence for portable tests. Every read is built from the suite's own title-stack oracle (the model App agrees with it by
// construction), so this fixture exercises the journal, report and receipt plumbing and the sensitivity of the graders to tampering, not the oracle itself:
// the oracle is checked against the real parser separately, and the frozen real-App publication is the evidence.
import { screenSnapshot, snapshotHash } from '../agent-observer.mjs';
import { planFeeds } from '../graded-plan.mjs';
import { copyMetadataStatus, copyPaneMetadata } from '../metadata-state.mjs';
import { metadataProjection } from '../metadata.mjs';
import { expectedTitleStackScreen, makeTitleStackExperiments, replayTitleStackTerminal, replayTitleStackTermless, simulateTitleStack, titleStackCaseIds, titleStackFixture,
    titleStackNonceRow, titleStackPinned, titleStackPrefix } from '../title-stack-contracts.mjs';
import { titleStackAdapterId } from '../title-stack-grading-collector.mjs';
import { colEditCols, colEditRows, paletteBinding, syntheticColEditScreen } from './col-edit-evidence.mjs';

export const titleStackPaneId = '0f0e0d0c-0b0a-4009-8008-700600500400';
const barrier = '\x1b[?1;2c';

/** A valid pristine initial main screen with the given theme binding, in the shape the metadata observer retains (cell details, cursor mode, buffer state, colors). */
function blankScreen(binding) {
    const { attributeRuns, ...screen } = syntheticColEditScreen([], [0, 0], undefined, [], binding);
    return screenSnapshot({ ...screen, cursorMode: { style: 2, blink: true, isDefault: true } }, false, true, true, true, true, true);
}
const withOverride = binding => ({ ...binding, paletteOverrideIndices: [7] });

function wire(runId, screen, state, { initial = false } = {}) {
    const fixture = titleStackFixture(runId), title = initial ? null : state.title;
    return { paneId: titleStackPaneId, oscTitle: title, workingDirectory: initial ? null : fixture.cwd, displayTitle: initial ? 'Owned fixture' : state.title,
        buffer: { rows: screen.rows, cols: screen.cols, cursorRow: screen.cursorRow, cursorCol: screen.cursorCol, alternateScreen: false, scrollbackLines: 0,
            historyGeneration: 4, rowsEvicted: 0, acceptingCommandInput: false, commandStart: null, outputStart: null, links: [], requestedIconName: initial ? '' : state.icon },
        presentation: { windowActive: false, windowVisible: true, paneActive: true, focusMode: false, notificationsEnabled: true, progressEnabled: true,
            tabTextVisible: true, tabText: title ?? 'Owned fixture', progressOwner: null, progressKind: null, progressPercent: null, progressBar: null,
            toastVisible: false, toastTitle: '', toastMessage: '' } };
}
const statusOf = ({ initial = false } = {}) => ({ paneId: titleStackPaneId, status: 'awaitingInput', confidence: initial ? 'heuristic' : 'precise', currentCommand: null,
    exitCode: null, statusSinceMs: 100, lastOutputAtMs: 101, isStalled: false, stallThresholdSeconds: 30, idleThresholdSeconds: 60 });

/** A paired frame of `screen` and the oracle `state`. */
function frameOf(runId, index, kind, screen, state, options = {}) {
    const metadata = copyPaneMetadata(wire(runId, screen, state, options)), rawStatus = statusOf(options), status = copyMetadataStatus(rawStatus, titleStackPaneId);
    const before = { screen, metadata, status, rawStatus }, sha256 = snapshotHash(metadataProjection(before));
    return { index, kind, paneId: titleStackPaneId, barrier: index === 0 ? null : barrier, before, sha256, after: structuredClone(before), afterSha256: sha256, caseId: null, stageId: null };
}

/** A complete, valid synthetic run: frames, worker run log and adapter facts. `adjust(experimentId, label, state)` may change the model App's answer to one read. */
export function syntheticTitleStackEvidence({ runId = 'a'.repeat(32), adjust = null, bundleSha256 = 'f'.repeat(64) } = {}) {
    const fixture = titleStackFixture(runId), experiments = makeTitleStackExperiments(runId);
    const initialBinding = paletteBinding(), boundBinding = withOverride(initialBinding);
    const blank = blankScreen(initialBinding);
    const baseline = simulateTitleStack([fixture.stage], fixture);
    const bound = expectedTitleStackScreen(blankScreen(boundBinding), baseline);
    const frames = [frameOf(runId, 0, `${titleStackPrefix}-start`, blank, baseline, { initial: true }), frameOf(runId, 1, `${titleStackPrefix}-bind`, bound, baseline)];
    const run = { schemaVersion: 1, adapterId: titleStackAdapterId, probeIds: [...titleStackCaseIds], experiments: [], pinned: [] };
    const observed = new Map();
    for (const contract of experiments) {
        const feeds = [], reads = [];
        for (const step of contract.plan) {
            if (step.feed !== undefined) { feeds.push(step.feed); continue; }
            const state = structuredClone(simulateTitleStack(feeds, fixture));
            adjust?.(contract.caseId, step.read, state);
            const screen = expectedTitleStackScreen(bound, state);
            frames.push(frameOf(runId, frames.length, `${titleStackPrefix}-read`, screen, state));
            reads.push({ kind: step.read, cursor: [screen.cursorRow, screen.cursorCol], facts: { title: state.title, icon: state.icon } });
            observed.set(`${contract.caseId}/${step.read}`, state);
        }
        run.experiments.push({ caseId: contract.caseId, feeds: planFeeds(contract), barriers: planFeeds(contract).map(() => barrier), reads });
    }
    // The finish frame restores the prepared title and icon baseline: the hidden initial null metadata is not restored.
    frames.push(frameOf(runId, frames.length, `${titleStackPrefix}-finish`, blank, baseline));
    for (const caseId of titleStackCaseIds) {
        const titles = Object.fromEntries(titleStackPinned[caseId].reads.map(([name]) => [name, observed.get(`${caseId}/stock/${name}`).title]));
        run.pinned.push({ caseId, termless: { feeds: [...titleStackPinned[caseId].termlessFeeds], result: replayTitleStackTermless(caseId, titles) },
            terminal: { writes: [...titleStackPinned[caseId].terminalWrites], result: replayTitleStackTerminal(caseId, true) } });
    }
    const adapter = { id: titleStackAdapterId, derivedFromAdapterId: 'ntilde-owned-left-right-margins-grading-v1', bundleSha256,
        upstreamBundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6' };
    return { frames, run, runId, adapter, nonceRow: titleStackNonceRow, rows: colEditRows, cols: colEditCols };
}
