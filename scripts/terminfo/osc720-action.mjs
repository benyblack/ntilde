// The parent's wheel action for the OSC 720 experiments. One routed mouse-wheel notch (+1, away from the live line) goes through the same owned generated-input path the routed-input suites use,
// with its ownership proofs before and after; the observed view position, not this record, is the evidence. The whole action stays inside the owner socket's five-second inactivity limit, so it
// is exactly one notch and the caller's remaining deadline bounds the input request.
import { generateOwnedInput } from './input-action.mjs';

export const osc720WheelNotches = 1;
const compact = attempt => ({ request: attempt.request, eventDispatched: attempt.app?.eventDispatched === true, handled: attempt.app?.handled === true,
    boundary: attempt.app?.boundary ?? null, observationAvailable: attempt.app?.observationAvailable === true, postVerified: attempt.postVerified === true, observedAt: attempt.observedAt ?? null });

/** One `scroll-up` action: `config` is the owned-input configuration, `budget()` the remaining owner deadline. A failed notch rejects with the attempt attached; it is never retried. */
export async function runViewAction({ action, config, budget, input = generateOwnedInput, now = Date.now }) {
    if (action !== 'scroll-up') throw new Error('Invalid OSC 720 action.');
    const startedAt = new Date(now()).toISOString(), notches = [];
    for (let index = 0; index < osc720WheelNotches; index++) notches.push(compact(await input(config, { kind: 'mouseWheel', wheelDelta: 1 }, budget))); // NOSONAR serial by design: each notch is a verified, never-retried action
    return { action, notches, startedAt, finishedAt: new Date(now()).toISOString() };
}
