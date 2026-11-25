// Synthetic, fully valid DECSCLM grading evidence for portable tests. Every frame, run-log entry and pinned result is produced through the same production validators used for real measurements. The
// expected replies are hand-written literal byte strings, a second oracle independent of the contract's constants, so the fixture is a cross-check, not a mirror. The screens carry the attributed seed
// every exchange must leave untouched (or the blank band a burst leaves), every frame carries the parent's timing record, and no exchange changes a color, so every frame shares one binding.
import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { planFeeds } from '../graded-plan.mjs';
import { dslBurstCursor, dslContracts, dslContractsVersion, dslCursorAt, dslObservation, dslPauseMs, dslSuiteId, gradeDslCase, replayDslTermless, replayDslTerminal } from '../dsl-contracts.mjs';
import { adaptDslCollector, dslAdapterId } from '../dsl-grading-collector.mjs';
import { timingClock } from '../dsl-capture.mjs';
import { syntheticColEditScreen, paletteBinding } from './col-edit-evidence.mjs';
import { colorCell, plainCell } from './sgr-palette-evidence.mjs';
import { seedRows, frame, alternate } from './devq-evidence.mjs';

const deviceAttribute = '\x1b[?62;4;22c';
/** Mode 4 is not recognized (Pm 0); DECAWM and DECANM are set (Pm 1). Written out by hand. */
const four = '\x1b[?4;0$y';

/** The expected reply list of every report role, written out by hand. */
const exchanges = Object.freeze({
    recognized: ['\x1b[?7;1$y', '\x1b[?2;1$y'],
    pinned: [four], 'pinned-query': [four], set: [four], reset: [four],
    'burst-off': [four], 'burst-set': [four], 'burst-held': [four], 'burst-reset': [four], restored: [four], paced: [four],
    'control-clear': [],
});
/** The timing record of one frame, in milliseconds on the parent clock. */
export const timingOf = elapsedMs => ({ elapsedMs, clock: timingClock });
/** The roles whose exchange scrolls the margin band, and the timing an ordinary and the held exchange read. */
export const burstRoles = Object.freeze(['burst-off', 'burst-set', 'burst-held', 'burst-reset', 'paced']);
export const fastMs = 15, heldMs = dslPauseMs + 90;
const frameWithTiming = (index, kind, screen, barrier, report, timing = timingOf(fastMs)) => ({ ...frame(index, kind, screen, barrier, report), timing });

/** The seed rows with the witness the plan writes at the cursor (row 3, column 6, plain unless `witness` says otherwise: a cell written with a pen another exchange changed). */
export const dslSeedRows = (witness = plainCell('W')) => {
    const rows = seedRows();
    rows[dslCursorAt[0]] = [...rows[dslCursorAt[0]], ...Array.from({ length: dslCursorAt[1] - rows[dslCursorAt[0]].length }, () => plainCell(' ')), witness];
    return rows;
};
export { colorCell, literalChannel } from './sgr-palette-evidence.mjs';

/** The rows a burst leaves: everything scrolled out of the margin band but the witness the exchange rewrote on the bottom margin row. */
export const dslBurstRows = (witness = plainCell('W')) => [[], [], [], [], [], [witness]];

/** The screens, timings and reply lists of every read of the case; `mutate(caseId, role, read)` may return a changed read to build negative evidence. */
export function syntheticDslScreens(contract, binding = paletteBinding(), mutate = null, runId = 'a'.repeat(32)) {
    const nonce = 'NTILDE_DSL_' + runId;
    return contract.roles.map(role => {
        const fresh = () => {
            let screen;
            if (role === 'control-clear') screen = alternate(syntheticColEditScreen([], [0, 0], { margins: [1, 5] }, [], structuredClone(binding)));
            else if (burstRoles.includes(role)) screen = alternate(syntheticColEditScreen(dslBurstRows(), dslBurstCursor, { margins: [1, 5] }, [], structuredClone(binding)), nonce);
            else screen = alternate(syntheticColEditScreen(dslSeedRows(), dslCursorAt, { margins: [1, 5] }, [], structuredClone(binding)), nonce);
            return { screen, timing: timingOf(role === 'paced' ? heldMs : fastMs), replies: [...(exchanges[role] ?? [])] };
        };
        const read = mutate?.(contract.caseId, role, fresh()) ?? fresh();
        return { role, screen: read.screen, timing: read.timing, replies: read.replies };
    });
}

/** A blank, valid alternate-screen state of another grid, for evidence in which the App's grid changed: the same canonical screen the DECCOLM fixture builds. */
export { resizedDclScreen as resizedDslScreen } from './dcl-evidence.mjs';

/** The bare main screen every observed screen starts and ends on: DECLRMM off with full-width margins. */
const mainScreen = binding => {
    const bare = syntheticColEditScreen([], [0, 0], undefined, [], binding);
    return { ...bare, horizontalMargins: { leftRightMarginMode: false, leftMargin: 0, rightMargin: bare.cols - 1 }, cursorMode: { style: 0, blink: true, isDefault: true } };
};

/** A complete, valid synthetic run: frames, worker run log and adapter facts. `mutate(caseId, role, read)` may return a changed read. */
export function syntheticDslEvidence({ runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64), binding = paletteBinding(), mutate = null, finishBinding = null } = {}) {
    const nonce = 'NTILDE_DSL_' + runId;
    const bound = alternate(syntheticColEditScreen(seedRows(), [0, 0], { margins: [1, 5] }, [], structuredClone(binding)), nonce);
    bound.cursorRow = 0;
    bound.cursorCol = nonce.length;
    const frames = [frameWithTiming(0, 'dsl-start', mainScreen(binding), null, undefined, timingOf(0)), frameWithTiming(1, 'dsl-bind', bound, deviceAttribute)];
    const cases = [];
    for (const contract of dslContracts) {
        const reads = syntheticDslScreens(contract, binding, mutate, runId);
        const records = reads.map((read, index) => {
            const isReport = contract.roles[index] !== 'initial';
            const exchange = isReport ? { barrier: deviceAttribute, replies: read.replies } : undefined;
            frames.push(frameWithTiming(frames.length, 'dsl-read', read.screen, deviceAttribute, exchange ? structuredClone(exchange) : undefined, read.timing));
            return { kind: isReport ? 'report' : 'cells', cursor: [read.screen.cursorRow, read.screen.cursorCol], size: [read.screen.rows, read.screen.cols], ...(isReport ? { report: structuredClone(exchange) } : {}) };
        });
        const feeds = planFeeds(contract);
        const termlessRead = reads[contract.pinnedRead], terminalRead = reads[contract.pinnedTerminalRead];
        const termless = { probeId: contract.caseId, ...replayDslTermless(contract.caseId, termlessRead.replies.join('')) };
        const terminal = { probeId: contract.caseId, ...replayDslTerminal(contract.caseId, terminalRead.replies) };
        cases.push({ caseId: contract.caseId, feeds, barriers: feeds.map(() => deviceAttribute), reads: records, pinned: { termless, terminal } });
    }
    frames.push(frameWithTiming(frames.length, 'dsl-finish', mainScreen(finishBinding ?? binding), deviceAttribute));
    const run = { schemaVersion: 1, adapterId: dslAdapterId, probeIds: dslContracts.map(item => item.caseId), cases };
    const adapter = { id: dslAdapterId, derivedFromAdapterId: 'ntilde-owned-left-right-margins-grading-v1', bundleSha256,
        upstreamBundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6' };
    return { frames, run, runId, adapter };
}

/** The independent contract verdict for the synthetic case. */
export function syntheticDslGrade(caseId = dslContracts[0].caseId, binding = paletteBinding(), mutate = null) {
    const contract = dslContracts.find(item => item.caseId === caseId);
    const reads = syntheticDslScreens(contract, binding, mutate);
    return gradeDslCase(contract, dslObservation(contract, reads, reads.map(read => read.replies), 'NTILDE_DSL_' + 'a'.repeat(32)));
}

export { colEditRows as dslRows, colEditCols as dslCols } from './col-edit-evidence.mjs';
export { exchanges as syntheticDslExchanges };

/** The opt-in DECSCLM suffix for the shared receipt fixture: synthetic frames from the suite's own oracle (never collected ones), layered over the left/right-margin bytes. It writes the retained
 *  artifacts and extends `context` and `evidence` exactly as a real run would. */
export async function addDslEvidence({ output, write, context, evidence, lrMarginsBytes }) {
    const { buildDslGradingReport } = await import('../dsl-grading.mjs');
    const { dslCounts } = await import('../dsl-receipt.mjs');
    const dslBytes = Buffer.from(adaptDslCollector(lrMarginsBytes.toString()));
    const dsl = syntheticDslEvidence({ runId: context.runId, bundleSha256: hashOf(dslBytes) });
    dsl.adapter = { ...dsl.adapter, upstreamBundleSha256: context.upstreamBundleSha256 };
    const report = buildDslGradingReport(dsl.frames, dsl.run, context.runId, dsl.adapter);
    write('dsl-observations.json', report);
    write('dsl-run.json', dsl.run);
    writeFileSync(join(output, 'dsl-snapshots.jsonl'), dsl.frames.map(item => JSON.stringify(item)).join('\n') + '\n');
    writeFileSync(join(output, 'collector-dsl.mjs'), dslBytes);
    Object.assign(context, { dslAdapterId, dslSuiteId, dslBundleSha256: hashOf(dslBytes), dslContractsVersion });
    evidence.dslEvidence = { runId: context.runId, suiteId: dslSuiteId, file: 'dsl-observations.json', sha256: hashOf(readFileSync(join(output, 'dsl-observations.json'))),
        runFile: 'dsl-run.json', runSha256: hashOf(readFileSync(join(output, 'dsl-run.json'))), adapterBundleSha256: hashOf(dslBytes),
        contractsVersion: dslContractsVersion, counts: dslCounts(report), provisionalUntil: 'cell-receipt.json' };
}

const hashOf = bytes => createHash('sha256').update(bytes).digest('hex');
