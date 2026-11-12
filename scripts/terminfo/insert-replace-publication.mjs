// Projection for the live inventory; the receipt checker remains the authority
// on retained bytes, journal identity, cleanup and rederivation.
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { join } from 'node:path';

const json = (directory, file) => JSON.parse(readFileSync(join(directory, file)));

export function insertReplacePublication(directory, { appRevision, scriptsRevision }) {
    if (![appRevision, scriptsRevision].every(value => /^[a-f\d]{40}$/.test(value ?? ''))) throw new Error('Missing measured App or scripts revision.');
    const context = json(directory, 'context.json'), receipt = json(directory, 'cell-receipt.json');
    const measured = json(directory, 'irm-observations.json'), raw = json(directory, 'raw.json'), stock = json(directory, 'summary.json');
    if (stock.rawSha256 !== undefined && (!/^[a-f\d]{64}$/.test(stock.rawSha256)
        || createHash('sha256').update(readFileSync(join(directory, 'raw.json'))).digest('hex') !== stock.rawSha256)) {
        throw new Error('Stock summary digest does not match raw.json.');
    }
    if (!context.runId || measured.runId !== context.runId || receipt.insertReplaceEvidence?.runId !== context.runId
        || receipt.insertReplaceEvidence?.suiteId !== measured.suiteId || !raw.runId) throw new Error('Publication run identity does not match the receipt.');
    const stockObservation = raw.observations.find(item => item.featureId === 'modes.insert-replace');
    if (!stockObservation) throw new Error('Missing stock insert/replace observation.');
    const pinnedOutcome = measured.pinned.observation.outcome, effectOutcome = measured.effect.outcome;
    const item = { caseId: 'modes.insert-replace', pinned: measured.pinned, terminalPinned: measured.terminalPinned,
        terminalCursorEvidence: measured.terminalCursorEvidence, contractResult: measured.effect, stock: stockObservation,
        observed: measured.observed,
        ...(pinnedOutcome !== effectOutcome ? { divergence: { kind: 'pinned-termless-vs-effect', pinnedOutcome, contractOutcome: effectOutcome } } : {}),
        conclusion: { outcome: pinnedOutcome, evidence: 'owned-stored-cells', grading: 'pinned-termless-replayed-over-owned-observations' } };
    const report = { schemaVersion: 1, suiteId: measured.suiteId, contractsVersion: measured.contractsVersion, runId: measured.runId,
        adapter: measured.adapter, scope: 'Bounded stored ASCII cells, cursor and stored attributes on the owned disposable App; terminal-path cursor projection and stock collector remain separate.', cases: [item] };
    const summary = { schemaVersion: 1, measuredAt: stock.measuredAt, ownedRunId: context.runId, stockCollectorRunId: raw.runId,
        appRevision, scriptsRevision, context, receipt, stockCollector: { counts: stock.counts, suiteComplete: stock.suiteComplete,
            rawSha256: stock.rawSha256, probeHash: raw.probeHash, sourceRevision: raw.sourceRevision,
            note: 'Stock collector result remains separate from the owned effect.' } };
    return { summary, report };
}
