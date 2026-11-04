import { createHash, randomInt } from 'node:crypto';
import { writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { rawInputCapture } from './input-bytes.mjs';
import { imagePlan, imageRestore, imageModeQueries, checkImages } from './static-images.mjs';

const write = (output, bytes) => new Promise((resolve, reject) => {
    let settled = false;
    const timer = setTimeout(() => finish(new Error('Image fixture write timed out.')), 1000);
    const finish = error => {
        if (settled) return;
        settled = true; clearTimeout(timer);
        if (error) reject(error); else resolve();
    };
    try { output.write(bytes, finish); } catch (error) { finish(error); }
});
/** One exclusive byte reader after the input reader is sealed; no retries or native input. */
export async function collectStaticImages({ config, owner, fence, detachFence = false, input = process.stdin, output = process.stdout }) {
    let initial;
    try { initial = await owner('images-start'); }
    catch (error) {
        if (['screenDetailsUnavailable', 'imageGeometryUnavailable'].includes(error.code)) return { status: 'unavailable', reason: error.code };
        throw error;
    }
    checkImages(Array.isArray(initial.priorChallenges) && initial.priorChallenges.length <= 80 && typeof initial.screen?.cursorVisible === 'boolean');
    if (detachFence) fence.detachInput();
    const prior = new Set(initial.priorChallenges);
    const capture = rawInputCapture(input, output, 4500, used => {
        for (let attempt = 0; attempt < 8; attempt++) {
            const pair = [randomInt(32768, 65536), randomInt(32768, 65536)];
            if (pair[0] !== pair[1] && !used.has(pair.join(',')) && !prior.has(pair.join(','))) return pair;
        }
        throw new Error('Image challenge generation exhausted.');
    });
    let attempts = 0;
    try {
        await imagePlan.reduce((previous, entry) => previous.then(async () => {
            const extra = { ...(entry.caseId ? { caseId: entry.caseId } : {}), ...(entry.stageId ? { stageId: entry.stageId } : {}) };
            const action = await owner('images-admit', null, extra);
            checkImages(typeof action.commandHex === 'string' && /^[a-f\d]*$/.test(action.commandHex)
                && action.commandHex.length % 2 === 0 && action.commandHex.length <= 8192);
            const bytes = Buffer.from(action.commandHex, 'hex');
            checkImages(createHash('sha256').update(bytes).digest('hex') === action.commandSha256
                && action.index === attempts && action.kind === 'images-' + entry.kind
                && action.caseId === (entry.caseId ?? null) && action.stageId === (entry.stageId ?? null));
            const command = Buffer.concat([bytes, Buffer.from(imageModeQueries)]);
            attempts++;
            const sample = await capture.capture(() => write(output, command));
            await owner('images-' + entry.kind, null, { ...extra, capture: sample });
        }), Promise.resolve());
        capture.dispose();
        await owner('images-seal', null, { captureStatus: { failed: capture.failed, disposed: true, attempts, lastAttempt: capture.lastAttempt } });
    } catch (error) {
        writeFileSync(join(config.output, 'image-raw-failure.json'), JSON.stringify({ attempts, failed: capture.failed,
            lastAttempt: capture.lastAttempt, error: String(error.message ?? error) }) + '\n', { flag: 'wx', mode: 0o600 });
        try { await write(output, imageRestore(initial.screen.cursorVisible)); }
        catch (cleanupError) { throw new AggregateError([error, cleanupError], 'Image collection and cleanup failed.'); }
        throw error;
    } finally { capture.dispose(); }
}
