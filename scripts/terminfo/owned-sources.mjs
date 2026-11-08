import { literalImports } from './source-imports.mjs';
import { chmodSync, lstatSync, readFileSync, readdirSync, realpathSync, rmSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { dirname, join } from 'node:path';
import { tmpdir } from 'node:os';
export const capturedV8SourcePolicy = 'readonly-owned-source-graph-v1';
export const capturedV9SourcePolicy = 'readonly-owned-source-graph-v2';
export const capturedV10SourcePolicy = 'readonly-owned-source-graph-v3';
export const capturedV11SourcePolicy = 'readonly-owned-source-graph-v4';
export const sourceSnapshotPolicy = 'readonly-owned-source-graph-v14';
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
// Historical 1f4403f2 captured graph: keep its paths/policy independent of future inputs.
export const capturedV8SourceFiles = Object.freeze({
    ...legacyV8SourceFiles,
    runnerSourceSha256: 'terminfo-owned-runner.mjs',
    sourceInventorySha256: 'owned-source-inventory.json',
});
// The v9 scope is fixed independently of the evolving active inventory.
export const capturedV9SourceFiles = Object.freeze({
    ...capturedV8SourceFiles,
    inputBytesSourceSha256: 'input-bytes.mjs', inputCasesSourceSha256: 'input-cases.mjs',
    inputRequestSourceSha256: 'input-request.mjs', inputActionSourceSha256: 'input-action.mjs',
    inputStateSourceSha256: 'input-state.mjs', inputCaptureSourceSha256: 'input-capture.mjs',
    inputCollectorSourceSha256: 'input-collector.mjs', inputReceiptSourceSha256: 'input-receipt.mjs',
});
export const capturedV10SourceFiles = Object.freeze({
    ...capturedV9SourceFiles,
    staticImageInputsSourceSha256: 'static-images-inputs.mjs', staticImageCasesSourceSha256: 'static-images-cases.mjs',
    staticImageOracleSourceSha256: 'static-images-oracle.mjs', staticImageSuiteSourceSha256: 'static-images.mjs',
    staticImageCaptureSourceSha256: 'static-images-capture.mjs', staticImageCollectorSourceSha256: 'static-images-collector.mjs',
    staticImageReceiptSourceSha256: 'static-images-receipt.mjs',
});
// Frozen v11 query scope; future experiments must create a distinct policy and map.
export const capturedV11SourceFiles = Object.freeze({
    ...capturedV10SourceFiles,
    querySuiteSourceSha256: 'queries.mjs', queryCaptureSourceSha256: 'query-capture.mjs',
    queryCollectorSourceSha256: 'query-collector.mjs', queryReceiptSourceSha256: 'query-receipt.mjs',
    queryWitnessCasesSourceSha256: 'query-witness-cases.mjs', queryWitnessFrameSourceSha256: 'query-witness-frame.mjs',
    sourceImportsSourceSha256: 'source-imports.mjs',
});
// Selected observational App/VT reference inputs, not a complete App closure or binary-source attestation.
export const privateClipboardReferencePaths = Object.freeze({
    "clipboard-app-program.cs": "src/Ntilde.App/Program.cs",
    "clipboard-app.cs": "src/Ntilde.App/App.axaml.cs",
    "clipboard-private-mode.cs": "src/Ntilde.App/Shell/Clipboard/PrivateClipboardQaMode.cs",
    "clipboard-disposable-fixture.cs": "src/Ntilde.App/Shell/Clipboard/DisposableClipboardFixture.cs",
    "clipboard-main-window.cs": "src/Ntilde.App/MainWindow.ClipboardFixture.cs",
    "clipboard-main-window-resize.cs": "src/Ntilde.App/MainWindow.AgentResize.cs",
    "clipboard-pane.cs": "src/Ntilde.App/Controls/TerminalPane.Clipboard.cs",
    "clipboard-pane-session.cs": "src/Ntilde.App/Controls/TerminalPane.Session.cs",
    "clipboard-pane-lifecycle.cs": "src/Ntilde.App/Controls/TerminalPane.Lifecycle.cs",
    "clipboard-pane-settings.cs": "src/Ntilde.App/Controls/TerminalPane.axaml.cs",
    "clipboard-settings.cs": "src/Ntilde.App/Shell/TerminalSettings.cs",
    "clipboard-agent-service.cs": "src/Ntilde.App/AgentHost/AgentHostService.cs",
    "clipboard-agent-fixture.cs": "src/Ntilde.App/AgentHost/AgentHostService.ClipboardFixture.cs",
    "clipboard-agent-executor.cs": "src/Ntilde.App/AgentHost/IAgentActionExecutor.cs",
    "clipboard-contract.cs": "src/Ntilde.AgentHost.Contracts/ClipboardFixture.cs",
    "clipboard-session-contract.cs": "src/Ntilde.AgentHost.Contracts/SessionContracts.cs",
    "clipboard-agent-registration.cs": "src/Ntilde.App/AgentHost/AgentSessionRegistration.cs",
    "clipboard-vt-osc.cs": "src/Ntilde.VT/AnsiParser.Osc.cs",
    "clipboard-vt-csi.cs": "src/Ntilde.VT/AnsiParser.Csi.cs",
    "clipboard-agent-registry.cs": "src/Ntilde.App/AgentHost/AgentSessionRegistry.cs",
    "clipboard-agent-protocol.cs": "src/Ntilde.AgentHost.Contracts/AgentHostProtocol.cs"
});
export const capturedV12SourceFiles = Object.freeze({
    ...capturedV11SourceFiles,
    privateClipboardSourceSha256: "private-clipboard.mjs",
    privateClipboardCaptureSourceSha256: "private-clipboard-capture.mjs",
    privateClipboardReceiptSourceSha256: "private-clipboard-receipt.mjs",
    privateClipboardWorkerSourceSha256: "private-clipboard-worker.mjs",
    privateClipboardRequestSourceSha256: "private-clipboard-request.mjs",
    privateClipboardControllerSourceSha256: "private-clipboard-controller.mjs",
    privateClipboardSourceSourceSha256: "private-clipboard-source.mjs",
    privateClipboardBridgeSourceSha256: "private-clipboard-bridge.mjs",
    privateClipboardInput1SourceSha256: "clipboard-app-program.cs",
    privateClipboardInput2SourceSha256: "clipboard-app.cs",
    privateClipboardInput3SourceSha256: "clipboard-private-mode.cs",
    privateClipboardInput4SourceSha256: "clipboard-disposable-fixture.cs",
    privateClipboardInput5SourceSha256: "clipboard-main-window.cs",
    privateClipboardInput6SourceSha256: "clipboard-main-window-resize.cs",
    privateClipboardInput7SourceSha256: "clipboard-pane.cs",
    privateClipboardInput8SourceSha256: "clipboard-pane-session.cs",
    privateClipboardInput9SourceSha256: "clipboard-pane-lifecycle.cs",
    privateClipboardInput10SourceSha256: "clipboard-pane-settings.cs",
    privateClipboardInput11SourceSha256: "clipboard-settings.cs",
    privateClipboardInput12SourceSha256: "clipboard-agent-service.cs",
    privateClipboardInput13SourceSha256: "clipboard-agent-fixture.cs",
    privateClipboardInput14SourceSha256: "clipboard-agent-executor.cs",
    privateClipboardInput15SourceSha256: "clipboard-contract.cs",
    privateClipboardInput16SourceSha256: "clipboard-session-contract.cs",
    privateClipboardInput17SourceSha256: "clipboard-agent-registration.cs",
    privateClipboardInput18SourceSha256: "clipboard-vt-osc.cs",
    privateClipboardInput19SourceSha256: "clipboard-vt-csi.cs",
    privateClipboardInput20SourceSha256: "clipboard-agent-registry.cs",
    privateClipboardInput21SourceSha256: "clipboard-agent-protocol.cs",
});
// Frozen v13 ASCII/SGR grading scope; future experiments must create a distinct policy and map.
export const capturedV13SourceFiles = Object.freeze({
    ...capturedV12SourceFiles,
    gradingCollectorSourceSha256: "grading-collector.mjs",
    asciiSgrContractsSourceSha256: "ascii-sgr-contracts.mjs",
    asciiSgrCollectorSourceSha256: "ascii-sgr-collector.mjs",
    asciiSgrGradingSourceSha256: "ascii-sgr-grading.mjs",
    asciiSgrReceiptSourceSha256: "ascii-sgr-receipt.mjs",
});
// Frozen v14 SGR-colors grading scope (113 files, as measured under policy v7).
export const capturedV14SourceFiles = Object.freeze({
    ...capturedV13SourceFiles,
    sgrColorsGradingCollectorSourceSha256: "sgr-colors-grading-collector.mjs",
    sgrColorsContractsSourceSha256: "sgr-colors-contracts.mjs",
    sgrColorsCollectorSourceSha256: "sgr-colors-collector.mjs",
    sgrColorsGradingSourceSha256: "sgr-colors-grading.mjs",
    sgrColorsReceiptSourceSha256: "sgr-colors-receipt.mjs",
});
// Frozen v15 shared graded-plan scope; future experiments must create a distinct policy and map.
export const capturedV15SourceFiles = Object.freeze({
    ...capturedV14SourceFiles,
    gradedPlanSourceSha256: "graded-plan.mjs",
});
// Selected App/VT render-resolution reference inputs for the SGR-RGB batch, not a
// complete App closure or binary-source attestation. Inputs the clipboard scope
// already captures (SessionContracts.cs, AgentHostService.cs,
// AgentSessionRegistration.cs) are deliberately not duplicated here: one captured
// byte set per file, under its established alias.
export const sgrRgbReferencePaths = Object.freeze({
    "sgr-rgb-resolved-color-snapshot.cs": "src/Ntilde.App/AgentHost/ResolvedColorSnapshot.cs",
    "sgr-rgb-draw-operation.cs": "src/Ntilde.App/Shell/TerminalDrawOperation.cs",
    "sgr-rgb-render-snapshots.cs": "src/Ntilde.VT/RenderSnapshots.cs",
});
// Frozen v16 SGR-RGB grading scope; future experiments must create a distinct policy and map.
export const capturedV16SourceFiles = Object.freeze({
    ...capturedV15SourceFiles,
    resolvedColorsSourceSha256: "resolved-colors.mjs",
    sgrRgbContractsSourceSha256: "sgr-rgb-contracts.mjs",
    sgrRgbCollectorSourceSha256: "sgr-rgb-collector.mjs",
    sgrRgbGradingSourceSha256: "sgr-rgb-grading.mjs",
    sgrRgbGradingCollectorSourceSha256: "sgr-rgb-grading-collector.mjs",
    sgrRgbReceiptSourceSha256: "sgr-rgb-receipt.mjs",
    sgrRgbInput1SourceSha256: "sgr-rgb-resolved-color-snapshot.cs",
    sgrRgbInput2SourceSha256: "sgr-rgb-draw-operation.cs",
    sgrRgbInput3SourceSha256: "sgr-rgb-render-snapshots.cs",
});
// Frozen v17 SGR-attributes grading scope; future experiments must create a distinct policy and map.
export const capturedV17SourceFiles = Object.freeze({
    ...capturedV16SourceFiles,
    sgrAttrsContractsSourceSha256: "sgr-attrs-contracts.mjs",
    sgrAttrsCollectorSourceSha256: "sgr-attrs-collector.mjs",
    sgrAttrsGradingSourceSha256: "sgr-attrs-grading.mjs",
    sgrAttrsGradingCollectorSourceSha256: "sgr-attrs-grading-collector.mjs",
    sgrAttrsReceiptSourceSha256: "sgr-attrs-receipt.mjs",
});
// Frozen v18 SGR-palette grading scope; future experiments must create a distinct policy and map.
export const capturedV18SourceFiles = Object.freeze({
    ...capturedV17SourceFiles,
    sgrPaletteContractsSourceSha256: "sgr-palette-contracts.mjs",
    sgrPaletteCollectorSourceSha256: "sgr-palette-collector.mjs",
    sgrPaletteGradingSourceSha256: "sgr-palette-grading.mjs",
    sgrPaletteGradingCollectorSourceSha256: "sgr-palette-grading-collector.mjs",
    sgrPaletteReceiptSourceSha256: "sgr-palette-receipt.mjs",
});
/** Resolve paths from the caller's fixed inventory; selected C# inputs remain nonexecuted reference bytes. */
export function ownedSourcePath(root, file) {
    if (Object.hasOwn(privateClipboardReferencePaths, file)) return join(root, privateClipboardReferencePaths[file]);
    if (Object.hasOwn(sgrRgbReferencePaths, file)) return join(root, sgrRgbReferencePaths[file]);
    const relative = file === 'terminfo-owned-runner.mjs' || file === 'terminfo-owned.mjs' ? file : 'terminfo/' + file;
    return join(root, 'scripts', relative);
}
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
/** Literal relative imports must close over the retained fixed scope; never fall back to live sources. */
export function assertRetainedSourceClosure(directory, inventory) {
    const paths = new Set(Object.values(inventory));
    for (const file of paths) {
        if (!file.endsWith('.mjs')) continue;
        const source = readFileSync(join(directory, file), 'utf8');
        const imports = literalImports(source);
        for (const target of imports) {
            if (/^node:[a-z][a-z0-9_/-]*$/.test(target)) continue;
            if (!/^\.\/(?:terminfo\/)?[a-z][a-z0-9-]*\.mjs$/.test(target)
                || !paths.has(target.split('/').at(-1))) throw new Error('Retained source dependency is outside its versioned scope: ' + target);
        }
    }
}
/** Rehash every fixed current input, including nonexecuted C# references; any missing or changed byte prevents completion. */
export function assertOwnedSourcesUnchanged(root, context) {
    for (const [key, file] of Object.entries(ownedSourceFiles)) {
        if (digest(readFileSync(ownedSourcePath(root, file))) !== context[key]) throw new Error('Owned source changed before completion: ' + file);
    }
}
// Frozen v19 SGR-truecolor grading scope; future experiments must create a distinct policy and map.
export const capturedV19SourceFiles = Object.freeze({
    ...capturedV18SourceFiles,
    sgrTruecolorContractsSourceSha256: "sgr-truecolor-contracts.mjs",
    sgrTruecolorCollectorSourceSha256: "sgr-truecolor-collector.mjs",
    sgrTruecolorGradingSourceSha256: "sgr-truecolor-grading.mjs",
    sgrTruecolorGradingCollectorSourceSha256: "sgr-truecolor-grading-collector.mjs",
    sgrTruecolorReceiptSourceSha256: "sgr-truecolor-receipt.mjs",
    sgrColorCoreSourceSha256: "sgr-color-core.mjs",
    sgrColorGradingCoreSourceSha256: "sgr-color-grading-core.mjs",
});

// Frozen v20 truecolor-literal finding scope; future experiments must create a distinct policy and map.
export const capturedV20SourceFiles = Object.freeze({
    ...capturedV19SourceFiles,
    truecolorFindingContractsSourceSha256: "truecolor-literal-finding-contracts.mjs",
    truecolorFindingCollectorSourceSha256: "truecolor-literal-finding-collector.mjs",
    truecolorFindingGradingSourceSha256: "truecolor-literal-finding-grading.mjs",
    truecolorFindingReceiptSourceSha256: "truecolor-literal-finding-receipt.mjs",
});

// Frozen v21 SGR-underline grading scope; future experiments must create a distinct policy and map.
export const capturedV21SourceFiles = Object.freeze({
    ...capturedV20SourceFiles,
    sgrUnderlineContractsSourceSha256: "sgr-underline-contracts.mjs",
    sgrUnderlineCollectorSourceSha256: "sgr-underline-collector.mjs",
    sgrUnderlineGradingSourceSha256: "sgr-underline-grading.mjs",
    sgrUnderlineGradingCollectorSourceSha256: "sgr-underline-grading-collector.mjs",
    sgrUnderlineReceiptSourceSha256: "sgr-underline-receipt.mjs",
});

/** Bind the private temporary root, exact v21 inventory and manifest environment pin before trusting any captured source. */
export function readSourceSnapshot(root) {
    if (realpathSync(root) !== process.env.NTILDE_TERMINFO_SOURCE_SNAPSHOT
        || dirname(root) !== realpathSync(tmpdir()) || !root.split(/[\\/]/).at(-1).startsWith('ntilde-owned-sources-')) throw new Error('Expected the private captured source tree.');
    if (JSON.stringify(ownedSourceFiles) !== JSON.stringify(capturedV21SourceFiles)) throw new Error('Invalid current v21 source inventory.');
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
