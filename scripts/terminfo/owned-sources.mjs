import { chmodSync, lstatSync, readFileSync, readdirSync, realpathSync, rmSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { dirname, join } from 'node:path';
import { tmpdir } from 'node:os';
export const sourceSnapshotPolicy = 'readonly-owned-source-graph-v1';
export const ownedSourceFiles = Object.freeze(JSON.parse(readFileSync(new URL('./owned-source-inventory.json', import.meta.url))));
// Historical dc32fc8e scope: never derive it from the evolving measured graph.
export const legacyV8SourceFiles = Object.freeze({
    "runnerSourceSha256": "terminfo-owned.mjs",
    "adapterSourceSha256": "collector.mjs",
    "ownershipSourceSha256": "ownership.mjs",
    "workerSourceSha256": "worker.mjs",
    "runtimeSourceSha256": "runtime.mjs",
    "observerSourceSha256": "agent-observer.mjs",
    "cellSuiteSourceSha256": "cells.mjs",
    "cellFenceSourceSha256": "terminal-fence.mjs",
    "attributeSourceSha256": "attributes.mjs",
    "sgrSuiteSourceSha256": "sgr.mjs",
    "cellDetailsSourceSha256": "cell-details.mjs",
    "rgbSuiteSourceSha256": "rgb.mjs",
    "renditionSuiteSourceSha256": "rendition.mjs",
    "cursorSuiteSourceSha256": "cursor.mjs",
    "pixelCaptureSourceSha256": "pixels-capture.mjs",
    "pixelSuiteSourceSha256": "pixels.mjs",
    "pixelCasesSourceSha256": "pixels-cases.mjs",
    "pixelPngSourceSha256": "pixels-png.mjs",
    "colorCaptureSourceSha256": "color-capture.mjs",
    "colorSuiteSourceSha256": "colors.mjs",
    "colorCasesSourceSha256": "color-cases.mjs",
    "colorQuerySourceSha256": "color-query.mjs",
    "editingStateSourceSha256": "editing-state.mjs",
    "editingCasesSourceSha256": "editing-cases.mjs",
    "editingExpectationsSourceSha256": "editing-expectations.mjs",
    "editingCaptureSourceSha256": "editing-capture.mjs",
    "editingSuiteSourceSha256": "editing.mjs",
    "metadataCasesSourceSha256": "metadata-cases.mjs",
    "metadataCaptureSourceSha256": "metadata-capture.mjs",
    "metadataStateSourceSha256": "metadata-state.mjs",
    "metadataExpectationsSourceSha256": "metadata-expectations.mjs",
    "metadataSuiteSourceSha256": "metadata.mjs",
    "metadataReceiptSourceSha256": "metadata-receipt.mjs",
    "savedCasesSourceSha256": "saved-cases.mjs",
    "savedCaptureSourceSha256": "saved-capture.mjs",
    "savedSuiteSourceSha256": "saved.mjs",
    "savedExpectationsSourceSha256": "saved-expectations.mjs",
    "savedReceiptSourceSha256": "saved-receipt.mjs",
    "resizeCasesSourceSha256": "resize-cases.mjs",
    "resizeCaptureSourceSha256": "resize-capture.mjs",
    "resizeSuiteSourceSha256": "resize.mjs",
    "resizeActionSourceSha256": "resize-action.mjs",
    "resizeReceiptSourceSha256": "resize-receipt.mjs",
    "historyCasesSourceSha256": "history-cases.mjs",
    "historyCaptureSourceSha256": "history-capture.mjs",
    "historySuiteSourceSha256": "history.mjs",
    "terminalOutputSourceSha256": "terminal-output.mjs",
    "receiptSourceSha256": "receipt.mjs",
    "collectorManifestSha256": "collector-package.json",
    "collectorLockSha256": "collector-package-lock.json",
    "sourceRegistrySourceSha256": "owned-sources.mjs"
});
export const ownedSourcePath = (root, file) => join(root, 'scripts', file === 'terminfo-owned-runner.mjs' || file === 'terminfo-owned.mjs' ? file : 'terminfo/' + file);
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
export function assertOwnedSourcesUnchanged(root, context) {
    for (const [key, file] of Object.entries(ownedSourceFiles)) {
        if (digest(readFileSync(ownedSourcePath(root, file))) !== context[key]) throw new Error('Owned source changed before completion: ' + file);
    }
}
export function readSourceSnapshot(root) {
    if (realpathSync(root) !== process.env.NTILDE_TERMINFO_SOURCE_SNAPSHOT
        || dirname(root) !== realpathSync(tmpdir()) || !root.split(/[\\/]/).at(-1).startsWith('ntilde-owned-sources-')) throw new Error('Expected the private captured source tree.');
    const bytes = readFileSync(join(root, 'source-snapshot.json'));
    if (digest(bytes) !== process.env.NTILDE_TERMINFO_SOURCE_SNAPSHOT_SHA256) throw new Error('Source snapshot manifest changed.');
    const manifest = JSON.parse(bytes);
    if (manifest.schemaVersion !== 1 || manifest.policy !== sourceSnapshotPolicy
        || Object.keys(manifest.hashes ?? {}).length !== Object.keys(ownedSourceFiles).length) throw new Error('Invalid source snapshot manifest.');
    assertOwnedSourcesUnchanged(root, manifest.hashes);
    return { bytes, sha256: digest(bytes), manifest };
}
function writableDirectories(root) {
    for (const entry of readdirSync(root, { withFileTypes: true })) if (entry.isDirectory()) writableDirectories(join(root, entry.name));
    chmodSync(root, 0o700);
}
export function removeSourceSnapshot(root) {
    readSourceSnapshot(root); writableDirectories(root); rmSync(root, { recursive: true, force: true });
    try { lstatSync(root); throw new Error('Captured source tree remains.'); }
    catch (error) { if (error.code !== 'ENOENT') throw error; }
}
