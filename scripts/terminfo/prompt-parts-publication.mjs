import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { caseIds, contractsVersion, fileStem, suiteId } from './prompt-parts-contracts.mjs';
import { deriveRun } from './prompt-parts-owned.mjs';
import { pinnedMetadataProbes, replayPinned, verifySources } from './osc633-metadata-publication.mjs';
import { capturedPromptPartsSourceFiles, capturedPromptPartsSourcePolicy } from './owned-sources.mjs';
import { focusedMeasurement, pinnedBundle, readJson, readManifest, withOwnedArchive } from './unicode-publication-inputs.mjs';

const qa = fileURLToPath(new URL('../../docs/qa/terminfo/', import.meta.url));
export const publication = join(qa, '2026-11-27-contour-prompt-parts');
const manifestHash = 'abbede4cc2fa420eb7fe9bafb78de819ceb66d5748b6e987d93b89d079c2acaf';
export const withArchive = callback => withOwnedArchive(publication, readManifest(publication, manifestHash, assert.ok), callback);

export async function buildPublication() {
    const stock = readJson(qa, '2026-11-09-stock-baseline.json');
    assert.equal(stock.source.pinnedBundleSha256, pinnedBundle);
    const [probe] = pinnedMetadataProbes(readFileSync(join(qa, '2026-11-16-static-images/terminfo-dev-4.0.0.bundle.mjs.gz')),
        caseIds, '    {\n      ...probe("extensions.osc633-a",');
    const { context, receipt, measurement, frames, report } = withArchive((directory, manifest) => {
        const run = deriveRun(directory);
        assert.deepEqual(readJson(directory, fileStem + '-receipt.json'), run.receipt);
        assert.deepEqual(readJson(directory, fileStem + '-effects.json'), run.report);
        verifySources(directory, run.context, capturedPromptPartsSourceFiles, capturedPromptPartsSourcePolicy);
        const measurement = focusedMeasurement(directory, manifest, run.context, run.receipt, run.frames[0].before.screen);
        assert.match(measurement.appRevision, /^[0-9a-f]{40}$/);
        assert.match(measurement.scriptsRevision, /^[0-9a-f]{40}$/);
        assert.ok(Date.parse(measurement.releasedAt) >= Date.parse(readJson(directory, 'ownership-proof.json').sourceSnapshot.removedAt));
        return { ...run, measurement };
    });
    const frame = stage => frames.find(f => f.stage === stage);
    const pinned = await replayPinned(probe, frame('p-termless'), frame('p-terminal'));
    const [{ caseId, ...contractResult }] = report.cases;
    const outcome = pinned.termless.observation.outcome;
    return {
        summary: { ownedRunId: context.runId, measuredAt: context.startedAt, appRevision: measurement.appRevision,
            scriptsRevision: measurement.scriptsRevision, context, receipt,
            stockCollector: { rerun: false, note: 'Per-case grade carried forward from 2026-11-09-stock-baseline.json' } },
        report: { suiteId, contractsVersion, scope: 'One disposable direct-local macOS App pane, fenced PTY writes, paired pane metadata and session status.',
            pinnedBundleSha256: pinnedBundle, probeHash: stock.source.probeHash, upstreamCollectorRevision: stock.source.collectorSourceRevision,
            measurement, cases: [{ caseId, pinned, contractResult, stock: { outcome: stock.outcomes[caseId], runId: stock.source.stockCollectorRunId, rerun: false },
                conclusion: { outcome, grading: 'pinned', evidence: 'stored-cells+cpr+pane-metadata' },
                divergence: { kind: 'pinned-inconclusive-by-design', pinnedOutcome: outcome, contractOutcome: contractResult.outcome } }] },
    };
}

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
    assert.ok(process.argv.length === 3 && ['--write', '--check'].includes(process.argv[2]));
    for (const [name, value] of Object.entries(await buildPublication())) {
        if (process.argv[2] === '--check') assert.deepEqual(readJson(publication, name + '.json'), value);
        else writeFileSync(join(publication, name + '.json'), JSON.stringify(value, null, 2) + '\n');
    }
}
