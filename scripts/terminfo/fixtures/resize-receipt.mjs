// Synthetic v6 receipt data for refusal tests; none of these records are runtime measurements.
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { ownedReceiptFixture, runId } from './editing-receipt.mjs';
import { syntheticResizeJournal } from './resize-journal.mjs';
import { buildResizeReport } from '../resize.mjs';
import { resizeSuiteId, ownedResizePolicy } from '../resize-cases.mjs';
import { resizeSources } from '../resize-receipt.mjs';
import { snapshotHash } from '../agent-observer.mjs';
export { runId };
export function ownedResizeReceiptFixture(action) {
    return ownedReceiptFixture(f => {
        const { frames, actions } = syntheticResizeJournal(runId), report = buildResizeReport(frames, actions, runId);
        Object.assign(f.context, { resizeSuiteId, ownedResizePolicy });
        const settings = { AgentAccessObserveEnabled: true, AgentAccessActEnabled: true, SessionPersistence: 'Off', Profiles: [{ Type: 0 }] };
        f.write('initial-settings.json', settings); f.context.initialSettingsSha256 = snapshotHash(settings);
        mkdirSync(join(f.output, 'sources'));
        for (const [key, file] of Object.entries(resizeSources)) {
            const data = readFileSync(new URL('../' + file, import.meta.url));
            writeFileSync(join(f.output, 'sources', file), data); f.context[key] = snapshotHashSource(data);
        }
        f.proof.proofs[0].resize = { policy: ownedResizePolicy, paneId: frames[0].paneId,
            originalGeometry: { rows: 12, cols: 60 }, actions };
        f.write('ownership-proof.json', f.proof); f.write('context.json', f.context);
        const cell = { snapshots: [{ paneId: frames[0].paneId, screen: { rows: 12, cols: 60 } }] };
        f.write('cell-observations.json', cell); f.evidence.sha256 = snapshotHash(cell);
        f.write('resize-observations.json', report);
        writeFileSync(join(f.output, 'resize-snapshots.jsonl'), frames.map(value => JSON.stringify(value)).join('\n'));
        writeFileSync(join(f.output, 'resize-actions.jsonl'), actions.map(value => JSON.stringify(value)).join('\n'));
        f.evidence.resizeEvidence = { runId, suiteId: resizeSuiteId, file: 'resize-observations.json', sha256: snapshotHash(report), counts: { passed: 3 } };
        action({ ...f, frames, actions, report });
    }, { history: true, rows: 12, cols: 60 });
}
import { createHash } from 'node:crypto';
const snapshotHashSource = bytes => createHash('sha256').update(bytes).digest('hex');
