// Synthetic, fully valid iTerm2 feature-reporting grading evidence for portable tests. Every frame, run-log entry and pinned result is produced through the same production validators used for real
// measurements. The expected reply is a hand-written literal byte string, a second oracle independent of the contract's constant, so the fixture is a cross-check, not a mirror. The screens carry the
// attributed seed every exchange must leave untouched, and no exchange changes a color, so every frame shares one binding.
import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { planFeeds } from '../graded-plan.mjs';
import { capsContracts, capsContractsVersion, capsObservation, capsSuiteId, gradeCapsCase, replayCapsPinned } from '../caps-contracts.mjs';
import { adaptCapsCollector, capsAdapterId } from '../caps-grading-collector.mjs';
import { syntheticColEditScreen, paletteBinding } from './col-edit-evidence.mjs';
import { seedRows, frame, alternate } from './devq-evidence.mjs';

const deviceAttribute = '\x1b[?62;4;22c';
const st = '\x1b\\';
/** The hand-written expected reply, spelled out independently of the contract's constant. */
const reply = '\x1b]1337;Capabilities=T3CwLrMSc7UTs3BFGsGoSyHNoP' + st;

/** The expected reply list of every report role of the case, written out by hand. */
const exchanges = Object.freeze({
    pinned: [reply],
    'st-terminated': [reply],
    repeat: [reply, reply],
    'silent-forms': [],
    'silent-spellings': [],
    recover: [reply],
});

/** The screens and reply lists of every read of the case; `mutate(role, read)` may return a changed read to build negative evidence. */
export function syntheticCapsScreens(contract, binding = paletteBinding(), mutate = null, runId = 'a'.repeat(32)) {
    const nonce = 'NTILDE_CAP_' + runId;
    // The plan's static reseed homes the cursor: every read (and every exchange) observes it at home over the nonce row.
    return contract.roles.map(role => {
        const fresh = () => {
            const screen = alternate(syntheticColEditScreen(seedRows(), [0, 0], { margins: [1, 5] }, [], structuredClone(binding)), nonce);
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
export function syntheticCapsEvidence({ runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64), binding = paletteBinding(), mutate = null, finishBinding = null } = {}) {
    const nonce = 'NTILDE_CAP_' + runId;
    const bound = alternate(syntheticColEditScreen(seedRows(), [0, 0], { margins: [1, 5] }, [], structuredClone(binding)), nonce);
    bound.cursorRow = 0;
    bound.cursorCol = nonce.length;
    const frames = [frame(0, 'caps-start', mainScreen(binding), null), frame(1, 'caps-bind', bound, deviceAttribute)];
    const cases = [];
    for (const contract of capsContracts) {
        const reads = syntheticCapsScreens(contract, binding, mutate, runId);
        const records = reads.map((read, index) => {
            const isReport = contract.roles[index] !== 'initial';
            const exchange = isReport ? { barrier: deviceAttribute, replies: read.replies } : undefined;
            frames.push(frame(frames.length, 'caps-read', read.screen, deviceAttribute, exchange ? structuredClone(exchange) : undefined));
            return { kind: isReport ? 'report' : 'cells', cursor: [read.screen.cursorRow, read.screen.cursorCol], ...(isReport ? { report: structuredClone(exchange) } : {}) };
        });
        const feeds = planFeeds(contract), replies = reads[contract.pinnedRead].replies.join('');
        const termless = { probeId: contract.caseId, ...replayCapsPinned(contract.caseId, 'termless', { replies }) };
        const terminal = { probeId: contract.caseId, ...replayCapsPinned(contract.caseId, 'terminal', { replies, barrier: deviceAttribute }) };
        cases.push({ caseId: contract.caseId, feeds, barriers: feeds.map(() => deviceAttribute), reads: records, pinned: { termless, terminal } });
    }
    frames.push(frame(frames.length, 'caps-finish', mainScreen(finishBinding ?? binding), deviceAttribute));
    const run = { schemaVersion: 1, adapterId: capsAdapterId, probeIds: capsContracts.map(item => item.caseId), cases };
    const adapter = { id: capsAdapterId, derivedFromAdapterId: 'ntilde-owned-left-right-margins-grading-v1', bundleSha256,
        upstreamBundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6' };
    return { frames, run, runId, adapter };
}

/** The independent contract verdict for the synthetic case. */
export function syntheticCapsGrade(caseId = capsContracts[0].caseId, binding = paletteBinding(), mutate = null) {
    const contract = capsContracts.find(item => item.caseId === caseId);
    const reads = syntheticCapsScreens(contract, binding, mutate);
    return gradeCapsCase(contract, capsObservation(contract, reads.map(read => read.screen), reads.map(read => read.replies), 'NTILDE_CAP_' + 'a'.repeat(32)));
}

export { colEditRows as capsRows, colEditCols as capsCols } from './col-edit-evidence.mjs';
export { exchanges as syntheticCapsExchanges };

/** The opt-in feature-reporting suffix for the shared receipt fixture: synthetic frames from the suite's own oracle (never collected ones), layered over the left/right-margin bytes. It writes the
 *  retained artifacts and extends `context` and `evidence` exactly as a real run would. */
export async function addCapsEvidence({ output, write, context, evidence, lrMarginsBytes }) {
    const { buildCapsGradingReport } = await import('../caps-grading.mjs');
    const { capsCounts } = await import('../caps-receipt.mjs');
    const capsBytes = Buffer.from(adaptCapsCollector(lrMarginsBytes.toString()));
    const caps = syntheticCapsEvidence({ runId: context.runId, bundleSha256: hashOf(capsBytes) });
    caps.adapter = { ...caps.adapter, upstreamBundleSha256: context.upstreamBundleSha256 };
    const report = buildCapsGradingReport(caps.frames, caps.run, context.runId, caps.adapter);
    write('caps-observations.json', report);
    write('caps-run.json', caps.run);
    writeFileSync(join(output, 'caps-snapshots.jsonl'), caps.frames.map(item => JSON.stringify(item)).join('\n') + '\n');
    writeFileSync(join(output, 'collector-caps.mjs'), capsBytes);
    Object.assign(context, { capsAdapterId, capsSuiteId, capsBundleSha256: hashOf(capsBytes), capsContractsVersion });
    evidence.capsEvidence = { runId: context.runId, suiteId: capsSuiteId, file: 'caps-observations.json', sha256: hashOf(readFileSync(join(output, 'caps-observations.json'))),
        runFile: 'caps-run.json', runSha256: hashOf(readFileSync(join(output, 'caps-run.json'))), adapterBundleSha256: hashOf(capsBytes),
        contractsVersion: capsContractsVersion, counts: capsCounts(report), provisionalUntil: 'cell-receipt.json' };
}

const hashOf = bytes => createHash('sha256').update(bytes).digest('hex');
