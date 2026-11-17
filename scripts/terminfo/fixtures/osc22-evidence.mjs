// Synthetic plumbing/refusal fixture. It is not App evidence or an independent oracle check.
import { copyPaneMetadata } from '../metadata-state.mjs';
import { snapshotHash } from '../agent-observer.mjs';
import { metadataProjection } from '../metadata.mjs';
import { expectedOsc133Screen, osc133Fixture, simulateOsc133 } from '../osc133-contracts.mjs';
import { makeOsc22Experiments, osc22CaseId, osc22CaseIds, osc22Prefix, osc22Baseline, replayOsc22PinnedStatic, withoutPointerSequences } from '../osc22-contracts.mjs';
import { osc22AdapterId, osc22DerivedFromAdapterId } from '../osc22-grading-collector.mjs';
import { paletteBinding } from './col-edit-evidence.mjs';
import { blankScreen, frameOf, withOverride } from './osc133-evidence.mjs';

export function withPointer(frame, pointer) {
    const metadata = { ...frame.before.metadata };
    if (pointer === undefined) delete metadata.pointerShape;
    else metadata.pointerShape = structuredClone(pointer);
    const before = { ...frame.before, metadata: copyPaneMetadata(metadata) }, sha256 = snapshotHash(metadataProjection(before));
    return { ...frame, before, sha256, after: structuredClone(before), afterSha256: sha256 };
}

export function syntheticOsc22Evidence({ runId = 'a'.repeat(32), adjust = null, bundleSha256 = 'f'.repeat(64) } = {}) {
    const fixture = osc133Fixture(runId), experiments = makeOsc22Experiments(runId);
    const binding = paletteBinding(), blank = blankScreen(binding), baseline = simulateOsc133([fixture.stage], fixture);
    const bound = expectedOsc133Screen(blankScreen(withOverride(binding)), baseline);
    const frames = [withPointer(frameOf(runId, 0, `${osc22Prefix}-start`, blank, baseline, { initial: true }), osc22Baseline),
        withPointer(frameOf(runId, 1, `${osc22Prefix}-bind`, bound, baseline), osc22Baseline)];
    const run = { schemaVersion: 1, adapterId: osc22AdapterId, probeIds: [...osc22CaseIds], experiments: [], pinned: [] };
    let stockCursor;
    for (const contract of experiments) {
        const feeds = [], reads = [];
        for (const step of contract.plan) {
            if (step.feed !== undefined) { feeds.push(step.feed); continue; }
            const state = simulateOsc133(feeds.map(withoutPointerSequences), fixture);
            const pointer = structuredClone(contract.expected);
            adjust?.(contract.name, pointer);
            const screen = expectedOsc133Screen(bound, state);
            frames.push(withPointer(frameOf(runId, frames.length, `${osc22Prefix}-read`, screen, state), pointer));
            reads.push({ kind: step.read, cursor: [screen.cursorRow, screen.cursorCol] });
            if (contract.name === 'stock') stockCursor = [screen.cursorRow, screen.cursorCol];
        }
        run.experiments.push({ caseId: contract.caseId, feeds, barriers: feeds.map(() => '\x1b[?1;2c'), reads });
    }
    frames.push(withPointer(frameOf(runId, frames.length, `${osc22Prefix}-finish`, blank, baseline), osc22Baseline));
    run.pinned.push(replayOsc22PinnedStatic(stockCursor));
    const adapter = { id: osc22AdapterId, derivedFromAdapterId: osc22DerivedFromAdapterId, bundleSha256,
        upstreamBundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6' };
    return { frames, run, runId, adapter, caseId: osc22CaseId };
}
