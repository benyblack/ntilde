import { execFileSync } from 'node:child_process';
import { realpathSync } from 'node:fs';
import { observerEndpoint, ownedResizeRequest } from './agent-observer.mjs';
import { assertProcessOwnership, processRecord } from './ownership.mjs';
import { readOwnedOutputMode } from './terminal-output.mjs';
const commandFor = (pid, timeout) => execFileSync('/bin/ps', ['-p', String(pid), '-o', 'command='], {
    encoding: 'utf8', timeout, maxBuffer: 16384,
});

/** Re-prove the exact disposable process/PTY relation before and after the only allowed App action. */
export async function resizeOwnedPane(config, rows, cols, budget, {
    record = processRecord, command = commandFor, canonical = realpathSync,
    endpoint = observerEndpoint, rpc = ownedResizeRequest, outputMode = readOwnedOutputMode,
} = {}) {
    const verify = () => {
        if (!config.app?.pid || config.app.exitCode !== null || config.app.signalCode !== null) throw new Error('Owned resize app is no longer live.');
        const app = record(config.app.pid, budget()), worker = record(config.workerPid, budget());
        assertProcessOwnership({ app: { ...app, executable: canonical(app.executable) }, worker,
            ownerPid: config.app.pid, executable: config.executable, uid: config.uid, runId: config.runId,
            appCommand: command(config.app.pid, budget()), tty: config.tty });
    };
    verify();
    const app = await rpc(endpoint(config.appData, config.app.pid), config.paneId, rows, cols, budget());
    verify();
    const terminalOutput = outputMode(config.tty, budget());
    return { paneId: config.paneId, runId: config.runId, observedAt: new Date().toISOString(), app, terminalOutput };
}
