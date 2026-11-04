// Unwired Linux output foundation. No App launch, collector admission, signals to target PIDs or desktop proof.
import { readLinuxOwnedChildFacts } from './linux-process-provider.mjs';
import { linuxOutputDeadline, prepareLinuxOutputWithIo } from './linux-owned-output-core.mjs';
import { runLinuxStty } from './linux-owned-output-utility.mjs';
import { linuxOutputResources } from './linux-owned-output-resources.mjs';

function productionIo() {
    return {
        now: () => process.hrtime.bigint(), currentUid: () => process.getuid(),
        facts: (expected, timeoutMs) => readLinuxOwnedChildFacts({ parentPid: expected.app.pid,
            childPid: expected.worker.pid, uid: expected.uid, executable: expected.worker.executable,
            argv: expected.worker.argv, runId: expected.runId }, { timeoutMs }),
        ...linuxOutputResources,
        run: runLinuxStty,
    };
}

/** Caller chooses an explicit phase budget within its remaining run/cleanup budget. No per-step reset. */
export function createLinuxOutputDeadline(timeoutMs) { return linuxOutputDeadline(timeoutMs); }

/** Strict fixed-proc/system-stty/private-held-PTY foundation. This does not enable Linux collection. */
export function prepareLinuxOwnedOutput(expected, tool, options) {
    if (process.platform !== 'linux') throw new Error('Linux owned-output adapter requires actual Linux.');
    return prepareLinuxOutputWithIo(productionIo(), expected, tool, options);
}
