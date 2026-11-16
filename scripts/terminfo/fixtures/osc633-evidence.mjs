// Synthetic, fully valid OSC 633 evidence for portable tests. Every read is built from the suite's own lifecycle oracle (the model App agrees with it by
// construction), so this fixture exercises the journal, report and receipt plumbing and the sensitivity of the graders to tampering, not the oracle itself:
// neither branch is an App measurement.
import { planFeeds } from '../graded-plan.mjs';
import { expectedOsc133Screen, osc133Fixture, osc133NonceRow, simulateOsc133 } from '../osc133-contracts.mjs';
import { makeOsc633Experiments, osc633Pinned, osc633Prefix, replayOsc633Pinned } from '../osc633-contracts.mjs';
import { osc633AdapterId, osc633CaseIds } from '../osc633-grading-collector.mjs';
import { withoutOsc633Markers } from '../osc633-grading.mjs';
import { paletteBinding } from './col-edit-evidence.mjs';
import { blankScreen, frameOf, withOverride } from './osc133-evidence.mjs';
export { osc133PaneId } from './osc133-evidence.mjs';
const barrier = '\x1b[?1;2c';

/** A complete, valid synthetic run: frames, worker run log and adapter facts. `adjust(experimentId, label, state)` may change the model App's answer to one read. */
export function syntheticOsc633Evidence({ runId = 'a'.repeat(32), adjust = null, supported = false, bundleSha256 = 'f'.repeat(64) } = {}) {
    const fixture = osc133Fixture(runId), experiments = makeOsc633Experiments(runId);
    const initialBinding = paletteBinding(), boundBinding = withOverride(initialBinding);
    const blank = blankScreen(initialBinding);
    const baseline = simulateOsc133([fixture.stage], fixture);
    const bound = expectedOsc133Screen(blankScreen(boundBinding), baseline);
    const frames = [frameOf(runId, 0, `${osc633Prefix}-start`, blank, baseline, { initial: true }), frameOf(runId, 1, `${osc633Prefix}-bind`, bound, baseline)];
    const run = { schemaVersion: 1, adapterId: osc633AdapterId, probeIds: [...osc633CaseIds], experiments: [], pinned: [] };
    const stockScreens = new Map();
    for (const contract of experiments) {
        const feeds = [], reads = [];
        for (const step of contract.plan) {
            if (step.feed !== undefined) { feeds.push(step.feed); continue; }
            const state = structuredClone(simulateOsc133(feeds.map(feed => supported ? feed.replaceAll('\x1b]633;', '\x1b]133;') : withoutOsc633Markers(feed)), fixture));
            adjust?.(contract.caseId, step.read, state);
            const screen = expectedOsc133Screen(bound, state);
            frames.push(frameOf(runId, frames.length, `${osc633Prefix}-read`, screen, state));
            reads.push({ kind: step.read, cursor: [screen.cursorRow, screen.cursorCol] });
            if (contract.name === 'stock') stockScreens.set(contract.owner, screen);
        }
        run.experiments.push({ caseId: contract.caseId, feeds: planFeeds(contract), barriers: planFeeds(contract).map(() => barrier), reads });
    }
    frames.push(frameOf(runId, frames.length, `${osc633Prefix}-finish`, blank, baseline));
    // The finish frame restores the prepared title/cwd baseline: the hidden initial null metadata is not restored.
    for (const caseId of osc633CaseIds) {
        const replay = replayOsc633Pinned(caseId, stockScreens.get(caseId));
        run.pinned.push({ caseId, termless: { feeds: [...osc633Pinned[caseId].termlessFeeds], result: replay.termless },
            terminal: { writes: [...osc633Pinned[caseId].terminalWrites], result: replay.terminal } });
    }
    const adapter = { id: osc633AdapterId, derivedFromAdapterId: 'ntilde-owned-osc133-grading-v1', bundleSha256,
        upstreamBundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6' };
    return { frames, run, runId, adapter, nonceRow: osc133NonceRow };
}
