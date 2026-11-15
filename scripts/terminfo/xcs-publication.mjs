// A publication-only projection of the independently rederived retained owned run for the xterm color-stack qualification.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { xcsCaseIds } from './xcs-contracts.mjs';

const json = (dir, file) => JSON.parse(readFileSync(join(dir, file)));
export const measuredScriptsRevision = 'fe332981a1b69000eac4d597d99bcafeef00fb59';
export const measuredAppRevision = 'fb6c7fd6b81b4e231afb86b500fbef26354100fd';
const scope = 'Pinned terminfo.dev@4.0.0 criteria for modes.xtpushcolors (CSI # P) and modes.xtpopcolors (CSI # Q), which can only return inconclusive (they measure DA1 and cursor responsiveness, not '
    + 'saved/restored stack state), replayed byte-exactly over the journaled raw bytes of their own fenced exchange and the cursor of its read, beside a separately identified effect contract graded by an '
    + 'independent oracle written from xterm\'s control-sequence text: silence of both sequences, snapshot and restoration of the foreground, background and cursor colors and ANSI palette entry 5 read '
    + 'back through OSC 10/11/12 and OSC 4 replies, restoration of the untouched host default, LIFO order, snapshot copy semantics and numbered slots; palette entry 200 and a pop with nothing stored are '
    + 'policy groups that never decide; raw terminal state, not host presentation or painted pixels; no depth-report (XTREPORTCOLORS) claim, no kitty color-stack claim, no remote/mux, Windows/Linux or '
    + 'common-harness claim; the stock grades are unchanged by this suite.';
const stockOf = (raw, caseId) => {
    const observation = raw.observations.find(entry => entry.featureId === caseId);
    if (!observation) throw new Error(`Missing stock outcome: ${caseId}`);
    return observation;
};

export function xcsPublication(dir) {
    const context = json(dir, 'context.json'), receipt = json(dir, 'cell-receipt.json');
    const measured = json(dir, 'xcs-observations.json'), raw = json(dir, 'raw.json'), stock = json(dir, 'summary.json');
    const cases = measured.cases.map(item => ({ caseId: item.caseId, pinnedCriterion: item.pinnedCriterion, pinnedScript: item.pinnedScript, effectContract: item.effectContract,
        observed: item.observed, pinned: item.pinned, contractResult: item.contractResult, stock: stockOf(raw, item.caseId),
        ...(item.divergence ? { divergence: item.divergence } : {}), conclusion: item.conclusion }));
    if (JSON.stringify(cases.map(item => item.caseId)) !== JSON.stringify([...xcsCaseIds])) throw new Error('The published cases differ from the contracted cases.');
    const report = { schemaVersion: 1, suiteId: measured.suiteId, contractsVersion: measured.contractsVersion, runId: measured.runId, adapter: measured.adapter, scope, cases };
    const summary = { schemaVersion: 1, measuredAt: stock.measuredAt, ownedRunId: context.runId, stockCollectorRunId: raw.runId, appRevision: measuredAppRevision,
        scriptsRevision: measuredScriptsRevision, context, receipt, stockCollector: { counts: stock.counts, suiteComplete: stock.suiteComplete, rawSha256: stock.rawSha256, probeHash: raw.probeHash,
            sourceRevision: raw.sourceRevision, note: 'Stock terminal-path grades from the same owned run; both pinned probes stay inconclusive under the unchanged pinned code, so this suite moves no stock grade.' } };
    return { summary, report };
}
