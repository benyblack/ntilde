// Unwired Linux endpoint foundation; no App/GUI launch or collection admission.
import { readLinuxProcessFacts } from './linux-process-provider.mjs';
import { createEndpointDeadline, observeEndpointWithIo } from './linux-owned-endpoint-core.mjs';
import { endpointResources, runEndpointProbe } from './linux-owned-endpoint-platform.mjs';

export const createLinuxEndpointDeadline = createEndpointDeadline;

/** Probe-only credentials do not authenticate another connection or any application transaction. */
export function observeLinuxPrivateAgentEndpoint(expected, options) {
    if (process.platform !== 'linux') throw new Error('Private endpoint observation requires actual Linux.');
    return observeEndpointWithIo({ now: () => process.hrtime.bigint(), currentUid: () => process.getuid(),
        facts: (pid, timeoutMs) => readLinuxProcessFacts(pid, { timeoutMs }),
        ...endpointResources, probe: runEndpointProbe }, expected, options);
}
