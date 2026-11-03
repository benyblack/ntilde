import { screenSnapshot, snapshotHash } from './agent-observer.mjs';
import { expandCellDetails } from './cell-details.mjs';
import { pristineEditing } from './editing-capture.mjs';
import { compressExpected, matchesExpected } from './editing-expectations.mjs';
import { makeResizeCases, resizeSuiteId } from './resize-cases.mjs';
import { validateOutputGeometry } from './terminal-output.mjs';
const same = (left, right) => snapshotHash(left) === snapshotHash(right);
const check = value => { if (!value) throw new Error('Invalid or incomplete owned resize evidence.'); };
const geometry = screen => ({ rows: screen.rows, cols: screen.cols });

/** Derive each geometry and complete state from the retained source plan, never worker-supplied expectations. */
export function validateResizeFrames(frames, actions, runId) {
    check(/^[a-f\d]{32}$/.test(runId ?? '') && Array.isArray(frames) && frames.length === 12
        && Array.isArray(actions) && actions.length === 6);
    const first = frames[0], last = frames.at(-1), original = geometry(first.screen ?? {});
    const cases = makeResizeCases(original.rows, original.cols);
    const plan = cases.flatMap(item => ['control', 'after', 'restored'].map(stageId => ({
        caseId: item.id, stageId, target: stageId === 'after' ? item.target : original,
    })));
    let actionIndex = 0;
    for (const [index, frame] of frames.entries()) {
        const stage = plan[index - 2];
        const kind = index === 0 ? 'start' : index === 1 ? 'bind' : index === frames.length - 1 ? 'finish' : 'read';
        const expectedAction = stage && stage.stageId !== 'control' ? actionIndex++ : null;
        const screen = screenSnapshot(frame.screen, false, true, true, true, true);
        const after = screenSnapshot(frame.afterScreen, false, true, true, true, true);
        check(frame.index === index && frame.kind === 'resize-' + kind && frame.paneId === first.paneId
            && /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i.test(frame.paneId ?? '')
            && same(geometry(screen), stage?.target ?? original) && same(screen, after)
            && frame.sha256 === snapshotHash(screen) && frame.afterSha256 === frame.sha256
            && frame.caseId === (stage?.caseId ?? null) && frame.stageId === (stage?.stageId ?? null)
            && frame.actionIndex === expectedAction
            && (index === 0 ? frame.barrier === null : typeof frame.barrier === 'string'
                && frame.barrier.length <= 128 && /^\x1b\[\?[\d;]+c$/.test(frame.barrier)));
        if (expectedAction !== null) {
            const action = actions[expectedAction], observed = action?.observed;
            check(action?.index === expectedAction && action.caseId === stage.caseId && action.stageId === stage.stageId
                && same(action.target, stage.target) && observed?.paneId === first.paneId && observed.runId === runId
                && typeof observed.observedAt === 'string' && Number.isFinite(Date.parse(observed.observedAt))
                && new Date(observed.observedAt).toISOString() === observed.observedAt
                && observed.app?.exact === true && observed.app.layoutSettled === true
                && same(geometry(observed.app), stage.target) && observed.terminalOutput?.tool === '/bin/stty'
                && observed.terminalOutput.outputProcessing === false);
            validateOutputGeometry(observed.terminalOutput.description, stage.target.rows, stage.target.cols);
        }
    }
    check(pristineEditing(first.screen) && same(first.screen, last.screen));
    const bound = structuredClone(first.screen);
    bound.lines[0] = 'NTILDE_RZ_' + runId; bound.cursorCol = 42; bound.bufferState.alternateScreen = true;
    check(same(bound, frames[1].screen));
    return cases;
}

/** Supplemental full-state resize comparisons; absent or malformed action evidence cannot become a support claim. */
export function buildResizeReport(frames, actions, runId) {
    const cases = validateResizeFrames(frames, actions, runId);
    const inputs = cases.map(item => {
        const value = { id: item.id, setup: item.setup, target: item.target,
            before: compressExpected(item.before), after: compressExpected(item.after), restored: compressExpected(item.restored) };
        return { ...value, sha256: snapshotHash(value) };
    });
    const compare = (frame, expected) => matchesExpected(frame.screen, expected,
        expandCellDetails(frame.screen.cellDetails, frame.screen.rows * frame.screen.cols, true));
    const observations = inputs.map((item, index) => {
        const [control, changed, restored] = frames.slice(index * 3 + 2, index * 3 + 5);
        const controls = [{ description: 'known complete hard-line viewport, cursor, modes and empty history before resizing',
            passed: compare(control, item.before) }];
        const assertions = [
            { description: 'exact expected reflowed cells, cursor and complete ordered displaced history at the independently verified smaller grid',
                passed: compare(changed, item.after) },
            { description: 'exact top-anchored viewport, cursor and complete retained history after independently verified geometry restoration',
                passed: compare(restored, item.restored) },
        ];
        const result = controls.some(value => !value.passed) ? 'inconclusive' : assertions.some(value => !value.passed) ? 'failed' : 'passed';
        return { featureId: item.id, snapshots: [control.index, changed.index, restored.index],
            actions: [changed.actionIndex, restored.actionIndex], controls, assertions, result };
    });
    return { schemaVersion: 1, suiteId: resizeSuiteId, runId, complete: true,
        scope: "Owned ASCII hard-line width reflow, height/history movement and combined shrink/restore. Geometry restoration preserves Ntilde's documented top anchor, including retained history; every original hard line remains represented across history and viewport. Paired complete viewport and bounded ordered history text; parent-selected App actions with independent kernel geometry/output-processing proof. No Unicode, history-attribute, pixel, saved-state or stock-grade claim.",
        inputs, initialSnapshot: 0, restoredSnapshot: frames.length - 1, snapshots: frames, actions, observations };
}
