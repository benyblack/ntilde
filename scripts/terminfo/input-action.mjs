import { execFileSync } from 'node:child_process';
import { realpathSync, statSync } from 'node:fs';
import { agentRequest, observerEndpoint } from './agent-observer.mjs';
import { ownedInputRequest } from './input-request.mjs';
import { assertProcessOwnership, processRecord } from './ownership.mjs';
import { readOwnedOutputMode } from './terminal-output.mjs';
const commandFor = (pid, timeout) => execFileSync('/bin/ps', ['-p', String(pid), '-o', 'command='], { encoding: 'utf8', timeout, maxBuffer: 16384 });

/** Preserve dispatch facts even if post-action ownership/observation fails. Never retry. */
export async function generateOwnedInput(config, event, budget, {
    record = processRecord, command = commandFor, canonical = realpathSync, image = statSync,
    endpoint = observerEndpoint, read = agentRequest, rpc = ownedInputRequest, outputMode = readOwnedOutputMode,
} = {}) {
    const attempt = { paneId: config.paneId, runId: config.runId, request: { ...structuredClone(event), paneId: config.paneId },
        rpcStarted: false, app: null, before: null, after: null, postVerified: false };
    const verify = async () => {
        if (!config.app?.pid || config.app.exitCode !== null || config.app.signalCode !== null) throw new Error('Owned input app is no longer live.');
        const current = image(config.executable);
        if (!config.image || ['ino', 'size', 'mtimeMs'].some(key => current[key] !== config.image[key])) throw new Error('Owned input executable changed.');
        const app = record(config.app.pid, budget()), worker = record(config.workerPid, budget());
        assertProcessOwnership({ app: { ...app, executable: canonical(app.executable) }, worker,
            ownerPid: config.app.pid, executable: config.executable, uid: config.uid, runId: config.runId,
            appCommand: command(config.app.pid, budget()), tty: config.tty });
        const { sessions } = await read(endpoint(config.appData, config.app.pid), 'listSessions', {}, budget());
        const pane = sessions?.[0];
        if (sessions?.length !== 1 || pane.paneId !== config.paneId || pane.kind !== 'local' || pane.windowless
            || pane.profileName !== config.profileName) throw new Error('Owned input single direct-local pane changed.');
        return { pane: structuredClone(pane), terminalOutput: outputMode(config.tty, budget()) };
    };
    try {
        attempt.before = await verify();
        const target = endpoint(config.appData, config.app.pid), timeout = budget();
        attempt.rpcStarted = true;
        attempt.app = structuredClone(await rpc(target, attempt.request, timeout));
        attempt.after = await verify();
        attempt.postVerified = true;
        attempt.observedAt = new Date().toISOString();
        return attempt;
    } catch (error) {
        attempt.error = error instanceof Error ? error.message : String(error);
        const failure = new Error('Owned generated-input action failed.', { cause: error });
        failure.inputAttempt = attempt;
        throw failure;
    }
}
