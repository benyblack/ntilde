import { readFileSync } from 'node:fs';
import { setTimeout as delay } from 'node:timers/promises';
import { assertPrivateFile, requestOwner, verifyNtildeOutput } from './ownership.mjs';
import { ownedTerminalRestorer, prepareOwnedOutput, restoreOwnedTerminalState } from './terminal-output.mjs';
import { terminalFence } from './terminal-fence.mjs';
import { clear, da, maxWheelNotches, phases, prefix, requireViewport, syncReset, viewportFixture } from './modern-viewport.mjs';

const writeOutput = bytes => new Promise((resolve, reject) => {
    process.stdout.write(Buffer.from(bytes, 'ascii'), error => error ? reject(error) : resolve());
});

async function scroll(owner, phase, direction, reached) {
    for (let notch = 0; notch < maxWheelNotches; notch++) {
        const { offset } = await owner(direction, { phase }); // NOSONAR: each real wheel notch is dispatched and observed in sequence.
        if (reached(offset)) return;
    }
    throw new Error('Viewport wheel movement did not reach its control within the notch budget.');
}

async function measure(owner, write, runId, phase, rows) {
    const fixture = viewportFixture(runId, phase, rows);
    await write('bottom', fixture.seedBytes, { phase });
    await scroll(owner, phase, 'scroll-up', offset => offset >= rows);
    await owner('anchor', { phase });

    // No terminal query or user input between the anchor and the post-output read.
    // The synchronized pair and marker are written together in a single PTY write.
    await writeOutput(fixture.markerBytes);
    await delay(150);
    await owner('output', { phase, requestBytes: fixture.markerBytes });

    await scroll(owner, phase, 'scroll-down', offset => offset === 0);
    await owner('restored', { phase });
}

async function collect(config, tty, fence) {
    const owner = (kind, fields = {}) => requestOwner(config, prefix + kind, { tty, ...fields });
    const write = async (kind, bytes, fields = {}) => owner(kind, { ...fields, requestBytes: bytes + da, barrier: await fence.write(bytes) });
    const { rows } = await owner('start');
    let failure;
    try {
        for (const phase of phases) await measure(owner, write, config.runId, phase, rows); // NOSONAR: fresh seeded phases run serially in one owned pane.
        requireViewport(!fence.failed, 'unexpected terminal input');
    } catch (error) {
        failure = error;
    }
    try {
        if (fence.failed) await fence.writeCleanup(syncReset + clear);
        else await write('finish', syncReset + clear);
    } catch (error) {
        if (failure) throw new AggregateError([failure, error], 'Viewport collection and pane restoration failed.');
        throw error;
    }
    if (failure) throw failure;
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
        if (failure) throw new AggregateError([failure, error], 'Viewport collection and kernel terminal restoration failed.');
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
