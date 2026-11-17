import { createHash, randomInt } from 'node:crypto';
import { writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { rawInputCapture } from './input-bytes.mjs';
import { imageModeQueries, imageRestore, checkImages } from './static-images.mjs';
import { write } from './static-images-collector.mjs';
import { animationSuites, animationPriorCap } from './kitty-animation.mjs';
import { animationCasesByVersion, currentAnimationVersion } from './kitty-animation-cases.mjs';

const delay = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
const stageOf = (entry, version) => animationCasesByVersion[version].find(item => item.id === entry.caseId)?.stages.find(item => item.id === entry.stageId);

/**
 * How long to wait before admitting a read so its capture lands where the stage plans it. A hold waits out its
 * gap after the previous read; a running read aims its capture centre at its offset from the start admission,
 * assuming the capture centre trails admission by about 60% of the last measured read.
 */
export function animationWait(stage, { now, previousEnd, startAdmitted, lastReadMs }) {
    if (!stage) return 0;
    if (stage.waitMs !== undefined) return Math.max(0, previousEnd + stage.waitMs - now);
    if (stage.clock === 'running' && stage.targetMs !== undefined && startAdmitted !== null) {
        return Math.max(0, Math.min(30000, startAdmitted + stage.targetMs - Math.round(lastReadMs * 0.6) - now));
    }
    return 0;
}

/** One exclusive byte reader after the static-image reader is sealed; no retries or native input. */
export async function collectAnimation({ config, owner, fence, detachFence = false, input = process.stdin, output = process.stdout, sleep = delay, now = Date.now, version = currentAnimationVersion }) {
    const { plan } = animationSuites[version];
    let initial;
    try { initial = await owner('kan-start'); }
    catch (error) {
        if (['screenDetailsUnavailable', 'imageGeometryUnavailable'].includes(error.code)) return { status: 'unavailable', reason: error.code };
        throw error;
    }
    // When neither earlier collector installed its reader the original fence still holds stdin; hand it over now that this suite really starts.
    if (detachFence) fence.detachInput();
    checkImages(Array.isArray(initial.priorChallenges) && initial.priorChallenges.length <= animationPriorCap && typeof initial.screen?.cursorVisible === 'boolean');
    const prior = new Set(initial.priorChallenges);
    const capture = rawInputCapture(input, output, 4500, used => {
        for (let attempt = 0; attempt < 8; attempt++) {
            const pair = [randomInt(32768, 65536), randomInt(32768, 65536)];
            if (pair[0] !== pair[1] && !used.has(pair.join(',')) && !prior.has(pair.join(','))) return pair;
        }
        throw new Error('Animation challenge generation exhausted.');
    });
    let attempts = 0, previousEnd = now(), lastReadMs = 2500;
    const startAdmitted = new Map();
    try {
        await plan.reduce((previous, entry) => previous.then(async () => {
            const extra = { ...(entry.caseId ? { caseId: entry.caseId } : {}), ...(entry.stageId ? { stageId: entry.stageId } : {}) };
            const stage = entry.kind === 'read' ? stageOf(entry, version) : null;
            const wait = animationWait(stage, { now: now(), previousEnd, startAdmitted: startAdmitted.get(entry.caseId) ?? null, lastReadMs });
            if (wait > 0) await sleep(wait);
            // A relaunch stage: the parent quits the App and starts it again on the same persistent session before the stage's command is admitted.
            if (stage?.relaunch) {
                const begin = stage.relaunchAtMs === undefined ? undefined : startAdmitted.get(entry.caseId);
                if (begin !== undefined) await sleep(Math.max(0, begin + stage.relaunchAtMs - now()));
                await owner('kan-relaunch', null, extra);
            }
            const began = now();
            const action = await owner('kan-admit', null, extra);
            checkImages(typeof action.commandHex === 'string' && /^[a-f\d]*$/.test(action.commandHex)
                && action.commandHex.length % 2 === 0 && action.commandHex.length <= 16384);
            const bytes = Buffer.from(action.commandHex, 'hex');
            checkImages(createHash('sha256').update(bytes).digest('hex') === action.commandSha256
                && action.index === attempts && action.kind === 'kan-' + entry.kind
                && action.caseId === (entry.caseId ?? null) && action.stageId === (entry.stageId ?? null));
            if (stage?.reply === 'start') startAdmitted.set(entry.caseId, Date.parse(action.ownership.at));
            const command = Buffer.concat([bytes, Buffer.from(imageModeQueries)]);
            attempts++;
            const sample = await capture.capture(() => write(output, command));
            await owner('kan-' + entry.kind, null, { ...extra, capture: sample });
            previousEnd = now();
            if (stage) lastReadMs = previousEnd - began;
        }), Promise.resolve());
        capture.dispose();
        await owner('kan-seal', null, { captureStatus: { failed: capture.failed, disposed: true, attempts, lastAttempt: capture.lastAttempt } });
        return { status: 'complete' };
    } catch (error) {
        writeFileSync(join(config.output, 'animation-raw-failure.json'), JSON.stringify({ attempts, failed: capture.failed,
            lastAttempt: capture.lastAttempt, error: String(error.message ?? error) }) + '\n', { flag: 'wx', mode: 0o600 });
        try { await write(output, imageRestore(initial.screen.cursorVisible)); }
        catch (cleanupError) { throw new AggregateError([error, cleanupError], 'Animation collection and cleanup failed.'); }
        throw error;
    } finally { capture.dispose(); }
}
