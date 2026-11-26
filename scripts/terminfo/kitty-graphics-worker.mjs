import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { rawInputCapture } from './input-bytes.mjs';
import { assertPrivateFile, requestOwner, verifyNtildeOutput } from './ownership.mjs';
import { withOwnedOutput } from './terminal-output.mjs';
import { checkImages, imageModeQueries, imageRestore } from './static-images.mjs';
import { write } from './static-images-collector.mjs';
import { pngHash } from './pixels-capture.mjs';
import { plan, prefix } from './kitty-graphics-owned.mjs';

async function collect(config, tty) {
    const send = (step, value = {}) => requestOwner(config, prefix + step, { tty, ...value });
    const initial = await send('start');
    const capture = rawInputCapture(process.stdin, process.stdout);
    try {
        for (const [index, entry] of plan.entries()) {
            const ids = { caseId: entry.caseId, stageId: entry.stageId };
            const action = await send('admit', ids); // NOSONAR: owner must admit each serial write.
            const bytes = Buffer.from(action.commandHex, 'hex');
            checkImages(bytes.toString('hex') === action.commandHex && pngHash(bytes) === action.commandSha256
                && action.index === index && action.kind === prefix + entry.kind
                && action.caseId === (entry.caseId ?? null) && action.stageId === (entry.stageId ?? null));
            const observed = await capture.capture(() => write(process.stdout, Buffer.concat([bytes, Buffer.from(imageModeQueries)])));
            await send(entry.kind, { ...ids, capture: observed }); // NOSONAR: owner must seal each pixel pair before the next write.
        }
        capture.dispose();
        await send('seal', { captureStatus: { failed: capture.failed, disposed: true, attempts: plan.length, lastAttempt: capture.lastAttempt } });
    } catch (error) {
        writeFileSync(join(config.output, 'graphics-raw-failure.json'), JSON.stringify({
            lastAttempt: capture.lastAttempt, error: String(error.message ?? error),
        }) + '\n', { flag: 'wx', mode: 0o600 });
        try { await write(process.stdout, imageRestore(initial.screen.cursorVisible)); }
        catch (cleanupError) { throw new AggregateError([error, cleanupError], 'Kitty graphics collection and restoration failed.'); }
        throw error;
    } finally { capture.dispose(); }
}

async function run(config) {
    const binding = await verifyNtildeOutput(config, process.stdout);
    const tty = binding.tabTty;
    try {
        await withOwnedOutput({ tty, original: binding.originalTerminalState, restoreInput: () => {} }, async () => {
            await requestOwner(config, 'raw-output', { tty });
            await collect(config, tty);
        });
        await requestOwner(config, 'done', { tty });
    } catch (error) {
        const detail = error instanceof AggregateError ? error.errors.map(item => item.message).join('; ') : error.message;
        await requestOwner(config, 'failed', { tty, error: detail }).catch(() => {});
        throw new Error(detail, { cause: error });
    }
}

try {
    assertPrivateFile(process.argv[2]);
    await run(JSON.parse(readFileSync(process.argv[2], 'utf8')));
} catch (error) {
    console.error(error.message);
    process.exitCode = 1;
}
