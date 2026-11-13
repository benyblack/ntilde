import assert from 'node:assert/strict';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { insertReplacePublication } from './insert-replace-publication.mjs';
import { buildInsertReplaceReport } from './insert-replace-grading.mjs';
import { evidence, runId } from './fixtures/insert-replace-grading-evidence.mjs';

test('publication keeps pinned, effect and stock outcomes and measured revisions distinct', () => {
    const directory = mkdtempSync(join(tmpdir(), 'ntilde-irm-pub-test-'));
    const write = (name, data) => writeFileSync(join(directory, name), JSON.stringify(data));
    try {
        const { frames, run, adapter } = evidence();
        const measured = buildInsertReplaceReport(frames, run, runId, adapter);
        write('irm-observations.json', measured); write('context.json', { runId });
        write('cell-receipt.json', { insertReplaceEvidence: { runId, suiteId: measured.suiteId } });
        write('raw.json', { runId: 'stock-run', observations: [{ featureId: 'modes.insert-replace', outcome: 'inconclusive' }] });
        write('summary.json', { measuredAt: '2026-11-12T08:00:00Z', counts: {}, suiteComplete: false });
        const appRevision = 'a'.repeat(40), scriptsRevision = 'b'.repeat(40);
        const { summary, report } = insertReplacePublication(directory, { appRevision, scriptsRevision });
        assert.equal(summary.appRevision, appRevision); assert.equal(summary.scriptsRevision, scriptsRevision);
        assert.equal(report.cases[0].pinned.observation.outcome, 'supported');
        assert.equal(report.cases[0].contractResult.outcome, 'inconclusive');
        assert.equal(report.cases[0].stock.outcome, 'inconclusive');
        assert.equal(report.cases[0].terminalPinned.observation.outcome, 'inconclusive');
        assert.throws(() => insertReplacePublication(directory, { appRevision: 'unknown', scriptsRevision }), /revision/);
        write('summary.json', { measuredAt: '2026-11-12T08:00:00Z', counts: {}, suiteComplete: false, rawSha256: 'f'.repeat(64) });
        assert.throws(() => insertReplacePublication(directory, { appRevision, scriptsRevision }), /digest does not match/);
        write('summary.json', { rawSha256: '' });
        assert.throws(() => insertReplacePublication(directory, { appRevision, scriptsRevision }), /digest does not match/);
    } finally { rmSync(directory, { recursive: true, force: true }); }
});
