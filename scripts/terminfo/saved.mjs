import { screenSnapshot, snapshotHash } from './agent-observer.mjs';
import { expandCellDetails } from './cell-details.mjs';
import { pristineEditing } from './editing-capture.mjs';
import { makeSavedCases, savedSuiteId, ownedSavedPolicy } from './saved-cases.mjs';
import { compressSavedExpected, matchesSavedExpected } from './saved-expectations.mjs';
const same = (left, right) => snapshotHash(left) === snapshotHash(right);
const check = value => { if (!value) throw new Error('Invalid or incomplete owned saved-state evidence.'); };
const keys = ['index', 'kind', 'paneId', 'barrier', 'screen', 'sha256', 'afterScreen', 'afterSha256', 'caseId', 'stageId'].sort();
function frameKind(index, last) {
    if (index === 0) return 'start';
    if (index === 1) return 'bind';
    if (index === last) return 'finish';
    return 'read';
}
function validateFrame(frame, index, last, first, stage) {
    check(frame && same(Object.keys(frame).sort(), keys));
    const screen = screenSnapshot(frame.screen, false, true, true, true, true);
    const after = screenSnapshot(frame.afterScreen, false, true, true, true, true);
    check(frame.index === index && frame.kind === 'saved-' + frameKind(index, last)
        && frame.paneId === first.paneId && /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i.test(frame.paneId ?? '')
        && screen.rows === first.screen.rows && screen.cols === first.screen.cols
        && same(screen, after) && same(screen, frame.screen) && same(after, frame.afterScreen)
        && frame.sha256 === snapshotHash(screen) && frame.afterSha256 === frame.sha256
        && frame.caseId === (stage?.caseId ?? null) && frame.stageId === (stage?.stageId ?? null)
        && (index === 0 ? frame.barrier === null : typeof frame.barrier === 'string'
            && frame.barrier.length <= 128 && /^\x1b\[\?[\d;]+c$/.test(frame.barrier)));
}
/** Reconstruct the entire bounded staged plan; malformed evidence cannot become a support grade. */
export function buildSavedReport(frames, runId) {
    check(/^[a-f\d]{32}$/.test(runId ?? '') && Array.isArray(frames) && frames.length >= 3);
    const first = frames[0], last = frames.at(-1);
    const cases = makeSavedCases(first?.screen?.rows, first?.screen?.cols);
    const plan = cases.flatMap(item => item.stages.map(stage => ({ caseId: item.id, stageId: stage.id })));
    check(frames.length === plan.length + 3 && frames.length <= 64);
    for (const [index, frame] of frames.entries()) validateFrame(frame, index, frames.length - 1, first, plan[index - 2]);
    check(pristineEditing(first.screen) && same(first.screen, last.screen));
    const bound = structuredClone(first.screen);
    bound.lines[0] = 'NTILDE_SV_' + runId; bound.cursorCol = 42; bound.bufferState.alternateScreen = true;
    check(same(bound, frames[1].screen));
    const inputs = cases.map(item => {
        const value = { id: item.id, setup: item.setup, stages: item.stages.map(stage => ({ id: stage.id,
            sequence: stage.sequence, expected: compressSavedExpected(stage.expected) })) };
        return { ...value, sha256: snapshotHash(value) };
    });
    let offset = 2;
    const observations = inputs.map(item => {
        const comparisons = item.stages.map(stage => {
            const frame = frames[offset++];
            const passed = matchesSavedExpected(frame.screen, stage.expected,
                expandCellDetails(frame.screen.cellDetails, frame.screen.rows * frame.screen.cols, true));
            return { stageId: stage.id, snapshot: frame.index, passed };
        });
        const [control, ...assertions] = comparisons;
        let result = assertions.every(value => value.passed) ? 'passed' : 'failed';
        if (!control.passed) result = 'inconclusive';
        return { featureId: item.id, control, assertions, result };
    });
    return { schemaVersion: 1, suiteId: savedSuiteId, runId, complete: true, policy: ownedSavedPolicy,
        scope: 'Paired complete active cells/attributes, cursor presentation/position, exposed buffer modes and ordered bounded history. Source-known transitions and newly written witnesses qualify alternate retention/clearing, independent saved cursor/pen/charset restoration and reset boundaries. Invisible bank fields are not read directly; no pixels, generic Unicode editing, platform breadth or stock-grade claim.',
        inputs, initialSnapshot: 0, restoredSnapshot: frames.length - 1, snapshots: frames, observations };
}
