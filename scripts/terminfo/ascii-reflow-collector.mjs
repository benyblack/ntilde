// Worker-side driver for the ASCII reflow qualification (extensions.reflow). Executes the fixed two-experiment plan through
// the owned fence: the nonce bind on the alternate screen, then per experiment the seed write, the parent-selected shrink
// action, the restore action and their parent-journaled reads (never a rewrite of already-written content), then the final
// cleanup. The pinned termless probe is replayed over the journaled declared capability after collection.
import { pristineEditing } from './editing-capture.mjs';
import { asciiReflowCaseIds, asciiReflowFinalRestore, asciiReflowNoncePrefix, asciiReflowSuiteId, makeReflowCases } from './ascii-reflow-contracts.mjs';

/** Serve the declared capability to the pinned termless probe, recording every capability property the pinned code reads. */
export function reflowRecordingContext() {
    const read = [];
    const capabilities = new Proxy({}, { get: (target, key) => { read.push(String(key)); return undefined; } });
    return { capabilities, get readCapabilities() { return [...read]; } };
}

/** Write one planned feed through the fence and record its exact bytes and ordering barrier. */
async function recordFeed(fence, feeds, barriers, bytes) {
    feeds.push(bytes);
    const barrier = await fence.write(bytes);
    barriers.push(barrier);
    return barrier;
}

/** Execute only the fixed private plan; a failed collection restores geometry/output without recording completion. */
export async function collectReflowGrading({ config, fence, owner, gradingModule }) {
    let initial;
    try { initial = await owner('reflow-start'); }
    catch (error) {
        if (['screenDetailsUnavailable', 'asciiReflowGeometryUnavailable'].includes(error.code)) return { status: 'unavailable', reason: error.code };
        throw error;
    }
    if (!pristineEditing(initial.screen)) throw new Error('ASCII reflow fixture requires a pristine private main screen.');
    const cases = makeReflowCases(initial.screen.rows, initial.screen.cols);
    const feeds = [], barriers = [], stages = [], reads = [];
    let bound = false;
    try {
        await owner('reflow-bind', await fence.write('\x1b[?1049h\x1b[!p\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[H' + asciiReflowNoncePrefix + config.runId));
        bound = true;
        for (const item of cases) {
            stages.push({ caseId: item.id, stageId: 'control' });
            // Serial by design: each observation must finish before the next write.
            const seeded = await owner('reflow-read', await recordFeed(fence, feeds, barriers, item.setup), { caseId: item.id, stageId: 'control' }); // NOSONAR
            reads.push({ cursor: [seeded.screen.cursorRow, seeded.screen.cursorCol] });
            for (const stageId of ['after', 'restored']) {
                await owner('reflow-apply', null, { caseId: item.id, stageId }); // NOSONAR
                stages.push({ caseId: item.id, stageId });
                // The parent chose and verified the geometry. Fence later output before reading its state.
                const observed = await owner('reflow-read', await fence.write(''), { caseId: item.id, stageId }); // NOSONAR
                reads.push({ cursor: [observed.screen.cursorRow, observed.screen.cursorCol] });
            }
        }
        await owner('reflow-finish', await recordFeed(fence, feeds, barriers, asciiReflowFinalRestore));
    } catch (error) {
        return await restoreAfterFailure(error, bound, owner, fence);
    }
    const adapter = gradingModule.ntildeReflowAdapter;
    const context = reflowRecordingContext();
    const pinned = gradingModule.ntildeRunReflowTermless('extensions.reflow', context);
    // The pinned code must have consulted only the declared reflow capability, which upstream never declares.
    if (JSON.stringify(context.readCapabilities) !== JSON.stringify(['reflow'])) {
        throw new Error('The pinned extensions.reflow replay read an uncontracted capability.');
    }
    const run = { schemaVersion: 1, suiteId: asciiReflowSuiteId, runId: config.runId, adapterId: adapter.id, probeIds: [...asciiReflowCaseIds],
        feeds, barriers, stages, reads, pinned };
    return { status: 'complete', run };
}

/** Attempt geometry and output cleanup independently, preserving every failure. */
async function restoreAfterFailure(error, bound, owner, fence) {
    const failures = [error];
    if (bound) {
        try { await owner('reflow-abort'); }
        catch (error_) { failures.push(error_); }
    }
    try {
        if (fence.failed) await fence.writeCleanup(asciiReflowFinalRestore);
        else await fence.write(asciiReflowFinalRestore);
    } catch (error_) { failures.push(error_); }
    if (failures.length > 1) throw new AggregateError(failures, 'ASCII reflow collection and cleanup failed.');
    throw error;
}
