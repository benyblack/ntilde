import { readFileSync } from 'node:fs';
import { assertPrivateFile, requestOwner, verifyNtildeOutput } from './ownership.mjs';
import { withOwnedOutput } from './terminal-output.mjs';
import { terminalFence } from './terminal-fence.mjs';
import { cursorReportQuery } from './cursor-report.mjs';
import { da, planFor, prefix } from './osc66-contracts.mjs';

async function record(fence, send, step) {
    const captured = step.query ? await fence.cursorReport(step.bytes) : { barrier: await fence.write(step.bytes) };
    await send(step.stage, { ...captured, requestBytes: step.bytes + (step.query ? cursorReportQuery : '') + da });
}

async function collect(runId, request, fence) {
    const send = (stage, value) => request(prefix + stage, value);
    const { cursorVisible } = await send('start');
    const plan = planFor(runId, cursorVisible), finish = plan.at(-1);
    let failure;
    try {
        for (const step of plan.slice(1, -1)) {
            await record(fence, send, step); // NOSONAR: Observe the fenced transition before the next write.
        }
    } catch (error) {
        failure = error;
    }
    try {
        if (fence.failed) await fence.writeCleanup(finish.bytes);
        else if (failure) await fence.write(finish.bytes);
        else await record(fence, send, finish);
    } catch (error) {
        if (failure) throw new AggregateError([failure, error], 'OSC 66 collection and restoration failed.');
        throw error;
    }
    if (failure) throw failure;
}

async function run(config) {
    const { tabTty: tty, originalTerminalState: original } = await verifyNtildeOutput(config, process.stdout);
    const request = (kind, details) => requestOwner(config, kind, { ...details, tty });
    const fence = terminalFence(process.stdin, process.stdout);
    try {
        await withOwnedOutput({ tty, original, restoreInput: fence.dispose }, async () => {
            await request('raw-output');
            await collect(config.runId, request, fence);
        });
        await request('done');
    } catch (error) {
        const message = error instanceof AggregateError ? error.errors.map(item => item.message).join('; ') : error.message;
        await request('failed', { error: message }).catch(() => {});
        throw error;
    }
}

try {
    assertPrivateFile(process.argv[2]);
    await run(JSON.parse(readFileSync(process.argv[2], 'utf8')));
} catch (error) {
    console.error(error.message);
    process.exitCode = 1;
}
