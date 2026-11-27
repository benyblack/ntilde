import { readFileSync } from 'node:fs';
import { rawInputCapture } from './input-bytes.mjs';
import { assertPrivateFile, requestOwner, verifyNtildeOutput } from './ownership.mjs';
import { withOwnedOutput } from './terminal-output.mjs';
import { write } from './static-images-collector.mjs';
import { pathToFileURL } from 'node:url';
import * as osc633 from './osc633-metadata-contracts.mjs';

async function collect(runId, send, { planFor, requireMetadata, restore }) {
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

function describeFailure(error) {
    if (error instanceof AggregateError) {
        return error.message + ': ' + error.errors.map(describeFailure).join(' / ');
    }
    return error.message;
}

export async function runMetadataWorker(configPath, contract = osc633) {
    assertPrivateFile(configPath);
    const config = JSON.parse(readFileSync(configPath, 'utf8'));
    const { tabTty: tty, originalTerminalState: original } = await verifyNtildeOutput(config, process.stdout);
    const request = (kind, details) => requestOwner(config, kind, { ...details, tty });
    const collectMetadata = async () => {
        await request('raw-output');
        await collect(config.runId, (stage, value) => request(contract.prefix + stage, value), contract);
    };
    try {
        await withOwnedOutput({ tty, original, restoreInput: () => {} }, collectMetadata);
        await request('done');
    } catch (error) {
        await request('failed', { error: describeFailure(error) }).catch(() => {});
        throw error;
    }
}

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) await runMetadataWorker(process.argv[2]).catch(error => {
    console.error(describeFailure(error));
    process.exitCode = 1;
});
