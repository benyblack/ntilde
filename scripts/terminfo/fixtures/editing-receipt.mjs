// Synthetic receipt inputs only; these are not runtime measurements.
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { snapshotHash } from '../agent-observer.mjs';
import { syntheticEditingJournal, syntheticCellPlanJournal } from './editing-journal.mjs';
import { buildEditingReport } from '../editing.mjs';
import { buildHistoryReport } from '../history.mjs';
import { historySuiteId, makeHistoryCases } from '../history-cases.mjs';
import { ownedOutputPolicy, sttyTool } from '../terminal-output.mjs';
import { completeOwnedReceipt } from '../receipt.mjs';
import { pixelSuiteId } from '../pixels-cases.mjs';
import { colorSuiteId } from '../color-cases.mjs';
import { eraseSuiteId, editingSuiteId } from '../editing-cases.mjs';
export const runId = 'a'.repeat(32);
/** Derive the selected synthetic report from its source-owned fixture plan. */
function reportFor(prefix, rows, cols) {
    if (prefix === 'history') return buildHistoryReport(syntheticCellPlanJournal(prefix, runId, { casesFor: makeHistoryCases, nonce: 'NTILDE_HS_', rows, cols }), runId);
    return buildEditingReport(syntheticEditingJournal(prefix, runId, { rows, cols }), runId, prefix);
}
/** Provide isolated logical receipt inputs, optionally including the v5 transport proof. */
export function ownedReceiptFixture(action, { history = false, rows = 10, cols = 48 } = {}) {
    const output = mkdtempSync(join(tmpdir(), 'nt-edit-receipt-'));
    const write = (file, value) => writeFileSync(join(output, file), JSON.stringify(value));
    const context = { runId, pixelSuiteId, colorSuiteId, eraseSuiteId, editingSuiteId }, evidence = {};
    const proof = { completed: true, cleanedUpAt: '2026-11-03', cleanupError: null };
    if (history) {
        Object.assign(context, { historySuiteId, terminalOutput: ownedOutputPolicy });
        proof.proofs = [{ terminalOutput: { tool: sttyTool, outputProcessing: false, description: `speed 9600 baud; ${rows} rows; ${cols} columns;\noflags: -opost onlcr`, verifiedAt: '2026-11-03T00:00:00Z' } }];
    }
    try {
        write('context.json', context); write('raw.json', {});
        write('ownership-proof.json', proof);
        for (const prefix of ['cell', 'sgr', 'rgb', 'rendition', 'cursor', 'pixel', 'color']) {
            let value = {}, suiteId = prefix;
            const optional = prefix === 'pixel' || prefix === 'color';
            if (optional) {
                suiteId = prefix === 'pixel' ? pixelSuiteId : colorSuiteId;
                value = { runId, suiteId, status: 'unavailable', reason: 'screenDetailsUnavailable', fixtureStarted: false };
            }
            const file = prefix + (optional ? '-unavailable.json' : '-observations.json');
            write(file, value);
            const binding = { runId, suiteId, file, sha256: snapshotHash(value) };
            if (prefix === 'cell') Object.assign(evidence, binding); else evidence[prefix + 'Evidence'] = binding;
        }
        for (const prefix of history ? ['erase', 'editing', 'history'] : ['erase', 'editing']) {
            const report = reportFor(prefix, rows, cols);
            write(prefix + '-observations.json', report);
            writeFileSync(join(output, prefix + '-snapshots.jsonl'), report.snapshots.map(value => JSON.stringify(value)).join('\n'));
            evidence[prefix + 'Evidence'] = { runId, suiteId: report.suiteId, file: prefix + '-observations.json', sha256: snapshotHash(report), counts: { passed: report.observations.length } };
        }
        action({ output, context, proof, evidence, write, finish: () => completeOwnedReceipt(output, join(output, 'removed'), evidence) });
    } finally { rmSync(output, { recursive: true, force: true }); }
}
