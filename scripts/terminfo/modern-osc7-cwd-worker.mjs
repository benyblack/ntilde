import { readFileSync } from 'node:fs';
import { assertPrivateFile, requestOwner, verifyNtildeOutput } from './ownership.mjs';
import { terminalFence } from './terminal-fence.mjs';
import { withOwnedOutput } from './terminal-output.mjs';
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

async function emergencyRestore(fence, bytes, primary) {
    try {
        if (fence.failed) await fence.writeCleanup(bytes);
        else await fence.write(bytes);
    } catch (cleanupError) {
        throw new AggregateError([primary, cleanupError], 'OSC 7 collection and emergency directory restoration failed.');
    }
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
        if (changed) await emergencyRestore(fence, restore.bytes, error);
        throw error;
    }
}

function failureMessage(error) {
    return error instanceof AggregateError ? error.errors.map(failureMessage).join('; ') : error.message;
}

async function main(configPath) {
    assertPrivateFile(configPath);
    const config = JSON.parse(readFileSync(configPath, 'utf8'));
    const { tabTty: tty, originalTerminalState: original } = await verifyNtildeOutput(config, process.stdout);
    const request = (kind, fields) => requestOwner(config, kind, { tty, ...fields });
    const owner = (kind, fields) => request(prefix + kind, fields);
    const fence = terminalFence(process.stdin, process.stdout);
    try {
        await withOwnedOutput({ tty, original, restoreInput: () => fence.dispose() }, async () => {
            await request('raw-output');
            await collect(owner, fence, config.runId);
        });
        await request('done');
    } catch (error) {
        const message = failureMessage(error);
        await request('failed', { error: message }).catch(() => {});
        throw new Error(message, { cause: error });
    }
}

try {
    await main(process.argv[2]);
} catch (error) {
    console.error(error.message);
    process.exitCode = 1;
}
