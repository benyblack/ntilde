// Synthetic, fully valid OSC 3008 evidence for portable tests. Every read is built from the suite's own oracles (the model App agrees with them by construction), so this fixture exercises the
// journal, report and receipt plumbing and the sensitivity of the graders to tampering, not the oracles themselves: neither branch is an App measurement.
import { planFeeds } from '../graded-plan.mjs';
import { copyPaneMetadata } from '../metadata-state.mjs';
import { snapshotHash } from '../agent-observer.mjs';
import { metadataProjection } from '../metadata.mjs';
import { expectedOsc133Screen, osc133Fixture, osc133NonceRow, simulateOsc133 } from '../osc133-contracts.mjs';
import { makeOsc3008Experiments, osc3008CaseId, osc3008CaseIds, osc3008Prefix, replayOsc3008PinnedStatic, simulateContexts, withoutContextSequences } from '../osc3008-contracts.mjs';
import { osc3008AdapterId, osc3008DerivedFromAdapterId } from '../osc3008-grading-collector.mjs';
import { paletteBinding } from './col-edit-evidence.mjs';
import { blankScreen, frameOf, withOverride } from './osc133-evidence.mjs';
export { osc133PaneId } from './osc133-evidence.mjs';
const barrier = '\x1b[?1;2c';

/** `frame` with its pane metadata carrying `contexts` (the shape the App reports), re-hashed so the pair stays coherent. */
export function withContexts(frame, contexts) {
    const metadata = copyPaneMetadata({ ...frame.before.metadata, buffer: { ...frame.before.metadata.buffer, contexts: { untrusted: true, ...structuredClone(contexts) } } });
    const before = { ...frame.before, metadata }, sha256 = snapshotHash(metadataProjection(before));
    return { ...frame, before, sha256, after: structuredClone(before), afterSha256: sha256 };
}

/** A complete, valid synthetic run: frames, worker run log and adapter facts. `adjust(experimentId, label, { state, contexts })` may change the model App's answer to one read. */
export function syntheticOsc3008Evidence({ runId = 'a'.repeat(32), adjust = null, bundleSha256 = 'f'.repeat(64) } = {}) {
    const fixture = osc133Fixture(runId), experiments = makeOsc3008Experiments(runId);
    const initialBinding = paletteBinding(), boundBinding = withOverride(initialBinding);
    const blank = blankScreen(initialBinding);
    const baseline = simulateOsc133([fixture.stage], fixture);
    const bound = expectedOsc133Screen(blankScreen(boundBinding), baseline);
    const empty = { stack: [], ended: [] };
    const frames = [withContexts(frameOf(runId, 0, `${osc3008Prefix}-start`, blank, baseline, { initial: true }), empty),
        withContexts(frameOf(runId, 1, `${osc3008Prefix}-bind`, bound, baseline), empty)];
    const run = { schemaVersion: 1, adapterId: osc3008AdapterId, probeIds: [...osc3008CaseIds], experiments: [], pinned: [] };
    const history = [];
    let stockCursor = null;
    for (const contract of experiments) {
        const feeds = [], reads = [];
        for (const step of contract.plan) {
            if (step.feed !== undefined) { feeds.push(step.feed); history.push(step.feed); continue; }
            const state = structuredClone(simulateOsc133(feeds.map(feed => (feed === fixture.stage ? feed : withoutContextSequences(feed))), fixture));
            const contexts = structuredClone(simulateContexts(history));
            adjust?.(contract.caseId, step.read, { state, contexts });
            const screen = expectedOsc133Screen(bound, state);
            frames.push(withContexts(frameOf(runId, frames.length, `${osc3008Prefix}-read`, screen, state), contexts));
            reads.push({ kind: step.read, cursor: [screen.cursorRow, screen.cursorCol] });
            if (contract.name === 'stock') stockCursor = [screen.cursorRow, screen.cursorCol];
        }
        run.experiments.push({ caseId: contract.caseId, feeds: planFeeds(contract), barriers: planFeeds(contract).map(() => barrier), reads });
    }
    // The finish frame restores the prepared baseline: the stack is empty, but the ended ring cannot be cleared by any sequence and keeps its last records.
    frames.push(withContexts(frameOf(runId, frames.length, `${osc3008Prefix}-finish`, blank, baseline), { stack: [], ended: simulateContexts(history).ended }));
    run.pinned.push(replayOsc3008PinnedStatic(stockCursor));
    const adapter = { id: osc3008AdapterId, derivedFromAdapterId: osc3008DerivedFromAdapterId, bundleSha256,
        upstreamBundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6' };
    return { frames, run, runId, adapter, nonceRow: osc133NonceRow, caseId: osc3008CaseId };
}
