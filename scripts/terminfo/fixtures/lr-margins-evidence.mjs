// Synthetic, fully valid left/right-margin grading evidence for portable tests. Screens are built from the suite's margins model, so
// this fixture exercises the journal, report and receipt plumbing, not the model itself: the model was checked against the VT
// implementation separately (every control's exact bytes through the parser), and the frozen real-App publication is the evidence.
import { snapshotHash } from '../agent-observer.mjs';
import { planFeeds } from '../graded-plan.mjs';
import { lrMarginsContracts, lrMarginsObservation, gradeLrMarginsCase, lrMarginsModel, lrMarginsFixture } from '../lr-margins-contracts.mjs';
import { replayLrMarginsPinned } from '../lr-margins-grading.mjs';
import { lrMarginsAdapterId } from '../lr-margins-grading-collector.mjs';
import { syntheticColEditScreen, colEditRows, colEditCols, paletteBinding, plainCell } from './col-edit-evidence.mjs';

const H = colEditRows, W = colEditCols;
const paneId = '0f0e0d0c-0b0a-4009-8008-700600500400';
const deviceAttribute = '\x1b[?62;4;22c';

/** One read as a screen: the model's grid, cursor, region, modes and margins. */
function screenOf(model, binding) {
    const rows = model.grid.map(line => [...line.join('').trimEnd()].map(char => plainCell(char)));
    const screen = syntheticColEditScreen(rows, [model.row, model.col], { margins: [model.top, model.bottom], origin: model.origin, insert: model.insert }, [], binding);
    return { ...screen, horizontalMargins: { leftRightMarginMode: model.mode, leftMargin: model.left, rightMargin: model.right } };
}

/** The model of every read of a conclusive, supported run; `mutate(role, model)` may change one to build negative evidence. */
export function syntheticLrMarginsScreens(binding = paletteBinding(), mutate = null) {
    const contract = lrMarginsContracts[0];
    const { MarginsModel, seededModel, modelOf } = lrMarginsModel;
    return contract.roles.map(role => {
        let model;
        if (role === 'initial' || role === 'reset') model = new MarginsModel(H, W);
        else if (role === 'set') { model = new MarginsModel(H, W); model.mode = true; }
        else if (role === 'blockBefore') model = seededModel(H, W);
        else model = modelOf(lrMarginsFixture.controls.find(item => item.role === role), H, W);
        mutate?.(role, model);
        return screenOf(model, binding);
    });
}

const frame = (index, kind, screen, barrier) => ({ index, kind, paneId, barrier, screen, sha256: snapshotHash(screen) });

/** A complete, valid synthetic run: frames, worker run log and adapter facts. */
export function syntheticLrMarginsEvidence(runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64), binding = paletteBinding(), mutate = null) {
    const contract = lrMarginsContracts[0];
    const nonce = 'NTILDE_LM_' + runId;
    const blank = { ...syntheticColEditScreen([], [0, 0], undefined, [], binding), horizontalMargins: { leftRightMarginMode: false, leftMargin: 0, rightMargin: W - 1 } };
    const bound = { ...blank, lines: [nonce, ...Array.from({ length: H - 1 }, () => '')], cursorCol: nonce.length };
    const frames = [frame(0, 'lrmm-start', blank, null), frame(1, 'lrmm-bind', bound, deviceAttribute)];
    const screens = syntheticLrMarginsScreens(binding, mutate);
    for (const screen of screens) frames.push(frame(frames.length, 'lrmm-read', screen, deviceAttribute));
    frames.push(frame(frames.length, 'lrmm-finish', blank, deviceAttribute));
    const feeds = planFeeds(contract);
    const pinned = { probeId: contract.caseId, ...replayLrMarginsPinned(contract, screens) };
    const run = { schemaVersion: 1, adapterId: lrMarginsAdapterId, probeIds: [contract.caseId],
        cases: [{ caseId: contract.caseId, feeds, barriers: feeds.map(() => deviceAttribute),
            reads: screens.map(screen => ({ kind: 'cells', cursor: [screen.cursorRow, screen.cursorCol] })), pinned }] };
    const adapter = { id: lrMarginsAdapterId, derivedFromAdapterId: 'ntilde-owned-rectangle-checksum-grading-v1', bundleSha256,
        upstreamBundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6' };
    return { frames, run, runId, adapter };
}

/** The independent effect verdict for the synthetic case. */
export function syntheticLrMarginsGrade(binding = paletteBinding(), mutate = null) {
    const contract = lrMarginsContracts[0];
    return gradeLrMarginsCase(contract, lrMarginsObservation(contract, syntheticLrMarginsScreens(binding, mutate)));
}
