import { screenSnapshot, snapshotHash } from './agent-observer.mjs';
import { pristineEditing } from './editing-capture.mjs';
import { copyPaneMetadata, copyMetadataStatus } from './metadata-state.mjs';
import { makeMetadataCases, metadataSuiteId, ownedMetadataPolicy } from './metadata-cases.mjs';
import { matchesMetadataExpected } from './metadata-expectations.mjs';
const same = (a, b) => snapshotHash(a) === snapshotHash(b);
const check = value => { if (!value) throw new Error('Invalid or incomplete owned metadata evidence.'); };
export function metadataProjection(value) { return { screen: value.screen, metadata: value.metadata, status: value.status }; }
/** Validate the full cell and host payload, preserving both raw timestamp-bearing status reads. */
export function validateMetadataSnapshot(value, first) {
    check(value && Object.keys(value).length === 4 && ['screen', 'metadata', 'status', 'rawStatus'].every(key => Object.hasOwn(value, key)));
    const screen = screenSnapshot(value.screen, false, true, true, true, true);
    const metadata = copyPaneMetadata(value.metadata), status = copyMetadataStatus(value.rawStatus, metadata.paneId);
    check(same(screen, value.screen) && same(metadata, value.metadata) && same(status, value.status));
    const b = metadata.buffer;
    check(b.rows === screen.rows && b.cols === screen.cols && b.cursorRow === screen.cursorRow && b.cursorCol === screen.cursorCol
        && b.alternateScreen === screen.bufferState.alternateScreen && b.scrollbackLines === screen.bufferState.scrollbackLines
        && (!first || metadata.paneId === first.metadata.paneId && b.rows === first.screen.rows && b.cols === first.screen.cols
            && b.historyGeneration === first.metadata.buffer.historyGeneration));
    return { screen, metadata, status, rawStatus: structuredClone(value.rawStatus) };
}
function frameOperation(index, last) {
    if (index === 0) return 'start';
    if (index === 1) return 'bind';
    if (index === last) return 'finish';
    return 'read';
}
function validateFrame(frame, index, last, first, stage) {
    const keys = ['index', 'kind', 'paneId', 'barrier', 'before', 'sha256', 'after', 'afterSha256', 'caseId', 'stageId'];
    check(frame && Object.keys(frame).length === keys.length && keys.every(key => Object.hasOwn(frame, key)));
    const before = validateMetadataSnapshot(frame.before, first), after = validateMetadataSnapshot(frame.after, first);
    const sha = snapshotHash(metadataProjection(before));
    const operation = frameOperation(index, last);
    check(frame.index === index && frame.kind === 'metadata-' + operation && frame.paneId === first.metadata.paneId
        && before.metadata.paneId === frame.paneId && after.metadata.paneId === frame.paneId
        && same(metadataProjection(before), metadataProjection(after)) && frame.sha256 === sha && frame.afterSha256 === sha
        && frame.caseId === (stage?.caseId ?? null) && frame.stageId === (stage?.stageId ?? null)
        && (index === 0 ? frame.barrier === null : typeof frame.barrier === 'string'
            && frame.barrier.length <= 128 && /^\x1b\[\?[\d;]+c$/.test(frame.barrier)));
}
/** Reconstruct the fixed complete staged plan and preserve controlled negatives/inconclusive probes. */
export function buildMetadataReport(frames, runId) {
    check(/^[a-f\d]{32}$/.test(runId ?? '') && Array.isArray(frames) && frames.length >= 3);
    const first = validateMetadataSnapshot(frames[0]?.before), screen = first.screen;
    const cases = makeMetadataCases(screen.rows, screen.cols, runId);
    const plan = cases.flatMap(item => item.stages.map(stage => ({ caseId: item.id, stageId: stage.id })));
    check(frames.length === plan.length + 3 && frames.length <= 64 && pristineEditing(screen));
    for (const [index, frame] of frames.entries()) validateFrame(frame, index, frames.length - 1, first, plan[index - 2]);
    check(matchesMetadataExpected(frames[1].before, { type: 'baseline' }, screen, runId)
        && same(frames.at(-1).before.screen, screen)
        && matchesMetadataExpected(frames.at(-1).before, { type: 'baseline' }, screen, runId, true));
    let offset = 2;
    const inputs = cases.map(item => ({ ...item, sha256: snapshotHash(item) }));
    const observations = cases.map(item => {
        const comparisons = item.stages.map(stage => { const frame = frames[offset++];
            return { stageId: stage.id, snapshot: frame.index, passed: matchesMetadataExpected(frame.before, stage.expected, screen, runId) }; });
        const [control, ...assertions] = comparisons;
        const preconditions = item.id === 'metadata.notification.osc9' ? !frames[control.snapshot].before.metadata.presentation.windowActive : true;
        let result = assertions.every(value => value.passed) ? 'passed' : 'failed';
        if (!control.passed || !preconditions || item.qualification === 'inconclusive') result = 'inconclusive';
        return { featureId: item.id, control, positiveDeliveryPreconditions: preconditions, assertions, result,
            ...(item.limitation ? { limitation: item.limitation } : {}) };
    });
    return { schemaVersion: 1, suiteId: metadataSuiteId, runId, complete: true, policy: ownedMetadataPolicy,
        scope: 'Paired complete stored active cells/attributes, bounded title/cwd/link/shell facts and actual private-window UI controls. Restores the explicitly prepared title/cwd baseline and initial cells; original unprepared null metadata and hidden-control content are not claimed restored. Verified disposable teardown is mandatory. No URL opening, clipboard, pixels, OS taskbar/native notifications, general prompt-part semantics or stock-grade claim.',
        inputs, initialSnapshot: 0, preparedSnapshot: 1, restoredSnapshot: frames.length - 1, snapshots: frames, observations };
}
