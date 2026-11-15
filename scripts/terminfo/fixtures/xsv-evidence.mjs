// Synthetic, fully valid xterm saved-mode grading evidence for portable tests. Every frame, run-log entry and pinned result is produced through the same production validators used for real
// measurements. The expected state of every role is a hand-written table (modes, cursor, pending wrap and probe character spelled out per role), a second oracle independent of the contract's model,
// so the fixture is a cross-check, not a mirror. The screens carry the attributed seed (bold and indexed-red text, a curly-underlined cell, margins 2..6) that every exchange must leave untouched.
import { planFeeds } from '../graded-plan.mjs';
import { xsvContracts, xsvObservation, gradeXsvCase, replayXsvPinned, xsvProbeRow } from '../xsv-contracts.mjs';
import { xsvAdapterId } from '../xsv-grading-collector.mjs';
import { syntheticColEditScreen, colEditCols } from './col-edit-evidence.mjs';
import { paletteBinding, colorCell, plainCell } from './sgr-palette-evidence.mjs';
import { seedRows, frame, alternate } from './devq-evidence.mjs';

const deviceAttribute = '\x1b[?62;4;22c';
const lastColumn = colEditCols - 1;
/** The baseline stored state of every role, and the way a role differs from it: `wrap`, `origin`, `margin` (DECLRMM) and `cursor` (visible) overrides, the cursor position (`at`, default home), whether
 *  the probe character stands in the last column and whether a wrap is pending. */
const baseline = Object.freeze({ wrap: true, origin: false, margin: false, cursor: true });
const changed = Object.freeze({ wrap: false, origin: true, margin: true, cursor: false });
const nameOf = Object.freeze({ wrap: 'wrap', origin: 'origin', 'margin-mode': 'margin', cursor: 'cursor' });
const shared = Object.freeze({
    baseline: {}, 'modes-change': { ...changed, at: [1, 0] }, pinned: {}, 'after-pinned': {},
    'only-listed-modes': { cursor: false }, 'several-modes': { wrap: false, cursor: false },
    'wrap-effect-on': { probe: true, pending: true, at: [xsvProbeRow, lastColumn] }, 'wrap-effect-off': { wrap: false, probe: true, pending: false, at: [xsvProbeRow, lastColumn] },
    'unlisted-values': {}, 'restore-twice': {}, 'cursor-1048': { at: [2, 2] }, 'origin-restore-homes': {},
});
/** The state each role demands, written out by hand: the four modes each restored to its saved value in both polarities, and the cases' own groups. */
function expectation(caseId, role) {
    if (role in shared) return shared[role];
    for (const [name, key] of Object.entries(nameOf)) {
        if (role === `${name}-restores-default`) return {};
        if (role === `${name}-restores-changed`) return { [key]: changed[key], ...(key === 'origin' ? { at: [1, 0] } : {}) };
    }
    if (role === 'second-save-replaces') return {};
    if (role === 'not-scosc') return { at: [2, 2] };
    if (role === 'not-decstbm-or-scorc') return { at: [4, 4] };
    throw new Error(`No hand-written expectation for ${caseId}/${role}.`);
}
/** The reply list of every report role: both sequences are silent. */
const exchanges = Object.freeze(Object.fromEntries(xsvContracts.map(contract => [contract.caseId, Object.freeze(Object.fromEntries(contract.roles.map(role => [role, []])))])));

/** One synthetic screen of a role: the seed, the stored modes, the cursor and, for a probe role, the probe character alone in the last column. */
function screenOf(expected, binding, nonce) {
    const state = { ...baseline, ...expected };
    const rows = [...seedRows(), [], [], [], []];
    rows[xsvProbeRow] = state.probe ? [...Array.from({ length: lastColumn }, () => plainCell(' ')), colorCell('z')] : [];
    const screen = alternate(syntheticColEditScreen(rows, state.at ?? [0, 0], { margins: [1, 5], origin: state.origin }, [], binding), nonce);
    return { ...screen, cursorVisible: state.cursor, bufferState: { ...screen.bufferState, autoWrap: state.wrap, pendingWrap: state.pending ?? false },
        horizontalMargins: { ...screen.horizontalMargins, leftRightMarginMode: state.margin } };
}

/** The screens and reply lists of every read of one case. `mutate(caseId, role, read)` may return a changed read (a replaced screen or reply list) and `mutateState(caseId, role, expected)` a changed
 *  expectation (which persists in no later read: every group starts from the baseline again). */
export function syntheticXsvScreens(contract, binding = paletteBinding(), mutate = null, runId = 'a'.repeat(32), mutateState = null) {
    const nonce = 'NTILDE_XSV_' + runId;
    return contract.roles.map(role => {
        const expected = role === 'initial' ? {} : expectation(contract.caseId, role);
        const screen = screenOf(mutateState?.(contract.caseId, role, expected) ?? expected, binding, nonce);
        const fresh = () => ({ screen, replies: [...(exchanges[contract.caseId][role] ?? [])] });
        const read = mutate?.(contract.caseId, role, fresh()) ?? fresh();
        return { role, screen: read.screen, replies: read.replies };
    });
}

/** A complete, valid synthetic run: frames, worker run log and adapter facts. */
export function syntheticXsvEvidence({ runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64), binding = paletteBinding(), mutate = null, mutateState = null, finishBinding = null } = {}) {
    const nonce = 'NTILDE_XSV_' + runId;
    const bare = syntheticColEditScreen([], [0, 0], undefined, [], binding);
    // The main screen carries DECLRMM off with full-width margins, like every observed screen.
    const main = { ...bare, horizontalMargins: { leftRightMarginMode: false, leftMargin: 0, rightMargin: bare.cols - 1 }, cursorMode: { style: 0, blink: true, isDefault: true } };
    const bound = alternate(syntheticColEditScreen(seedRows(), [0, 0], { margins: [1, 5] }, [], binding), nonce);
    bound.cursorRow = 0;
    bound.cursorCol = nonce.length;
    const frames = [frame(0, 'xsv-start', main, null), frame(1, 'xsv-bind', bound, deviceAttribute)];
    const cases = [];
    for (const contract of xsvContracts) {
        const reads = syntheticXsvScreens(contract, binding, mutate, runId, mutateState);
        const records = reads.map((read, index) => {
            const isReport = contract.roles[index] !== 'initial';
            const exchange = isReport ? { barrier: deviceAttribute, replies: read.replies } : undefined;
            frames.push(frame(frames.length, 'xsv-read', read.screen, deviceAttribute, exchange ? structuredClone(exchange) : undefined));
            return { kind: isReport ? 'report' : 'cells', cursor: [read.screen.cursorRow, read.screen.cursorCol], ...(isReport ? { report: structuredClone(exchange) } : {}) };
        });
        const feeds = planFeeds(contract);
        const pinnedRead = reads[contract.pinnedRead];
        const position = { row: pinnedRead.screen.cursorRow + 1, col: pinnedRead.screen.cursorCol + 1 };
        const termless = { probeId: contract.caseId, ...replayXsvPinned(contract.caseId, 'termless', { replies: pinnedRead.replies.join(''), barrier: deviceAttribute }) };
        const terminal = { probeId: contract.caseId, ...replayXsvPinned(contract.caseId, 'terminal', { position }) };
        cases.push({ caseId: contract.caseId, feeds, barriers: feeds.map(() => deviceAttribute), reads: records, pinned: { termless, terminal } });
    }
    const finishBare = finishBinding === null ? null : syntheticColEditScreen([], [0, 0], undefined, [], finishBinding);
    const finishMain = finishBare === null ? main
        : { ...finishBare, horizontalMargins: { leftRightMarginMode: false, leftMargin: 0, rightMargin: finishBare.cols - 1 }, cursorMode: { style: 0, blink: true, isDefault: true } };
    frames.push(frame(frames.length, 'xsv-finish', finishMain, deviceAttribute));
    const run = { schemaVersion: 1, adapterId: xsvAdapterId, probeIds: xsvContracts.map(item => item.caseId), cases };
    const adapter = { id: xsvAdapterId, derivedFromAdapterId: 'ntilde-owned-left-right-margins-grading-v1', bundleSha256,
        upstreamBundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6' };
    return { frames, run, runId, adapter };
}

/** The independent contract verdict for one synthetic case. */
export function syntheticXsvGrade(caseId, binding = paletteBinding(), mutate = null, mutateState = null) {
    const contract = xsvContracts.find(item => item.caseId === caseId);
    const reads = syntheticXsvScreens(contract, binding, mutate, undefined, mutateState);
    return gradeXsvCase(contract, xsvObservation(contract, reads.map(read => read.screen), reads.map(read => read.replies), 'NTILDE_XSV_' + 'a'.repeat(32)));
}

export { colEditRows as xsvRows, colEditCols as xsvCols } from './col-edit-evidence.mjs';
export { exchanges as syntheticXsvExchanges, expectation as syntheticXsvExpectation };
