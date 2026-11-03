// Synthetic v7 receipt fixtures, never runtime qualification.
import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { ownedResizeReceiptFixture, runId } from './resize-receipt.mjs';
import { syntheticSavedJournal } from './saved-journal.mjs';
import { buildSavedReport } from '../saved.mjs';
import { savedSuiteId, ownedSavedPolicy } from '../saved-cases.mjs';
import { savedSources } from '../saved-receipt.mjs';
import { snapshotHash } from '../agent-observer.mjs';
export { runId };
export function ownedSavedReceiptFixture(action) {
    return ownedResizeReceiptFixture(f => {
        const frames = syntheticSavedJournal(runId), report = buildSavedReport(frames, runId);
        Object.assign(f.context, { savedSuiteId, ownedSavedPolicy });
        for (const [key, file] of Object.entries(savedSources)) {
            const data = readFileSync(new URL('../' + file, import.meta.url));
            writeFileSync(join(f.output, 'sources', file), data); f.context[key] = createHash('sha256').update(data).digest('hex');
        }
        f.proof.proofs[0].saved = { policy: ownedSavedPolicy, paneId: frames[0].paneId, geometry: { rows: 12, cols: 60 } };
        f.write('context.json', f.context); f.write('ownership-proof.json', f.proof); f.write('saved-observations.json', report);
        writeFileSync(join(f.output, 'saved-snapshots.jsonl'), frames.map(value => JSON.stringify(value)).join('\n'));
        f.evidence.savedEvidence = { runId, suiteId: savedSuiteId, file: 'saved-observations.json', sha256: snapshotHash(report), counts: { passed: 9 } };
        action({ ...f, frames, savedReport: report });
    });
}
