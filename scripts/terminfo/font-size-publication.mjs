// Publication-only projection; the receipt and retained journal are rederived
// independently before this projection is compared with the published files.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fontSizeIds } from './font-size-contracts.mjs';

const json = (dir, name) => JSON.parse(readFileSync(join(dir, name)));
const scope = 'Read-only exact OSC7770/7777 integer typographic point queries in a disposable direct-local macOS pane. '
    + 'Raw framed replies are graded against actual view FontSize and effective/backing scales; actual client geometry and stored state must remain unchanged. '
    + 'Completed DA barriers, CSI16t controls, BEL/ST, unavailable mutation forms and verified restoration/cleanup are mandatory. '
    + 'OSC710 remains a separate no-operation pinned audit, outside the graded inventory. No font/window mutation, glyph appearance, remote/mux, other-platform or Core/common-harness score claim.';

export function fontSizePublication(dir, { appRevision, scriptsRevision }) {
    if (![appRevision, scriptsRevision].every(value => /^[a-f\d]{40}$/.test(value ?? ''))) throw new Error('Missing measured revision identities.');
    const context = json(dir, 'context.json'), receipt = json(dir, 'cell-receipt.json');
    const measured = json(dir, 'font-size-observations.json'), raw = json(dir, 'raw.json'), stock = json(dir, 'summary.json');
    const cases = measured.cases.map(item => {
        const observation = raw.observations.find(entry => entry.featureId === item.caseId);
        if (!observation) throw new Error('Missing OSC font-size stock observation.');
        return { ...item, stock: observation };
    });
    if (JSON.stringify(cases.map(item => item.caseId)) !== JSON.stringify(fontSizeIds)) throw new Error('Published font-size cases differ.');
    const report = { schemaVersion: 1, suiteId: measured.suiteId, contractsVersion: measured.contractsVersion,
        runId: measured.runId, adapter: measured.adapter, scope, cases };
    const summary = { schemaVersion: 1, measuredAt: stock.measuredAt, ownedRunId: context.runId, stockCollectorRunId: raw.runId,
        appRevision, scriptsRevision, context, receipt, stockCollector: { counts: stock.counts, suiteComplete: stock.suiteComplete,
            rawSha256: stock.rawSha256, probeHash: raw.probeHash, sourceRevision: raw.sourceRevision,
            note: 'Fresh stock grades from this same complete owned run; frozen inventory baseline and Core/common-harness scores remain separate.' } };
    return { summary, report };
}
