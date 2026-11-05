import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { adapterId } from './collector.mjs';
import { assertPrivateFile, processRecord, requestOwner, verifyNtildeOutput } from './ownership.mjs';
import { collectCells } from './cells.mjs';
import { terminalFence } from './terminal-fence.mjs';
import { ownedTerminalRestorer, restoreOwnedTerminalState, prepareOwnedOutput } from './terminal-output.mjs';
import { collectSgr } from './sgr.mjs';
import { collectRgb } from './rgb.mjs';
import { collectRendition } from './rendition.mjs';
import { collectCursor } from './cursor.mjs';
import { collectErase, collectEditing } from './editing-cases.mjs';
import { collectInput } from './input-collector.mjs';
import { collectQueries } from './query-collector.mjs';
import { collectStaticImages } from './static-images-collector.mjs';
import { collectMetadata } from './metadata-cases.mjs';
import { collectSaved } from './saved-cases.mjs';
import { collectResize } from './resize-cases.mjs';
import { collectHistory } from './history-cases.mjs';
import { collectColors } from './color-cases.mjs';
import { collectPixels } from './pixels-cases.mjs';

/** Bound failed worker evidence to eight nested/aggregate reasons and a caller-side 64 KiB cap; keep falsy causes. */
function retainedFailure(error, depth = 0) {
    if (depth >= 8) return '[nested failure limit]';
    const text = String(error?.stack ?? error).slice(0, 8192);
    return error instanceof AggregateError ? text + '\n' + error.errors.slice(0, 8)
        .map((value, index) => '[' + index + '] ' + retainedFailure(value, depth + 1)).join('\n') : text;
}
let config;
try {
    assertPrivateFile(process.argv[2]);
    config = JSON.parse(readFileSync(process.argv[2], 'utf8'));
    // Establish the real process/PTY relation before loading the probe engine.
    const binding = await verifyNtildeOutput(config, process.stdout);
    const fence = terminalFence(process.stdin, process.stdout);
    let restoreOutput;
    const restore = ownedTerminalRestorer([() => restoreOutput?.(), () => fence.dispose(),
        () => { if (config.privateClipboard === true) restoreOwnedTerminalState(binding.tabTty, binding.originalTerminalState); }]);
    let run, primary, failed = false, restoreFailure, restoreFailed = false;
    try {
        restoreOutput = prepareOwnedOutput(binding.tabTty);
        await requestOwner(config, 'raw-output', { tty: binding.tabTty });
        const owner = (kind, barrier = null, extra = {}) => requestOwner(config, kind, { ...extra, tty: binding.tabTty, barrier });
        await collectCells({ config, fence, owner });
        await collectSgr({ config, fence, owner });
        await collectRgb({ config, fence, owner });
        await collectRendition({ config, fence, owner });
        await collectCursor({ config, fence, owner });
        await collectPixels({ config, fence, owner });
        await collectColors({ config, fence, owner });
        await collectErase({ config, fence, owner });
        await collectEditing({ config, fence, owner });
        await collectHistory({ config, fence, owner });
        await collectResize({ config, fence, owner });
        await collectSaved({ config, fence, owner });
        await collectMetadata({ config, fence, owner });
        const inputResult = await collectInput({ config, fence, owner });
        await collectStaticImages({ config, fence, owner, detachFence: inputResult?.status === 'unavailable' });
        await collectQueries({ runId: config.runId, owner, expectedOwnerPid: binding.ownerPid, outputDirectory: config.output });
        if (config.privateClipboard === true) fence.retireInput();
        else restore();
        const source = readFileSync(config.bundle);
        const digest = createHash('sha256').update(source).digest('hex');
        if (digest !== config.adapterBundleSha256) throw new Error('Local adapter bundle changed.');
        const { collectNtildeRun } = await import(pathToFileURL(config.bundle).href);
        run = await collectNtildeRun(config);
    } catch (error) { primary = error; failed = true; }
    finally {
        try { restore(); }
        catch (error) { restoreFailure = error; restoreFailed = true; }
    }
    if (failed && restoreFailed) throw new AggregateError([primary, restoreFailure], 'Owned collection and terminal restoration failed.');
    if (restoreFailed) throw restoreFailure;
    if (failed) throw primary;
    run.collectorAdapter = { id: adapterId, bundleSha256: config.adapterBundleSha256, upstreamBundleSha256: config.upstreamBundleSha256 };
    writeFileSync(join(config.output, 'raw.json'), JSON.stringify(run) + '\n', { flag: 'wx', mode: 0o600 });
    await requestOwner(config, 'done', { tty: `/dev/${processRecord(process.pid).tty}` });
} catch (error) {
    if (config) {
        writeFileSync(join(config.output, 'worker-error.txt'), retainedFailure(error).slice(0, 65536) + '\n', { flag: 'wx', mode: 0o600 });
        try { await requestOwner(config, 'failed', { error: String(error?.message ?? error).slice(0, 1024), tty: `/dev/${processRecord(process.pid).tty}` }); } catch {}
    }
    console.error(error);
    process.exitCode = 1;
}
