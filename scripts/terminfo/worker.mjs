import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { adapterId } from './collector.mjs';
import { assertPrivateFile, processRecord, requestOwner, verifyNtildeOutput } from './ownership.mjs';

let config;
try {
    assertPrivateFile(process.argv[2]);
    config = JSON.parse(readFileSync(process.argv[2], 'utf8'));
    // Establish the real process/PTY relation before loading the probe engine.
    await verifyNtildeOutput(config, process.stdout);
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
