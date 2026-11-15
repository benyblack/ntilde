// A publication-only projection of the independently rederived retained owned run for the xterm saved-mode qualification.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { xsvCaseIds } from './xsv-contracts.mjs';

const json = (dir, file) => JSON.parse(readFileSync(join(dir, file)));
export const measuredScriptsRevision = 'pending';
export const measuredAppRevision = 'pending';
const scope = 'Pinned terminfo.dev@4.0.0 criteria for modes.xtsave (CSI ? 7 s) and modes.xtrestore (CSI ? 7 r), which can only return inconclusive (they measure DA1 and cursor responsiveness, not '
    + 'saved/restored mode state), replayed byte-exactly over the journaled raw bytes of their own fenced exchange and the cursor of its read, beside a separately identified effect contract graded by an '
    + 'independent oracle written from xterm\'s control-sequence text: silence of both sequences, and the stored DECAWM (?7, also as a pending wrap after a character in the last column), DECOM (?6), DECLRMM (?69) '
    + 'and DECTCEM (?25) state read back after restores, each mode returning to its saved value in both polarities, only the listed modes being restored, a list restoring together, a second save replacing '
    + 'the first, and neither sequence being SCOSC, SCORC or DECSTBM; values the text does not list, a repeated restore, xterm\'s ?1048 cursor save and the cursor move of a restored ?6 are policy groups that never '
    + 'decide; stored state, not host presentation or painted pixels; the input-side, mouse, alternate-screen, reverse-wrap and sixel modes, and a restore with nothing saved, are not measured; no remote/mux, '
    + 'Windows/Linux or common-harness claim; the stock grades are unchanged by this suite.';
const stockOf = (raw, caseId) => {
    const observation = raw.observations.find(entry => entry.featureId === caseId);
    if (!observation) throw new Error(`Missing stock outcome: ${caseId}`);
    return observation;
};

export function xsvPublication(dir) {
    const context = json(dir, 'context.json'), receipt = json(dir, 'cell-receipt.json');
    const measured = json(dir, 'xsv-observations.json'), raw = json(dir, 'raw.json'), stock = json(dir, 'summary.json');
    const cases = measured.cases.map(item => ({ caseId: item.caseId, pinnedCriterion: item.pinnedCriterion, pinnedScript: item.pinnedScript, effectContract: item.effectContract,
        observed: item.observed, pinned: item.pinned, contractResult: item.contractResult, stock: stockOf(raw, item.caseId),
        ...(item.divergence ? { divergence: item.divergence } : {}), conclusion: item.conclusion }));
    if (JSON.stringify(cases.map(item => item.caseId)) !== JSON.stringify([...xsvCaseIds])) throw new Error('The published cases differ from the contracted cases.');
    const report = { schemaVersion: 1, suiteId: measured.suiteId, contractsVersion: measured.contractsVersion, runId: measured.runId, adapter: measured.adapter, scope, cases };
    const summary = { schemaVersion: 1, measuredAt: stock.measuredAt, ownedRunId: context.runId, stockCollectorRunId: raw.runId, appRevision: measuredAppRevision,
        scriptsRevision: measuredScriptsRevision, context, receipt, stockCollector: { counts: stock.counts, suiteComplete: stock.suiteComplete, rawSha256: stock.rawSha256, probeHash: raw.probeHash,
            sourceRevision: raw.sourceRevision, note: 'Stock terminal-path grades from the same owned run; both pinned probes stay inconclusive under the unchanged pinned code, so this suite moves no stock grade.' } };
    return { summary, report };
}
