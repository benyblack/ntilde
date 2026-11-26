import { readFileSync } from 'node:fs';
import { rawInputCapture } from './input-bytes.mjs';
import { assertPrivateFile, requestOwner, verifyNtildeOutput } from './ownership.mjs';
import { withOwnedOutput } from './terminal-output.mjs';
import { write } from './static-images-collector.mjs';
import { planFor, prefix, requireMetadata, restore } from './osc633-metadata-contracts.mjs';

async function collect(runId, send) {
    await send('start');
    const capture = rawInputCapture(process.stdin, process.stdout);
    try {
        for (const { stage, writes, query } of planFor(runId).slice(1, -1)) {
            const sample = await capture.capture(async () => { // NOSONAR: a fence per sequential stage.
                for (const bytes of writes) await write(process.stdout, bytes); // NOSONAR: preserve the pinned write order.
                if (query) await write(process.stdout, query);
            });
            await send(stage, { writes, query, sample }); // NOSONAR: observe this stage before another write.
        }
        requireMetadata(!capture.failed, 'unexpected input after final fence');
        capture.dispose();
        await send('seal', { attempts: capture.lastAttempt });
    } catch (error) {
        capture.dispose();
        try { await write(process.stdout, restore(runId)); }
        catch (restoreError) { throw new AggregateError([error, restoreError], 'Metadata collection and restoration failed.'); }
        throw error;
    } finally { capture.dispose(); }
}

async function run(config) {
    const { tabTty: tty, originalTerminalState: original } = await verifyNtildeOutput(config, process.stdout);
    const request = (kind, details) => requestOwner(config, kind, { ...details, tty });
    try {
        await withOwnedOutput({ tty, original, restoreInput: () => {} }, async () => {
            await request('raw-output');
            await collect(config.runId, (stage, value) => request(prefix + stage, value));
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
