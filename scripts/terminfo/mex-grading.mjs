// Parent-side report builder for the extended-mouse-encoding qualification (modes.utf8-mouse-1005 and input.urxvt-mouse).
// It re-derives the owned input report from the retained journal (strict controls, ownership, raw capture and restoration), requires
// the plan to be exactly the v4 plan (which keeps every earlier event verbatim, so the routed-input and modifyOtherKeys layers keep
// re-deriving from the same journal), grades each benchmark from the child-received bytes against the independent contract, and
// requires the pinned probes' own results to equal their replay over the child's DECRQM replies. The pinned verdicts are inconclusive
// by design and are preserved as the conclusions; the separately identified effect contracts carry the graded verdicts, so a
// difference is recorded as a divergence, never absorbed.
import { createHash } from 'node:crypto';
import { requirePinnedAgreement } from './graded-plan.mjs';
import { buildInputReport, checkInput, sameInputFact } from './input-state.mjs';
import { makeRoutedInputCases } from './routed-input-cases.mjs';
import { experimentsOf, routedInputsDiverge } from './routed-input-grading.mjs';
import { mexAdapterId } from './mex-grading-collector.mjs';
import { gradeMexCase, mexCaseIds, mexContractsVersion, mexEffectContracts, mexGradedSuiteId, mexPinned, mexRecordingContext,
    oracleDisagreements, oracleHex, pinnedOutcome, replayMexPinned, servedMex } from './mex-contracts.mjs';

const label = 'Extended-mouse';
const derivedFrom = 'ntilde-owned-left-right-margins-grading-v1';
const scope = 'Pinned terminfo.dev@4.0.0 modes.utf8-mouse-1005 and input.urxvt-mouse termless code (always inconclusive by design: the 1005 probe feeds ?1005h, captures '
    + 'the DECRQM reply, feeds ?1005l and returns notTestedResult; mouseInputProbe validates no generated event or report) replayed over the DECRQM reply and tracking state the child '
    + 'itself received, preserved beside separately identified effect contracts graded from 46 declared Avalonia events routed through the production App input path in a '
    + 'disposable direct-local pane: negotiation proved by the child DECRQM replies for 1005, 1006, 1015 and the tracking selectors before every dispatch, exact '
    + 'child-received bytes from an independent raw stdin reader, literal protocol expectations cross-checked by an independent oracle written from xterm button.c, legacy and SGR '
    + 'controls, UTF-8 and decimal reports for presses, releases, wheel, held-button and any-event motion, modifiers and X10 press-only tracking, the single-slot selection '
    + 'rules between 1005, 1006 and 1015, ordered FIFO capture barriers, and restoration. Generated App events are not physical pointer input, App admission is not child '
    + 'receipt, direct-local delivery does not qualify SSH, mux or Windows, the stock terminal path of modes.utf8-mouse-1005 (a DECRQM recognition check) is a separate '
    + 'stock-collector result, and no stock grade is changed by this layer.';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');

/** Validate run identity and the adapter binding. */
export function validateMexEvidence(run, adapter) {
    if (!adapter || adapter.id !== mexAdapterId || run.adapterId !== mexAdapterId
        || !/^[a-f\d]{64}$/.test(adapter.bundleSha256 ?? '') || !/^[a-f\d]{64}$/.test(adapter.upstreamBundleSha256 ?? '')
        || adapter.derivedFromAdapterId !== derivedFrom) throw new Error(`Invalid ${label} adapter identity.`);
    if (JSON.stringify(run.probeIds) !== JSON.stringify([...mexCaseIds]) || !Array.isArray(run.cases) || run.cases.length !== mexCaseIds.length) {
        throw new Error(`${label} run does not cover the contracted cases.`);
    }
}

/** Child-served values for one benchmark, re-derived from the journaled control and restoration captures. */
function replayFor(caseId, frames) {
    const controls = {};
    for (const frame of frames) if (frame.kind === 'input-control') controls[frame.caseId] = frame.capture.hex;
    return servedMex(caseId, controls, frames.at(-1).capture.hex);
}

/** The recorded pinned result must equal its replay over the child's replies (and the parent module replay), and be inconclusive by design. */
function checkPinned(caseId, recorded, served, replayModule) {
    const { probeId, ...result } = recorded.pinned ?? {};
    if (probeId !== caseId) throw new Error(`Missing pinned ${caseId} grading result.`);
    requirePinnedAgreement({ caseId }, result, replayMexPinned(caseId, served), label);
    if (pinnedOutcome(result) !== 'inconclusive') throw new Error(`Pinned ${caseId} must be inconclusive by design.`);
    if (replayModule) {
        const context = mexRecordingContext(caseId, served);
        const { probeId: replayId, ...replay } = replayModule.ntildeRunMexTermless(caseId, context);
        if (replayId !== caseId || JSON.stringify(replay) !== JSON.stringify(result) || context.fedCount !== mexPinned[caseId].feeds.length
            || context.captureCount !== (caseId === 'modes.utf8-mouse-1005' ? 1 : 0)) {
            throw new Error(`Pinned ${caseId} replay does not reproduce the live grading.`);
        }
    }
    return result;
}

const effectOf = (caseId, contractsVersion, effect) => ({ id: mexEffectContracts[caseId], version: contractsVersion, outcome: effect.outcome,
    ...(effect.reason ? { reason: effect.reason } : {}), ...(effect.failedControls?.length ? { failedControls: effect.failedControls } : {}),
    ...(effect.failedRequired?.length ? { failedRequired: effect.failedRequired } : {}),
    attribution: 'separately identified Ntilde effect contract; not the pinned probe' });

/** One benchmark's report entry. */
function caseReport(caseId, recorded, context) {
    const { plan, input, actions, frames, contractsVersion, replayModule } = context, pinnedFixture = mexPinned[caseId];
    if (JSON.stringify(recorded.feeds) !== JSON.stringify([...pinnedFixture.feeds])) throw new Error(`Pinned ${caseId} fixture differs from the contract.`);
    const experiments = experimentsOf(caseId, plan, input, actions, oracleHex);
    const effect = gradeMexCase(caseId, experiments, contractsVersion), served = replayFor(caseId, frames);
    if (JSON.stringify(recorded.served) !== JSON.stringify(served)) throw new Error(`Pinned ${caseId} served values differ from the child's replies.`);
    const result = checkPinned(caseId, recorded, served, replayModule), outcome = pinnedOutcome(result);
    return { caseId, pinnedCriterion: pinnedFixture.criterion, pinnedFeeds: [...pinnedFixture.feeds], served,
        pinned: { pass: result.pass, response: result.response, note: result.note ?? null, observation: result.observation ?? null, notTested: result.notTested ?? null },
        experiments, effectContract: effectOf(caseId, contractsVersion, effect), contractResult: effect,
        ...(effect.outcome === 'inconclusive' ? {} : { divergence: { pinnedOutcome: outcome, contractOutcome: effect.outcome, kind: 'pinned-inconclusive-by-design' } }),
        conclusion: { outcome, evidence: 'owned-generated-input-child-bytes', grading: 'pinned-termless-preserved-by-design',
            note: result.note ?? null, scope: 'declared Avalonia events in a disposable direct-local pane; child bytes captured independently of the App' } };
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export function buildMexGradingReport({ frames, actions, seal }, run, runId, adapter, { contractsVersion = mexContractsVersion, replayModule = null,
    inputPlan, inputReportSha256 } = {}) {
    if (!inputPlan || typeof inputPlan.make !== 'function') throw new Error(`${label} requires the policy-selected input plan.`);
    validateMexEvidence(run, adapter);
    const input = buildInputReport(frames, actions, seal, runId, inputPlan);
    const plan = inputPlan.make(frames[0].screen.rows, frames[0].screen.cols);
    checkInput(sameInputFact(input.inputs, plan) && oracleDisagreements(plan).length === 0);
    const diverge = routedInputsDiverge(input.inputs, makeRoutedInputCases(frames[0].screen.rows, frames[0].screen.cols));
    if (diverge) throw new Error(`The shared journal no longer carries the routed-input benchmarks verbatim: ${diverge}.`);
    const context = { plan, input, actions, frames, contractsVersion, replayModule };
    const cases = mexCaseIds.map((caseId, position) => {
        if (run.cases[position].caseId !== caseId) throw new Error(`${label} case order differs from the contracts.`);
        return caseReport(caseId, run.cases[position], context);
    });
    return { schemaVersion: 1, suiteId: mexGradedSuiteId, runId, contractsVersion, adapter, scope, inputSuiteId: inputPlan.suiteId,
        inputReportSha256: inputReportSha256 ?? hash(JSON.stringify(input, null, 2) + '\n'), initialSnapshot: 0, restoredSnapshot: frames.length - 1, cases };
}

/** The outcome counts of the case conclusions, keyed `pinned/effect`. */
export function mexCounts(report) {
    const counts = {};
    for (const item of report.cases) {
        const key = item.conclusion.outcome + '/' + item.effectContract.outcome;
        counts[key] = (counts[key] ?? 0) + 1;
    }
    return counts;
}
