// Publication-only projection; the receipt and retained journal are rederived
// independently before this projection is compared with the published files.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { cellSizeIds } from './cell-size-contracts.mjs';

const json = (dir, name) => JSON.parse(readFileSync(join(dir, name)));
const scope = 'Exact OSC 1337 ReportCellSize and OSC 776 question-mark query effects in a disposable direct-local macOS pane, '
    + 'graded from paired raw replies and independently observed view dimensions, font ascent, effective render scale and display backing scale. '
    + 'The unchanged pinned two-value OSC 1337 criterion and bare OSC 776 request retain their own results. '
    + 'Completed DA barriers, CSI 16 t delivery controls, BEL/ST framing, unsupported-form controls, unchanged stored state and restoration are required. '
    + 'No resize is needed for the cell-size experiment. No rendered-pixel, physical-input, remote/mux, Windows/Linux, frozen Core/Modern or common-harness score claim.';

export function cellSizePublication(dir, { appRevision, scriptsRevision }) {
    if (![appRevision, scriptsRevision].every(value => /^[a-f\d]{40}$/.test(value ?? ''))) throw new Error('Missing measured revision identities.');
    const context = json(dir, 'context.json'), receipt = json(dir, 'cell-receipt.json');
    const measured = json(dir, 'cell-size-observations.json'), raw = json(dir, 'raw.json'), stock = json(dir, 'summary.json');
    const cases = measured.cases.map(item => {
        const observation = raw.observations.find(entry => entry.featureId === item.caseId);
        if (!observation) throw new Error('Missing OSC cell-size stock observation.');
        return { ...item, stock: observation };
    });
    if (JSON.stringify(cases.map(item => item.caseId)) !== JSON.stringify(cellSizeIds)) throw new Error('Published cell-size cases differ.');
    const report = { schemaVersion: 1, suiteId: measured.suiteId, contractsVersion: measured.contractsVersion,
        runId: measured.runId, adapter: measured.adapter, scope, cases };
    const summary = { schemaVersion: 1, measuredAt: stock.measuredAt, ownedRunId: context.runId, stockCollectorRunId: raw.runId,
        appRevision, scriptsRevision, context, receipt, stockCollector: { counts: stock.counts, suiteComplete: stock.suiteComplete,
            rawSha256: stock.rawSha256, probeHash: raw.probeHash, sourceRevision: raw.sourceRevision,
            note: 'Stock grades collected in this same scoped run. Omitted notification suites retain their separate pinned predecessor receipt; combined evidence is not a fresh complete default run.' } };
    return { summary, report };
}
