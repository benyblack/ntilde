// Shared machinery for the pinned-fixture grading suites. Both the ASCII/SGR
// batch and the layered SGR-colors batch drive identical plan/feed/read
// schedules over the owned fence and validate identical journal shapes; the
// common logic lives here once so the layered suite adds no duplicated code.
import { screenSnapshot, snapshotHash } from './agent-observer.mjs';

/** One plan step: write exact bytes through the owned fence. */
export const planFeed = bytes => ({ feed: bytes });

/** One plan step: the pinned probe reads the screen once after a feed. */
export const planRead = kind => ({ read: kind });

/** The exact feed schedule (including setup) of one contract. */
export const planFeeds = contract => contract.plan.filter(step => step.feed).map(step => step.feed);

/** The number of read groups one contract performs. */
export const planReads = contract => contract.plan.filter(step => step.read).length;

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateExactFeeds(contract, feeds, label) {
    const expected = planFeeds(contract);
    if (!Array.isArray(feeds) || feeds.length !== expected.length
        || feeds.some((value, index) => value !== expected[index])) {
        throw new Error(`Recorded ${contract.caseId} feeds do not match the pinned ${label}.`);
    }
}

/** Validate one parent-journaled frame against its expected kind and shape. */
export function validateGradedFrame(frame, index, baseline, kindAt, label, screenOf = value => screenSnapshot(value, true)) {
    screenOf(frame.screen);
    if (frame.index !== index || frame.kind !== kindAt(index) || frame.sha256 !== snapshotHash(frame.screen)
        || frame.paneId !== baseline.paneId
        || frame.screen.rows !== baseline.screen.rows || frame.screen.cols !== baseline.screen.cols) {
        throw new Error(`Changed ${label} snapshot evidence.`);
    }
    if (index === 0 ? frame.barrier !== null : !/^\x1b\[\?[\d;]+c$/.test(frame.barrier ?? '')) {
        throw new Error(`Missing ${label} ordering barrier evidence.`);
    }
}

/** Validate a complete journal: kinds, nonce binding and restoration. */
export function validateGradedJournal(frames, { frameCount, prefix, noncePrefix, runId, label, screenOf }) {
    const kindAt = index => {
        if (index === 0) return `${prefix}-start`;
        if (index === 1) return `${prefix}-bind`;
        if (index === frameCount - 1) return `${prefix}-finish`;
        return `${prefix}-read`;
    };
    if (!Array.isArray(frames) || frames.length !== frameCount) throw new Error(`Incomplete ${label} snapshot sequence.`);
    for (const [index, frame] of frames.entries()) validateGradedFrame(frame, index, frames[0], kindAt, label, screenOf);
    const binding = frames[1].screen;
    if (!/^[a-f\d]{32}$/.test(runId ?? '') || binding.lines[0] !== noncePrefix + runId
        || binding.cursorRow !== 0 || binding.cursorCol !== (noncePrefix + runId).length) {
        throw new Error(`${label} evidence is not bound to this run.`);
    }
    if (frames[0].sha256 !== frames.at(-1).sha256) throw new Error(`${label} fixture was not restored.`);
}

/** Every read frame must carry the barrier and cursor of its preceding feed. */
export function crossCheckGradedCapture(contract, recorded, readFrames, label) {
    let feedSteps = 0, readSteps = 0;
    for (const step of contract.plan) {
        if (step.feed) feedSteps++;
        else {
            const frame = readFrames[readSteps];
            if (frame.barrier !== recorded.barriers[feedSteps - 1]) {
                throw new Error(`${label} ${contract.caseId} barrier differs from its read frame.`);
            }
            if (JSON.stringify(recorded.reads[readSteps].cursor) !== JSON.stringify([frame.screen.cursorRow, frame.screen.cursorCol])) {
                throw new Error(`${label} ${contract.caseId} cursor differs from its read frame.`);
            }
            readSteps++;
        }
    }
}

/** Validate one case's recorded capture record shape (feeds, barriers, reads). */
export function validateGradedCaptureRecord(contract, recorded, label) {
    validateExactFeeds(contract, recorded.feeds, 'contract');
    if (!Array.isArray(recorded.barriers) || recorded.barriers.length !== planFeeds(contract).length
        || recorded.barriers.some(barrier => !/^\x1b\[\?[\d;]+c$/.test(barrier ?? ''))
        || !Array.isArray(recorded.reads) || recorded.reads.length !== planReads(contract)) {
        throw new Error(`Invalid ${contract.caseId} graded capture record (${label}).`);
    }
    for (const [step, read] of contract.plan.filter(item => item.read).entries()) {
        if (recorded.reads[step].kind !== read.read) throw new Error(`Wrong ${contract.caseId} read kind (${label}).`);
    }
}

/** The pinned result comparison: empty and absent assertion lists are equal. */
const present = value => (value?.length ? value : null);

/** Require a retained pinned result to equal its screen-derived replay. */
export function requirePinnedAgreement(contract, pinned, replayed, label) {
    if (pinned.pass !== replayed.pass || pinned.response !== replayed.response
        || (pinned.note ?? null) !== (replayed.note ?? null)
        || JSON.stringify(present(pinned.assertions)) !== JSON.stringify(present(replayed.assertions))
        || JSON.stringify(pinned.observation) !== JSON.stringify(replayed.observation)) {
        throw new Error(`Pinned ${contract.caseId} grading does not match its observed evidence (${label}).`);
    }
}

/** Require the parent-side module replay to reproduce the live pinned result. */
export function requireReplayAgreement(contract, replay, pinned) {
    if (replay.pass !== pinned.pass || replay.response !== pinned.response
        || replay.observation.outcome !== pinned.observation.outcome
        || JSON.stringify(present(replay.assertions)) !== JSON.stringify(present(pinned.assertions))) {
        throw new Error(`Pinned ${contract.caseId} replay does not reproduce the live grading.`);
    }
}

/**
 * Drive every contracted plan serially through the owned fence, journaling
 * each read through the parent observer. Serial awaits are the contract:
 * every write/reply/read must complete before the next write changes state.
 * Optional `contractsFor(startScreen)`, `bind` and `finish` let a suite measure geometry-dependent fixtures on the main screen;
 * the defaults keep every earlier suite byte-identical.
 * Returns per-case { feeds, barriers, screens, reads } in contract order and
 * aggregates collection and restoration failures instead of dropping either.
 */
export async function collectGradedPlans({ contracts, contractsFor, prefix, reset, bind, finish, nonce, fence, owner, planFeeds: feedsOf, planReads: readsOf }) {
    const cases = [];
    const started = await owner(`${prefix}-start`);
    let primary, failed = false;
    try {
        // A suite whose fixtures depend on the measured geometry derives its contracts from the observed start screen.
        const plans = contractsFor ? contractsFor(started.screen) : contracts;
        const bindBarrier = await fence.write(bind ?? ('\x1b[?1049h' + reset + nonce));
        await owner(`${prefix}-bind`, bindBarrier);
        for (const contract of plans) {
            const feeds = [], barriers = [], reads = [], screens = [], facts = [], actions = [];
            for (const step of contract.plan) {
                // Optional steps only the OSC 9 suite uses: a bounded host wait, and a parent-performed action answered by the owner (its record is retained there).
                if (step.wait !== undefined) { await new Promise(resolve => setTimeout(resolve, step.wait)); continue; } // NOSONAR serial by design
                if (step.action !== undefined) { actions.push({ action: step.action, response: await owner(`${prefix}-${step.action}`, barriers.at(-1)) }); continue; } // NOSONAR serial by design
                if (step.feed) {
                    feeds.push(step.feed);
                    barriers.push(await fence.write(step.feed)); // serial by design: each write must settle before the next (NOSONAR)
                } else {
                    const observed = await owner(`${prefix}-read`, barriers.at(-1)); // serial by design: reads journal between writes (NOSONAR)
                    reads.push({ kind: step.read, cursor: [observed.screen.cursorRow, observed.screen.cursorCol] });
                    screens.push(observed.screen);
                    facts.push(observed.facts);
                }
            }
            if (feeds.length !== feedsOf(contract).length || screens.length !== readsOf(contract)) {
                throw new Error(`Incomplete ${contract.caseId} graded fixture.`);
            }
            // Facts (small bounded values a suite's observer exposed beside the screen) are carried only when present.
            cases.push({ caseId: contract.caseId, feeds, barriers, reads, screens, ...(facts.some(item => item !== undefined) ? { facts } : {}), ...(actions.length ? { actions } : {}) });
        }
    } catch (error) { primary = error; failed = true; }
    let restoreFailed = false, restoreError;
    try {
        await owner(`${prefix}-finish`, await fence.write(finish ?? (reset + '\x1b[?1049l')));
    } catch (error) { restoreFailed = true; restoreError = error; }
    if (failed && restoreFailed) throw new AggregateError([primary, restoreError], `${prefix} collection and restoration failed.`);
    if (restoreFailed) throw restoreError;
    if (failed) throw primary;
    return cases;
}
