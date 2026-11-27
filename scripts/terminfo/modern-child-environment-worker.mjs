import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';
import { assertPrivateFile, requestOwner, verifyNtildeOutput } from './ownership.mjs';
import { snapshotEnvironment } from './modern-child-environment.mjs';
import { ownedTerminalRestorer, prepareOwnedOutput, restoreOwnedTerminalState } from './terminal-output.mjs';

async function collect(config, binding, kind, raw) {
    const tty = binding.tabTty;
    let restoreOutput;
    const restore = ownedTerminalRestorer([() => restoreOutput?.(),
        () => restoreOwnedTerminalState(tty, binding.originalTerminalState)]);
    let failure;
    try {
        restoreOutput = prepareOwnedOutput(tty);
        await requestOwner(config, 'raw-output', { tty });
        await requestOwner(config, kind, { tty, raw });
    } catch (error) {
        failure = error;
    }
    try { restore(); }
    catch (error) {
        if (failure) throw new AggregateError([failure, error], 'Environment collection and kernel terminal restoration failed.');
        throw error;
    }
    if (failure) throw failure;
    await requestOwner(config, 'done', { tty });
}

export async function runEnvironmentWorker(snapshot, kind) {
    // The direct Node profile samples before its first fixture I/O.
    const raw = JSON.stringify(snapshot(process.env)) + '\n';
    try { await collectFromProfile(process.argv[2], kind, raw); }
    catch (error) {
        console.error(error.message);
        process.exitCode = 1;
    }
}

async function collectFromProfile(configPath, kind, raw) {
    assertPrivateFile(configPath);
    const config = JSON.parse(readFileSync(configPath, 'utf8'));
    const binding = await verifyNtildeOutput(config, process.stdout, { requireTermProgram: false });
    try { await collect(config, binding, kind, raw); }
    catch (error) {
        const message = error instanceof AggregateError ? error.errors.map(item => item.message).join('; ') : error.message;
        await requestOwner(config, 'failed', { tty: binding.tabTty, error: message }).catch(() => {});
        throw new Error(message, { cause: error });
    }
}

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
    await runEnvironmentWorker(snapshotEnvironment, 'mce-environment');
}
