// Synthetic, fully valid device-query grading evidence for portable tests. Every frame, run-log entry and pinned result is produced through the same production validators used for
// real measurements. The expected replies are hand-written literal bytes, a second oracle independent of the contract's model, so the fixture is a cross-check, not a mirror. The screens
// carry the attributed seed (bold and indexed-red text, a curly-underlined cell, margins 2..6) that every exchange must leave untouched.
import { snapshotHash } from '../agent-observer.mjs';
import { planFeeds } from '../graded-plan.mjs';
import { devqContracts, devqObservation, gradeDevqCase, replayDevqPinned } from '../devq-contracts.mjs';
import { devqAdapterId } from '../devq-grading-collector.mjs';
import { syntheticColEditScreen, paletteBinding, colorCell } from './col-edit-evidence.mjs';
import { paletteChannel } from './sgr-palette-evidence.mjs';

const paneId = '0f0e0d0c-0b0a-4009-8008-700600500400';
const deviceAttribute = '\x1b[?62;4;22c';
const st = '\x1b\\';
/** The hex of the documented session TERM xterm-256color, written out by hand. */
const term = '787465726D2D323536636F6C6F72';
const name = '7465726d4e616d65';
const dcs = body => '\x1bP' + body + st;
const value = dcs(`1+R${name}=${term}`);
const rgbBase = ['\x1b]11;rgb:1111/2222/3333' + st, '\x1b]4;5;rgb:4444/5555/6666' + st];
const rgbSecond = ['\x1b]11;rgb:aaaa/bbbb/cccc' + st, '\x1b]4;5;rgb:dddd/eeee/ffff' + st];
const report = (depth, deepest) => `\x1b[?${depth};${deepest}#Q`;
const title = (code, text) => `\x1b]${code}${text}${st}`;

/** The expected reply list of every report role of every case, written out by hand (the contract's model is a separate implementation). */
const exchanges = Object.freeze({
    'device.xtgetxres': {
        pinned: [value],
        'echo-upper': [dcs(`1+R7465726D4E616D65=${term}`)],
        unknown: [dcs('0+R666f6f')],
        'case-and-class': [dcs('0+R7465726d6e616d65'), dcs('0+R5465726d4e616d65')],
        'list-known': [dcs(`1+R${name}=${term};${name}=${term}`)],
        'list-stop': [dcs(`1+R${name}=${term};666f6f`)],
        'list-first-unknown': [dcs('0+R666f6f')],
        'malformed-odd': [value], 'malformed-hex': [value], 'malformed-empty': [value],
    },
    'device.xtreportcolors': {
        pinned: [report(0, 0)],
        'set-base': rgbBase,
        'push-1': [report(1, 1)],
        'set-second': rgbSecond,
        'push-2': [report(2, 2)],
        'set-third': ['\x1b]11;rgb:0101/0202/0303' + st, '\x1b]4;5;rgb:0a0a/0b0b/0c0c' + st],
        'pop-1': [report(1, 2), ...rgbSecond],
        'pop-2': [report(0, 2), ...rgbBase],
        'pop-empty': [report(0, 2), ...rgbBase],
        'slot-store': [report(4, 2)],
        'set-loud': ['\x1b]11;rgb:ffff/0000/0000' + st, '\x1b]4;5;rgb:0000/ffff/0000' + st],
        'slot-restore': [report(3, 2), ...rgbBase],
        'kitty-independent': [report(3, 2), report(3, 2), ...rgbBase],
        'out-of-range': [report(3, 2)],
        fill: [report(10, 10)],
        spellings: [report(10, 10)],
        drain: [report(0, 10)],
    },
    'device.xtwinops-20': {
        pinned: [title('L', 'test-icon')],
        distinct: [title('L', 'icon-a1'), title('l', 'title-b2')],
        'osc0-both': [title('L', 'both-c3'), title('l', 'both-c3')],
        empty: [title('L', '')],
        semicolons: [title('L', 'a;b c')],
        stack: [title('L', 'stk-1'), title('L', 'stk-2'), title('L', 'stk-1')],
        stable: [title('L', 'same-d4'), title('L', 'same-d4')],
        'control-character': [title('L', '')],
        spellings: [title('L', 'test-icon')],
    },
    'device.xtwinops-21': {
        pinned: [title('l', 'test-title')],
        distinct: [title('l', 'title-b2'), title('L', 'icon-a1')],
        'osc0-both': [title('l', 'both-c3'), title('L', 'both-c3')],
        empty: [title('l', '')],
        semicolons: [title('l', 'a;b c')],
        stack: [title('l', 'stk-1'), title('l', 'stk-2'), title('l', 'stk-1')],
        stable: [title('l', 'same-d4'), title('l', 'same-d4')],
        'control-character': [title('l', '')],
        spellings: [title('l', 'test-title')],
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

/** The screens and reply lists of every read of one case; `mutate(role, read)` may return a changed read to build negative evidence. */
export function syntheticDevqScreens(contract, binding = paletteBinding(), mutate = null, runId = 'a'.repeat(32)) {
    const nonce = 'NTILDE_DQ_' + runId;
    // The plan's static reseed homes the cursor: every read (and every exchange) observes it at home over the nonce row.
    const base = alternate(syntheticColEditScreen(seedRows(), [0, 0], { margins: [1, 5] }, [], binding), nonce);
    return contract.roles.map(role => {
        const fresh = () => ({ screen: structuredClone(base), replies: [...(exchanges[contract.caseId][role] ?? [])] });
        const read = mutate?.(contract.caseId, role, fresh()) ?? fresh();
        return { role, screen: read.screen, replies: read.replies };
    });
}

/** A complete, valid synthetic run: frames, worker run log and adapter facts. `mutate(caseId, role, read)` may return a changed read. */
export function syntheticDevqEvidence({ runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64), binding = paletteBinding(), mutate = null, recolored = null, finishBinding = null } = {}) {
    const nonce = 'NTILDE_DQ_' + runId;
    const recolor = recolored ?? { caseId: null, binding };
    const bare = syntheticColEditScreen([], [0, 0], undefined, [], binding);
    // The main screen carries DECLRMM off with full-width margins, like every observed screen.
    const main = { ...bare, horizontalMargins: { leftRightMarginMode: false, leftMargin: 0, rightMargin: bare.cols - 1 }, cursorMode: { style: 0, blink: true, isDefault: true } };
    const bound = alternate(syntheticColEditScreen(seedRows(), [0, 0], { margins: [1, 5] }, [], binding), nonce);
    bound.cursorRow = 0;
    bound.cursorCol = nonce.length;
    const frames = [frame(0, 'dvq-start', main, null), frame(1, 'dvq-bind', bound, deviceAttribute)];
    const cases = [];
    for (const contract of devqContracts) {
        const reads = syntheticDevqScreens(contract, recolor.caseId === contract.caseId ? recolor.binding : binding, mutate, runId);
        const records = reads.map((read, index) => {
            const isReport = contract.roles[index] !== 'initial';
            const exchange = isReport ? { barrier: deviceAttribute, replies: read.replies } : undefined;
            frames.push(frame(frames.length, 'dvq-read', read.screen, deviceAttribute, exchange ? structuredClone(exchange) : undefined));
            return { kind: isReport ? 'report' : 'cells', cursor: [read.screen.cursorRow, read.screen.cursorCol], ...(isReport ? { report: structuredClone(exchange) } : {}) };
        });
        const feeds = planFeeds(contract);
        const pinnedRead = reads[contract.pinnedRead];
        const termless = { probeId: contract.caseId, ...replayDevqPinned(contract.caseId, pinnedRead.replies.join('')) };
        const terminal = { probeId: contract.caseId, ...replayDevqPinned(contract.caseId, pinnedRead.replies.join('') + deviceAttribute) };
        cases.push({ caseId: contract.caseId, feeds, barriers: feeds.map(() => deviceAttribute), reads: records, pinned: { termless, terminal } });
    }
    const finishBare = finishBinding === null ? null : syntheticColEditScreen([], [0, 0], undefined, [], finishBinding);
    const finishMain = finishBare === null ? main
        : { ...finishBare, horizontalMargins: { leftRightMarginMode: false, leftMargin: 0, rightMargin: finishBare.cols - 1 }, cursorMode: { style: 0, blink: true, isDefault: true } };
    frames.push(frame(frames.length, 'dvq-finish', finishMain, deviceAttribute));
    const run = { schemaVersion: 1, adapterId: devqAdapterId, probeIds: devqContracts.map(item => item.caseId), cases };
    const adapter = { id: devqAdapterId, derivedFromAdapterId: 'ntilde-owned-left-right-margins-grading-v1', bundleSha256,
        upstreamBundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6' };
    return { frames, run, runId, adapter };
}

/** The independent contract verdict for one synthetic case. */
export function syntheticDevqGrade(caseId, binding = paletteBinding(), mutate = null) {
    const contract = devqContracts.find(item => item.caseId === caseId);
    const reads = syntheticDevqScreens(contract, binding, mutate);
    return gradeDevqCase(contract, devqObservation(contract, reads.map(read => read.screen), reads.map(read => read.replies), 'NTILDE_DQ_' + 'a'.repeat(32)));
}

export { colEditRows as devqRows, colEditCols as devqCols } from './col-edit-evidence.mjs';
export { exchanges as syntheticDevqExchanges };
