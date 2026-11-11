// Synthetic examples only (a transliteration of the pinned probes over served modes, never collected child bytes).
import { inputPlanV2 } from '../input-plan.mjs';
import { routedInputAdapterId } from '../routed-input-grading-collector.mjs';
import { collectRoutedInputPinned } from '../routed-input-collector.mjs';
import { inputModeReplyHex } from '../input-state.mjs';
import { inputRunId, syntheticInputJournal } from './input-journal.mjs';
import { routedInputCaseIds } from '../routed-input-contracts.mjs';
import { buildInputReport } from '../input-state.mjs';

/** A stand-in for the derived bundle: the pinned code's exact calls (see the seams) over the supplied context. */
export const fakeGradingModule = Object.freeze({
    ntildeRoutedInputAdapter: Object.freeze({ id: routedInputAdapterId, probeIds: Object.freeze([...routedInputCaseIds]) }),
    ntildeRunRoutedInputTermless(probeId, ctx) {
        if (probeId === 'modes.application-keypad') {
            ctx.feed('\x1B=');
            const on = ctx.getMode('applicationKeypad') === true;
            ctx.feed('\x1B>');
            const off = ctx.getMode('applicationKeypad') === false;
            return { probeId, pass: false, response: JSON.stringify({ on, off }), note: 'keypad input', notTested: { reason: 'no-semantic-observable', noObservable: 'keypad input' } };
        }
        const note = 'No generated input event and encoded report were validated';
        return { probeId, pass: false, response: JSON.stringify({ currentMode: ctx.getMode('mouseTracking') === true }), note,
            observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'legacy', note } };
    },
});

export const syntheticAdapter = Object.freeze({ id: routedInputAdapterId, bundleSha256: 'b'.repeat(64), upstreamBundleSha256: 'c'.repeat(64),
    derivedFromAdapterId: 'ntilde-owned-left-right-margins-grading-v1' });

/** Journal of the v2 plan plus the worker's pinned-run log. `actual(item)` substitutes child bytes for a read; `mutateControl` edits one control capture. */
export function syntheticRoutedInputEvidence({ runId = inputRunId, actual = item => item.expectedHex, mutate = () => {} } = {}) {
    const journal = syntheticInputJournal(runId, { plan: inputPlanV2, actual });
    mutate(journal);
    const controls = {};
    for (const frame of journal.frames) if (frame.kind === 'input-control') controls[frame.caseId] = frame.capture.hex;
    const geometry = { rows: journal.frames[0].screen.rows, cols: journal.frames[0].screen.cols };
    const run = collectRoutedInputPinned({ geometry, controls, restored: journal.frames.at(-1).capture.hex, gradingModule: fakeGradingModule });
    return { ...journal, run, runId, adapter: { ...syntheticAdapter },
        inputReport: () => buildInputReport(journal.frames, journal.actions, journal.seal, runId, inputPlanV2) };
}
export { inputModeReplyHex };
