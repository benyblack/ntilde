import { snapshotHash } from '../agent-observer.mjs';
import { metadataProjection } from '../metadata.mjs';
import { planFeeds } from '../graded-plan.mjs';
import { syntheticTitleStackEvidence } from './title-stack-evidence.mjs';
import { exposeNotifyFacts } from '../notify-collector.mjs';
import { notifyCaseIds, notifyExperiments, notifyFixture, osc99CaseId } from '../notify-contracts.mjs';
import { notifyAdapterId } from '../notify-grading-collector.mjs';
import { replayOsc777PinnedStatic } from '../notify-grading.mjs';

const barrier = '\x1b[?1;2c';
const focusRecord = action => ({ ok: true, ownerPid: 4242, action, method: 'osascript', script: `synthetic ${action}`, appPid: 4242, attempts: [{ exitCode: 0, signal: null, stderr: '', frontmostPid: action === 'refocus' ? 4242 : 1 }],
    startedAt: '2026-11-14T12:00:00.000Z', finishedAt: '2026-11-14T12:00:02.000Z' });

/** The presentation a read of this plan step shows when the App behaves exactly as the contract's oracle says (never a collected observation). */
function presentationFor(contract, step) {
    const toast = step.kind === 'effect' && contract.expected !== null;
    return { windowActive: contract.focus === 'active', toastVisible: toast, toastTitle: toast ? contract.expected.title : '', toastMessage: toast ? contract.expected.message : '' };
}

/** A faithful synthetic journal: every read shows what the contracts' oracle expects. The pinned-fixture experiment parks the cursor at the origin. */
export function syntheticNotifyEvidence(options = {}) {
    const runId = options.runId ?? 'a'.repeat(32), fixture = notifyFixture(runId), contracts = notifyExperiments(runId), previous = syntheticTitleStackEvidence({ runId });
    const base = structuredClone(previous.frames[1]);
    base.before.screen.lines[9] = fixture.nonce;
    const frame = (source, index, kind, patch, finish = false, cursor = null) => {
        const f = structuredClone(source); f.index = index; f.kind = `ntf-${kind}`; f.barrier = index ? barrier : null;
        if (finish) f.before.screen = structuredClone(previous.frames[0].before.screen);
        if (cursor) { f.before.screen.cursorRow = cursor[0]; f.before.screen.cursorCol = cursor[1]; }
        const m = f.before.metadata, p = m.presentation;
        if (patch) {
            m.oscTitle = fixture.title; m.displayTitle = fixture.title; m.workingDirectory = fixture.cwd; m.buffer.requestedIconName = fixture.title;
            m.buffer.cursorRow = f.before.screen.cursorRow; m.buffer.cursorCol = f.before.screen.cursorCol;
            Object.assign(p, { tabText: fixture.title, progressOwner: null, progressKind: null, progressPercent: null, toastVisible: false, ...patch });
        }
        f.after = structuredClone(f.before); f.sha256 = snapshotHash(metadataProjection(f.before)); f.afterSha256 = f.sha256;
        return f;
    };
    const frames = [frame(previous.frames[0], 0, 'start', null), frame(base, 1, 'bind', {})];
    const run = { schemaVersion: 1, adapterId: notifyAdapterId, probeIds: [...notifyCaseIds], experiments: [], pinned: [] };
    for (const contract of contracts) {
        const reads = [], actions = [], cursor = contract.name === 'pinned-fixture' ? [0, 0] : null;
        for (const step of contract.plan.filter(item => item.read !== undefined)) {
            const patched = frame(base, frames.length, 'read', presentationFor(contract, step), false, cursor);
            frames.push(patched);
            reads.push({ kind: step.read, cursor: cursor ?? [9, fixture.nonce.length], facts: exposeNotifyFacts(patched.before) });
        }
        for (const step of contract.plan.filter(item => item.action !== undefined)) actions.push({ action: step.action, response: focusRecord(step.action) });
        run.experiments.push({ caseId: contract.caseId, feeds: planFeeds(contract), barriers: planFeeds(contract).map(() => barrier), reads, ...(actions.length ? { actions } : {}) });
    }
    for (const caseId of notifyCaseIds) {
        run.pinned.push(caseId === osc99CaseId ? { caseId, termlessFunction: false, termless: null, terminal: null, replay: 'stock-collector-and-exact-query-witness' }
            : replayOsc777PinnedStatic([0, 0]));
    }
    frames.push(frame(base, frames.length, 'finish', {}, true));
    const focus = run.experiments.flatMap(item => item.actions ?? []).map(({ response: { ok, ownerPid, ...record } }) => record);
    return { frames, run, runId, focus, adapter: { id: notifyAdapterId, derivedFromAdapterId: 'ntilde-owned-left-right-margins-grading-v1',
        bundleSha256: options.bundleSha256 ?? 'f'.repeat(64), upstreamBundleSha256: 'a'.repeat(64) } };
}
