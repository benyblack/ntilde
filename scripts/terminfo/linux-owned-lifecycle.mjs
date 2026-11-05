// Unwired Linux foundation. Stable exit observation is not a parent reap acknowledgment.
import { readLinuxOwnedChildFacts, readLinuxProcessFacts } from './linux-process-provider.mjs';
import { acquireWithIo } from './linux-lifecycle/core.mjs';
import { openLifecycleChannel } from './linux-lifecycle/platform.mjs';

export { createLinuxLifecycleDeadline } from './linux-lifecycle/core.mjs';
export function acquireLinuxLifecycle(expected, pythonPin, options) {
    if (process.platform !== 'linux') throw new Error('Pidfd lifecycle requires actual Linux.');
    return acquireWithIo({ now: () => process.hrtime.bigint(), currentUid: () => process.getuid(),
        paired: (value, timeoutMs) => readLinuxOwnedChildFacts({ parentPid: value.app.pid,
            childPid: value.worker.pid, uid: value.uid, runId: value.runId,
            executable: value.worker.executable, argv: value.worker.argv }, { timeoutMs }),
        process: (pid, timeoutMs) => readLinuxProcessFacts(pid, { timeoutMs }), open: openLifecycleChannel }, expected, pythonPin, options);
}
