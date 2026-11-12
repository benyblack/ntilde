import { snapshotHash } from '../agent-observer.mjs';
import { metadataProjection } from '../metadata.mjs';
import { planFeeds } from '../graded-plan.mjs';
import { syntheticTitleStackEvidence } from './title-stack-evidence.mjs';
import { oscTitleCaseIds, oscTitleExperiments, oscTitleFixture } from '../osc-title-contracts.mjs';
import { oscTitleAdapterId } from '../osc-title-grading-collector.mjs';
import { replayOscTitlePinned } from '../osc-title-grading.mjs';

const runId = 'a'.repeat(32), barrier = '\x1b[?1;2c';
export function syntheticOscTitleEvidence() {
    const previous = syntheticTitleStackEvidence({ runId }), fixture = oscTitleFixture(runId), contracts = oscTitleExperiments(runId);
    const base = structuredClone(previous.frames[1]);
    base.before.screen.lines[9] = fixture.nonce;
    const frame = (source, index, kind, state, finish = false) => {
        const f = structuredClone(source); f.index = index; f.kind = `ost-${kind}`; f.barrier = index ? barrier : null;
        if (finish) f.before.screen = structuredClone(previous.frames[0].before.screen);
        const m = f.before.metadata;
        if (state) {
            m.oscTitle = state.title; m.buffer.requestedIconName = state.icon; m.workingDirectory = fixture.cwd;
            m.displayTitle = state.title.trim() ? state.title : `terminfo disposable worker ${runId} · osc-title-base`; m.presentation.tabText = m.displayTitle;
            m.buffer.cursorRow = f.before.screen.cursorRow; m.buffer.cursorCol = f.before.screen.cursorCol;
        }
        f.after = structuredClone(f.before); f.sha256 = snapshotHash(metadataProjection(f.before)); f.afterSha256 = f.sha256;
        return f;
    };
    const initial = frame(previous.frames[0], 0, 'start', null);
    const frames = [initial, frame(base, 1, 'bind', { title: fixture.title, icon: fixture.title })];
    const run = { schemaVersion: 1, adapterId: oscTitleAdapterId, probeIds: [...oscTitleCaseIds], experiments: [], pinned: [] };
    for (const contract of contracts) {
        const reads = [];
        for (const [name, state] of [['before', contract.baseline], ['after', contract.expected]]) {
            frames.push(frame(base, frames.length, 'read', state));
            reads.push({ kind: name, cursor: [9, fixture.nonce.length], facts: { title: state.title, icon: state.icon } });
        }
        run.experiments.push({ caseId: contract.caseId, feeds: planFeeds(contract), barriers: planFeeds(contract).map(() => barrier), reads });
        if (contract.role === 'pinned') run.pinned.push({ caseId: contract.owner, ...replayOscTitlePinned(contract.owner, contract.baseline.title, contract.expected.title) });
    }
    frames.push(frame(base, frames.length, 'finish', { title: fixture.title, icon: fixture.title }, true));
    return { frames, run, runId, adapter: { id: oscTitleAdapterId, derivedFromAdapterId: 'ntilde-owned-title-stack-grading-v1',
        bundleSha256: 'f'.repeat(64), upstreamBundleSha256: 'a'.repeat(64) } };
}
