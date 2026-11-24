import { readFileSync } from 'node:fs';
import { setTimeout as delay } from 'node:timers/promises';
import { assertPrivateFile, requestOwner, verifyNtildeOutput } from './ownership.mjs';
import { ownedTerminalRestorer, prepareOwnedOutput, restoreOwnedTerminalState } from './terminal-output.mjs';
import { terminalFence } from './terminal-fence.mjs';
import { blinkBind, blinkBlank, blinkFinish, blinkFrames, blinkIntervalMs, blinkPlain, blinkQuery, blinkStyled, da, parseBlinkReply } from './modern-blink.mjs';

async function collect(config, tty, fence) {
    const owner = (kind, extra = {}) => requestOwner(config, kind, { tty, ...extra });
    const write = async (kind, bytes) => owner(kind, { requestBytes: bytes + da, barrier: await fence.write(bytes) });
    await owner('mblink-start');
    let primary;
    try {
        await write('mblink-bind', blinkBind(config.runId));
        await write('mblink-blank', blinkBlank);
        await write('mblink-plain', blinkPlain);
        const query = blinkStyled + blinkQuery;
        const reply = await fence.exchange(query, parseBlinkReply);
        await owner('mblink-styled', { requestBytes: query + da, ...reply });
        const epoch = performance.now();
        for (let frame = 0; frame < blinkFrames; frame++) {
            await delay(Math.max(0, epoch + frame * blinkIntervalMs - performance.now())); // NOSONAR: pace each read against the same clock.
            await owner('mblink-frame', { frame }); // NOSONAR: keep samples serial with no PTY writes.
        }
        await owner('mblink-sampled');
    } catch (error) {
        primary = error;
    }
    try {
        if (fence.failed) await fence.writeCleanup(blinkFinish);
        else await write('mblink-finish', blinkFinish);
    } catch (error) {
        if (primary) throw new AggregateError([primary, error], 'Blink sampling and pane restoration failed.');
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
    } finally {
        try { restore(); }
        catch (error) {
            if (failure) throw new AggregateError([failure, error], 'Blink collection and terminal restoration failed.');
            throw error;
        }
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
