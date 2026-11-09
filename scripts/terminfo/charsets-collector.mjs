// Worker-side driver for the charset qualification (charsets.utf8, charsets.dec-special, charsets.dec-line-drawing,
// charsets.g0-g1-switching). Replays each contracted case's exact plan through the owned fence (shared graded-plan
// driver): the PINNED fixture bytes (including UTF-8 text, escape designations and shift controls, untouched) on a
// measured reset screen plus discriminating controls. Every read is journaled by the parent observer; the pinned
// termless probe then runs over the independently observed physical cells of its own fixture.
import { collectGradedPlans, planFeeds, planReads } from './graded-plan.mjs';
import { charsetsContracts, charsetsNoncePrefix, charsetsReset, charsetsCellAt, validateCharsetsScreen } from './charsets-contracts.mjs';

/** Serve the pre-captured screens of the pinned fixture to a pinned termless probe, cross-checking that the pinned
 * code itself replays exactly the contracted pinned fixture bytes (and nothing else). The pinned restoration bytes
 * (the `finally` of three cases) are accepted once, at any point after a feed, because the pinned code runs them on
 * every path; they are served no read. The screen the probe sees is the read taken after its latest pinned feed. */
export function charsetsRecordingContext(contract, screens) {
    let fed = 0, finalized = false;
    const screenAt = role => screens[contract.roles.indexOf(role)];
    const current = () => {
        if (fed === 0) throw new Error(`Pinned ${contract.caseId} read before its fixture.`);
        return screenAt(contract.pinnedReadRoles[fed - 1]);
    };
    return {
        get cols() { return screenAt('initial').cols; },
        get fedCount() { return fed; },
        get finalized() { return finalized; },
        feed(text) {
            if (fed < contract.pinnedFeeds.length && text === contract.pinnedFeeds[fed]) fed++;
            else if (contract.pinnedFinally !== null && fed > 0 && !finalized && text === contract.pinnedFinally) finalized = true;
            else throw new Error(`Pinned ${contract.caseId} feed ${fed} differs from the contract.`);
        },
        getText() {
            throw new Error(`Pinned ${contract.caseId} probe must not read text.`);
        },
        getScrollback() {
            const screen = screenAt('initial');
            return { screenLines: screen.rows, totalLines: screen.rows, viewportY: 0 };
        },
        getCursor() {
            const screen = current();
            return { x: screen.cursorCol, y: screen.cursorRow, visible: screen.cursorVisible };
        },
        getCell(row, col) {
            return charsetsCellAt(current(), row, col);
        },
    };
}

/** Require a pinned run to have replayed exactly its contracted fixture: the full feed list when the verdict is decided, a valid prefix otherwise, and the restoration bytes whenever anything was fed. */
export function requirePinnedReplay(contract, context, pinned) {
    const decided = pinned.observation?.outcome === 'supported' || pinned.observation?.outcome === 'unsupported';
    if (decided && context.fedCount !== contract.pinnedFeeds.length) throw new Error(`Pinned ${contract.caseId} did not replay its complete fixture.`);
    if (context.fedCount > 0 && contract.pinnedFinally !== null && !context.finalized) {
        throw new Error(`Pinned ${contract.caseId} did not run its restoration bytes.`);
    }
}

/** Run every contracted case serially with parent-journaled controls. */
export async function collectCharsetsGrading({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeCharsetsAdapter;
    const collected = await collectGradedPlans({ contracts: charsetsContracts, prefix: 'chrs',
        reset: charsetsReset, nonce: charsetsNoncePrefix + config.runId, fence, owner,
        planFeeds, planReads });
    const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], cases: [] };
    for (const item of collected) {
        const contract = charsetsContracts.find(entry => entry.caseId === item.caseId);
        for (const screen of item.screens) validateCharsetsScreen(screen);
        const context = charsetsRecordingContext(contract, item.screens);
        const pinned = gradingModule.ntildeRunCharsetsTermless(contract.caseId, context);
        requirePinnedReplay(contract, context, pinned);
        const { caseId, feeds, barriers, reads } = item;
        run.cases.push({ caseId, feeds, barriers, reads, pinned });
    }
    return run;
}
