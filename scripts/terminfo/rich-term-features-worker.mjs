import { readFileSync } from 'node:fs';
import { assertPrivateFile, requestOwner, verifyNtildeOutput } from './ownership.mjs';
import { terminalFence } from './terminal-fence.mjs';
import { withOwnedOutput } from './terminal-output.mjs';
import { parseDevqFrame } from './devq-report.mjs';
import { da, request, snapshotEnvironment } from './rich-term-features.mjs';

async function collect(configPath) {
    const environment = snapshotEnvironment(process.env);
    assertPrivateFile(configPath);
    const config = JSON.parse(readFileSync(configPath, 'utf8'));
    const { tabTty: tty, originalTerminalState: original } = await verifyNtildeOutput(config, process.stdout, { requireTermProgram: false });
    const owner = (kind, fields = {}) => requestOwner(config, kind, { tty, ...fields });
    try {
        const fence = terminalFence(process.stdin, process.stdout);
        await withOwnedOutput({ tty, original, restoreInput: fence.dispose }, async () => {
            await owner('raw-output');
            const preflight = await fence.write('');
            const received = await fence.exchange(request, bytes => parseDevqFrame(bytes) && bytes);
            const raw = JSON.stringify({ environment, preflight, sent: request + da, received }) + '\n';
            await owner('rtf-environment', { raw });
            fence.detachInput(); // Refuses a late response received while the owner was binding the pane.
        });
        await owner('done');
    } catch (error) {
        const message = error instanceof AggregateError ? error.errors.map(item => item.message).join('; ') : error.message;
        await owner('failed', { error: message }).catch(() => {});
        throw new Error(message, { cause: error });
    }
}

try { await collect(process.argv[2]); }
catch (error) { console.error(error.message); process.exitCode = 1; }
