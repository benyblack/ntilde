// Parent-side report builder for the SGR pixel-coordinate mouse qualification (input.pixel-mouse). It re-derives the owned input report from
// the retained journal (strict controls, ownership, raw capture and restoration), requires the plan to be exactly the v5 plan (which keeps every
// earlier event verbatim, so the routed-input, modifyOtherKeys and extended-mouse layers keep re-deriving from the same journal), grades the
// benchmark from the child-received bytes against the independent contract, and requires the pinned probe's own result to equal its replay
// over the child's DECRQM reply. The pinned verdict is inconclusive by design and is preserved as the conclusion; the separately identified
// effect contract carries the graded verdict, so a difference is recorded as a divergence, never absorbed.
import { createHash } from 'node:crypto';
import { requirePinnedAgreement } from './graded-plan.mjs';
import { buildInputReport, checkInput, sameInputFact } from './input-state.mjs';
import { makeRoutedInputCases } from './routed-input-cases.mjs';
import { experimentsOf, routedInputsDiverge } from './routed-input-grading.mjs';
import { pxmAdapterId } from './pxm-grading-collector.mjs';
import { gradePxmCase, oracleDisagreements, oracleHex, pinnedOutcome, pxmCaseIds, pxmContractsVersion, pxmEffectContracts, pxmGradedSuiteId, pxmPinned,
    pxmRecordingContext, replayPxmPinned, servedPxm } from './pxm-contracts.mjs';

const label = 'SGR-pixel';
const derivedFrom = 'ntilde-owned-left-right-margins-grading-v1';
const scope = 'Pinned terminfo.dev@4.0.0 input.pixel-mouse termless code (always inconclusive by design: mouseInputProbe reads ctx.getMode("pixelMouse") and validates no '
    + 'generated event or report) replayed over the DECRQM state the child itself received, preserved beside a separately identified effect contract graded from 32 declared '
    + 'Avalonia events routed through the production App input path in a disposable direct-local pane: negotiation proved by the child DECRQM replies for 1005, 1006, 1015, 1016 and '
    + 'the tracking selectors before every dispatch, exact child-received bytes from an independent raw stdin reader, literal protocol expectations cross-checked by an independent '
    + 'oracle written from xterm button.c, events declared by exact device-pixel positions in the pane text area, legacy and SGR cell-coordinate controls, SGR-Pixels reports '
    + 'for presses, releases, wheel, held-button and any-event motion (reported per pixel change, not per cell), modifiers and X10 press-only tracking, the single-slot '
    + 'selection rules between 1005, 1006, 1015 and 1016, ordered FIFO capture barriers, and restoration. Generated App events are not physical pointer input, the declared '
    + 'pixels are the App\'s device-pixel unit (the unit of its CSI 14 t / 16 t reports) and not a measurement of any other terminal\'s pixel geometry, App admission is not '
    + 'child receipt, direct-local delivery does not qualify SSH, mux or Windows, and no stock grade is changed by this layer.';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');

/** Validate run identity and the adapter binding. */
export function validatePxmEvidence(run, adapter) {
    if (!adapter || adapter.id !== pxmAdapterId || run.adapterId !== pxmAdapterId
        || !/^[a-f\d]{64}$/.test(adapter.bundleSha256 ?? '') || !/^[a-f\d]{64}$/.test(adapter.upstreamBundleSha256 ?? '')
        || adapter.derivedFromAdapterId !== derivedFrom) throw new Error(`Invalid ${label} adapter identity.`);
    if (JSON.stringify(run.probeIds) !== JSON.stringify([...pxmCaseIds]) || !Array.isArray(run.cases) || run.cases.length !== pxmCaseIds.length) {
        throw new Error(`${label} run does not cover the contracted case.`);
    }
}

/** The child-served value for the benchmark, re-derived from the journaled control and restoration captures. */
function replayFor(caseId, frames) {
    const controls = {};
    for (const frame of frames) if (frame.kind === 'input-control') controls[frame.caseId] = frame.capture.hex;
    return servedPxm(caseId, controls, frames.at(-1).capture.hex);
}

/** The recorded pinned result must equal its replay over the child's reply (and the parent module replay), and be inconclusive by design. */
function checkPinned(caseId, recorded, served, replayModule) {
    const { probeId, ...result } = recorded.pinned ?? {};
    if (probeId !== caseId) throw new Error(`Missing pinned ${caseId} grading result.`);
    requirePinnedAgreement({ caseId }, result, replayPxmPinned(caseId, served), label);
    if (pinnedOutcome(result) !== 'inconclusive') throw new Error(`Pinned ${caseId} must be inconclusive by design.`);
    if (replayModule) {
        const context = pxmRecordingContext(caseId, served);
        const { probeId: replayId, ...replay } = replayModule.ntildeRunPxmTermless(caseId, context);
        if (replayId !== caseId || JSON.stringify(replay) !== JSON.stringify(result) || context.fedCount !== 0 || context.captureCount !== 0) {
            throw new Error(`Pinned ${caseId} replay does not reproduce the live grading.`);
        }
    }
    return result;
}

const effectOf = (caseId, contractsVersion, effect) => ({ id: pxmEffectContracts[caseId], version: contractsVersion, outcome: effect.outcome,
    ...(effect.reason ? { reason: effect.reason } : {}), ...(effect.failedControls?.length ? { failedControls: effect.failedControls } : {}),
    ...(effect.failedRequired?.length ? { failedRequired: effect.failedRequired } : {}),
    attribution: 'separately identified Ntilde effect contract; not the pinned probe' });

/** The App's own pointer facts for a pixel-declared event: the cell it derived and the routed DIP position, kept apart from the graded bytes. */
const withPointer = (experiment, actions) => {
    if (experiment.event.pixelX === undefined) return experiment;
    const { column, row, x, y } = actions[experiment.actionIndex].observed.app;
    return { ...experiment, pointer: { column, row, x, y } };
};

/** The benchmark's report entry. */
function caseReport(caseId, recorded, context) {
    const { plan, input, actions, frames, contractsVersion, replayModule } = context, pinnedFixture = pxmPinned[caseId];
    if (JSON.stringify(recorded.feeds) !== JSON.stringify([...pinnedFixture.feeds])) throw new Error(`Pinned ${caseId} fixture differs from the contract.`);
    const experiments = experimentsOf(caseId, plan, input, actions, item => oracleHex(item, plan[plan.indexOf(item) - 1])).map(item => withPointer(item, actions));
    const effect = gradePxmCase(caseId, experiments, contractsVersion), served = replayFor(caseId, frames);
    if (JSON.stringify(recorded.served) !== JSON.stringify(served)) throw new Error(`Pinned ${caseId} served value differs from the child's reply.`);
    const result = checkPinned(caseId, recorded, served, replayModule), outcome = pinnedOutcome(result);
    return { caseId, pinnedCriterion: pinnedFixture.criterion, pinnedFeeds: [...pinnedFixture.feeds], served,
        pinned: { pass: result.pass, response: result.response, note: result.note ?? null, observation: result.observation ?? null, notTested: result.notTested ?? null },
        experiments, effectContract: effectOf(caseId, contractsVersion, effect), contractResult: effect,
        ...(effect.outcome === 'inconclusive' ? {} : { divergence: { pinnedOutcome: outcome, contractOutcome: effect.outcome, kind: 'pinned-inconclusive-by-design' } }),
        conclusion: { outcome, evidence: 'owned-generated-input-child-bytes', grading: 'pinned-termless-preserved-by-design',
            note: result.note ?? null, scope: 'declared Avalonia events in a disposable direct-local pane; child bytes captured independently of the App' } };
}

/** Re-derive and cross-check the case; throw on any disagreement. */
export function buildPxmGradingReport({ frames, actions, seal }, run, runId, adapter, { contractsVersion = pxmContractsVersion, replayModule = null,
    inputPlan, inputReportSha256 } = {}) {
    if (!inputPlan || typeof inputPlan.make !== 'function') throw new Error(`${label} requires the policy-selected input plan.`);
    validatePxmEvidence(run, adapter);
    const input = buildInputReport(frames, actions, seal, runId, inputPlan);
    const plan = inputPlan.make(frames[0].screen.rows, frames[0].screen.cols);
    checkInput(sameInputFact(input.inputs, plan) && oracleDisagreements(plan).length === 0);
    const diverge = routedInputsDiverge(input.inputs, makeRoutedInputCases(frames[0].screen.rows, frames[0].screen.cols));
    if (diverge) throw new Error(`The shared journal no longer carries the routed-input benchmarks verbatim: ${diverge}.`);
    const context = { plan, input, actions, frames, contractsVersion, replayModule };
    const cases = pxmCaseIds.map((caseId, position) => {
        if (run.cases[position].caseId !== caseId) throw new Error(`${label} case order differs from the contracts.`);
        return caseReport(caseId, run.cases[position], context);
    });
    return { schemaVersion: 1, suiteId: pxmGradedSuiteId, runId, contractsVersion, adapter, scope, inputSuiteId: inputPlan.suiteId,
        inputReportSha256: inputReportSha256 ?? hash(JSON.stringify(input, null, 2) + '\n'), initialSnapshot: 0, restoredSnapshot: frames.length - 1, cases };
}

/** The outcome counts of the case conclusions, keyed `pinned/effect`. */
export function pxmCounts(report) {
    const counts = {};
    for (const item of report.cases) {
        const key = item.conclusion.outcome + '/' + item.effectContract.outcome;
        counts[key] = (counts[key] ?? 0) + 1;
    }
    return counts;
}
