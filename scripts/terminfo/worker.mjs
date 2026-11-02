import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { adapterId } from './collector.mjs';
import { assertPrivateFile, processRecord, requestOwner, verifyNtildeOutput } from './ownership.mjs';
import { collectCells } from './cells.mjs';
import { terminalFence } from './terminal-fence.mjs';
import { collectSgr } from './sgr.mjs';
import { collectRgb } from './rgb.mjs';
import { collectRendition } from './rendition.mjs';
import { collectCursor } from './cursor.mjs';

let config;
try {
    assertPrivateFile(process.argv[2]);
    config = JSON.parse(readFileSync(process.argv[2], 'utf8'));
    // Establish the real process/PTY relation before loading the probe engine.
    const binding = await verifyNtildeOutput(config, process.stdout);
    const fence = terminalFence(process.stdin, process.stdout);
    try {
        const owner = (kind, barrier = null) => requestOwner(config, kind, { tty: binding.tabTty, barrier });
        await collectCells({ config, fence, owner });
        await collectSgr({ config, fence, owner });
        await collectRgb({ config, fence, owner });
        await collectRendition({ config, fence, owner });
        await collectCursor({ config, fence, owner });
    } finally { fence.dispose(); }
    const source = readFileSync(config.bundle);
    const digest = createHash('sha256').update(source).digest('hex');
    if (digest !== config.adapterBundleSha256) throw new Error('Local adapter bundle changed.');
    const { collectNtildeRun } = await import(pathToFileURL(config.bundle).href);
    const run = await collectNtildeRun(config);
    run.collectorAdapter = { id: adapterId, bundleSha256: digest, upstreamBundleSha256: config.upstreamBundleSha256 };
    writeFileSync(join(config.output, 'raw.json'), JSON.stringify(run) + '\n', { flag: 'wx', mode: 0o600 });
    await requestOwner(config, 'done', { tty: `/dev/${processRecord(process.pid).tty}` });
} catch (error) {
    if (config) {
        writeFileSync(join(config.output, 'worker-error.txt'), error.stack + '\n', { flag: 'wx', mode: 0o600 });
        try { await requestOwner(config, 'failed', { error: error.message, tty: `/dev/${processRecord(process.pid).tty}` }); } catch {}
    }
    console.error(error);
    process.exitCode = 1;
}
