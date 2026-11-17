import { collectGradedPlans, planFeeds, planReads } from './graded-plan.mjs';
import { osc720CaseIds, osc720CaseId, osc720Experiments, osc720Fixture, osc720Pinned, osc720Prefix, osc720ScrollbackState } from './osc720-contracts.mjs';

const unavailableReasons = new Set(['screenDetailsUnavailable', 'paneMetadataUnavailable', 'metadataGeometryUnavailable']);

/** The small, bounded facts the worker needs from one paired observation: the view's scroll position, the stored history and the cursor. */
export const exposeOsc720Facts = snapshot => {
    const { buffer, viewport } = snapshot.metadata;
    return { rows: snapshot.screen.rows, viewport: { scrollOffset: viewport.scrollOffset, maxScrollOffset: viewport.maxScrollOffset },
        scrollbackLines: buffer.scrollbackLines, historyGeneration: buffer.historyGeneration, rowsEvicted: buffer.rowsEvicted,
        cursor: [buffer.cursorRow, buffer.cursorCol], alternateScreen: buffer.alternateScreen };
};

/** A prerequisite may be skipped only through its typed initial refusal; otherwise it must have restored. */
export function osc720PrerequisiteReason(restored, unavailable, reason) {
    if (unavailable === true) {
        if (!unavailableReasons.has(reason)) throw new Error('Unexpected OSC 720 prerequisite refusal.');
        return reason;
    }
    if (!restored) throw new Error('The XTGETTCAP suite must restore before OSC 720 observations.');
    return null;
}

/** A context that serves the pinned code nothing but what it legitimately uses: any other access is a changed probe. */
function strict(served) {
    return new Proxy(served, {
        get(target, key) {
            if (!(key in target)) throw new Error(`Unexpected pinned context access: ${String(key)}.`);
            return target[key];
        },
    });
}

/** The termless context: `feed` is recorded, `getScrollback` answers the two journaled observations in order. The recorded calls must equal the pinned feeds. */
export function osc720TermlessContext(rows, before, after) {
    const calls = [], served = [before, after];
    let reads = 0;
    const context = strict({ feed: data => { calls.push(data); },
        getScrollback: () => {
            if (reads >= served.length) throw new Error('Pinned OSC 720 probe read the scrollback more than twice.');
            return osc720ScrollbackState(rows, served[reads++]);
        } });
    return { context, calls, reads: () => reads };
}

/** The terminal context: `write` is recorded (the owned run does not perform these writes) and the cursor position is answered with the journaled cursor. */
export function osc720TerminalContext(cursor) {
    const writes = [];
    const context = strict({ write: data => { writes.push(data); }, queryCursorPosition: async () => ({ row: cursor[0] + 1, col: cursor[1] + 1 }) });
    return { context, writes };
}

/** Run both pinned paths over the journaled observations of the pinned-bare experiment. The pinned code is executed unchanged; only its context is parent-built. */
export async function replayOsc720Pinned(gradingModule, reads) {
    const [before, after] = reads;
    if (!gradingModule.ntildeOsc720HasTermless(osc720CaseId)) throw new Error('Pinned OSC 720 termless presence changed.');
    const termless = osc720TermlessContext(before.rows, before, after);
    const { probeId, ...result } = gradingModule.ntildeRunOsc720Termless(osc720CaseId, termless.context);
    if (probeId !== osc720CaseId || termless.reads() !== osc720Pinned.termless.scrollbackReads || JSON.stringify(termless.calls) !== JSON.stringify(osc720Pinned.termless.feeds)) {
        throw new Error('Pinned OSC 720 termless replay made different calls.');
    }
    const terminal = osc720TerminalContext(after.cursor);
    const { probeId: terminalId, ...terminalResult } = await gradingModule.ntildeRunOsc720Terminal(osc720CaseId, terminal.context);
    if (terminalId !== osc720CaseId || JSON.stringify(terminal.writes) !== JSON.stringify(osc720Pinned.terminal.writes)) throw new Error('Pinned OSC 720 terminal replay made different calls.');
    return { caseId: osc720CaseId, termless: result, terminal: terminalResult };
}

/** Run the contracted experiments serially with parent-journaled paired reads. A typed refusal of the very first observation is the only optional outcome. */
export async function collectOsc720({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeOsc720Adapter;
    if (JSON.stringify(adapter?.probeIds) !== JSON.stringify([...osc720CaseIds])) throw new Error('Changed OSC 720 adapter.');
    const fixture = osc720Fixture(config.runId), contracts = osc720Experiments(config.runId);
    let collected;
    try {
        collected = await collectGradedPlans({ contracts, prefix: osc720Prefix, reset: fixture.reset, bind: fixture.bind, finish: fixture.finish, nonce: fixture.nonce,
            fence, owner, planFeeds, planReads });
    } catch (error) {
        if (unavailableReasons.has(error?.code) && !error.message?.includes('after partial')) return { status: 'unavailable', reason: error.code };
        throw error;
    }
    const experiments = collected.map(({ caseId, feeds, barriers, reads, facts, actions }) => ({ caseId, feeds, barriers,
        reads: reads.map((entry, index) => ({ ...entry, facts: facts[index] })), ...(actions ? { actions } : {}) }));
    const pinnedBare = experiments.find(item => item.caseId === `${osc720CaseId}/pinned-bare`);
    const pinned = await replayOsc720Pinned(gradingModule, pinnedBare.reads.map(entry => entry.facts));
    return { status: 'complete', run: { schemaVersion: 1, adapterId: adapter.id, probeIds: [...osc720CaseIds], experiments, pinned } };
}
