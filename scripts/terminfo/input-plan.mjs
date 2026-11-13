// A plan is the fixed, source-selected experiment list one routed-input collection executes. v1 is the frozen 39-event
// foundation whose publication must keep re-deriving unchanged; v2 is the benchmark plan whose read observations record
// mismatches (graded by the layer) instead of aborting the run; v3 keeps every v2 event verbatim and appends the
// modifyOtherKeys qualification (whose control captures also prove the XTQMODKEYS level). Modes are always proved
// exactly before any dispatch.
import { inputSuiteId, makeInputCases, ownedInputPolicy } from './input-cases.mjs';
import { makeRoutedInputCases, ownedRoutedInputPolicy, routedInputPlanId, routedInputSuiteId } from './routed-input-cases.mjs';
import { makeModifyOtherKeysCases, modifyOtherKeysPlanId, ownedModifyOtherKeysPolicy, routedInputV3SuiteId, xtqmodkeysQuery } from './modify-other-keys-cases.mjs';

/** The mode-profile constants the plans share; input-state re-exports them so existing importers stay unchanged. */
export const inputModeNumbers = Object.freeze([66, 9, 1000, 1002, 1003, 1006]);
export const inputModeQueries = inputModeNumbers.map(n => `\x1b[?${n}$p`).join('') + '\x1b[?u';
export const defaultInputModes = Object.freeze({ applicationKeypad: false, mouseTracking: 0, mouseSgr: false, kittyKeyboardFlags: 0 });
export const inputPlanV1 = Object.freeze({ id: 'v1', suiteId: inputSuiteId, policy: ownedInputPolicy, make: makeInputCases, exactEvents: true,
    scope: count => `${count} standalone declared Avalonia events; paired complete state, independent raw local-child bytes and source-selected mode queries. No native keyboard, remote/mux transport, Windows runtime or stock-grade claim.` });
export const inputPlanV2 = Object.freeze({ id: routedInputPlanId, suiteId: routedInputSuiteId, policy: ownedRoutedInputPolicy, make: makeRoutedInputCases, exactEvents: false,
    scope: count => `${count} standalone declared Avalonia events in the benchmark plan (input.x10-mouse, input.button-event-mouse, modes.application-keypad); paired complete state, independent raw local-child bytes, source-selected mode queries and recorded (not asserted) event bytes. No native keyboard, remote/mux transport, Windows runtime or stock-grade claim.` });
export const inputPlanV3 = Object.freeze({ id: modifyOtherKeysPlanId, suiteId: routedInputV3SuiteId, policy: ownedModifyOtherKeysPolicy, make: makeModifyOtherKeysCases, exactEvents: false,
    queries: inputModeQueries + xtqmodkeysQuery,
    finalModes: Object.freeze({ ...defaultInputModes, modifyOtherKeysLevel: 0 }),
    captureBudget: 256, readerBudget: 128,
    scope: count => `${count} standalone declared Avalonia events in the benchmark plan (input.x10-mouse, input.button-event-mouse, modes.application-keypad, input.modify-other-keys levels 1 and 2); paired complete state, independent raw local-child bytes, source-selected mode and XTQMODKEYS queries and recorded (not asserted) event bytes. No native keyboard, remote/mux transport, Windows runtime or stock-grade claim.` });
/** The plan the current runner and worker execute; every older plan stays available for historical re-derivation. */
export const currentInputPlan = inputPlanV3;
export const inputPlans = Object.freeze([inputPlanV1, inputPlanV2, inputPlanV3]);
/** Frames: start, bind, a control and a read per case, finish. Raw attempts: bind, two per case, the final restoration. */
export const inputFrameCount = count => 2 * count + 3;
export const inputAttemptCount = count => 2 * count + 2;
/** The exact raw-capture count of one execution of each plan (bind + two per case + restoration); case counts do not vary with geometry. */
export const inputPlanCaptureCounts = Object.freeze(inputPlans.map(plan => inputAttemptCount(plan.make(40, 140).length)));
/** The largest prior-capture count any later suite can owe after an input collection of the current plans. */
export const inputPriorCaptureCap = Math.max(...inputPlanCaptureCounts);
/** First source policy whose runs execute the v2 benchmark plan; the v3 plan (modifyOtherKeys) follows the same rule. */
export const routedInputFirstPolicy = 36;
export const modifyOtherKeysFirstPolicy = 50;
export function inputPlanForPolicy(policy) {
    const version = /^readonly-owned-source-graph-v(\d+)$/.exec(policy ?? '')?.[1];
    if (version === undefined) return inputPlanV1;
    return Number(version) >= modifyOtherKeysFirstPolicy ? inputPlanV3 : Number(version) >= routedInputFirstPolicy ? inputPlanV2 : inputPlanV1;
}
/** The plan a run declared: receipts bind journals to the declared suite, so a plan generation is proved by its own
 * journal rather than by the policy number; the policy mapping above only chooses what the current runner executes. */
export function inputPlanForSuiteId(suiteId) {
    return inputPlans.find(plan => plan.suiteId === suiteId);
}
