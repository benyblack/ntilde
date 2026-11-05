// Worker-side driver for the ASCII/SGR qualification batch. Replays each
// contracted case's exact pinned fixture bytes through the owned fence, reads
// the resulting cells/flags through the parent-observed readScreen journal,
// and then executes the pinned termless probe (from the derived grading
// extension) against those independently observed screens.
import { asciiSgrContracts, asciiSgrNoncePrefix, asciiSgrReset, contractFeeds, contractReads } from './ascii-sgr-contracts.mjs';

/** Serve pre-captured screens to a pinned termless probe, cross-checking that
 * the pinned code itself replays exactly the contracted fixture bytes. */
export function recordingContext(contract, screens) {
    const pinnedFeeds = contractFeeds(contract).slice(1);
    let feedIndex = 0, readIndex = 0, served;
    const serve = kind => {
        const step = contract.plan.filter(item => item.read)[readIndex];
        if (!step || step.read !== kind || readIndex >= screens.length) {
            throw new Error(`Pinned ${contract.caseId} read order differs from the contract.`);
        }
        if (!served) served = screens[readIndex];
        return served;
    };
    return {
        feed(text) {
            if (text !== pinnedFeeds[feedIndex]) {
                throw new Error(`Pinned ${contract.caseId} feed ${feedIndex} differs from the contract.`);
            }
            feedIndex++;
            served = undefined;
            readIndex = feedIndex - 1;
        },
        getText() {
            return serve('text').lines.join('\n');
        },
        getCell(row, col) {
            const screen = serve('cells');
            const flags = screenFlags(screen)[row * screen.cols + col] ?? 0;
            return { char: (screen.lines[row] ?? ' ')[col] ?? ' ', flags,
                bold: (flags & 0x0001) !== 0, italic: (flags & 0x0004) !== 0 };
        },
    };
}

/** Pure per-screen flag expansion; observed evidence is never mutated. */
function screenFlags(screen) {
    const cells = screen.rows * screen.cols;
    const all = Array(cells).fill(0);
    let offset = 0;
    for (const run of screen.attributeRuns ?? []) {
        if (!Number.isSafeInteger(run?.count) || run.count < 1 || !Number.isSafeInteger(run.flags)
            || run.flags < 0 || run.flags > 65535 || offset + run.count > cells) {
            throw new Error('Invalid graded attribute runs.');
        }
        all.fill(run.flags, offset, offset + run.count);
        offset += run.count;
    }
    if (offset !== cells) throw new Error('Incomplete graded attribute runs.');
    return all;
}

/** Run every contracted case serially with parent-journaled controls. */
export async function collectAsciiSgrGrading({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeGradingAdapter;
    const nonce = asciiSgrNoncePrefix + config.runId;
    const restore = asciiSgrReset + '\x1b[?1049l';
    const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], cases: [] };
    await owner('grading-start');
    try {
        const bindBarrier = await fence.write('\x1b[?1049h' + asciiSgrReset + nonce);
        await owner('grading-bind', bindBarrier);
        for (const contract of asciiSgrContracts) {
            const feeds = [], barriers = [], reads = [], screens = [];
            for (const step of contract.plan) {
                if (step.feed) {
                    feeds.push(step.feed);
                    barriers.push(await fence.write(step.feed));
                } else {
                    const observed = await owner('grading-read', barriers.at(-1));
                    reads.push({ kind: step.read, screen: observed.screen });
                    screens.push(observed.screen);
                }
            }
            if (feeds.length !== contractFeeds(contract).length || screens.length !== contractReads(contract)) {
                throw new Error(`Incomplete ${contract.caseId} graded fixture.`);
            }
            const pinned = gradingModule.ntildeRunTermless(contract.caseId, recordingContext(contract, screens));
            run.cases.push({ caseId: contract.caseId, feeds, barriers,
                reads: reads.map(read => ({ kind: read.kind, cursor: [read.screen.cursorRow, read.screen.cursorCol] })),
                pinned });
        }
    } finally {
        await owner('grading-finish', await fence.write(restore));
    }
    return run;
}
