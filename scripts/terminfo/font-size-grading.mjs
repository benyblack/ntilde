import { screenSnapshot } from './agent-observer.mjs';
import { validateGradedJournal, requirePinnedAgreement, requireReplayAgreement } from './graded-plan.mjs';
import { validateDevqScreen } from './devq-contracts.mjs';
import { fontSizeContracts, fontSizeEffectId, fontSizeGeometry, fontSizeIds, fontSizeMatches, fontSizeNonce,
    fontSizeIntegerPoints, fontSizeMutationControlPoints, fontSizePrefix, fontSizeSuiteId, fontSizeVersion, replayFontSizePinned } from './font-size-contracts.mjs';
import { fontSizeAdapterId } from './font-size-grading-collector.mjs';
import { fontSizeContext } from './font-size-collector.mjs';
import { validateCellSizeExchange } from './cell-size-frame.mjs';

const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const check = (value, message) => { if (!value) throw new Error(message); };
const screenOf = value => {
    const screen = screenSnapshot(value, true, true, true, true, true, true, false, true, true);
    validateDevqScreen(screen);
    return screen;
};

export function buildFontSizeReport(frames, run, runId, adapter, { replayModule = null } = {}) {
    const frameCount = 3 + fontSizeContracts.reduce((sum, item) => sum + item.exchanges.length, 0);
    validateGradedJournal(frames, { frameCount, prefix: fontSizePrefix, noncePrefix: fontSizeNonce, runId, label: 'OSC font-size', screenOf });
    check(adapter?.id === fontSizeAdapterId && /^[a-f\d]{64}$/.test(adapter.bundleSha256 ?? '')
        && /^[a-f\d]{64}$/.test(adapter.upstreamBundleSha256 ?? ''), 'Invalid font-size adapter.');
    check(run?.schemaVersion === 1 && run.runId === runId && run.cases?.length === fontSizeIds.length, 'Invalid font-size run.');
    const geometry = fontSizeGeometry(frames[0].geometry, frames[0].screen.rows, frames[0].screen.cols);
    check(fontSizeIntegerPoints(geometry) !== fontSizeMutationControlPoints, 'Absolute setter control must differ from the observed baseline.');
    for (const frame of frames) check(same(fontSizeGeometry(frame.geometry, frame.screen.rows, frame.screen.cols), geometry), 'Font geometry changed during the suite.');
    for (const index of [0, 1, frames.length - 1]) check(frames[index].report === undefined, 'Unexpected font-size setup report.');
    let cursor = 2;
    const cases = fontSizeContracts.map((contract, index) => {
        const recorded = run.cases[index];
        check(recorded.caseId === contract.caseId && recorded.exchanges?.length === contract.exchanges.length, 'Font-size case order differs.');
        const exchanges = contract.exchanges.map((item, position) => {
            const record = recorded.exchanges[position], frame = frames[cursor++];
            const report = validateCellSizeExchange(frame.report);
            check(record.id === item.id && record.request === item.request && same(record.report, report)
                && report.barrier === frame.barrier, 'Font-size request or paired exchange differs.');
            check(frame.sha256 === frames[1].sha256, 'A font-size query altered stored terminal state.');
            return { ...item, snapshot: frame.index, report, matches: fontSizeMatches(item, report.replies, geometry) };
        });
        const pinnedReport = exchanges.find(item => item.id === 'pinned').report;
        const pinned = recorded.pinned;
        for (const path of ['termless', 'terminal']) {
            check(pinned?.[path]?.probeId === contract.caseId, 'Missing pinned font-size path.');
            const raw = pinnedReport.replies.join('') + (path === 'terminal' ? pinnedReport.barrier : '');
            requirePinnedAgreement(contract, pinned[path], replayFontSizePinned(index, raw), 'font-size');
        }
        if (replayModule) requireReplayAgreement(contract,
            replayModule.ntildeRunFontSizeTermless(contract.caseId, fontSizeContext(index, pinnedReport)), pinned.termless);
        const validity = exchanges.filter(item => item.role === 'control').map(item => ({ id: item.id, held: item.matches }));
        const required = exchanges.filter(item => item.role === 'required').map(item => ({ id: item.id, held: item.matches }));
        const requiredOutcome = required.every(item => item.held) ? 'supported' : 'unsupported';
        const outcome = validity.every(item => item.held) ? requiredOutcome : 'inconclusive';
        const pinnedOutcome = pinned.terminal.observation.outcome;
        return { caseId: contract.caseId, pinned, contractResult: { outcome },
            conclusion: { outcome: pinnedOutcome, grading: 'unchanged-pinned-query', evidence: 'owned-raw-replies-and-unchanged-state' },
            effectContract: { id: fontSizeEffectId, version: fontSizeVersion, outcome, validity, required },
            ...(pinnedOutcome === outcome ? {} : { divergence: { pinnedOutcome, contractOutcome: outcome, kind: `pinned-${pinnedOutcome}-effect-${outcome}` } }),
            observed: { geometry, exchanges } };
    });
    return { schemaVersion: 1, suiteId: fontSizeSuiteId, contractsVersion: fontSizeVersion, runId, adapter, cases, snapshots: frames };
}

export const fontSizeCounts = report => report.cases.reduce((counts, item) => {
    counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    return counts;
}, {});
