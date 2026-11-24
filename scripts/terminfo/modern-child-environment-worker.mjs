import { readFileSync } from 'node:fs';
import { assertPrivateFile, requestOwner, verifyNtildeOutput } from './ownership.mjs';
import { snapshotEnvironment } from './modern-child-environment.mjs';
import { ownedTerminalRestorer, prepareOwnedOutput, restoreOwnedTerminalState } from './terminal-output.mjs';

// The profile executes Node directly. Sample before any fixture I/O.
const raw = JSON.stringify(snapshotEnvironment(process.env)) + '\n';

async function collect(config, binding) {
    const tty = binding.tabTty;
    let restoreOutput;
    const restore = ownedTerminalRestorer([() => restoreOutput?.(),
        () => restoreOwnedTerminalState(tty, binding.originalTerminalState)]);
    let failure;
    try {
        restoreOutput = prepareOwnedOutput(tty);
        await requestOwner(config, 'raw-output', { tty });
        await requestOwner(config, 'mce-environment', { tty, raw });
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

async function main(configPath) {
    assertPrivateFile(configPath);
    const config = JSON.parse(readFileSync(configPath, 'utf8'));
    const binding = await verifyNtildeOutput(config, process.stdout, { requireTermProgram: false });
    try { await collect(config, binding); }
    catch (error) {
        const message = error instanceof AggregateError ? error.errors.map(item => item.message).join('; ') : error.message;
        await requestOwner(config, 'failed', { tty: binding.tabTty, error: message }).catch(() => {});
        throw new Error(message, { cause: error });
    }
}

try { await main(process.argv[2]); }
catch (error) {
    console.error(error.message);
    process.exitCode = 1;
}
