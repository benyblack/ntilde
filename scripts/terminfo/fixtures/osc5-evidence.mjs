// Synthetic, fully valid rxvt special-color grading evidence for portable tests. Every frame, run-log entry and pinned result is produced through the same production validators used for real
// measurements. The expected replies are hand-written literal bytes, a second oracle independent of the contract's model, so the fixture is a cross-check, not a mirror. The synthetic App's host
// default foreground is hand-picked (every special color defaults to it, spelled `rgb:cccc/cccc/cccc` in the sixteen-bit reply); the screens carry the attributed seed every exchange must leave untouched.
import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { planFeeds } from '../graded-plan.mjs';
import { osc5Contracts, osc5ContractsVersion, osc5Observation, osc5SuiteId, gradeOsc5Case, replayOsc5Pinned } from '../osc5-contracts.mjs';
import { adaptOsc5Collector, osc5AdapterId } from '../osc5-grading-collector.mjs';
import { syntheticColEditScreen, paletteBinding } from './col-edit-evidence.mjs';
import { seedRows, frame, alternate } from './devq-evidence.mjs';

const deviceAttribute = '\x1b[?62;4;22c';
const st = '\x1b\\';
const osc5 = (selector, value) => `\x1b]5;${selector};${value}${st}`;
const legacy = (code, rgb) => `\x1b]${code};rgb:${rgb}${st}`;
/** The hand-picked host default foreground, spelled out per selector: an unset special color reports it. */
const def = 'rgb:cccc/cccc/cccc';
const defaults = Object.fromEntries([0, 1, 2, 3, 4].map(selector => [selector, osc5(selector, def)]));
/** Color set A (#102030, #112233, #a1b2c3, #0f1e2d, #d2e3f4) spelled out by hand as sixteen-bit reply frames. */
const setA = { 0: osc5(0, 'rgb:1010/2020/3030'), 1: osc5(1, 'rgb:1111/2222/3333'), 2: osc5(2, 'rgb:a1a1/b2b2/c3c3'), 3: osc5(3, 'rgb:0f0f/1e1e/2d2d'), 4: osc5(4, 'rgb:d2d2/e3e3/f4f4') };

/** The expected reply list of every report role of the case, written out by hand. */
const exchanges = Object.freeze({
    'baseline-special': [defaults[0], defaults[1], defaults[2], defaults[3], defaults[4]],
    // The synthetic theme: foreground rgb(211,211,211), canonical palette slot 1 rgb(205,0,0), cursor rgb(255,255,255).
    'baseline-other': [`\x1b]10;rgb:d3d3/d3d3/d3d3${st}`, `\x1b]4;200;rgb:ffff/0000/d7d7${st}`, `\x1b]21;cursor=rgb:ff/ff/ff${st}`],
    pinned: [defaults[0]],
    'setup-A': [setA[0], setA[1], setA[2], setA[3], setA[4]],
    'set-then-query': [osc5(0, 'rgb:0a0a/1b1b/2c2c')],
    'per-slot': [defaults[1], defaults[2], defaults[3]],
    encodings: [osc5(0, 'rgb:ffff/0000/8888'), osc5(1, 'rgb:aaaa/bbbb/cccc'), osc5(2, 'rgb:ffff/0000/3333')],
    'bel-terminated': [defaults[1]],
    'two-sequences': [defaults[0], defaults[2]],
    'reset-one': [defaults[0], osc5(1, 'rgb:2020/2020/2020')],
    'reset-all': [defaults[0], defaults[1], defaults[2], defaults[3], defaults[4]],
    'cross-dynamic-set': [defaults[0]],
    'cross-dynamic-read': [`\x1b]10;rgb:d3d3/d3d3/d3d3${st}`],
    'cross-palette-set': [defaults[1]],
    'cross-palette-read': [`\x1b]4;200;rgb:ffff/0000/d7d7${st}`],
    'cross-kitty-set': [defaults[2]],
    'cross-kitty-read': [`\x1b]21;cursor=rgb:ff/ff/ff${st}`],
    restore: [defaults[0], defaults[1], defaults[2], defaults[3], defaults[4]],
    'unrelated-selector': [osc5(0, 'rgb:1414/1414/1414')],
    malformed: [defaults[0]],
    'set-only-silent': [],
    'reset-noise': [defaults[0]],
    'oversize-dropped': [defaults[0]],
});

/** The screens and reply lists of every read of the case; `mutate(role, read)` may return a changed read to build negative evidence. */
export function syntheticOsc5Screens(contract, binding = paletteBinding(), mutate = null, runId = 'a'.repeat(32)) {
    const nonce = 'NTILDE_O5_' + runId;
    // The plan's static reseed homes the cursor: every read (and every exchange) observes it at home over the nonce row.
    const seed = alternate(syntheticColEditScreen(seedRows(), [0, 0], { margins: [1, 5] }, [], binding), nonce);
    return contract.roles.map(role => {
        const fresh = () => {
            let colors = structuredClone(binding);
            if (role === 'cross-dynamic-set') colors = { ...colors, foregroundOverride: true, foregroundArgb: 0xffabcdef };
            if (role === 'cross-palette-set') {
                colors = { ...binding, ansiPaletteArgb: [...binding.ansiPaletteArgb], paletteOverrideIndices: [...new Set([...binding.paletteOverrideIndices, 200])].sort((a, b) => a - b) };
                colors.ansiPaletteArgb[200] = 0xff121212;
            }
            const screen = alternate(syntheticColEditScreen(seedRows(), [0, 0], { margins: [1, 5] }, [], colors), nonce);
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
export function syntheticOsc5Evidence({ runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64), binding = paletteBinding(), mutate = null, finishBinding = null } = {}) {
    const nonce = 'NTILDE_O5_' + runId;
    const bound = alternate(syntheticColEditScreen(seedRows(), [0, 0], { margins: [1, 5] }, [], binding), nonce);
    bound.cursorRow = 0;
    bound.cursorCol = nonce.length;
    const frames = [frame(0, 'scol-start', mainScreen(binding), null), frame(1, 'scol-bind', bound, deviceAttribute)];
    const cases = [];
    for (const contract of osc5Contracts) {
        const reads = syntheticOsc5Screens(contract, binding, mutate, runId);
        const records = reads.map((read, index) => {
            const isReport = contract.roles[index] !== 'initial';
            const exchange = isReport ? { barrier: deviceAttribute, replies: read.replies } : undefined;
            frames.push(frame(frames.length, 'scol-read', read.screen, deviceAttribute, exchange ? structuredClone(exchange) : undefined));
            return { kind: isReport ? 'report' : 'cells', cursor: [read.screen.cursorRow, read.screen.cursorCol], ...(isReport ? { report: structuredClone(exchange) } : {}) };
        });
        const feeds = planFeeds(contract), replies = reads[contract.pinnedRead].replies.join('');
        const termless = { probeId: contract.caseId, ...replayOsc5Pinned(contract.caseId, 'termless', { replies }) };
        const terminal = { probeId: contract.caseId, ...replayOsc5Pinned(contract.caseId, 'terminal', { replies, barrier: deviceAttribute }) };
        cases.push({ caseId: contract.caseId, feeds, barriers: feeds.map(() => deviceAttribute), reads: records, pinned: { termless, terminal } });
    }
    frames.push(frame(frames.length, 'scol-finish', mainScreen(finishBinding ?? binding), deviceAttribute));
    const run = { schemaVersion: 1, adapterId: osc5AdapterId, probeIds: osc5Contracts.map(item => item.caseId), cases };
    const adapter = { id: osc5AdapterId, derivedFromAdapterId: 'ntilde-owned-left-right-margins-grading-v1', bundleSha256,
        upstreamBundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6' };
    return { frames, run, runId, adapter };
}

/** The independent contract verdict for the synthetic case. */
export function syntheticOsc5Grade(caseId = osc5Contracts[0].caseId, binding = paletteBinding(), mutate = null) {
    const contract = osc5Contracts.find(item => item.caseId === caseId);
    const reads = syntheticOsc5Screens(contract, binding, mutate);
    return gradeOsc5Case(contract, osc5Observation(contract, reads.map(read => read.screen), reads.map(read => read.replies), 'NTILDE_O5_' + 'a'.repeat(32)));
}

export { colEditRows as osc5Rows, colEditCols as osc5Cols } from './col-edit-evidence.mjs';
export { exchanges as syntheticOsc5Exchanges };

const hash = bytes => createHash('sha256').update(bytes).digest('hex');

/** The opt-in special-color suffix for the shared receipt fixture: synthetic frames from the suite's own oracle (never collected ones), layered over the left/right-margin bytes. It writes the retained
 *  artifacts and extends `context` and `evidence` exactly as a real run would. */
export async function addOsc5Evidence({ output, write, context, evidence, lrMarginsBytes }) {
    const { buildOsc5GradingReport } = await import('../osc5-grading.mjs');
    const { osc5Counts } = await import('../osc5-receipt.mjs');
    const osc5Bytes = Buffer.from(adaptOsc5Collector(lrMarginsBytes.toString()));
    const osc5 = syntheticOsc5Evidence({ runId: context.runId, bundleSha256: hash(osc5Bytes) });
    osc5.adapter = { ...osc5.adapter, upstreamBundleSha256: context.upstreamBundleSha256 };
    const report = buildOsc5GradingReport(osc5.frames, osc5.run, context.runId, osc5.adapter);
    write('osc5-observations.json', report);
    write('osc5-run.json', osc5.run);
    writeFileSync(join(output, 'osc5-snapshots.jsonl'), osc5.frames.map(item => JSON.stringify(item)).join('\n') + '\n');
    writeFileSync(join(output, 'collector-osc5.mjs'), osc5Bytes);
    Object.assign(context, { osc5AdapterId, osc5SuiteId, osc5BundleSha256: hash(osc5Bytes), osc5ContractsVersion });
    evidence.osc5Evidence = { runId: context.runId, suiteId: osc5SuiteId, file: 'osc5-observations.json', sha256: hash(readFileSync(join(output, 'osc5-observations.json'))),
        runFile: 'osc5-run.json', runSha256: hash(readFileSync(join(output, 'osc5-run.json'))), adapterBundleSha256: hash(osc5Bytes),
        contractsVersion: osc5ContractsVersion, counts: osc5Counts(report), provisionalUntil: 'cell-receipt.json' };
}
