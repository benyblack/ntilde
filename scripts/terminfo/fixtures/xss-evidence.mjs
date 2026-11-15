// Synthetic, fully valid xterm SGR-stack grading evidence for portable tests. Every frame, run-log entry and pinned result is produced through the same production validators used for real
// measurements. The expected probe cells are hand-written specs (colour channels, flags and underline styles spelled out per role), a second oracle independent of the contract's model, so the
// fixture is a cross-check, not a mirror. The screens carry the attributed seed (bold and indexed-red text, a curly-underlined cell, margins 2..6) that every exchange must leave untouched, and
// the probe row accumulates one stored probe cell per probe group, as the real run does.
import { planFeeds } from '../graded-plan.mjs';
import { xssContracts, xssObservation, gradeXssCase, replayXssPinned, xssProbeRow } from '../xss-contracts.mjs';
import { xssAdapterId } from '../xss-grading-collector.mjs';
import { syntheticColEditScreen } from './col-edit-evidence.mjs';
import { paletteBinding, colorCell, paletteChannel, literalChannel } from './sgr-palette-evidence.mjs';
import { seedRows, frame, alternate } from './devq-evidence.mjs';

const deviceAttribute = '\x1b[?62;4;22c';
const everyAttribute = ['bold', 'dim', 'italic', 'inverse', 'hidden', 'strikethrough', 'blink'];
/** The three hand-picked pens of the contract, spelled out as stored cell specs: A (every attribute, palette 196 over literal 10,20,30), B (nothing, literal 200,100,50 over palette 22), C (bold italic, palette 5 over 6). */
const all = { attrs: everyAttribute, underlineStyle: 1, fg: paletteChannel(196), bg: literalChannel(10, 20, 30) };
const clear = { fg: literalChannel(200, 100, 50), bg: paletteChannel(22) };
const mid = { attrs: ['bold', 'italic'], fg: paletteChannel(5), bg: paletteChannel(6) };
const clearPlus = attrs => ({ ...clear, attrs });

/** The demanded probe cell of every probe role of every case, written out by hand. */
const probes = Object.freeze({
    'modes.xtpushsgr': {
        baseline: {}, 'after-pinned': {}, 'set-A': all, 'push-silent': all, 'set-B': clear, 'pop-recovers-pushed': all, 'snapshot-is-copy': all,
        'numbered-1': clearPlus(['bold']), 'numbered-2': clearPlus(['dim']), 'numbered-3': clearPlus(['italic']), 'numbered-4': { ...clear, underlineStyle: 1 },
        'numbered-5': clearPlus(['blink']), 'numbered-7': clearPlus(['inverse']), 'numbered-8': clearPlus(['hidden']), 'numbered-9': clearPlus(['strikethrough']),
        'numbered-21': { ...clear, underlineStyle: 2 }, 'numbered-30': { ...clear, fg: paletteChannel(196) }, 'numbered-31': { ...clear, bg: literalChannel(10, 20, 30) },
        'depth-top': { fg: paletteChannel(50) }, 'depth-bottom': { fg: paletteChannel(41) }, 'overflow-eleventh': { fg: paletteChannel(41) }, 'unlisted-value': clear,
        'unstacked-state': { attrs: ['overline'], underlineStyle: 1, underlineColorIndex: 99 },
    },
    'modes.xtpopsgr': {
        baseline: {}, 'after-pinned': {}, 'push-baseline': {}, 'set-A': all, 'pop-restores-default': {}, 'push-A': all, 'push-B': clear, 'set-C': mid, 'pop-lifo-1': clear,
        'pop-lifo-2': all, 'pop-empty': all, 'pop-only-saved': clearPlus(['bold']),
    },
});
/** The reply list of every report role: both sequences are silent. */
const exchanges = Object.freeze(Object.fromEntries(Object.entries(probes).map(([caseId, roles]) => [caseId,
    { pinned: [], ...Object.fromEntries(Object.keys(roles).map(role => [role, []])) }])));

/** The screens and reply lists of every read of one case. `mutate(caseId, role, read)` may return a changed read (a replaced screen or reply list) and `mutateCell(caseId, role, spec)` a changed probe
 *  cell spec (which persists in the later reads, as a wrong cell would). */
export function syntheticXssScreens(contract, binding = paletteBinding(), mutate = null, runId = 'a'.repeat(32), mutateCell = null, leftover = '') {
    const nonce = 'NTILDE_XSS_' + runId;
    // `leftover` is probe-row text an earlier case left behind: it is on every screen of this case, as a missing erase would leave it.
    const cells = [...leftover].map(char => colorCell(char));
    let cursor = [0, 0];
    return contract.roles.map(role => {
        const group = contract.groups.find(item => item.role === role);
        if (group?.probe) {
            const spec = { ...probes[contract.caseId][role] };
            cells.push(colorCell(group.probe.char, mutateCell?.(contract.caseId, role, spec) ?? spec));
            cursor = [xssProbeRow, group.probe.col + 1];
        }
        const rows = [...seedRows(), [], [], [], []];
        rows[xssProbeRow] = [...cells];
        const screen = alternate(syntheticColEditScreen(rows, cursor, { margins: [1, 5] }, [], binding), nonce);
        const fresh = () => ({ screen, replies: [...(exchanges[contract.caseId][role] ?? [])] });
        const read = mutate?.(contract.caseId, role, fresh()) ?? fresh();
        return { role, screen: read.screen, replies: read.replies };
    });
}

/** A complete, valid synthetic run: frames, worker run log and adapter facts. */
export function syntheticXssEvidence({ runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64), binding = paletteBinding(), mutate = null, mutateCell = null, finishBinding = null } = {}) {
    const nonce = 'NTILDE_XSS_' + runId;
    const bare = syntheticColEditScreen([], [0, 0], undefined, [], binding);
    // The main screen carries DECLRMM off with full-width margins, like every observed screen.
    const main = { ...bare, horizontalMargins: { leftRightMarginMode: false, leftMargin: 0, rightMargin: bare.cols - 1 }, cursorMode: { style: 0, blink: true, isDefault: true } };
    const bound = alternate(syntheticColEditScreen(seedRows(), [0, 0], { margins: [1, 5] }, [], binding), nonce);
    bound.cursorRow = 0;
    bound.cursorCol = nonce.length;
    const frames = [frame(0, 'xss-start', main, null), frame(1, 'xss-bind', bound, deviceAttribute)];
    const cases = [];
    for (const contract of xssContracts) {
        const reads = syntheticXssScreens(contract, binding, mutate, runId, mutateCell);
        const records = reads.map((read, index) => {
            const isReport = contract.roles[index] !== 'initial';
            const exchange = isReport ? { barrier: deviceAttribute, replies: read.replies } : undefined;
            frames.push(frame(frames.length, 'xss-read', read.screen, deviceAttribute, exchange ? structuredClone(exchange) : undefined));
            return { kind: isReport ? 'report' : 'cells', cursor: [read.screen.cursorRow, read.screen.cursorCol], ...(isReport ? { report: structuredClone(exchange) } : {}) };
        });
        const feeds = planFeeds(contract);
        const pinnedRead = reads[contract.pinnedRead];
        const position = { row: pinnedRead.screen.cursorRow + 1, col: pinnedRead.screen.cursorCol + 1 };
        const termless = { probeId: contract.caseId, ...replayXssPinned(contract.caseId, 'termless', { replies: pinnedRead.replies.join(''), barrier: deviceAttribute }) };
        const terminal = { probeId: contract.caseId, ...replayXssPinned(contract.caseId, 'terminal', { position }) };
        cases.push({ caseId: contract.caseId, feeds, barriers: feeds.map(() => deviceAttribute), reads: records, pinned: { termless, terminal } });
    }
    const finishBare = finishBinding === null ? null : syntheticColEditScreen([], [0, 0], undefined, [], finishBinding);
    const finishMain = finishBare === null ? main
        : { ...finishBare, horizontalMargins: { leftRightMarginMode: false, leftMargin: 0, rightMargin: finishBare.cols - 1 }, cursorMode: { style: 0, blink: true, isDefault: true } };
    frames.push(frame(frames.length, 'xss-finish', finishMain, deviceAttribute));
    const run = { schemaVersion: 1, adapterId: xssAdapterId, probeIds: xssContracts.map(item => item.caseId), cases };
    const adapter = { id: xssAdapterId, derivedFromAdapterId: 'ntilde-owned-left-right-margins-grading-v1', bundleSha256,
        upstreamBundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6' };
    return { frames, run, runId, adapter };
}

/** The independent contract verdict for one synthetic case. */
export function syntheticXssGrade(caseId, binding = paletteBinding(), mutate = null, mutateCell = null, leftover = '') {
    const contract = xssContracts.find(item => item.caseId === caseId);
    const reads = syntheticXssScreens(contract, binding, mutate, undefined, mutateCell, leftover);
    return gradeXssCase(contract, xssObservation(contract, reads.map(read => read.screen), reads.map(read => read.replies), 'NTILDE_XSS_' + 'a'.repeat(32)));
}

export { colEditRows as xssRows, colEditCols as xssCols } from './col-edit-evidence.mjs';
export { exchanges as syntheticXssExchanges, probes as syntheticXssProbes };
