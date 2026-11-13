import { snapshotHash } from '../agent-observer.mjs';
import { metadataProjection } from '../metadata.mjs';
import { planFeeds } from '../graded-plan.mjs';
import { syntheticTitleStackEvidence } from './title-stack-evidence.mjs';
import { exposeOsc9Facts } from '../osc9-collector.mjs';
import { osc9CaseIds, osc9Experiments, osc9Fixture, progressSuffix } from '../osc9-contracts.mjs';
import { osc9AdapterId } from '../osc9-grading-collector.mjs';
import { replayOsc9PinnedStatic } from '../osc9-grading.mjs';

const barrier = '\x1b[?1;2c';
const focusRecord = action => ({ action, method: 'osascript', script: `synthetic ${action}`, exitCode: 0, signal: null, stderr: '',
    startedAt: '2026-11-13T12:00:00.000Z', finishedAt: '2026-11-13T12:00:02.000Z' });

/** The presentation a read of this plan step shows when the App behaves exactly as the contract's oracle says (never a collected observation). */
function presentationFor(contract, step, fixture) {
    if (contract.kind === 'progress') {
        const progress = step.kind === 'setup' ? contract.setup : contract.expected;
        return { progressOwner: progress ? 'pane' : null, progressKind: progress?.kind ?? null, progressPercent: progress?.percent ?? null,
            tabText: fixture.title + progressSuffix(progress ?? null) };
    }
    const toast = step.kind === 'effect' && contract.expected !== null;
    return { windowActive: contract.focus === 'active', toastVisible: toast, toastTitle: toast ? fixture.title : '', toastMessage: toast ? contract.expected : '' };
}

export function syntheticOsc9Evidence(options = {}) {
    const runId = options.runId ?? 'a'.repeat(32), fixture = osc9Fixture(runId), contracts = osc9Experiments(runId), previous = syntheticTitleStackEvidence({ runId });
    const base = structuredClone(previous.frames[1]);
    base.before.screen.lines[9] = fixture.nonce;
    const frame = (source, index, kind, patch, finish = false) => {
        const f = structuredClone(source); f.index = index; f.kind = `osn-${kind}`; f.barrier = index ? barrier : null;
        if (finish) f.before.screen = structuredClone(previous.frames[0].before.screen);
        const m = f.before.metadata, p = m.presentation;
        if (patch) {
            m.oscTitle = fixture.title; m.displayTitle = fixture.title; m.workingDirectory = fixture.cwd; m.buffer.requestedIconName = fixture.title;
            m.buffer.cursorRow = f.before.screen.cursorRow; m.buffer.cursorCol = f.before.screen.cursorCol;
            Object.assign(p, { tabText: fixture.title, progressOwner: null, progressKind: null, progressPercent: null, toastVisible: false, ...patch });
            if (p.progressOwner === 'pane') p.progressOwner = m.paneId;
        }
        f.after = structuredClone(f.before); f.sha256 = snapshotHash(metadataProjection(f.before)); f.afterSha256 = f.sha256;
        return f;
    };
    const frames = [frame(previous.frames[0], 0, 'start', null), frame(base, 1, 'bind', {})];
    const run = { schemaVersion: 1, adapterId: osc9AdapterId, probeIds: [...osc9CaseIds], experiments: [], pinned: [] };
    for (const contract of contracts) {
        const reads = [], actions = [];
        for (const step of contract.plan.filter(item => item.read !== undefined)) {
            const patched = frame(base, frames.length, 'read', presentationFor(contract, step, fixture));
            frames.push(patched);
            reads.push({ kind: step.read, cursor: [9, fixture.nonce.length], facts: exposeOsc9Facts(patched.before) });
        }
        for (const step of contract.plan.filter(item => item.action !== undefined)) actions.push({ action: step.action, response: focusRecord(step.action) });
        run.experiments.push({ caseId: contract.caseId, feeds: planFeeds(contract), barriers: planFeeds(contract).map(() => barrier), reads, ...(actions.length ? { actions } : {}) });
    }
    for (const caseId of osc9CaseIds) run.pinned.push({ caseId, ...replayOsc9PinnedStatic(caseId) });
    frames.push(frame(base, frames.length, 'finish', {}, true));
    const focus = run.experiments.flatMap(item => item.actions ?? []).map(item => item.response);
    return { frames, run, runId, focus, adapter: { id: osc9AdapterId, derivedFromAdapterId: 'ntilde-owned-left-right-margins-grading-v1',
        bundleSha256: options.bundleSha256 ?? 'f'.repeat(64), upstreamBundleSha256: 'a'.repeat(64) } };
}
