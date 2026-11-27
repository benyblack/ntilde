import { readFileSync } from 'node:fs';
import { assertPrivateFile, requestOwner, verifyNtildeOutput } from './ownership.mjs';
import { collectInput } from './input-collector.mjs';
import { terminalFence } from './terminal-fence.mjs';
import { withOwnedOutput } from './terminal-output.mjs';
import { planFor } from './modify-other-keys-3-contracts.mjs';

async function run(path) {
    assertPrivateFile(path);
    const config = JSON.parse(readFileSync(path, 'utf8'));
    const { tabTty: tty, originalTerminalState: original } = await verifyNtildeOutput(config, process.stdout);
    const request = (kind, fields) => requestOwner(config, kind, { ...fields, tty });
    const fence = terminalFence(process.stdin, process.stdout);
    try {
        await withOwnedOutput({ tty, original, restoreInput: fence.dispose }, async () => {
            await request('raw-output');
            const result = await collectInput({ config, fence, owner: (kind, _barrier, fields) => request(kind, fields),
                plan: planFor(config.collectionScope.id) });
            if (result.status !== 'complete') throw new Error('Input observation unavailable: ' + result.reason);
        });
        await request('done');
    } catch (error) {
        const message = error instanceof AggregateError ? error.errors.map(String).join(' / ') : error.message;
        await request('failed', { error: message }).catch(() => {});
        throw error;
    }
}

await run(process.argv[2]);
