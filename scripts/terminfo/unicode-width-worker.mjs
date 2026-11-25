import { readFileSync } from 'node:fs';
import { assertPrivateFile, requestOwner, verifyNtildeOutput } from './ownership.mjs';
import { ownedTerminalRestorer, prepareOwnedOutput, restoreOwnedTerminalState } from './terminal-output.mjs';
import { terminalFence } from './terminal-fence.mjs';
import { cursorReportQuery } from './cursor-report.mjs';
import { da, leave, prefix, restoreWidth, steps, widthWrites } from './unicode-width-contracts.mjs';

async function measure(fence, sample) {
    const frames = [];
    try {
        for (const bytes of widthWrites(sample)) {
            const exchange = await fence.cursorReport(bytes); // NOSONAR: PTY exchanges must run in order.
            frames.push({ bytes, exchange });
        }
    } finally {
        if (fence.failed) await fence.writeCleanup(restoreWidth);
    }
    return { frames, restoreBarrier: await fence.write(restoreWidth) };
}

async function collect(config, tty, fence) {
    const send = (stage, data = {}) => requestOwner(config, prefix + stage, { tty, ...data });
    const plan = steps(config.runId);
    await send('start');
    let primary;
    try {
        for (const { stage, item, bytes } of plan.slice(1, -1)) {
            const id = item?.id ?? null;
            if (stage === 'width') {
                const ascii = await measure(fence, item.width === 1 ? 'A' : 'AA'); // NOSONAR: same cursor and saved state.
                const sample = await measure(fence, item.sample); // NOSONAR: same cursor and saved state.
                await send(stage, { id, measurements: { ascii, sample } }); // NOSONAR: retain each case before the next write.
            } else if (stage === 'sample') {
                const { barrier, reply } = await fence.cursorReport(bytes); // NOSONAR: observe the exact write in the same pane.
                await send(stage, { id, requestBytes: bytes + cursorReportQuery + da, barrier, reply }); // NOSONAR: wait for parent snapshot.
            } else {
                const barrier = await fence.write(bytes); // NOSONAR: wait for the DA fence.
                await send(stage, { id, requestBytes: bytes + da, barrier }); // NOSONAR: wait for parent snapshot.
            }
        }
    } catch (error) {
        primary = error;
    }
    try {
        if (fence.failed) await fence.writeCleanup(leave);
        else await send('finish', { id: null, requestBytes: leave + da, barrier: await fence.write(leave) });
    } catch (error) {
        if (primary) throw new AggregateError([primary, error], 'Unicode collection and pane restoration failed.');
        throw error;
    }
    if (primary) throw primary;
}

async function run(config, binding) {
    const tty = binding.tabTty, fence = terminalFence(process.stdin, process.stdout);
    let restoreOutput, failure;
    const restore = ownedTerminalRestorer([() => restoreOutput?.(), () => fence.dispose(),
        () => restoreOwnedTerminalState(tty, binding.originalTerminalState)]);
    try {
        restoreOutput = prepareOwnedOutput(tty);
        await requestOwner(config, 'raw-output', { tty });
        await collect(config, tty, fence);
    } catch (error) {
        failure = error;
    }
    try { restore(); }
    catch (error) {
        if (failure) throw new AggregateError([failure, error], 'Unicode collection and terminal restoration failed.');
        throw error;
    }
    if (failure) throw failure;
    await requestOwner(config, 'done', { tty });
}

let config, binding;
try {
    assertPrivateFile(process.argv[2]);
    config = JSON.parse(readFileSync(process.argv[2], 'utf8'));
    binding = await verifyNtildeOutput(config, process.stdout);
    await run(config, binding);
} catch (error) {
    const cause = error instanceof AggregateError ? error.errors.map(item => item.message).join('; ') : error.message;
    console.error(cause);
    if (binding) await requestOwner(config, 'failed', { tty: binding.tabTty, error: cause }).catch(() => {});
    process.exitCode = 1;
}
