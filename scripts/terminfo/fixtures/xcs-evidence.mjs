// Synthetic, fully valid xterm color-stack grading evidence for portable tests. Every frame, run-log entry and pinned result is produced through the same production validators used for real
// measurements. The expected replies are hand-written literal bytes, a second oracle independent of the contract's model, so the fixture is a cross-check, not a mirror. The screens carry the
// attributed seed (bold and indexed-red text, a curly-underlined cell, margins 2..6) that every exchange must leave untouched.
import { snapshotHash } from '../agent-observer.mjs';
import { planFeeds } from '../graded-plan.mjs';
import { xcsContracts, xcsObservation, gradeXcsCase, replayXcsPinned } from '../xcs-contracts.mjs';
import { xcsAdapterId } from '../xcs-grading-collector.mjs';
import { syntheticColEditScreen, paletteBinding, colorCell } from './col-edit-evidence.mjs';
import { paletteChannel } from './sgr-palette-evidence.mjs';

const paneId = '0f0e0d0c-0b0a-4009-8008-700600500400';
const deviceAttribute = '\x1b[?62;4;22c';
const st = '\x1b\\';
const osc = (code, rgb) => `\x1b]${code};rgb:${rgb}${st}`;
/** One literal core-color reply list: foreground, background, cursor, palette 5, each as the spelled rgb of the hand-picked 8-bit colors. */
const core = (fg, bg, cursor, p5) => [osc('10', fg), osc('11', bg), osc('12', cursor), osc('4;5', p5)];
/** The hand-picked host default colors of the synthetic App, and sets A, B and C of the contract (#102030/#112233/#a1b2c3/#445566 and so on), spelled out by hand. */
const base = core('cccc/cccc/cccc', '1e1e/1e1e/1e1e', 'ffff/ffff/ffff', 'aaaa/0000/aaaa');
const setA = core('1010/2020/3030', '1111/2222/3333', 'a1a1/b2b2/c3c3', '4444/5555/6666');
const setB = core('c0c0/d0d0/e0e0', 'aaaa/bbbb/cccc', '3a3a/2b2b/1c1c', 'dddd/eeee/ffff');
const setC = core('0505/0606/0707', '0101/0202/0303', 'f0f0/e0e0/d0d0', '0a0a/0b0b/0c0c');
const extendedX = [osc('4;200', '1212/3434/5656')];

/** The expected reply list of every report role of every case, written out by hand. */
const exchanges = Object.freeze({
    'modes.xtpushcolors': {
        baseline: base, pinned: [], 'after-pinned': base, 'set-A': setA, 'push-silent': setA, 'set-B': setB, 'pop-recovers-pushed': setA, 'snapshot-is-copy': setA,
        'store-slot-2': setA, 'set-B-again': setB, 'store-slot-1': setB, 'set-C': setC, 'restore-slot-2': setA, 'restore-slot-1': setB, 'extended-palette': extendedX,
    },
    'modes.xtpopcolors': {
        baseline: base, pinned: [], 'after-pinned': base, 'push-baseline': base, 'set-A': setA, 'pop-restores-default': base, 'push-A': setA, 'push-B': setB, 'set-C': setC,
        'pop-lifo-1': setB, 'pop-lifo-2': setA, 'pop-empty': setA, 'store-slot-1': setA, 'set-B': setB, 'restore-slot-1': setA, 'extended-palette': extendedX,
    },
});

/** The attributed seed rows of the measured screen. */
const seedRows = () => [
    [],
    [colorCell('N', { attrs: ['bold'] }), colorCell('t', { attrs: ['bold'] }), colorCell('i', { attrs: ['bold'] }),
        colorCell('l', { attrs: ['bold'] }), colorCell('d', { attrs: ['bold'] }), colorCell('e', { attrs: ['bold'] }),
        colorCell('X', { attrs: ['bold'], fg: paletteChannel(1) }), colorCell('T', { attrs: ['bold'], fg: paletteChannel(1) })],
    [],
    [colorCell('U', { underlineStyle: 3 })],
];

/** One journaled frame with its digest. */
const frame = (index, kind, screen, barrier, reportValue) => ({ index, kind, paneId, barrier, screen, sha256: snapshotHash(screen), ...(reportValue ? { report: reportValue } : {}) });
/** A synthetic screen on the alternate screen the suite runs on, with the suite's margins (vertical and DECLRMM off/full-width). */
const alternate = (screen, nonce) => ({
    ...screen,
    ...(nonce === undefined ? {} : { lines: [nonce, ...screen.lines.slice(1)] }),
    bufferState: { ...screen.bufferState, scrollTop: 1, scrollBottom: 5, alternateScreen: true },
    horizontalMargins: { leftRightMarginMode: false, leftMargin: 0, rightMargin: screen.cols - 1 },
    cursorMode: { style: 0, blink: true, isDefault: true },
});

/** The screens and reply lists of every read of one case; `mutate(caseId, role, read)` may return a changed read to build negative evidence. */
export function syntheticXcsScreens(contract, binding = paletteBinding(), mutate = null, runId = 'a'.repeat(32)) {
    const nonce = 'NTILDE_XCS_' + runId;
    // The plan's static reseed homes the cursor: every read (and every exchange) observes it at home over the nonce row.
    const seed = alternate(syntheticColEditScreen(seedRows(), [0, 0], { margins: [1, 5] }, [], binding), nonce);
    return contract.roles.map(role => {
        const fresh = () => ({ screen: structuredClone(seed), replies: [...(exchanges[contract.caseId][role] ?? [])] });
        const read = mutate?.(contract.caseId, role, fresh()) ?? fresh();
        return { role, screen: read.screen, replies: read.replies };
    });
}

/** A complete, valid synthetic run: frames, worker run log and adapter facts. `mutate(caseId, role, read)` may return a changed read. */
export function syntheticXcsEvidence({ runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64), binding = paletteBinding(), mutate = null, finishBinding = null } = {}) {
    const nonce = 'NTILDE_XCS_' + runId;
    const bare = syntheticColEditScreen([], [0, 0], undefined, [], binding);
    // The main screen carries DECLRMM off with full-width margins, like every observed screen.
    const main = { ...bare, horizontalMargins: { leftRightMarginMode: false, leftMargin: 0, rightMargin: bare.cols - 1 }, cursorMode: { style: 0, blink: true, isDefault: true } };
    const bound = alternate(syntheticColEditScreen(seedRows(), [0, 0], { margins: [1, 5] }, [], binding), nonce);
    bound.cursorRow = 0;
    bound.cursorCol = nonce.length;
    const frames = [frame(0, 'xcs-start', main, null), frame(1, 'xcs-bind', bound, deviceAttribute)];
    const cases = [];
    for (const contract of xcsContracts) {
        const reads = syntheticXcsScreens(contract, binding, mutate, runId);
        const records = reads.map((read, index) => {
            const isReport = contract.roles[index] !== 'initial';
            const exchange = isReport ? { barrier: deviceAttribute, replies: read.replies } : undefined;
            frames.push(frame(frames.length, 'xcs-read', read.screen, deviceAttribute, exchange ? structuredClone(exchange) : undefined));
            return { kind: isReport ? 'report' : 'cells', cursor: [read.screen.cursorRow, read.screen.cursorCol], ...(isReport ? { report: structuredClone(exchange) } : {}) };
        });
        const feeds = planFeeds(contract);
        const pinnedRead = reads[contract.pinnedRead];
        const position = { row: pinnedRead.screen.cursorRow + 1, col: pinnedRead.screen.cursorCol + 1 };
        const termless = { probeId: contract.caseId, ...replayXcsPinned(contract.caseId, 'termless', { replies: pinnedRead.replies.join(''), barrier: deviceAttribute }) };
        const terminal = { probeId: contract.caseId, ...replayXcsPinned(contract.caseId, 'terminal', { position }) };
        cases.push({ caseId: contract.caseId, feeds, barriers: feeds.map(() => deviceAttribute), reads: records, pinned: { termless, terminal } });
    }
    const finishBare = finishBinding === null ? null : syntheticColEditScreen([], [0, 0], undefined, [], finishBinding);
    const finishMain = finishBare === null ? main
        : { ...finishBare, horizontalMargins: { leftRightMarginMode: false, leftMargin: 0, rightMargin: finishBare.cols - 1 }, cursorMode: { style: 0, blink: true, isDefault: true } };
    frames.push(frame(frames.length, 'xcs-finish', finishMain, deviceAttribute));
    const run = { schemaVersion: 1, adapterId: xcsAdapterId, probeIds: xcsContracts.map(item => item.caseId), cases };
    const adapter = { id: xcsAdapterId, derivedFromAdapterId: 'ntilde-owned-left-right-margins-grading-v1', bundleSha256,
        upstreamBundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6' };
    return { frames, run, runId, adapter };
}

/** The independent contract verdict for one synthetic case. */
export function syntheticXcsGrade(caseId, binding = paletteBinding(), mutate = null) {
    const contract = xcsContracts.find(item => item.caseId === caseId);
    const reads = syntheticXcsScreens(contract, binding, mutate);
    return gradeXcsCase(contract, xcsObservation(contract, reads.map(read => read.screen), reads.map(read => read.replies), 'NTILDE_XCS_' + 'a'.repeat(32)));
}

export { colEditRows as xcsRows, colEditCols as xcsCols } from './col-edit-evidence.mjs';
export { exchanges as syntheticXcsExchanges };
