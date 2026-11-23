// Synthetic, fully valid DECCOLM grading evidence for portable tests. Every frame, run-log entry and pinned result is produced through the same production validators used for real measurements. The
// expected replies are hand-written literal byte strings, a second oracle independent of the contract's constants, so the fixture is a cross-check, not a mirror. The screens carry the attributed seed
// every exchange must leave untouched, every read carries a kernel grid equal to the App's, and no exchange changes a color, so every frame shares one binding.
import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { planFeeds } from '../graded-plan.mjs';
import { dclContracts, dclContractsVersion, dclCursorAt, dclObservation, dclSuiteId, gradeDclCase, replayDclTermless, replayDclTerminal } from '../dcl-contracts.mjs';
import { adaptDclCollector, dclAdapterId } from '../dcl-grading-collector.mjs';
import { kernelGrid } from '../dcl-capture.mjs';
import { syntheticColEditScreen, paletteBinding } from './col-edit-evidence.mjs';
import { colorCell, plainCell, syntheticSgrPaletteScreen } from './sgr-palette-evidence.mjs';
import { screenSnapshot } from '../agent-observer.mjs';
import { seedRows, frame, alternate } from './devq-evidence.mjs';

const deviceAttribute = '\x1b[?62;4;22c';
/** Mode 3 and mode 40 are not recognized (Pm 0); DECAWM and DECANM are set (Pm 1). Written out by hand. */
const three = '\x1b[?3;0$y', forty = '\x1b[?40;0$y';

/** The expected reply list of every report role, written out by hand. */
const exchanges = Object.freeze({
    recognized: ['\x1b[?7;1$y', '\x1b[?2;1$y'],
    pinned: [three], 'pinned-query': [three], set: [three], reset: [three],
    allow: [three, forty],
    'allow-set': [three], 'allow-reset': [three], 'allow-cycle': [three], 'disallow-set': [three], restored: [three],
    'control-clear': [],
});

/** A `/bin/stty -a` description for a grid, with output processing disabled the way the owned runs prepare the PTY. */
export const sttyDescription = (rows, cols) => `speed 9600 baud; ${rows} rows; ${cols} columns;\nlflags: icanon isig iexten echo echoe echok echoke echoctl\noflags: -opost onlcr -oxtabs`;
/** The canonical kernel record of a grid, exactly as the parent observer builds it. */
export const kernelOf = (rows, cols) => kernelGrid({ tool: '/bin/stty', outputProcessing: false, description: sttyDescription(rows, cols) });
const frameWithKernel = (index, kind, screen, barrier, report, kernel = kernelOf(screen.rows, screen.cols)) => ({ ...frame(index, kind, screen, barrier, report), kernel });

/** The seed rows with the witness the plan writes at the cursor (row 3, column 6, plain unless `witness` says otherwise: a cell written with a pen another exchange changed). */
export const dclSeedRows = (witness = plainCell('W')) => {
    const rows = seedRows();
    rows[dclCursorAt[0]] = [...rows[dclCursorAt[0]], ...Array.from({ length: dclCursorAt[1] - rows[dclCursorAt[0]].length }, () => plainCell(' ')), witness];
    return rows;
};
export { colorCell };

/** The screens, kernel grids and reply lists of every read of the case; `mutate(caseId, role, read)` may return a changed read to build negative evidence. */
export function syntheticDclScreens(contract, binding = paletteBinding(), mutate = null, runId = 'a'.repeat(32)) {
    const nonce = 'NTILDE_DCL_' + runId;
    return contract.roles.map(role => {
        const fresh = () => {
            const screen = role === 'control-clear'
                ? alternate(syntheticColEditScreen([], [0, 0], { margins: [1, 5] }, [], structuredClone(binding)))
                : alternate(syntheticColEditScreen(dclSeedRows(), dclCursorAt, { margins: [1, 5] }, [], structuredClone(binding)), nonce);
            return { screen, kernel: kernelOf(screen.rows, screen.cols), replies: [...(exchanges[role] ?? [])] };
        };
        const read = mutate?.(contract.caseId, role, fresh()) ?? fresh();
        return { role, screen: read.screen, kernel: read.kernel, replies: read.replies };
    });
}

/** A blank, valid alternate-screen state of another grid, for evidence in which the App's grid changed (the content differs too: the grid check is judged first). */
export function resizedDclScreen(rows, cols, binding = paletteBinding()) {
    const screen = syntheticSgrPaletteScreen(Array.from({ length: rows * cols }, () => plainCell(' ')), binding, { rows, cols });
    const draft = { ...screen, lines: Array.from({ length: rows }, () => ''), cursorRow: 0, cursorCol: 0, cellDetails: screen.cellDetails.map(run => ({ ...run, protected: false })),
        bufferState: { scrollTop: 1, scrollBottom: 5, originMode: false, autoWrap: true, insertMode: false, pendingWrap: false, alternateScreen: true, scrollbackLines: 0 },
        history: { startLine: 0, totalLines: 0, lines: [] } };
    // The canonical stored-detail key order is the observer's own.
    const onAlternate = alternate(draft);
    return { ...onAlternate, cellDetails: screenSnapshot(onAlternate, true, true, true, true, true, true, false, true, true).cellDetails };
}

/** The bare main screen every observed screen starts and ends on: DECLRMM off with full-width margins. */
const mainScreen = binding => {
    const bare = syntheticColEditScreen([], [0, 0], undefined, [], binding);
    return { ...bare, horizontalMargins: { leftRightMarginMode: false, leftMargin: 0, rightMargin: bare.cols - 1 }, cursorMode: { style: 0, blink: true, isDefault: true } };
};

/** A complete, valid synthetic run: frames, worker run log and adapter facts. `mutate(caseId, role, read)` may return a changed read. */
export function syntheticDclEvidence({ runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64), binding = paletteBinding(), mutate = null, finishBinding = null } = {}) {
    const nonce = 'NTILDE_DCL_' + runId;
    const bound = alternate(syntheticColEditScreen(seedRows(), [0, 0], { margins: [1, 5] }, [], structuredClone(binding)), nonce);
    bound.cursorRow = 0;
    bound.cursorCol = nonce.length;
    const frames = [frameWithKernel(0, 'dcl-start', mainScreen(binding), null), frameWithKernel(1, 'dcl-bind', bound, deviceAttribute)];
    const cases = [];
    for (const contract of dclContracts) {
        const reads = syntheticDclScreens(contract, binding, mutate, runId);
        const records = reads.map((read, index) => {
            const isReport = contract.roles[index] !== 'initial';
            const exchange = isReport ? { barrier: deviceAttribute, replies: read.replies } : undefined;
            frames.push(frameWithKernel(frames.length, 'dcl-read', read.screen, deviceAttribute, exchange ? structuredClone(exchange) : undefined, read.kernel));
            return { kind: isReport ? 'report' : 'cells', cursor: [read.screen.cursorRow, read.screen.cursorCol], size: [read.screen.rows, read.screen.cols], ...(isReport ? { report: structuredClone(exchange) } : {}) };
        });
        const feeds = planFeeds(contract);
        const termlessRead = reads[contract.pinnedRead], terminalRead = reads[contract.pinnedTerminalRead];
        const termless = { probeId: contract.caseId, ...replayDclTermless(contract.caseId, termlessRead.replies.join(''), termlessRead.screen.cols) };
        const terminal = { probeId: contract.caseId, ...replayDclTerminal(contract.caseId, terminalRead.replies) };
        cases.push({ caseId: contract.caseId, feeds, barriers: feeds.map(() => deviceAttribute), reads: records, pinned: { termless, terminal } });
    }
    frames.push(frameWithKernel(frames.length, 'dcl-finish', mainScreen(finishBinding ?? binding), deviceAttribute));
    const run = { schemaVersion: 1, adapterId: dclAdapterId, probeIds: dclContracts.map(item => item.caseId), cases };
    const adapter = { id: dclAdapterId, derivedFromAdapterId: 'ntilde-owned-left-right-margins-grading-v1', bundleSha256,
        upstreamBundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6' };
    return { frames, run, runId, adapter };
}

/** The independent contract verdict for the synthetic case. */
export function syntheticDclGrade(caseId = dclContracts[0].caseId, binding = paletteBinding(), mutate = null) {
    const contract = dclContracts.find(item => item.caseId === caseId);
    const reads = syntheticDclScreens(contract, binding, mutate);
    return gradeDclCase(contract, dclObservation(contract, reads, reads.map(read => read.replies), 'NTILDE_DCL_' + 'a'.repeat(32)));
}

export { colEditRows as dclRows, colEditCols as dclCols } from './col-edit-evidence.mjs';
export { exchanges as syntheticDclExchanges };

/** The opt-in DECCOLM suffix for the shared receipt fixture: synthetic frames from the suite's own oracle (never collected ones), layered over the left/right-margin bytes. It writes the retained
 *  artifacts and extends `context` and `evidence` exactly as a real run would. */
export async function addDclEvidence({ output, write, context, evidence, lrMarginsBytes }) {
    const { buildDclGradingReport } = await import('../dcl-grading.mjs');
    const { dclCounts } = await import('../dcl-receipt.mjs');
    const dclBytes = Buffer.from(adaptDclCollector(lrMarginsBytes.toString()));
    const dcl = syntheticDclEvidence({ runId: context.runId, bundleSha256: hashOf(dclBytes) });
    dcl.adapter = { ...dcl.adapter, upstreamBundleSha256: context.upstreamBundleSha256 };
    const report = buildDclGradingReport(dcl.frames, dcl.run, context.runId, dcl.adapter);
    write('dcl-observations.json', report);
    write('dcl-run.json', dcl.run);
    writeFileSync(join(output, 'dcl-snapshots.jsonl'), dcl.frames.map(item => JSON.stringify(item)).join('\n') + '\n');
    writeFileSync(join(output, 'collector-dcl.mjs'), dclBytes);
    Object.assign(context, { dclAdapterId, dclSuiteId, dclBundleSha256: hashOf(dclBytes), dclContractsVersion });
    evidence.dclEvidence = { runId: context.runId, suiteId: dclSuiteId, file: 'dcl-observations.json', sha256: hashOf(readFileSync(join(output, 'dcl-observations.json'))),
        runFile: 'dcl-run.json', runSha256: hashOf(readFileSync(join(output, 'dcl-run.json'))), adapterBundleSha256: hashOf(dclBytes),
        contractsVersion: dclContractsVersion, counts: dclCounts(report), provisionalUntil: 'cell-receipt.json' };
}

const hashOf = bytes => createHash('sha256').update(bytes).digest('hex');
