// Parent-side report builder for the modifyOtherKeys qualification (input.modify-other-keys, levels 1 and 2).
// It re-derives the owned input report from the retained journal (strict controls, ownership, raw capture and
// restoration), requires the routed-input benchmarks of the same journal to re-derive (the plan keeps their events
// verbatim), grades the benchmark from the child-received bytes against the independent contract, and requires the
// pinned probe's own result to equal its replay over the child's XTGETTCAP declaration. The pinned verdict is
// inconclusive by design and is preserved as the conclusion; the separately identified effect contract carries the
// graded verdict, so the difference is recorded as a divergence, never absorbed.
import { createHash } from 'node:crypto';
import { requirePinnedAgreement } from './graded-plan.mjs';
import { buildInputReport, checkInput, sameInputFact } from './input-state.mjs';
import { makeRoutedInputCases } from './routed-input-cases.mjs';
import { experimentsOf, routedInputsDiverge } from './routed-input-grading.mjs';
import { makeModifyOtherKeysCases } from './modify-other-keys-cases.mjs';
import { modifyOtherKeysAdapterId } from './modify-other-keys-grading-collector.mjs';
import { gradeModifyOtherKeys, modifyOtherKeysCaseIds, modifyOtherKeysContractsVersion, modifyOtherKeysEffectContracts,
    modifyOtherKeysGradedSuiteId, modifyOtherKeysPinned, modifyOtherKeysRecordingContext, oracleDisagreements, oracleHex,
    pinnedOutcome, replayModifyOtherKeysPinned, servedDeclaration } from './modify-other-keys-contracts.mjs';

const label = 'modifyOtherKeys';
const derivedFrom = 'ntilde-owned-left-right-margins-grading-v1';
const scope = 'Pinned terminfo.dev@4.0.0 input.modify-other-keys termless code (always inconclusive by design: the probe validates no generated '
    + 'event or report and reads only the capability declaration) replayed over the declaration value the child itself answered (its XTGETTCAP refusal for the '
    + 'modifyOtherKeys capability name), preserved beside a separately identified effect contract graded from 58 declared Avalonia events routed through the '
    + 'production App input path in a disposable direct-local pane: negotiation proved by the child XTQMODKEYS reply before every dispatch, exact child-received '
    + 'bytes from an independent raw stdin reader, literal protocol expectations cross-checked by an independent oracle, disabled-mode legacy controls, level 1 '
    + 'alias versus extended-chord cases, level 2 Ctrl/Ctrl+Shift/Alt chords, the Enter/Tab/Backspace/Escape table, disabled/1/2 transitions including the reset '
    + 'and disable forms and the ignored level 3, paired key-down/backend-text duplicate suppression, Shift-only-printable and Meta and AltGr policy controls, '
    + 'key-up and deliberately unhandled-event controls, a bounded kitty-precedence control in a verified neutral state, ordered FIFO capture barriers, and '
    + 'restoration of the original negotiation state. Generated App events are not physical keyboard input, App admission is not child receipt, direct-local '
    + 'delivery does not qualify SSH, mux or Windows, and no stock grade changes.';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');

/** Validate run identity and the adapter binding. */
export function validateModifyOtherKeysEvidence(run, adapter) {
    if (!adapter || adapter.id !== modifyOtherKeysAdapterId || run.adapterId !== modifyOtherKeysAdapterId
        || !/^[a-f\d]{64}$/.test(adapter.bundleSha256 ?? '') || !/^[a-f\d]{64}$/.test(adapter.upstreamBundleSha256 ?? '')
        || adapter.derivedFromAdapterId !== derivedFrom) throw new Error(`Invalid ${label} adapter identity.`);
    if (JSON.stringify(run.probeIds) !== JSON.stringify([...modifyOtherKeysCaseIds]) || !Array.isArray(run.cases) || run.cases.length !== modifyOtherKeysCaseIds.length) {
        throw new Error(`${label} run does not cover the contracted cases.`);
    }
}



/** The recorded pinned result must equal its replay over the child's declaration (and the parent module replay), and be inconclusive by design. */
function checkPinned(recorded, served, replayModule) {
    const caseId = modifyOtherKeysCaseIds[0], { probeId, ...result } = recorded.pinned ?? {};
    if (probeId !== caseId) throw new Error(`Missing pinned ${caseId} grading result.`);
    requirePinnedAgreement({ caseId }, result, replayModifyOtherKeysPinned(served), label);
    if (pinnedOutcome(result) !== 'inconclusive') throw new Error(`Pinned ${caseId} must be inconclusive by design.`);
    if (replayModule) {
        const context = modifyOtherKeysRecordingContext(served);
        const { probeId: replayId, ...replay } = replayModule.ntildeRunModifyOtherKeysTermless(caseId, context);
        if (replayId !== caseId || JSON.stringify(replay) !== JSON.stringify(result) || context.readCount !== 1) {
            throw new Error(`Pinned ${caseId} replay does not reproduce the live grading.`);
        }
    }
    return result;
}

const effectOf = (contractsVersion, effect) => ({ id: modifyOtherKeysEffectContracts[modifyOtherKeysCaseIds[0]], version: contractsVersion,
    outcome: effect.outcome, ...(effect.reason ? { reason: effect.reason } : {}),
    ...(effect.failedControls?.length ? { failedControls: effect.failedControls } : {}),
    ...(effect.failedRequired?.length ? { failedRequired: effect.failedRequired } : {}),
    attribution: 'separately identified Ntilde effect contract; not the pinned probe' });

/** The benchmark's report entry. */
function caseReport(recorded, context) {
    const caseId = modifyOtherKeysCaseIds[0], { plan, input, actions, contractsVersion, replayModule } = context;
    const pinnedFixture = modifyOtherKeysPinned[caseId];
    if (recorded.caseId !== caseId || recorded.declarationCase !== 'input.mok.declaration.xtgettcap'
        || JSON.stringify(recorded.feeds) !== JSON.stringify([...pinnedFixture.feeds])) throw new Error(`Pinned ${caseId} fixture differs from the contract.`);
    const experiments = experimentsOf(caseId, plan, input, actions, oracleHex);
    const effect = gradeModifyOtherKeys(caseId, experiments, contractsVersion);
    const controls = {};
    for (const frame of context.frames) if (frame.kind === 'input-control') controls[frame.caseId] = frame.capture.hex;
    const served = servedDeclaration(controls[recorded.declarationCase]);
    if (JSON.stringify(recorded.served) !== JSON.stringify(served)) throw new Error(`Pinned ${caseId} served declaration differs from the child's reply.`);
    const result = checkPinned(recorded, served, replayModule), outcome = pinnedOutcome(result);
    return { caseId, pinnedCriterion: pinnedFixture.criterion, pinnedFeeds: [...pinnedFixture.feeds], served,
        pinned: { pass: result.pass, response: result.response, note: result.note ?? null, observation: result.observation ?? null, notTested: result.notTested ?? null },
        experiments, effectContract: effectOf(contractsVersion, effect), contractResult: effect,
        ...(effect.outcome === 'inconclusive' ? {} : { divergence: { pinnedOutcome: outcome, contractOutcome: effect.outcome, kind: 'pinned-inconclusive-by-design' } }),
        conclusion: { outcome, evidence: 'owned-generated-input-child-bytes', grading: 'pinned-termless-preserved-by-design',
            note: result.note ?? null, scope: 'declared Avalonia events in a disposable direct-local pane; child bytes captured independently of the App' } };
}

/** Re-derive and cross-check the benchmark; throw on any disagreement. */
export function buildModifyOtherKeysGradingReport({ frames, actions, seal }, run, runId, adapter, { contractsVersion = modifyOtherKeysContractsVersion,
    replayModule = null, inputPlan, inputReportSha256 } = {}) {
    if (!inputPlan || typeof inputPlan.make !== 'function') throw new Error(`${label} requires the policy-selected input plan.`);
    validateModifyOtherKeysEvidence(run, adapter);
    const input = buildInputReport(frames, actions, seal, runId, inputPlan);
    const plan = makeModifyOtherKeysCases(frames[0].screen.rows, frames[0].screen.cols);
    checkInput(sameInputFact(input.inputs, plan) && oracleDisagreements(plan).length === 0);
    const routedPlan = makeRoutedInputCases(frames[0].screen.rows, frames[0].screen.cols);
    const diverge = routedInputsDiverge(input.inputs, routedPlan);
    if (diverge) throw new Error(`The shared journal no longer carries the routed-input benchmarks verbatim: ${diverge}.`);
    const context = { plan, input, actions, frames, contractsVersion, replayModule };
    const cases = modifyOtherKeysCaseIds.map((caseId, position) => {
        if (run.cases[position].caseId !== caseId) throw new Error(`${label} case order differs from the contracts.`);
        return caseReport(run.cases[position], context);
    });
    return { schemaVersion: 1, suiteId: modifyOtherKeysGradedSuiteId, runId, contractsVersion, adapter, scope, inputSuiteId: inputPlan.suiteId,
        inputReportSha256: inputReportSha256 ?? hash(JSON.stringify(input, null, 2) + '\n'), initialSnapshot: 0, restoredSnapshot: frames.length - 1, cases };
}

/** The outcome counts of the case conclusions, keyed `pinned/effect`. */
export function modifyOtherKeysCounts(report) {
    const counts = {};
    for (const item of report.cases) {
        const key = item.conclusion.outcome + '/' + item.effectContract.outcome;
        counts[key] = (counts[key] ?? 0) + 1;
    }
    return counts;
}
