// Synthetic, fully valid XTGETTCAP grading evidence for portable tests. Every frame, run-log entry and pinned result is produced
// through the same production validators used for real measurements. The expected replies are hand-written literal bytes, a second
// oracle independent of the contract's model, so the fixture is a cross-check, not a mirror. The screens carry the attributed seed
// (bold and indexed-red text, a curly-underlined cell, margins 2..6) that every exchange must leave untouched.
import { snapshotHash } from '../agent-observer.mjs';
import { planFeeds } from '../graded-plan.mjs';
import { xtgettcapContracts, xtgettcapObservation, gradeXtgettcapCase, replayXtgettcapPinned } from '../xtgettcap-contracts.mjs';
import { xtgettcapAdapterId } from '../xtgettcap-grading-collector.mjs';
import { syntheticColEditScreen, paletteBinding, colorCell } from './col-edit-evidence.mjs';
import { paletteChannel } from './sgr-palette-evidence.mjs';

const paneId = '0f0e0d0c-0b0a-4009-8008-700600500400';
const deviceAttribute = '\x1b[?62;4;22c';
const st = '\x1b\\';
const refusal = '\x1bP0+r' + st;
const coValue = '\x1bP1+r436f=323536' + st;
const smulxValue = '\x1bP1+r536d756c78=1B5B343A25703125646D' + st;

/** The expected reply list of every report role, written out by hand (the contract's model is a separate implementation). */
const exchanges = Object.freeze({
    pinned: [refusal],
    'numeric-co': [coValue],
    'numeric-colors': ['\x1bP1+r636f6c6f7273=323536' + st],
    'flag-rgb': ['\x1bP1+r524742' + st],
    'flag-tc': ['\x1bP1+r5463' + st],
    'string-smulx': [smulxValue],
    'list-order': [coValue, refusal, '\x1bP1+r5463' + st, coValue],
    'list-witness': [refusal, coValue],
    'unavailable-kcuu1': [refusal],
    unknown: [refusal],
    'case-co': [refusal],
    'echo-upper': ['\x1bP1+r436F=323536' + st],
    'malformed-odd': [refusal],
    'recovery-tc': ['\x1bP1+r5463' + st],
    'malformed-hex': [refusal],
    'recovery-co': [coValue],
    'malformed-empty': [refusal],
    'recovery-pinned': [refusal],
    'malformed-mixed': [refusal],
    'recovery-string': [smulxValue],
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
const frame = (index, kind, screen, barrier, report) => ({ index, kind, paneId, barrier, screen, sha256: snapshotHash(screen), ...(report ? { report } : {}) });
/** A synthetic screen on the alternate screen the suite runs on, with the suite's margins. */
const alternate = (screen, nonce) => ({
    ...screen,
    ...(nonce === undefined ? {} : { lines: [nonce, ...screen.lines.slice(1)] }),
    bufferState: { ...screen.bufferState, scrollTop: 1, scrollBottom: 5, alternateScreen: true },
});

/** The screens and reply lists of every read; `mutate(role, read)` may return a changed read to build negative evidence. */
export function syntheticXtgettcapScreens(binding = paletteBinding(), mutate = null, runId = 'a'.repeat(32)) {
    const contract = xtgettcapContracts[0];
    const nonce = 'NTILDE_XG_' + runId;
    // The plan's static reseed homes the cursor: every read (and every exchange) observes it at home over the nonce row.
    const base = alternate(syntheticColEditScreen(seedRows(), [0, 0], { margins: [1, 5] }, [], binding), nonce);
    return contract.roles.map(role => {
        const read = mutate?.(role, { screen: structuredClone(base), replies: [...(exchanges[role] ?? [])] }) ?? { screen: structuredClone(base), replies: [...(exchanges[role] ?? [])] };
        return { role, screen: read.screen, replies: read.replies };
    });
}

/** A complete, valid synthetic run: frames, worker run log and adapter facts. */
export function syntheticXtgettcapEvidence(runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64), binding = paletteBinding(), mutate = null) {
    const contract = xtgettcapContracts[0];
    const nonce = 'NTILDE_XG_' + runId;
    const main = syntheticColEditScreen([], [0, 0], undefined, [], binding);
    const bound = alternate(syntheticColEditScreen(seedRows(), [0, 0], { margins: [1, 5] }, [], binding), nonce);
    bound.cursorRow = 0;
    bound.cursorCol = nonce.length;
    const frames = [frame(0, 'xgtc-start', main, null), frame(1, 'xgtc-bind', bound, deviceAttribute)];
    const reads = syntheticXtgettcapScreens(binding, mutate, runId);
    const records = reads.map((read, index) => {
        const isReport = contract.roles[index] !== 'initial';
        const report = isReport ? { barrier: deviceAttribute, replies: read.replies } : undefined;
        if (isReport) frames.push(frame(frames.length, 'xgtc-read', read.screen, deviceAttribute, structuredClone(report)));
        else frames.push(frame(frames.length, 'xgtc-read', read.screen, deviceAttribute));
        return { kind: isReport ? 'report' : 'cells', cursor: [read.screen.cursorRow, read.screen.cursorCol], ...(isReport ? { report: structuredClone(report) } : {}) };
    });
    frames.push(frame(frames.length, 'xgtc-finish', main, deviceAttribute));
    const feeds = planFeeds(contract);
    const pinnedRead = reads[contract.pinnedRead];
    const termless = { probeId: contract.caseId, ...replayXtgettcapPinned(pinnedRead.replies.join('')) };
    const terminal = { probeId: contract.caseId, ...replayXtgettcapPinned(pinnedRead.replies.join('') + deviceAttribute) };
    const run = { schemaVersion: 1, adapterId: xtgettcapAdapterId, probeIds: [contract.caseId],
        cases: [{ caseId: contract.caseId, feeds, barriers: feeds.map(() => deviceAttribute), reads: records, pinned: { termless, terminal } }] };
    const adapter = { id: xtgettcapAdapterId, derivedFromAdapterId: 'ntilde-owned-title-stack-grading-v1', bundleSha256,
        upstreamBundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6' };
    return { frames, run, runId, adapter };
}

/** The independent contract verdict for the synthetic case. */
export function syntheticXtgettcapGrade(binding = paletteBinding(), mutate = null) {
    const contract = xtgettcapContracts[0];
    const reads = syntheticXtgettcapScreens(binding, mutate);
    return gradeXtgettcapCase(contract,
        xtgettcapObservation(contract, reads.map(read => read.screen), reads.map(read => read.replies), 'NTILDE_XG_' + 'a'.repeat(32)));
}

export { colEditRows as xtgettcapRows, colEditCols as xtgettcapCols } from './col-edit-evidence.mjs';
export { exchanges as syntheticXtgettcapExchanges };
