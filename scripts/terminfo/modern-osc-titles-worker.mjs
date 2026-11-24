import { readFileSync } from 'node:fs';
import { parseDevqFrame } from './devq-report.mjs';
import { assertPrivateFile, requestOwner, verifyNtildeOutput } from './ownership.mjs';
import { terminalFence } from './terminal-fence.mjs';
import { ownedTerminalRestorer, prepareOwnedOutput, restoreOwnedTerminalState } from './terminal-output.mjs';
import { da, popLabels, prefix, queries, titlePlans } from './modern-osc-titles.mjs';

async function captureStep(owner, fence, caseId, step) {
    const record = { caseId, phase: step.phase, action: { request: step.bytes ? step.bytes + da : '', barrier: null }, reads: [] };
    let partial = '';
    try {
        if (step.bytes) record.action.barrier = await fence.write(step.bytes);
        for (const query of queries) {
            partial = '';
            const exchange = await fence.exchange(query, raw => { // NOSONAR: serial exchanges pair observations before the next write.
                partial = raw;
                return parseDevqFrame(raw);
            });
            record.reads.push({ request: query + da, exchange });
        }
    } catch (error) {
        await owner('fault', { ...record, partial, error: error.message }).catch(() => {});
        throw error;
    }
    return owner('stage', record);
}

async function emergencyPop(fence, primary) {
    try {
        if (fence.failed) await fence.writeCleanup(popLabels);
        else await fence.write(popLabels);
    } catch (cleanupError) {
        throw new AggregateError([primary, cleanupError], 'Title collection and emergency stack pop failed.');
    }
}

async function collect(owner, fence, runId) {
    let saved = false;
    try {
        for (const plan of titlePlans(runId)) {
            for (const step of plan.steps) {
                if (step.phase === 'save') saved = true;
                // A restore that has been attempted must never pop a second entry.
                if (step.phase === 'restore') saved = false;
                await captureStep(owner, fence, plan.id, step); // NOSONAR: writes and paired reads must settle in exact plan order.
            }
        }
        if (fence.failed) throw new Error('Unexpected input after the last public label read.');
    } catch (error) {
        if (saved) await emergencyPop(fence, error);
        throw error;
    }
}

async function run(config, binding) {
    const tty = binding.tabTty, fence = terminalFence(process.stdin, process.stdout);
    const owner = (kind, fields = {}) => requestOwner(config, prefix + kind, { tty, ...fields });
    let restoreOutput;
    const restore = ownedTerminalRestorer([() => restoreOutput?.(), () => fence.dispose(),
        () => restoreOwnedTerminalState(tty, binding.originalTerminalState)]);
    let failure;
    try {
        restoreOutput = prepareOwnedOutput(tty);
        await requestOwner(config, 'raw-output', { tty });
        await collect(owner, fence, config.runId);
    } catch (error) {
        failure = error;
    }
    try { restore(); }
    catch (cleanupError) {
        if (failure) throw new AggregateError([failure, cleanupError], 'Title collection and kernel terminal restoration failed.');
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
