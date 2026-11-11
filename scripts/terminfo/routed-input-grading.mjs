// Parent-side report builder for the routed-input qualification (input.x10-mouse, input.button-event-mouse, modes.application-keypad).
// It re-derives the owned input report from the retained journal (strict controls, ownership, raw capture and restoration), grades each
// benchmark from the child-received bytes against the independent contract, and requires the pinned probes' own results to equal their
// replay over the child's DECRQM replies. The pinned verdicts are inconclusive by design and are preserved as the conclusions; the
// separately identified effect contracts carry the graded verdicts, so a difference is recorded as a divergence, never absorbed.
import { createHash } from 'node:crypto';
import { requirePinnedAgreement } from './graded-plan.mjs';
import { buildInputReport, checkInput, sameInputFact } from './input-state.mjs';
import { inputPlanV2 } from './input-plan.mjs';
import { makeRoutedInputCases } from './routed-input-cases.mjs';
import { routedInputAdapterId } from './routed-input-grading-collector.mjs';
import { gradeRoutedInputCase, oracleDisagreements, oracleHex, pinnedOutcome, replayRoutedInputPinned, routedInputCaseIds,
    routedInputContractsVersion, routedInputEffectContracts, routedInputGradedSuiteId, routedInputPinned, routedInputRecordingContext,
    servedModes } from './routed-input-contracts.mjs';

const label = 'Routed-input';
const derivedFrom = 'ntilde-owned-left-right-margins-grading-v1';
const scope = 'Pinned terminfo.dev@4.0.0 input.x10-mouse, input.button-event-mouse and modes.application-keypad termless code (always inconclusive by design: '
    + 'mouseInputProbe validates no generated event or report; the keypad probe returns notTestedResult) replayed over mode values the child itself received as DECRQM replies, '
    + 'preserved beside separately identified effect contracts graded from 58 declared Avalonia events routed through the production App input path in a disposable direct-local '
    + 'pane: exact child-received bytes from an independent raw stdin reader, literal protocol expectations cross-checked by an independent oracle, controls under the other '
    + 'tracking protocols and the numeric keypad, ordered FIFO capture barriers, and restoration. Generated App events are not physical keyboard or pointer input, App admission is '
    + 'not child receipt, and direct-local delivery does not qualify SSH, mux or Windows; no stock grades.';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');

/** Validate run identity and the adapter binding. */
export function validateRoutedInputEvidence(run, adapter) {
    if (!adapter || adapter.id !== routedInputAdapterId || run.adapterId !== routedInputAdapterId
        || !/^[a-f\d]{64}$/.test(adapter.bundleSha256 ?? '') || !/^[a-f\d]{64}$/.test(adapter.upstreamBundleSha256 ?? '')
        || adapter.derivedFromAdapterId !== derivedFrom) throw new Error(`Invalid ${label} adapter identity.`);
    if (JSON.stringify(run.probeIds) !== JSON.stringify([...routedInputCaseIds]) || !Array.isArray(run.cases) || run.cases.length !== routedInputCaseIds.length) {
        throw new Error(`${label} run does not cover the contracted cases.`);
    }
}

/** Child-served values and pinned replay for one benchmark, re-derived from the journaled control and restoration captures. */
function replayFor(caseId, frames, cases) {
    const controls = {};
    for (const frame of frames) if (frame.kind === 'input-control') controls[frame.caseId] = frame.capture.hex;
    const restored = frames.at(-1).capture.hex;
    return servedModes(caseId, controls, restored, cases);
}

/** The graded experiments of one benchmark: each plan case's role, literal and oracle expectation, child bytes and the App's own facts, kept apart. */
function experimentsOf(caseId, plan, input, actions) {
    const byId = new Map(input.observations.map((item, index) => [item.featureId, { observation: item, index }]));
    return plan.filter(item => item.roles[caseId]).map(item => {
        const { observation, index } = byId.get(item.id), app = actions[index].observed.app;
        return { id: item.id, role: item.roles[caseId], matched: observation.result === 'passed',
            event: item.event, modes: item.modes, setup: item.setup, expectedHex: item.expectedHex, actualHex: observation.actualHex,
            oracleHex: oracleHex(item, plan[index - 1]), controlSnapshot: observation.controlSnapshot, snapshot: observation.snapshot, actionIndex: index,
            appFacts: { eventDispatched: app.eventDispatched, handled: app.handled, inputSubmitted: app.inputSubmitted,
                observationAvailable: app.observationAvailable, boundary: app.boundary } };
    });
}

/** The recorded pinned result must equal its replay over the child's replies (and the parent module replay), and be inconclusive by design. */
function checkPinned(caseId, recorded, served, replayModule) {
    const { probeId, ...result } = recorded.pinned ?? {};
    if (probeId !== caseId) throw new Error(`Missing pinned ${caseId} grading result.`);
    requirePinnedAgreement({ caseId }, result, replayRoutedInputPinned(caseId, served), label);
    if (pinnedOutcome(result) !== 'inconclusive') throw new Error(`Pinned ${caseId} must be inconclusive by design.`);
    if (replayModule) {
        const context = routedInputRecordingContext(caseId, served);
        const { probeId: replayId, ...replay } = replayModule.ntildeRunRoutedInputTermless(caseId, context);
        if (replayId !== caseId || JSON.stringify(replay) !== JSON.stringify(result) || context.fedCount !== routedInputPinned[caseId].feeds.length) {
            throw new Error(`Pinned ${caseId} replay does not reproduce the live grading.`);
        }
    }
    return result;
}

const effectOf = (caseId, contractsVersion, effect) => ({ id: routedInputEffectContracts[caseId], version: contractsVersion, outcome: effect.outcome,
    ...(effect.reason ? { reason: effect.reason } : {}), ...(effect.failedControls?.length ? { failedControls: effect.failedControls } : {}),
    ...(effect.failedRequired?.length ? { failedRequired: effect.failedRequired } : {}),
    attribution: 'separately identified Ntilde effect contract; not the pinned probe' });

/** One benchmark's report entry. */
function caseReport(caseId, recorded, context) {
    const { plan, input, actions, frames, contractsVersion, replayModule } = context, pinnedFixture = routedInputPinned[caseId];
    if (JSON.stringify(recorded.feeds) !== JSON.stringify([...pinnedFixture.feeds])) throw new Error(`Pinned ${caseId} fixture differs from the contract.`);
    const experiments = experimentsOf(caseId, plan, input, actions);
    const effect = gradeRoutedInputCase(caseId, experiments, contractsVersion), served = replayFor(caseId, frames, plan);
    if (JSON.stringify(recorded.served) !== JSON.stringify(served)) throw new Error(`Pinned ${caseId} served modes differ from the child's replies.`);
    const result = checkPinned(caseId, recorded, served, replayModule), outcome = pinnedOutcome(result);
    return { caseId, pinnedCriterion: pinnedFixture.criterion, pinnedFeeds: [...pinnedFixture.feeds], served,
        pinned: { pass: result.pass, response: result.response, note: result.note ?? null, observation: result.observation ?? null, notTested: result.notTested ?? null },
        experiments, effectContract: effectOf(caseId, contractsVersion, effect), contractResult: effect,
        ...(effect.outcome === 'inconclusive' ? {} : { divergence: { pinnedOutcome: outcome, contractOutcome: effect.outcome, kind: 'pinned-inconclusive-by-design' } }),
        conclusion: { outcome, evidence: 'owned-generated-input-child-bytes', grading: 'pinned-termless-preserved-by-design',
            note: result.note ?? null, scope: 'declared Avalonia events in a disposable direct-local pane; child bytes captured independently of the App' } };
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export function buildRoutedInputGradingReport({ frames, actions, seal }, run, runId, adapter, { contractsVersion = routedInputContractsVersion, replayModule = null, inputReportSha256 } = {}) {
    validateRoutedInputEvidence(run, adapter);
    const input = buildInputReport(frames, actions, seal, runId, inputPlanV2);
    const plan = makeRoutedInputCases(frames[0].screen.rows, frames[0].screen.cols);
    checkInput(sameInputFact(input.inputs, plan) && oracleDisagreements(plan).length === 0);
    const context = { plan, input, actions, frames, contractsVersion, replayModule };
    const cases = routedInputCaseIds.map((caseId, position) => {
        if (run.cases[position].caseId !== caseId) throw new Error(`${label} case order differs from the contracts.`);
        return caseReport(caseId, run.cases[position], context);
    });
    return { schemaVersion: 1, suiteId: routedInputGradedSuiteId, runId, contractsVersion, adapter, scope, inputSuiteId: inputPlanV2.suiteId,
        inputReportSha256: inputReportSha256 ?? hash(JSON.stringify(input, null, 2) + '\n'), initialSnapshot: 0, restoredSnapshot: frames.length - 1, cases };
}

/** The outcome counts of the case conclusions, keyed `pinned/effect`. */
export function routedInputCounts(report) {
    const counts = {};
    for (const item of report.cases) {
        const key = item.conclusion.outcome + '/' + item.effectContract.outcome;
        counts[key] = (counts[key] ?? 0) + 1;
    }
    return counts;
}
