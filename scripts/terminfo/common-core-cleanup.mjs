import { pollCommonCore } from './common-core-poll.mjs';
/** Terminate only a still-matching launch identity, rechecking before escalation. */
export async function terminateCommonCoreOwner({ matches, exited, signal, pause, attempts = 50 }) {
    if (await exited()) return;
    if (!(await matches())) throw new Error('Startup cleanup refuses a changed launch identity.');
    await signal('SIGTERM');
    try {
        await pollCommonCore(exited, { timeoutMs: attempts * 100, maxAttempts: attempts, pause });
        return;
    } catch {
        /* Escalation still requires the recorded identity. */
    }
    if (await exited()) return;
    if (!(await matches())) throw new Error('Startup cleanup refuses escalation after launch identity changed.');
    await signal('SIGKILL');
    try {
        await pollCommonCore(exited, { timeoutMs: attempts * 100, maxAttempts: attempts, pause });
        return;
    } catch {
        /* Report a surviving owned process below. */
    }
    throw new Error('Recorded startup owner survived bounded termination.');
}
