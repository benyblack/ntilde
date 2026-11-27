import { readFileSync } from 'node:fs';
import { setTimeout as delay } from 'node:timers/promises';
import { rawInputCapture } from './input-bytes.mjs';
import { assertPrivateFile, requestOwner, verifyNtildeOutput } from './ownership.mjs';
import { write } from './static-images-collector.mjs';
import { withOwnedOutput } from './terminal-output.mjs';
import { clockMs, prepare, restore, trials } from './osc-flash-contract.mjs';

const output = bytes => write(process.stdout, bytes);

async function collect(runId, send) {
    const capture = rawInputCapture(process.stdin, process.stdout);
    const step = async (stage, writes, query, timing = {}) => {
        const sample = await capture.capture(async () => {
            for (const bytes of writes) {
                timing.sent = clockMs();
                await output(bytes); // NOSONAR: the upstream writes are ordered.
            }
            if (query) await output(query);
        });
        return { stage, writes, query, sample, ...timing, fenced: clockMs() };
    };
    try {
        await send('start');
        await send('prepare', await step('prepare', [prepare(runId)]));
        for (const trial of trials) {
            const target = clockMs() + 400;
            // The worker and owner share the host monotonic clock. Capture across
            // the write without putting an owner round-trip inside the flash.
            const fire = async () => {
                await delay(Math.max(0, target - clockMs()));
                return step(trial.name, trial.writes, trial.query);
            };
            const [sampling, fired] = await Promise.allSettled([send('sample-' + trial.name, { target }), fire()]); // NOSONAR: seal each trial in order.
            for (const result of [sampling, fired]) if (result.status === 'rejected') throw result.reason;
            await send(trial.name, fired.value); // NOSONAR: bind the raw child reply before proceeding.
        }
        await send('finish', await step('finish', [restore]));
        if (capture.failed) throw new Error('Unexpected input after restoration.');
        capture.dispose();
        await send('seal', { attempt: capture.lastAttempt });
    } catch (error) {
        try { await output(restore); }
        catch (cleanup) { throw new AggregateError([error, cleanup], 'Flash measurement and restoration failed'); }
        throw error;
    } finally { capture.dispose(); }
}

async function main(path) {
    assertPrivateFile(path);
    const config = JSON.parse(readFileSync(path, 'utf8'));
    const { tabTty: tty, originalTerminalState: original } = await verifyNtildeOutput(config, process.stdout);
    const owner = (kind, details) => requestOwner(config, kind, { ...details, tty });
    try {
        await withOwnedOutput({ tty, original, restoreInput: () => {} }, async () => {
            await owner('raw-output');
            await collect(config.runId, (stage, details) => owner('flash-' + stage, details));
        });
        await owner('done');
    } catch (error) {
        await owner('failed', { error: error.message }).catch(() => {});
        throw error;
    }
}
await main(process.argv[2]);

