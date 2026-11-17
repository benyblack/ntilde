// The parent's wheel action for the OSC 720 experiments. One routed mouse-wheel notch (+1, away from the live line) goes through the same owned generated-input path the routed-input suites use,
// with its ownership proofs before and after; the observed view position, not this record, is the evidence. The whole action stays inside the owner socket's five-second inactivity limit, so it
// is exactly one notch and the caller's remaining deadline bounds the input request.
import { generateOwnedInput } from './input-action.mjs';
import { osc720ActionDeltas } from './osc720-contracts.mjs';

export const osc720WheelNotches = 1;
/** The generated pointer event needs a viewport cell; the first cell is inside every grid. */
export const osc720WheelEvent = Object.freeze({ kind: 'mouseWheel', wheelDelta: 1, column: 1, row: 1 });
const eventFor = action => ({ ...osc720WheelEvent, wheelDelta: osc720ActionDeltas[action] });
const compact = attempt => ({ request: attempt.request, eventDispatched: attempt.app?.eventDispatched === true, handled: attempt.app?.handled === true,
    boundary: attempt.app?.boundary ?? null, observationAvailable: attempt.app?.observationAvailable === true, postVerified: attempt.postVerified === true, observedAt: attempt.observedAt ?? null });

/** One `scroll-up` or `scroll-down` action: `config` is the owned-input configuration, `budget()` the remaining owner deadline. A failed notch rejects with the attempt attached; it is never retried. */
export async function runViewAction({ action, config, budget, input = generateOwnedInput, now = Date.now }) {
    if (!Object.hasOwn(osc720ActionDeltas, action)) throw new Error('Invalid OSC 720 action.');
    const startedAt = new Date(now()).toISOString(), notches = [];
    for (let index = 0; index < osc720WheelNotches; index++) notches.push(compact(await input(config, eventFor(action), budget))); // NOSONAR serial by design: each notch is a verified, never-retried action
    return { action, notches, startedAt, finishedAt: new Date(now()).toISOString() };
}
