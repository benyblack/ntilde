import { scrollbackReset } from './scrollback-contracts.mjs';
import { wrapPlan } from './core-wrap-contract.mjs';

export const wrapSuiteId = 'ntilde-core-wrap-v1';
const reset = scrollbackReset.replace('\x1b[?1049l', '') + '\x1b[?69l\x1b[?7h\x1b[?25h';
/** Each text write is contiguous; fenced reads are independent experiments. The
 * parent owns resize targets and verifies their App and kernel observations. */
export async function collectWrap({ config, fence, owner }) {
    const initial = await owner('cw-geometry-start');
    if (typeof initial?.screen?.bufferState?.autoWrap !== 'boolean') throw new Error('Core wrap: original mode unavailable');
    const originalMode = initial.screen.bufferState.autoWrap;
    let resized = false, started = false, primary;
    const feeds = [], barriers = [], reads = [];
    try {
        resized = true; await owner('cw-narrow');
        await owner('cw-start'); started = true;
        await owner('cw-bind', await fence.write('\x1b[?47h' + reset + 'NTILDE_CW_' + config.runId));
        for (const step of wrapPlan()) {
            if (step.feed) { feeds.push(step.feed); barriers.push(await fence.write(step.feed)); }
            else {
                const cpr = await fence.cursorReport();
                await owner('cw-read', cpr.barrier, { cpr });
                reads.push({ role: step.read, cpr });
            }
        }
    } catch (error) { primary = error; }
    const failures = primary ? [primary] : [];
    if (started) {
        try {
            const cleanup = reset + (originalMode ? '\x1b[?7h' : '\x1b[?7l') + '\x1b7\x1b[?47l';
            const barrier = fence.failed ? await fence.writeCleanup(cleanup) : await fence.write(cleanup);
            await owner('cw-finish', barrier);
        } catch (error) { failures.push(error); }
    }
    if (resized) {
        if (failures.length) {
            try { await owner('cw-abort'); } catch (error) { failures.push(error); }
        } else {
            try { await owner('cw-restore'); }
            catch (error) {
                failures.push(error);
                try { await owner('cw-abort'); } catch (abortError) { failures.push(abortError); }
            }
            if (!failures.length) try { await owner('cw-seal'); } catch (error) { failures.push(error); }
        }
    }
    if (failures.length === 1) throw failures[0];
    if (failures.length) throw new AggregateError(failures, 'Core wrap collection and restoration failed');
    return { schemaVersion: 1, suiteId: wrapSuiteId, runId: config.runId, feeds, barriers, reads };
}
