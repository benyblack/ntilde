// Worker-side driver for the runtime-color reset qualification (extensions.osc104-reset-palette, osc110-reset-fg, osc111-reset-bg and
// osc112-reset-cursor). Replays each case's exact plan through the owned fence: every experiment's scene, then the PINNED bytes (BEL
// spelling) and the graded controls. A query step is one fenced exchange that retains the raw OSC reply (or its explicit absence) before
// the DA barrier; every read is journaled by the parent observer with the replies asked since the previous read. The pinned termless
// probe then runs over the three replies of its own pinned experiment. Runtime colors are session-wide and survive both the
// alternate-screen exit and DECSTR, so the finish write clears them explicitly; after a fatal input error only a bounded best-effort
// write is attempted, which can neither revive the fence nor establish restoration.
import { colorResetContracts, colorResetNoncePrefix, colorResetBind, colorResetFinish, colorResetFeeds, colorResetReads, validateColorResetScreen } from './color-reset-contracts.mjs';

/** Serve the journaled replies of the pinned experiment to the pinned termless probe, cross-checking that the pinned code itself issues
 * exactly the contracted bytes, each as the contracted kind (a capture or a plain feed), in order, and nothing else. */
export function colorResetRecordingContext(contract, replies) {
    let fed = 0, captured = 0;
    const expect = (text, kind) => {
        if (fed >= contract.pinnedFeeds.length || text !== contract.pinnedFeeds[fed] || kind !== contract.pinnedKinds[fed]) {
            throw new Error(`Pinned ${contract.caseId} feed ${fed} differs from the contract.`);
        }
        fed++;
    };
    return {
        get fedCount() { return fed; },
        feed: text => expect(text, 'feed'),
        feedCapture: text => {
            expect(text, 'capture');
            return replies[captured++]?.raw ?? '';
        },
        getCell: () => { throw new Error(`Pinned ${contract.caseId} probe must not read cells.`); },
        getCursor: () => { throw new Error(`Pinned ${contract.caseId} probe must not read the cursor.`); },
        getText: () => { throw new Error(`Pinned ${contract.caseId} probe must not read text.`); },
    };
}

/** The replies of the pinned experiment's reads, in order: the original, the changed and the restored query of the pinned code. */
export function pinnedReplies(contract, reads) {
    return contract.pinnedRoles.map(role => reads[contract.roles.indexOf(role)].queries[0].reply);
}

/** One plan step of one case. A query step is a fenced exchange whose reply the next read journals; a feed step only orders. */
async function runStep(step, record, { fence, owner, contract }) {
    if (step.feed !== undefined) {
        record.feeds.push(step.feed);
        if (step.query) {
            const exchange = await fence.colorExchange(step.feed, step.query);
            record.barriers.push(exchange.barrier);
            record.pending.push({ spec: step.query, barrier: exchange.barrier, reply: exchange.reply });
        } else record.barriers.push(await fence.write(step.feed));
        return;
    }
    const [experimentId, stageId] = contract.roles[record.screens.length].split('/');
    const queries = record.pending;
    const observed = await owner('crst-read', record.barriers.at(-1), { caseId: contract.caseId, experimentId, stageId, queries });
    record.reads.push({ kind: step.read, cursor: [observed.screen.cursorRow, observed.screen.cursorCol], queries });
    record.screens.push(observed.screen);
    record.pending = [];
}

/** Every step of one case, serially: each write, exchange and read must settle before the next changes state. */
async function collectCase(contract, io) {
    const record = { feeds: [], barriers: [], reads: [], screens: [], pending: [] };
    for (const step of contract.plan) await runStep(step, record, { ...io, contract }); // NOSONAR - serial by design
    if (record.feeds.length !== colorResetFeeds(contract).length || record.screens.length !== colorResetReads(contract) || record.pending.length) {
        throw new Error(`Incomplete ${contract.caseId} runtime-color fixture.`);
    }
    for (const screen of record.screens) validateColorResetScreen(screen);
    const { feeds, barriers, reads, screens } = record;
    return { caseId: contract.caseId, feeds, barriers, reads, screens };
}

/** Restore the runtime colors and the screen whatever happened, aggregating a collection failure with a restoration failure. */
async function collectAndRestore({ fence, owner, nonce }) {
    const cases = [];
    let primary, failed = false;
    try {
        await owner('crst-bind', await fence.write(colorResetBind + nonce));
        for (const contract of colorResetContracts) cases.push(await collectCase(contract, { fence, owner })); // NOSONAR - serial by design
    } catch (error) { primary = error; failed = true; }
    let restoreFailed = false, restoreError;
    try {
        // A failed fence never requests or accepts another barrier: the write is a bounded best-effort cleanup that the receipt cannot count.
        if (fence.failed) await fence.writeCleanup(colorResetFinish);
        else await owner('crst-finish', await fence.write(colorResetFinish));
    } catch (error) { restoreFailed = true; restoreError = error; }
    if (failed && restoreFailed) throw new AggregateError([primary, restoreError], 'crst collection and restoration failed.');
    if (restoreFailed) throw restoreError;
    if (failed) throw primary;
    return cases;
}

/** Run the contracted cases serially with parent-journaled reads and replies. */
export async function collectColorResetGrading({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeColorResetAdapter;
    await owner('crst-start');
    const cases = await collectAndRestore({ fence, owner, nonce: colorResetNoncePrefix + config.runId });
    const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], cases: [] };
    for (const item of cases) {
        const contract = colorResetContracts.find(entry => entry.caseId === item.caseId);
        const context = colorResetRecordingContext(contract, pinnedReplies(contract, item.reads));
        const pinned = gradingModule.ntildeRunColorResetTermless(contract.caseId, context);
        // The pinned probe stops after its first capture when the original reply is missing or malformed; any other early stop is an incomplete fixture.
        const stopped = context.fedCount === 1 && pinned.observation?.outcome === 'inconclusive' && ['no-response', 'invalid-reply'].includes(pinned.observation.reason);
        if (context.fedCount !== contract.pinnedFeeds.length && !stopped) throw new Error(`Pinned ${contract.caseId} did not replay its complete fixture.`);
        const { caseId, feeds, barriers, reads } = item;
        run.cases.push({ caseId, feeds, barriers, reads, pinned });
    }
    return run;
}
