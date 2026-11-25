import { readFileSync } from 'node:fs';
import { assertPrivateFile, requestOwner, verifyNtildeOutput } from './ownership.mjs';
import { withOwnedOutput } from './terminal-output.mjs';
import { terminalFence } from './terminal-fence.mjs';
import { cursorReportQuery } from './cursor-report.mjs';
import { da, enter, leave, restoreWidth, widthWrites } from './unicode-width-contracts.mjs';
import { boundaryPlan, prefix } from './unicode-boundary-contracts.mjs';

async function writeStage(fence, step) {
    if (step.sample) {
        const exchanges = [];
        try {
            for (const bytes of widthWrites(step.sample)) {
                const exchange = await fence.cursorReport(bytes); // NOSONAR: serial PTY measurements.
                exchanges.push({ bytes, exchange });
            }
        } finally {
            if (fence.failed) await fence.writeCleanup(restoreWidth);
        }
        return { exchanges, restoreBarrier: await fence.write(restoreWidth) };
    }
    if (step.query) {
        const result = await fence.cursorReport(step.bytes);
        return { ...result, requestBytes: step.bytes + cursorReportQuery + da };
    }
    return { barrier: await fence.write(step.bytes), requestBytes: step.bytes + da };
}

async function collect(config, tty, fence) {
    const send = (step, value = {}) => requestOwner(config, prefix + step, { tty, ...value });
    const { cols } = await send('start');
    let failure;
    try {
        const plan = [{ stage: 'bind', bytes: enter(config.runId) }, ...boundaryPlan(cols)];
        for (const step of plan) {
            const value = await writeStage(fence, step); // NOSONAR: preserve write order.
            await send(step.stage, value); // NOSONAR: confirm parent observation before the next write.
        }
    } catch (error) {
        failure = error;
    }
    try {
        if (fence.failed) await fence.writeCleanup(leave);
        else await send('finish', await writeStage(fence, { bytes: leave }));
    } catch (error) {
        if (failure) throw new AggregateError([failure, error], 'Unicode boundary collection and restoration failed.');
        throw error;
    }
    if (failure) throw failure;
}

async function run(config) {
    const binding = await verifyNtildeOutput(config, process.stdout);
    const tty = binding.tabTty, fence = terminalFence(process.stdin, process.stdout);
    try {
        await withOwnedOutput({ tty, original: binding.originalTerminalState, restoreInput: fence.dispose }, async () => {
            await requestOwner(config, 'raw-output', { tty });
            await collect(config, tty, fence);
        });
        await requestOwner(config, 'done', { tty });
    } catch (error) {
        const message = error instanceof AggregateError ? error.errors.map(item => item.message).join('; ') : error.message;
        await requestOwner(config, 'failed', { tty, error: message }).catch(() => {});
        throw new Error(message, { cause: error });
    }
}

try {
    assertPrivateFile(process.argv[2]);
    await run(JSON.parse(readFileSync(process.argv[2], 'utf8')));
} catch (error) {
    console.error(error.message);
    process.exitCode = 1;
}
