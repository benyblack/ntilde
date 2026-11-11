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
