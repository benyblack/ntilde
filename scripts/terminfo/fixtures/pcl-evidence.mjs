// Synthetic, fully valid pointer-color grading evidence for portable tests. Every frame, run-log entry and pinned result is produced through the same production validators used for real
// measurements. The expected reply is a hand-written literal byte string, a second oracle independent of the contract's constant, so the fixture is a cross-check, not a mirror. The screens carry the
// attributed seed every exchange must leave untouched, and no exchange changes a color, so every frame shares one binding.
import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { planFeeds } from '../graded-plan.mjs';
import { pclSeededBinding, pclContracts, pclContractsVersion, pclObservation, pclSuiteId, gradePclCase, replayPclTermless, replayPclTerminal } from '../pcl-contracts.mjs';
import { adaptPclCollector, pclAdapterId } from '../pcl-grading-collector.mjs';
import { syntheticColEditScreen, paletteBinding } from './col-edit-evidence.mjs';
import { seedRows, frame, alternate } from './devq-evidence.mjs';

const deviceAttribute = '\x1b[?62;4;22c';
const st = '\x1b\\';
/** The hand-written expected replies, spelled out independently of the contract's constants: the five seeded colors, a moved cursor color and the colors after the chained set. */
const seeded = ['10;rgb:1010/2020/3030', '11;rgb:4040/5050/6060', '12;rgb:7070/8080/9090', '17;rgb:a0a0/b0b0/c0c0', '19;rgb:d0d0/e0e0/f0f0'].map(body => '\x1b]' + body + st);
const afterChain = ['\x1b]10;rgb:1010/2020/3030' + st, '\x1b]11;rgb:4040/5050/6060' + st, '\x1b]12;rgb:0101/0202/0303' + st, '\x1b]17;rgb:1010/1111/1212' + st, '\x1b]19;rgb:d0d0/e0e0/f0f0' + st];

/** The expected reply list of every report role of each case, written out by hand. */
const exchanges = Object.freeze({
    seeded,
    pinned: [], 'query-st': [], set: [], 'query-after-set': [], 'set-hex': [], reset: [], 'reset-st': [], 'reset-argument': [],
    'colors-after-pointer': seeded,
    'control-move': ['\x1b]12;rgb:e0e0/a0a0/2020' + st],
    chain: [],
    'colors-after-chain': afterChain,
    recover: [afterChain[2]],
});

/** The screens and reply lists of every read of the case; `mutate(role, read)` may return a changed read to build negative evidence. */
export function syntheticPclScreens(contract, binding = paletteBinding(), mutate = null, runId = 'a'.repeat(32)) {
    const nonce = 'NTILDE_PCL_' + runId;
    // Every case read sits between the color seed and the shared reset: the App's resolved binding carries the seeded OSC 10 / OSC 11 overrides.
    const seeded = pclSeededBinding(binding);
    // The plan's static reseed homes the cursor: every read (and every exchange) observes it at home over the nonce row.
    return contract.roles.map(role => {
        const fresh = () => {
            const screen = alternate(syntheticColEditScreen(seedRows(), [0, 0], { margins: [1, 5] }, [], structuredClone(seeded)), nonce);
            return { screen, replies: [...(exchanges[role] ?? [])] };
        };
        const read = mutate?.(contract.caseId, role, fresh()) ?? fresh();
        return { role, screen: read.screen, replies: read.replies };
    });
}

/** The bare main screen every observed screen starts and ends on: DECLRMM off with full-width margins. */
const mainScreen = binding => {
    const bare = syntheticColEditScreen([], [0, 0], undefined, [], binding);
    return { ...bare, horizontalMargins: { leftRightMarginMode: false, leftMargin: 0, rightMargin: bare.cols - 1 }, cursorMode: { style: 0, blink: true, isDefault: true } };
};

/** A complete, valid synthetic run: frames, worker run log and adapter facts. `mutate(caseId, role, read)` may return a changed read. */
export function syntheticPclEvidence({ runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64), binding = paletteBinding(), mutate = null, finishBinding = null } = {}) {
    const nonce = 'NTILDE_PCL_' + runId;
    const bound = alternate(syntheticColEditScreen(seedRows(), [0, 0], { margins: [1, 5] }, [], structuredClone(binding)), nonce);
    bound.cursorRow = 0;
    bound.cursorCol = nonce.length;
    const frames = [frame(0, 'pcl-start', mainScreen(binding), null), frame(1, 'pcl-bind', bound, deviceAttribute)];
    const cases = [];
    for (const contract of pclContracts) {
        const reads = syntheticPclScreens(contract, binding, mutate, runId);
        const records = reads.map((read, index) => {
            const isReport = contract.roles[index] !== 'initial';
            const exchange = isReport ? { barrier: deviceAttribute, replies: read.replies } : undefined;
            frames.push(frame(frames.length, 'pcl-read', read.screen, deviceAttribute, exchange ? structuredClone(exchange) : undefined));
            return { kind: isReport ? 'report' : 'cells', cursor: [read.screen.cursorRow, read.screen.cursorCol], ...(isReport ? { report: structuredClone(exchange) } : {}) };
        });
        const feeds = planFeeds(contract), replies = reads[contract.pinnedRead].replies.join('');
        const termless = { probeId: contract.caseId, ...replayPclTermless(contract.caseId, replies) };
        const terminal = { probeId: contract.caseId, ...replayPclTerminal(contract.caseId, [0, 0]) };
        cases.push({ caseId: contract.caseId, feeds, barriers: feeds.map(() => deviceAttribute), reads: records, pinned: { termless, terminal } });
    }
    frames.push(frame(frames.length, 'pcl-finish', mainScreen(finishBinding ?? binding), deviceAttribute));
    const run = { schemaVersion: 1, adapterId: pclAdapterId, probeIds: pclContracts.map(item => item.caseId), cases };
    const adapter = { id: pclAdapterId, derivedFromAdapterId: 'ntilde-owned-left-right-margins-grading-v1', bundleSha256,
        upstreamBundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6' };
    return { frames, run, runId, adapter };
}

/** The independent contract verdict for the synthetic case. */
export function syntheticPclGrade(caseId = pclContracts[0].caseId, binding = paletteBinding(), mutate = null) {
    const contract = pclContracts.find(item => item.caseId === caseId);
    const reads = syntheticPclScreens(contract, binding, mutate);
    return gradePclCase(contract, pclObservation(contract, reads.map(read => read.screen), reads.map(read => read.replies), 'NTILDE_PCL_' + 'a'.repeat(32)));
}

export { colEditRows as pclRows, colEditCols as pclCols } from './col-edit-evidence.mjs';
export { exchanges as syntheticPclExchanges };

/** The opt-in pointer-color suffix for the shared receipt fixture: synthetic frames from the suite's own oracle (never collected ones), layered over the left/right-margin bytes. It writes the
 *  retained artifacts and extends `context` and `evidence` exactly as a real run would. */
export async function addPclEvidence({ output, write, context, evidence, lrMarginsBytes }) {
    const { buildPclGradingReport } = await import('../pcl-grading.mjs');
    const { pclCounts } = await import('../pcl-receipt.mjs');
    const pclBytes = Buffer.from(adaptPclCollector(lrMarginsBytes.toString()));
    const pcl = syntheticPclEvidence({ runId: context.runId, bundleSha256: hashOf(pclBytes) });
    pcl.adapter = { ...pcl.adapter, upstreamBundleSha256: context.upstreamBundleSha256 };
    const report = buildPclGradingReport(pcl.frames, pcl.run, context.runId, pcl.adapter);
    write('pcl-observations.json', report);
    write('pcl-run.json', pcl.run);
    writeFileSync(join(output, 'pcl-snapshots.jsonl'), pcl.frames.map(item => JSON.stringify(item)).join('\n') + '\n');
    writeFileSync(join(output, 'collector-pcl.mjs'), pclBytes);
    Object.assign(context, { pclAdapterId, pclSuiteId, pclBundleSha256: hashOf(pclBytes), pclContractsVersion });
    evidence.pclEvidence = { runId: context.runId, suiteId: pclSuiteId, file: 'pcl-observations.json', sha256: hashOf(readFileSync(join(output, 'pcl-observations.json'))),
        runFile: 'pcl-run.json', runSha256: hashOf(readFileSync(join(output, 'pcl-run.json'))), adapterBundleSha256: hashOf(pclBytes),
        contractsVersion: pclContractsVersion, counts: pclCounts(report), provisionalUntil: 'cell-receipt.json' };
}

const hashOf = bytes => createHash('sha256').update(bytes).digest('hex');
