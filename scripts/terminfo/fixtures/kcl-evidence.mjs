// Synthetic, fully valid kitty color-control grading evidence for portable tests. Every frame, run-log entry and pinned result is produced through the same production validators used for real
// measurements. The expected replies are hand-written literal bytes, a second oracle independent of the contract's model, so the fixture is a cross-check, not a mirror. The synthetic App's host
// defaults are hand-picked (a concrete selection background and an undefined selection foreground, so both reply shapes occur); the screens carry the attributed seed every exchange must leave untouched.
import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { planFeeds } from '../graded-plan.mjs';
import { kclContracts, kclContractsVersion, kclObservation, kclSuiteId, gradeKclCase, replayKclPinned } from '../kcl-contracts.mjs';
import { adaptKclCollector, kclAdapterId } from '../kcl-grading-collector.mjs';
import { syntheticColEditScreen, paletteBinding } from './col-edit-evidence.mjs';
import { seedRows, frame, alternate } from './devq-evidence.mjs';

const deviceAttribute = '\x1b[?62;4;22c';
const st = '\x1b\\';
const osc21 = (...fields) => `\x1b]21;${fields.join(';')}${st}`;
const legacy = (code, rgb) => `\x1b]${code};rgb:${rgb}${st}`;
/** The hand-picked host defaults, spelled out field by field. */
const host = { foreground: 'foreground=rgb:cc/cc/cc', background: 'background=rgb:1e/1e/1e', cursor: 'cursor=rgb:ff/ff/ff', selectionBackground: 'selection_background=rgb:26/4f/78',
    selectionForeground: 'selection_foreground=', five: '5=rgb:aa/00/aa', twoHundred: '200=rgb:ff/00/d7' };
const hostMain = osc21(host.foreground, host.background, host.cursor);
const hostSelection = osc21(host.selectionBackground, host.selectionForeground);
const hostPalette = osc21(host.five, host.twoHundred);
/** Color set A (#102030, #112233, #a1b2c3, #0f1e2d, #d2e3f4, #445566, #123456) spelled out by hand as eight-bit fields. */
const setA = { foreground: 'foreground=rgb:10/20/30', background: 'background=rgb:11/22/33', cursor: 'cursor=rgb:a1/b2/c3', selection_background: 'selection_background=rgb:0f/1e/2d',
    selection_foreground: 'selection_foreground=rgb:d2/e3/f4', 5: '5=rgb:44/55/66', 200: '200=rgb:12/34/56' };
const keyReplies = Object.fromEntries(Object.entries(setA).map(([key, field]) => [`key-${key}`, [osc21(field)]]));
const background = rgb => [osc21(`background=rgb:${rgb}`)];
const forced = [osc21('foreground=rgb:0a/0b/0c')];

/** The expected reply list of every report role of the case, written out by hand. */
const exchanges = Object.freeze({
    'baseline-main': [hostMain], 'baseline-selection': [hostSelection], 'baseline-palette': [hostPalette],
    pinned: [osc21(host.foreground)],
    'setup-A-main': [legacy('10', '1010/2020/3030'), legacy('11', '1111/2222/3333'), legacy('12', 'a1a1/b2b2/c3c3')],
    'setup-A-rest': [legacy('17', '0f0f/1e1e/2d2d'), legacy('19', 'd2d2/e3e3/f4f4'), legacy('4;5', '4444/5555/6666'), legacy('4;200', '1212/3434/5656')],
    ...keyReplies,
    'combined-order': [osc21(setA.cursor, setA[5], setA.foreground, setA[200], setA.background)],
    'bel-terminated': [osc21(setA.background)],
    'two-sequences': [osc21(setA.foreground), osc21(setA.cursor)],
    'set-then-query': [osc21('foreground=rgb:0a/1b/2c')],
    'set-readback-legacy': [legacy('10', '0a0a/1b1b/2c2c')],
    'encoding-rrggbb': background('22/33/44'), 'encoding-rgb-1': background('ff/00/88'), 'encoding-rgb-2': background('12/ab/ef'), 'encoding-rgb-4': background('12/56/9a'),
    'encoding-rgbi': background('ff/00/33'), 'encoding-sharp-12': background('12/45/78'),
    'palette-set': [osc21('5=rgb:66/77/88')], 'palette-readback-legacy': [legacy('4;5', '6666/7777/8888')],
    'selection-set': [osc21('selection_background=rgb:1f/2e/3d', 'selection_foreground=rgb:4c/5b/6a')],
    'selection-readback-legacy': [legacy('17', '1f1f/2e2e/3d3d'), legacy('19', '4c4c/5b5b/6a6a')],
    'reset-bare-key': [osc21(host.foreground, host.background)], 'reset-palette': [hostPalette],
    'restore-main': [hostMain], 'restore-selection': [hostSelection], 'restore-palette': [hostPalette],
    'unknown-key': [osc21('unknown=bm9uc2Vuc2U')], 'unknown-index': [osc21('unknown=MjU2')], 'unknown-padding': [osc21('unknown=YQ', 'unknown=YWI', 'unknown=YWJj')],
    'unknown-order': [osc21('unknown=emV0YQ', 'unknown=YWxwaGE')],
    'interleaved-unknown': [osc21(host.foreground, 'unknown=bm9uc2Vuc2U', host.background)],
    'unmodeled-keys': [osc21('unknown=Y3Vyc29yX3RleHQ', 'unknown=dmlzdWFsX2JlbGw')],
    'unmodeled-transparent': [osc21('unknown=dHJhbnNwYXJlbnRfYmFja2dyb3VuZF9jb2xvcjE')],
    'key-case': [osc21('unknown=Rm9yZWdyb3VuZA')],
    'encoding-sharp-4': background('30/a0/70'), 'encoding-rgbi-range': background('ff/00/00'),
    'empty-value': forced, 'color-name': forced, 'alpha-suffix': forced,
    'set-only-silent': [],
    'oversize-dropped': [osc21('foreground=rgb:0d/0e/0f', host.five)],
    'field-cap': [osc21('5=rgb:00/00/01')],
});

/** The screens and reply lists of every read of the case; `mutate(role, read)` may return a changed read to build negative evidence. */
export function syntheticKclScreens(contract, binding = paletteBinding(), mutate = null, runId = 'a'.repeat(32)) {
    const nonce = 'NTILDE_KC_' + runId;
    // The plan's static reseed homes the cursor: every read (and every exchange) observes it at home over the nonce row.
    const seed = alternate(syntheticColEditScreen(seedRows(), [0, 0], { margins: [1, 5] }, [], binding), nonce);
    return contract.roles.map(role => {
        const fresh = () => ({ screen: structuredClone(seed), replies: [...(exchanges[role] ?? [])] });
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
export function syntheticKclEvidence({ runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64), binding = paletteBinding(), mutate = null, finishBinding = null } = {}) {
    const nonce = 'NTILDE_KC_' + runId;
    const bound = alternate(syntheticColEditScreen(seedRows(), [0, 0], { margins: [1, 5] }, [], binding), nonce);
    bound.cursorRow = 0;
    bound.cursorCol = nonce.length;
    const frames = [frame(0, 'kcl-start', mainScreen(binding), null), frame(1, 'kcl-bind', bound, deviceAttribute)];
    const cases = [];
    for (const contract of kclContracts) {
        const reads = syntheticKclScreens(contract, binding, mutate, runId);
        const records = reads.map((read, index) => {
            const isReport = contract.roles[index] !== 'initial';
            const exchange = isReport ? { barrier: deviceAttribute, replies: read.replies } : undefined;
            frames.push(frame(frames.length, 'kcl-read', read.screen, deviceAttribute, exchange ? structuredClone(exchange) : undefined));
            return { kind: isReport ? 'report' : 'cells', cursor: [read.screen.cursorRow, read.screen.cursorCol], ...(isReport ? { report: structuredClone(exchange) } : {}) };
        });
        const feeds = planFeeds(contract), replies = reads[contract.pinnedRead].replies.join('');
        const termless = { probeId: contract.caseId, ...replayKclPinned(contract.caseId, 'termless', { replies }) };
        const terminal = { probeId: contract.caseId, ...replayKclPinned(contract.caseId, 'terminal', { replies, barrier: deviceAttribute }) };
        cases.push({ caseId: contract.caseId, feeds, barriers: feeds.map(() => deviceAttribute), reads: records, pinned: { termless, terminal } });
    }
    frames.push(frame(frames.length, 'kcl-finish', mainScreen(finishBinding ?? binding), deviceAttribute));
    const run = { schemaVersion: 1, adapterId: kclAdapterId, probeIds: kclContracts.map(item => item.caseId), cases };
    const adapter = { id: kclAdapterId, derivedFromAdapterId: 'ntilde-owned-left-right-margins-grading-v1', bundleSha256,
        upstreamBundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6' };
    return { frames, run, runId, adapter };
}

/** The independent contract verdict for the synthetic case. */
export function syntheticKclGrade(caseId = kclContracts[0].caseId, binding = paletteBinding(), mutate = null) {
    const contract = kclContracts.find(item => item.caseId === caseId);
    const reads = syntheticKclScreens(contract, binding, mutate);
    return gradeKclCase(contract, kclObservation(contract, reads.map(read => read.screen), reads.map(read => read.replies), 'NTILDE_KC_' + 'a'.repeat(32)));
}

export { colEditRows as kclRows, colEditCols as kclCols } from './col-edit-evidence.mjs';
export { exchanges as syntheticKclExchanges };

const hash = bytes => createHash('sha256').update(bytes).digest('hex');

/** The opt-in kitty color suffix for the shared receipt fixture: synthetic frames from the suite's own oracle (never collected ones), layered over the left/right-margin bytes. It writes the retained
 *  artifacts and extends `context` and `evidence` exactly as a real run would. */
export async function addKclEvidence({ output, write, context, evidence, lrMarginsBytes }) {
    const { buildKclGradingReport } = await import('../kcl-grading.mjs');
    const { kclCounts } = await import('../kcl-receipt.mjs');
    const kclBytes = Buffer.from(adaptKclCollector(lrMarginsBytes.toString()));
    const kcl = syntheticKclEvidence({ runId: context.runId, bundleSha256: hash(kclBytes) });
    kcl.adapter = { ...kcl.adapter, upstreamBundleSha256: context.upstreamBundleSha256 };
    const report = buildKclGradingReport(kcl.frames, kcl.run, context.runId, kcl.adapter);
    write('kcl-observations.json', report);
    write('kcl-run.json', kcl.run);
    writeFileSync(join(output, 'kcl-snapshots.jsonl'), kcl.frames.map(item => JSON.stringify(item)).join('\n') + '\n');
    writeFileSync(join(output, 'collector-kcl.mjs'), kclBytes);
    Object.assign(context, { kclAdapterId, kclSuiteId, kclBundleSha256: hash(kclBytes), kclContractsVersion });
    evidence.kclEvidence = { runId: context.runId, suiteId: kclSuiteId, file: 'kcl-observations.json', sha256: hash(readFileSync(join(output, 'kcl-observations.json'))),
        runFile: 'kcl-run.json', runSha256: hash(readFileSync(join(output, 'kcl-run.json'))), adapterBundleSha256: hash(kclBytes),
        contractsVersion: kclContractsVersion, counts: kclCounts(report), provisionalUntil: 'cell-receipt.json' };
}
