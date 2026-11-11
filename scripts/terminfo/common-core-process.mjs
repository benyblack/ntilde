import { spawn } from 'node:child_process';
/** Observe asynchronous spawn failures immediately so readiness waits cannot become unhandled errors. */
export function spawnCommonCoreApplication(executable, args, options) {
    const state = { proc: spawn(executable, args, options), spawned: false, error: null };
    state.proc.once('spawn', () => {
        state.spawned = true;
    });
    state.proc.on('error', (error) => {
        state.error = error;
    });
    return state;
}

/** Accept a published completion first; otherwise fail promptly if the recorded worker disappears. */
export function commonCoreCollectionReady({ completed, failure, running }) {
    if (completed()) return true;
    const error = failure();
    if (error) throw error;
    if (!running()) throw new Error('Recorded collection worker exited before publishing its final record.');
    return false;
}

/** An attempted launch without a confirmed outcome cannot authorize releasing the desktop lock. */
export function assertCommonCoreLaunchKnown(attempted) {
    if (attempted) throw new Error('Application launch outcome is unknown; retain the collection lock for inspection.');
}
