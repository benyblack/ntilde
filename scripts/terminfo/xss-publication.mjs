// A publication-only projection of the independently rederived retained owned run for the xterm SGR-stack qualification.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { xssCaseIds } from './xss-contracts.mjs';

const json = (dir, file) => JSON.parse(readFileSync(join(dir, file)));
export const measuredScriptsRevision = '684b99920428be3bed63dbfee1eb1dc3f4d66830';
export const measuredAppRevision = '7ba6fff36c36c1e4b805ad252c805835cfb0c42e';
const scope = 'Pinned terminfo.dev@4.0.0 criteria for modes.xtpushsgr (CSI # {) and modes.xtpopsgr (CSI # }), which can only return inconclusive (they measure DA1 and cursor responsiveness, not '
    + 'saved/restored stack state), replayed byte-exactly over the journaled raw bytes of their own fenced exchange and the cursor of its read, beside a separately identified effect contract graded by an '
    + 'independent oracle written from xterm\'s control-sequence text: silence of both sequences, and the stored bold, faint, italic, underline, blink, inverse, hidden, strikethrough, foreground and background '
    + 'of a probe character\'s cell read back after pops, the selection of each documented push parameter alone (1, 2, 3, 4, 5, 7, 8, 9, 21, 30, 31), the argument-less push saving all of them, LIFO order, '
    + 'snapshot copy semantics, ten levels holding and the baseline pen restored; the eleventh push, a pop with nothing saved, a value the text does not list and the overline and underline color are policy '
    + 'groups that never decide; stored cell attributes, not host presentation or painted pixels; no XTREPORTSGR (CSI # |) claim, no CSI Pm # p or CSI # q alias claim, no remote/mux, Windows/Linux or '
    + 'common-harness claim; the stock grades are unchanged by this suite.';
const stockOf = (raw, caseId) => {
    const observation = raw.observations.find(entry => entry.featureId === caseId);
    if (!observation) throw new Error(`Missing stock outcome: ${caseId}`);
    return observation;
};

export function xssPublication(dir) {
    const context = json(dir, 'context.json'), receipt = json(dir, 'cell-receipt.json');
    const measured = json(dir, 'xss-observations.json'), raw = json(dir, 'raw.json'), stock = json(dir, 'summary.json');
    const cases = measured.cases.map(item => ({ caseId: item.caseId, pinnedCriterion: item.pinnedCriterion, pinnedScript: item.pinnedScript, effectContract: item.effectContract,
        observed: item.observed, pinned: item.pinned, contractResult: item.contractResult, stock: stockOf(raw, item.caseId),
        ...(item.divergence ? { divergence: item.divergence } : {}), conclusion: item.conclusion }));
    if (JSON.stringify(cases.map(item => item.caseId)) !== JSON.stringify([...xssCaseIds])) throw new Error('The published cases differ from the contracted cases.');
    const report = { schemaVersion: 1, suiteId: measured.suiteId, contractsVersion: measured.contractsVersion, runId: measured.runId, adapter: measured.adapter, scope, cases };
    const summary = { schemaVersion: 1, measuredAt: stock.measuredAt, ownedRunId: context.runId, stockCollectorRunId: raw.runId, appRevision: measuredAppRevision,
        scriptsRevision: measuredScriptsRevision, context, receipt, stockCollector: { counts: stock.counts, suiteComplete: stock.suiteComplete, rawSha256: stock.rawSha256, probeHash: raw.probeHash,
            sourceRevision: raw.sourceRevision, note: 'Stock terminal-path grades from the same owned run; both pinned probes stay inconclusive under the unchanged pinned code, so this suite moves no stock grade.' } };
    return { summary, report };
}
