// Worker-side driver for the cursor qualification (the four pinned cursor.cup-scroll-region, cursor.hide, cursor.shape and cursor.reverse-wrap
// cases). Replays each contracted group's exact plan through the owned fence: the PINNED fixture bytes on a measured reset alternate screen plus
// discriminating controls. Every read is journaled by the parent observer with the complete viewport, margins, origin, stored cursor mode and
// visibility; CPR reads retain the raw reply beside the physical cursor; capture reads retain a controlled render. The pinned termless and app
// callbacks then run over the independently observed states of their own fixture, and the ORIGINAL reverse-wrap app callback is re-run so its
// UndeclaredTerminalGeometry failure is retained beside the geometry-repaired execution.
import { createHash } from 'node:crypto';
import { makeCursorQualContracts, cursorQualNoncePrefix, cursorQualReset, cursorQualCaseIds, validateCursorQualScreen, cursorQualView } from './cursor-qual-contracts.mjs';
import { cursorGeometryRepairId } from './cursor-qual-grading-collector.mjs';

const hash = text => createHash('sha256').update(text).digest('hex');

/** The suite starts from the pristine private main screen: blank viewport, home cursor, visible, default margins, modes and cursor shape. */
export function requirePristineCursorQualStart(screen) {
    validateCursorQualScreen(screen);
    const view = cursorQualView(screen);
    if (view.lines.some(line => line !== '') || view.cursor[0] !== 0 || view.cursor[1] !== 0 || !view.visible || !view.mode.isDefault || view.alternate
        || view.top !== 0 || view.bottom !== screen.rows - 1 || view.origin || view.insert || view.pending || !view.autoWrap || view.history !== 0 || !view.storedDefault) {
        throw new Error('Cursor fixtures require a pristine private main screen with a visible default-shape cursor.');
    }
}

/** The groups' slices of a case's flat read list: `{ group, reads: [offset, offset + count) }`. */
export function cursorQualGroupOffsets(contract) {
    let offset = 0;
    return contract.groups.map(group => {
        const entry = { group, offset };
        offset += group.reads.length;
        return entry;
    });
}

/** A pinned probe's feed accounting: it replays its pinned feeds in order, or (returning early) feeds only the `finally` cleanup tail. */
function feedTracker(contract, group, label) {
    const pinned = group.pinnedFeeds, tailBase = pinned.length - group.tailCount;
    let position = 0, skipped = false;
    return {
        get position() { return position; },
        get skipped() { return skipped; },
        /** Whether the pinned code replayed its whole fixture, refused before feeding anything, or fed only its declared cleanup after returning early. */
        get complete() { return position === pinned.length || position === 0; },
        accept(text) {
            if (position < pinned.length && text === pinned[position]) { position++; return; }
            // An early return still runs the `finally` cleanup: skip straight to the tail.
            if (group.tailCount > 0 && position < tailBase && text === pinned[tailBase]) { position = tailBase + 1; skipped = true; return; }
            throw new Error(`Pinned ${contract.caseId} ${label} feed ${position} differs from the contract.`);
        },
    };
}

/** Serve the pre-captured states of the pinned termless fixture to the pinned termless probe, cross-checking that the pinned code itself
 * replays exactly the contracted pinned feed bytes. A read is served only at a feed count where the contract journaled a state. */
export function cursorQualTermlessContext(contract, group, screens) {
    const tracker = feedTracker(contract, group, 'termless');
    const current = () => {
        const index = group.pinnedReadAt[tracker.position];
        if (index === undefined) throw new Error(`Pinned ${contract.caseId} read at feed ${tracker.position} without an observed state.`);
        return screens[index];
    };
    return {
        get cols() { return screens[0].cols; },
        get complete() { return tracker.complete; },
        get fedCount() { return tracker.position; },
        feed(text) { tracker.accept(text); },
        getText() { throw new Error(`Pinned ${contract.caseId} probe must not read text.`); },
        getScrollback() {
            const screen = current();
            return { screenLines: screen.rows, totalLines: screen.rows + screen.bufferState.scrollbackLines, viewportY: 0 };
        },
        getCursor() {
            const screen = current();
            return { x: screen.cursorCol, y: screen.cursorRow, visible: screen.cursorVisible, style: ['block', 'beam', 'underline'][screen.cursorMode.style] };
        },
        getMode(name) {
            if (name !== 'autoWrap') throw new Error(`Pinned ${contract.caseId} read unmodeled mode ${name}.`);
            return current().bufferState.autoWrap;
        },
    };
}

/** Serve the pre-captured states, CPR replies and controlled captures of the pinned app fixture to the pinned app callback. */
export function cursorQualAppBackend(contract, group, screens, reads) {
    const tracker = feedTracker(contract, group, 'app');
    const reports = reads.flatMap(read => (read.kind === 'cpr' ? [read.cpr?.reply ?? null] : []));
    const captures = group.pinnedCaptureAt;
    let report = 0, capture = 0, failure;
    const guard = work => {
        try { return work(); } catch (error) { failure ??= error; throw error; }
    };
    return {
        get failure() { return failure; },
        get complete() { return tracker.complete; },
        get fedCount() { return tracker.position; },
        write(text) { guard(() => tracker.accept(text)); },
        queryCursorPosition() {
            return guard(() => {
                if (report >= reports.length) throw new Error(`Pinned ${contract.caseId} queried the cursor position without a retained report.`);
                const reply = reports[report++];
                return reply ? { row: reply.row, col: reply.col } : null;
            });
        },
        capture(checkpoint) {
            return guard(() => {
                const index = captures[capture++];
                const read = group.reads[index];
                if (index === undefined || checkpoint.label !== read.label || !/^[a-f\d]{64}$/.test(reads[index].pixelSha256 ?? '')) {
                    throw new Error(`Pinned ${contract.caseId} capture ${capture - 1} differs from the contract.`);
                }
                return { role: checkpoint.role, label: checkpoint.label, ref: `sha256:${reads[index].pixelSha256}` };
            });
        },
    };
}

/** The pinned bookkeeping of the geometry repair: the exact metadata change and the digests of the callbacks it keeps. */
export function cursorQualRepairRecord(gradingModule, probeId) {
    const repair = gradingModule.ntildeCursorRepair(probeId);
    return { derivationId: cursorGeometryRepairId, probeId: repair.probeId, originalKeys: repair.originalKeys, repairedKeys: repair.repairedKeys,
        originalTermNeedsGeometry: repair.originalTermNeedsGeometry, repairedTermNeedsGeometry: repair.repairedTermNeedsGeometry,
        sameTermless: repair.sameTermless, sameTerm: repair.sameTerm, termlessSha256: repair.termlessSha256 ?? hash(repair.termlessSource), termSha256: repair.termSha256 ?? hash(repair.termSource),
        metadataChange: 'termNeedsGeometry: true added to the cursor.reverse-wrap probe record; both callbacks are the original function objects' };
}

/** A backend for the ORIGINAL reverse-wrap callback: it may not send a byte or query, because it must fail on its undeclared geometry first. (The
 * original shape callback is run over the same retained captures as its repaired twin, because it fails only after them.) */
const originalBackend = () => ({
    write() { throw new Error('The original reverse-wrap callback sent a byte before failing.'); },
    queryCursorPosition() { throw new Error('The original reverse-wrap callback queried before failing.'); },
});

/** Run the pinned termless, pinned app and (for reverse-wrap) original app callbacks of one case over its observed states. Shared by the worker and
 * the parent-side replay, so both execute exactly the same code over exactly the same evidence. */
export async function runCursorQualPinned(contract, screens, reads, gradingModule) {
    const result = {};
    const geometry = { rows: screens[0].rows, cols: screens[0].cols };
    for (const { group, offset } of cursorQualGroupOffsets(contract)) {
        const groupScreens = screens.slice(offset, offset + group.reads.length), groupReads = reads.slice(offset, offset + group.reads.length);
        if (group.kind === 'pinned-termless') {
            const context = cursorQualTermlessContext(contract, group, groupScreens);
            result.termless = gradingModule.ntildeRunCursorTermless(contract.caseId, context);
            if (!context.complete) throw new Error(`Pinned ${contract.caseId} termless probe did not replay its complete fixture.`);
        } else if (group.kind.startsWith('pinned-app')) {
            const repaired = group.kind === 'pinned-app-repaired';
            const backend = cursorQualAppBackend(contract, group, groupScreens, groupReads);
            const run = await gradingModule.ntildeRunCursorApp(contract.caseId, backend, { repaired, geometry });
            if (backend.failure) throw backend.failure;
            if (run.threw) throw new Error(`Pinned ${contract.caseId} app callback failed: ${run.threw.name}: ${run.threw.message}`);
            if (!backend.complete) throw new Error(`Pinned ${contract.caseId} app callback did not replay its complete fixture.`);
            result.app = { ...run, repaired, geometry };
            if (repaired) {
                const replay = contract.caseId === 'cursor.reverse-wrap' ? originalBackend() : cursorQualAppBackend(contract, group, groupScreens, groupReads);
                const original = await gradingModule.ntildeRunCursorApp(contract.caseId, replay, { repaired: false, geometry });
                if (replay.failure) throw replay.failure;
                result.original = { ...original, geometry };
                result.repair = cursorQualRepairRecord(gradingModule, contract.caseId);
            }
        }
    }
    return result;
}

/** Run every contracted case serially with parent-journaled controls. */
export async function collectCursorQualGrading({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeCursorAdapter;
    const nonce = cursorQualNoncePrefix + config.runId;
    const cases = [];
    const started = await owner('curq-start');
    let primary, failed = false, contracts;
    try {
        requirePristineCursorQualStart(started.screen);
        contracts = makeCursorQualContracts(started.screen.rows, started.screen.cols);
        const bindBarrier = await fence.write('\x1b[?1049h' + cursorQualReset + nonce);
        await owner('curq-bind', bindBarrier);
        for (const contract of contracts) {
            const feeds = [], barriers = [], reads = [], screens = [];
            let barrier = barriers.at(-1);
            for (const step of contract.plan) {
                if (step.feed) {
                    feeds.push(step.feed);
                    barrier = await fence.write(step.feed); // serial by design: each write must settle before the next (NOSONAR)
                    barriers.push(barrier);
                    continue;
                }
                let observed, extra = {}, cpr;
                if (step.read === 'cpr') {
                    cpr = await fence.cursorReport(); // serial by design: the reply orders against the next read (NOSONAR)
                    barrier = cpr.barrier;
                    extra = { cpr };
                } else if (step.read === 'capture') {
                    extra = { capture: { timeMs: contract.groups.flatMap(group => group.reads)[reads.length].timeMs } };
                }
                observed = await owner('curq-read', barrier, extra); // serial by design: reads journal between writes (NOSONAR)
                reads.push({ kind: step.read, cursor: [observed.screen.cursorRow, observed.screen.cursorCol], barrier,
                    ...(cpr ? { cpr } : {}), ...(step.read === 'capture' ? { timeMs: extra.capture.timeMs, pixelSha256: observed.pixel.sha256 } : {}) });
                screens.push(observed.screen);
            }
            if (feeds.length !== contract.plan.filter(step => step.feed).length || screens.length !== contract.plan.filter(step => step.read).length) {
                throw new Error(`Incomplete ${contract.caseId} cursor fixture.`);
            }
            cases.push({ caseId: contract.caseId, feeds, barriers, reads, screens });
        }
    } catch (error) { primary = error; failed = true; }
    let restoreFailed = false, restoreError;
    try {
        await owner('curq-finish', await fence.write(cursorQualReset + '\x1b[?1049l'));
    } catch (error) { restoreFailed = true; restoreError = error; }
    if (failed && restoreFailed) throw new AggregateError([primary, restoreError], 'curq collection and restoration failed.');
    if (restoreFailed) throw restoreError;
    if (failed) throw primary;
    const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], geometryRepairId: adapter.geometryRepairId, cases: [] };
    for (const item of cases) {
        const contract = contracts.find(entry => entry.caseId === item.caseId);
        for (const screen of item.screens) validateCursorQualScreen(screen);
        const pinned = await runCursorQualPinned(contract, item.screens, item.reads, gradingModule);
        const { caseId, feeds, barriers, reads } = item;
        run.cases.push({ caseId, feeds, barriers, reads, pinned });
    }
    if (JSON.stringify(run.cases.map(entry => entry.caseId)) !== JSON.stringify([...cursorQualCaseIds])) throw new Error('Cursor run does not cover the contracted cases.');
    return run;
}
