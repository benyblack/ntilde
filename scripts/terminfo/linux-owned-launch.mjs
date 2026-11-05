// Unwired source-owned Linux launch foundation. It does not qualify a desktop/collector transaction.
import { readLinuxProcessFacts } from './linux-process-provider.mjs';
import { launchWithIo } from './linux-launch/core.mjs';
import { openLaunchChannel } from './linux-launch/platform.mjs';
export { createLinuxLaunchDeadline } from './linux-launch/core.mjs';
/** Launch only the fixed private ELF boundary on supported Linux; this does not qualify an App desktop or collector transaction. */
export function launchLinuxOwnedApp(artifact, fixture, pythonPin, options) {
    if (process.platform !== 'linux' || !['x64', 'arm64'].includes(process.arch)) throw new Error('Owned App launch requires supported Linux.');
    return launchWithIo({ now: () => process.hrtime.bigint(), currentUid: () => process.getuid(), selfPid: () => process.pid,
        process: (pid, timeoutMs) => readLinuxProcessFacts(pid, { timeoutMs }), open: openLaunchChannel }, artifact, fixture, pythonPin, options);
}
