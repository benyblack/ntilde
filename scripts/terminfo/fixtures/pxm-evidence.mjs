// Synthetic examples only (a transliteration of the pinned probe over a served DECRQM reply, never collected child bytes).
import { inputPlanV5 } from '../input-plan.mjs';
import { pxmAdapterId } from '../pxm-grading-collector.mjs';
import { collectPxmPinned } from '../pxm-collector.mjs';
import { inputModeReplyHex, buildInputReport } from '../input-state.mjs';
import { initialInputScreen, inputRunId, syntheticInputJournal } from './input-journal.mjs';
import { pxmCaseIds } from '../pxm-contracts.mjs';

/** A stand-in for the derived bundle: the pinned code's exact calls (see the seams) over the supplied context. */
export const fakePxmGradingModule = Object.freeze({
    ntildePxmAdapter: Object.freeze({ id: pxmAdapterId, probeIds: Object.freeze([...pxmCaseIds]) }),
    ntildeRunPxmTermless(probeId, ctx) {
        const note = 'No generated input event and encoded report were validated';
        return { probeId, pass: false, response: JSON.stringify({ currentMode: ctx.getMode('pixelMouse') === true }), note,
            observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'legacy', note } };
    },
});

export const syntheticPxmAdapter = Object.freeze({ id: pxmAdapterId, bundleSha256: 'e'.repeat(64), upstreamBundleSha256: 'c'.repeat(64),
    derivedFromAdapterId: 'ntilde-owned-left-right-margins-grading-v1' });

const controlHex = item => (item.controlPrefixHex ?? '') + inputModeReplyHex(item.modes);
export const pxmFinalHex = () => inputModeReplyHex(inputPlanV5.finalModes);

/** Journal of the v5 plan plus the worker's pinned-run log. `actual(item)` substitutes child bytes for a read; `mutate` edits the journal. */
export function syntheticPxmEvidence({ runId = inputRunId, actual = item => item.expectedHex, mutate = () => {} } = {}) {
    const journal = syntheticInputJournal(runId, { initial: initialInputScreen(30, 120), plan: inputPlanV5, actual, controlHex, finalHex: pxmFinalHex });
    mutate(journal);
    const controls = {};
    for (const frame of journal.frames) if (frame.kind === 'input-control') controls[frame.caseId] = frame.capture.hex;
    const geometry = { rows: journal.frames[0].screen.rows, cols: journal.frames[0].screen.cols };
    const run = collectPxmPinned({ geometry, controls, restored: journal.frames.at(-1).capture.hex, gradingModule: fakePxmGradingModule });
    return { ...journal, run, runId, adapter: { ...syntheticPxmAdapter },
        inputReport: () => buildInputReport(journal.frames, journal.actions, journal.seal, runId, inputPlanV5) };
}
