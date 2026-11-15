// Synthetic examples only (a transliteration of the pinned probes over served DECRQM replies, never collected child bytes).
import { inputPlanV4 } from '../input-plan.mjs';
import { mexAdapterId } from '../mex-grading-collector.mjs';
import { collectMexPinned } from '../mex-collector.mjs';
import { inputModeReplyHex, buildInputReport } from '../input-state.mjs';
import { inputRunId, syntheticInputJournal } from './input-journal.mjs';
import { mexCaseIds } from '../mex-contracts.mjs';

/** A stand-in for the derived bundle: the pinned code's exact calls (see the seams) over the supplied context. */
export const fakeMexGradingModule = Object.freeze({
    ntildeMexAdapter: Object.freeze({ id: mexAdapterId, probeIds: Object.freeze([...mexCaseIds]) }),
    ntildeRunMexTermless(probeId, ctx) {
        if (probeId === 'modes.utf8-mouse-1005') {
            ctx.feed('\x1B[?1005h');
            const response = ctx.feedCapture('\x1B[?1005$p');
            ctx.feed('\x1B[?1005l');
            const note = 'encoded UTF-8 mouse events';
            return { probeId, pass: false, response: JSON.stringify({ response }), note, notTested: { reason: 'no-semantic-observable', noObservable: note } };
        }
        const note = 'No generated input event and encoded report were validated';
        return { probeId, pass: false, response: JSON.stringify({ currentMode: ctx.getMode('mouseTracking') === true }), note,
            observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'legacy', note } };
    },
});

export const syntheticMexAdapter = Object.freeze({ id: mexAdapterId, bundleSha256: 'e'.repeat(64), upstreamBundleSha256: 'c'.repeat(64),
    derivedFromAdapterId: 'ntilde-owned-left-right-margins-grading-v1' });

const controlHex = item => (item.controlPrefixHex ?? '') + inputModeReplyHex(item.modes);
export const mexFinalHex = () => inputModeReplyHex(inputPlanV4.finalModes);

/** Journal of the v4 plan plus the worker's pinned-run log. `actual(item)` substitutes child bytes for a read; `mutate` edits the journal. */
export function syntheticMexEvidence({ runId = inputRunId, actual = item => item.expectedHex, mutate = () => {} } = {}) {
    const journal = syntheticInputJournal(runId, { plan: inputPlanV4, actual, controlHex, finalHex: mexFinalHex });
    mutate(journal);
    const controls = {};
    for (const frame of journal.frames) if (frame.kind === 'input-control') controls[frame.caseId] = frame.capture.hex;
    const geometry = { rows: journal.frames[0].screen.rows, cols: journal.frames[0].screen.cols };
    const run = collectMexPinned({ geometry, controls, restored: journal.frames.at(-1).capture.hex, gradingModule: fakeMexGradingModule });
    return { ...journal, run, runId, adapter: { ...syntheticMexAdapter },
        inputReport: () => buildInputReport(journal.frames, journal.actions, journal.seal, runId, inputPlanV4) };
}
