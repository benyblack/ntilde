// A plan is the fixed, source-selected experiment list one routed-input collection executes. v1 is the frozen 39-event
// foundation whose publication must keep re-deriving unchanged; v2 is the benchmark plan whose read observations record
// mismatches (graded by the layer) instead of aborting the run. Modes are always proved exactly before any dispatch.
import { inputSuiteId, makeInputCases, ownedInputPolicy } from './input-cases.mjs';
import { makeRoutedInputCases, ownedRoutedInputPolicy, routedInputPlanId, routedInputSuiteId } from './routed-input-cases.mjs';

export const inputPlanV1 = Object.freeze({ id: 'v1', suiteId: inputSuiteId, policy: ownedInputPolicy, make: makeInputCases, exactEvents: true,
    scope: count => `${count} standalone declared Avalonia events; paired complete state, independent raw local-child bytes and source-selected mode queries. No native keyboard, remote/mux transport, Windows runtime or stock-grade claim.` });
export const inputPlanV2 = Object.freeze({ id: routedInputPlanId, suiteId: routedInputSuiteId, policy: ownedRoutedInputPolicy, make: makeRoutedInputCases, exactEvents: false,
    scope: count => `${count} standalone declared Avalonia events in the benchmark plan (input.x10-mouse, input.button-event-mouse, modes.application-keypad); paired complete state, independent raw local-child bytes, source-selected mode queries and recorded (not asserted) event bytes. No native keyboard, remote/mux transport, Windows runtime or stock-grade claim.` });
/** The plan the current runner and worker execute; every older plan stays available for historical re-derivation. */
export const currentInputPlan = inputPlanV2;
export const inputPlans = Object.freeze([inputPlanV1, inputPlanV2]);
/** Frames: start, bind, a control and a read per case, finish. Raw attempts: bind, two per case, the final restoration. */
export const inputFrameCount = count => 2 * count + 3;
export const inputAttemptCount = count => 2 * count + 2;
/** First source policy whose runs execute the v2 plan; earlier policies carry the frozen v1 plan. */
export const routedInputFirstPolicy = 35;
export function inputPlanForPolicy(policy) {
    const version = /^readonly-owned-source-graph-v(\d+)$/.exec(policy ?? '')?.[1];
    return version !== undefined && Number(version) >= routedInputFirstPolicy ? inputPlanV2 : inputPlanV1;
}
