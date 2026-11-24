import { readFileSync } from 'node:fs';
import { assertPrivateFile, requestOwner, verifyNtildeOutput } from './ownership.mjs';
import { terminalFence } from './terminal-fence.mjs';
import { ownedTerminalRestorer, prepareOwnedOutput, restoreOwnedTerminalState } from './terminal-output.mjs';
import { cwdPlan, expectedAction, prefix } from './modern-osc7-cwd.mjs';

async function captureStep(owner, fence, step) {
    const action = { request: expectedAction(step), barrier: null };
    try {
        action.barrier = await fence.write(step.bytes);
    } catch (error) {
        await owner('fault', { phase: step.phase, action, error: error.message }).catch(() => {});
        throw error;
    }
    await owner('stage', { phase: step.phase, action });
}

async function collect(owner, fence, runId) {
    const plan = cwdPlan(runId);
    const restore = plan.find(step => step.phase === 'restore');
    let changed = false;
    try {
        for (const step of plan) {
            if (step.phase === 'control') changed = true;
            await captureStep(owner, fence, step); // NOSONAR: each next write must wait for the ordered public reading.
            if (step.phase === 'restored') changed = false;
        }
        if (fence.failed) throw new Error('Unexpected input after final tooltip capture.');
    } catch (error) {
        if (changed) {
            try {
                if (fence.failed) await fence.writeCleanup(restore.bytes);
                else await fence.write(restore.bytes);
            } catch (cleanupError) {
                throw new AggregateError([error, cleanupError], 'OSC 7 collection and emergency directory restoration failed.');
            }
        }
        throw error;
    }
}

async function run(config, binding) {
    const tty = binding.tabTty, fence = terminalFence(process.stdin, process.stdout);
    const owner = (kind, fields) => requestOwner(config, prefix + kind, { tty, ...fields });
    let restoreOutput;
    const restore = ownedTerminalRestorer([() => restoreOutput?.(), () => fence.dispose(),
        () => restoreOwnedTerminalState(tty, binding.originalTerminalState)]);
    let failure;
    try {
        restoreOutput = prepareOwnedOutput(tty);
        await requestOwner(config, 'raw-output', { tty });
        await owner('prepare', {});
        await collect(owner, fence, config.runId);
    } catch (error) {
        failure = error;
    }
    try { restore(); }
    catch (cleanupError) {
        if (failure) throw new AggregateError([failure, cleanupError], 'OSC 7 collection and kernel terminal restoration failed.');
        throw cleanupError;
    }
    if (failure) throw failure;
    await requestOwner(config, 'done', { tty });
}

async function main(configPath) {
    assertPrivateFile(configPath);
    const config = JSON.parse(readFileSync(configPath, 'utf8'));
    const binding = await verifyNtildeOutput(config, process.stdout);
    try {
        await run(config, binding);
    } catch (error) {
        const message = error instanceof AggregateError ? error.errors.map(item => item.message).join('; ') : error.message;
        await requestOwner(config, 'failed', { tty: binding.tabTty, error: message }).catch(() => {});
        throw new Error(message, { cause: error });
    }
}

try {
    await main(process.argv[2]);
} catch (error) {
    console.error(error.message);
    process.exitCode = 1;
}
