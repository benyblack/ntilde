import { literalImports } from './source-imports.mjs';
import { chmodSync, lstatSync, readFileSync, readdirSync, realpathSync, rmSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { dirname, join } from 'node:path';
import { tmpdir } from 'node:os';
export const capturedV8SourcePolicy = 'readonly-owned-source-graph-v1';
export const capturedV9SourcePolicy = 'readonly-owned-source-graph-v2';
export const capturedV10SourcePolicy = 'readonly-owned-source-graph-v3';
export const capturedV11SourcePolicy = 'readonly-owned-source-graph-v4';
export const capturedViewportSourcePolicy = 'readonly-owned-source-graph-v76';
export const capturedOscTitlesSourcePolicy = 'readonly-owned-source-graph-v77';
export const capturedChildEnvironmentSourcePolicy = 'readonly-owned-source-graph-v79';
export const capturedOscCwdSourcePolicy = 'readonly-owned-source-graph-v78';
export const capturedSixelReportsSourcePolicy = 'readonly-owned-source-graph-v85';
export const capturedOsc66SourcePolicy = 'readonly-owned-source-graph-v84';
export const capturedKittyGraphicsSourcePolicy = 'readonly-owned-source-graph-v83';
export const capturedUnicodeBoundarySourcePolicy = 'readonly-owned-source-graph-v82';
export const capturedUnicodeWidthSourcePolicy = 'readonly-owned-source-graph-v81';
export const capturedBlinkSourcePolicy = 'readonly-owned-source-graph-v75';
export const ownedSourceFiles = Object.freeze(JSON.parse(readFileSync(new URL('./owned-source-inventory.json', import.meta.url))));
const focusedSourcePolicies = [
    ['sixelReportsOwnedSourceSha256', capturedSixelReportsSourcePolicy],
    ['osc66OwnedSourceSha256', capturedOsc66SourcePolicy],
    ['kittyGraphicsOwnedSourceSha256', capturedKittyGraphicsSourcePolicy],
    ['unicodeBoundaryContractsSourceSha256', capturedUnicodeBoundarySourcePolicy],
    ['unicodeWidthContractsSourceSha256', capturedUnicodeWidthSourcePolicy],
    ['modernBlinkSourceSha256', capturedBlinkSourcePolicy],
    ['modernViewportSourceSha256', capturedViewportSourcePolicy],
    ['modernOscTitlesSourceSha256', capturedOscTitlesSourcePolicy],
    ['modernChildEnvironmentSourceSha256', capturedChildEnvironmentSourcePolicy],
    ['modernOscCwdSourceSha256', capturedOscCwdSourcePolicy],
];
export const sourceSnapshotPolicy = focusedSourcePolicies.find(([field]) => Object.hasOwn(ownedSourceFiles, field))?.[1]
    ?? 'readonly-owned-source-graph-v80';
// Frozen forks are selected only by valid own-family source digests and absence of every other family's marker.
const graphDigest = value => typeof value === 'string' && /^[a-f\d]{64}$/.test(value);
const familyMarker = (context, prefix) => Object.keys(context).some(key => key.startsWith(prefix) && key.endsWith('Sha256'));
const ownDigest = (context, key) => Object.hasOwn(context, key) && graphDigest(context[key]);
const policyIs = (context, version) => context.sourceSnapshotPolicy === 'readonly-owned-source-graph-v' + version;
export const isCellSizePolicy63 = context => policyIs(context, 63) && ownDigest(context, 'cellSizeContractsSourceSha256')
    && !familyMarker(context, 'kittyAnimation') && !familyMarker(context, 'osc5');
export const isKittyAnimationPolicy63 = context => policyIs(context, 63) && ownDigest(context, 'kittyAnimationInputsSourceSha256')
    && !familyMarker(context, 'cellSize') && !familyMarker(context, 'osc5');
export const isOsc5Policy63 = context => policyIs(context, 63) && ownDigest(context, 'osc5ContractsSourceSha256')
    && !familyMarker(context, 'cellSize') && !familyMarker(context, 'kittyAnimation');
export const isCellSizeAnimationPolicy64 = context => policyIs(context, 64) && ownDigest(context, 'cellSizeContractsSourceSha256')
    && ownDigest(context, 'kittyAnimationInputsSourceSha256') && !familyMarker(context, 'osc5');
export const isOsc5AnimationPolicy64 = context => policyIs(context, 64) && ownDigest(context, 'osc5ContractsSourceSha256')
    && ownDigest(context, 'kittyAnimationInputsSourceSha256') && !familyMarker(context, 'cellSize');
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
export const coreGapsReferencePaths = Object.freeze({
    "core-icon-vt.cs": "src/Ntilde.VT/AnsiParser.Titles.cs",
    "core-icon-capture.cs": "src/Ntilde.App/AgentHost/AgentPaneMetadataCapture.cs",
    "core-icon-contract.cs": "src/Ntilde.AgentHost.Contracts/PaneMetadataContracts.cs",
    "core-icon-window.cs": "src/Ntilde.App/MainWindow.AgentMetadata.cs",
});
export const oscTitleReferencePaths = Object.freeze({
    "osc-title-tab-labels.cs": "src/Ntilde.App/MainWindow.TabLabels.cs",
});
export const insertReplaceReferencePaths = Object.freeze({
    'irm-vt-write.cs': 'src/Ntilde.VT/TerminalBuffer.WritePath.cs',
    'irm-vt-modes.cs': 'src/Ntilde.VT/ModeState.cs',
    'irm-vt-margins.cs': 'src/Ntilde.VT/TerminalBuffer.Margins.cs',
    'irm-observer-contract.cs': 'src/Ntilde.AgentHost.Contracts/BufferStateDto.cs',
});
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
export const cellSizeReferencePaths = Object.freeze({
    "cell-size-parser.cs": "src/Ntilde.VT/AnsiParser.CellSizeReports.cs",
    "cell-size-metrics.cs": "src/Ntilde.VT/CellSizeReportMetrics.cs",
    "cell-size-framing.cs": "src/Ntilde.VT/AnsiParser.cs",
    "cell-size-font.cs": "src/Ntilde.App/Shell/TerminalView.Fonts.cs",
    "cell-size-observer-contract.cs": "src/Ntilde.AgentHost.Contracts/PaneCellGeometry.cs"
});
const oscCwdReferencePaths = Object.freeze({
    'modern-osc7-cwd-tab-headers.cs': 'src/Ntilde.App/MainWindow.TabHeaders.cs',
});
export function ownedSourcePath(root, file) {
    if (Object.hasOwn(oscCwdReferencePaths, file)) return join(root, oscCwdReferencePaths[file]);
    if (Object.hasOwn(cellSizeReferencePaths, file)) return join(root, cellSizeReferencePaths[file]);
    if (Object.hasOwn(coreGapsReferencePaths, file)) return join(root, coreGapsReferencePaths[file]);
    if (Object.hasOwn(oscTitleReferencePaths, file)) return join(root, oscTitleReferencePaths[file]);
    if (Object.hasOwn(insertReplaceReferencePaths, file)) return join(root, insertReplaceReferencePaths[file]);
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

// Frozen v22 SGR-underline-color grading scope; future experiments must create a distinct policy and map.
export const capturedV22SourceFiles = Object.freeze({
    ...capturedV21SourceFiles,
    sgrUnderlineColorContractsSourceSha256: "sgr-underline-color-contracts.mjs",
    sgrUnderlineColorCollectorSourceSha256: "sgr-underline-color-collector.mjs",
    sgrUnderlineColorGradingSourceSha256: "sgr-underline-color-grading.mjs",
    sgrUnderlineColorGradingCollectorSourceSha256: "sgr-underline-color-grading-collector.mjs",
    sgrUnderlineColorReceiptSourceSha256: "sgr-underline-color-receipt.mjs",
});

// Frozen v23 App-observed underline-color resolution scope; future experiments must create a distinct policy and map.
export const capturedV23SourceFiles = Object.freeze({
    ...capturedV22SourceFiles,
    sgrUnderlineColorResolvedContractsSourceSha256: "sgr-underline-color-resolved-contracts.mjs",
    sgrUnderlineColorResolvedCollectorSourceSha256: "sgr-underline-color-resolved-collector.mjs",
    sgrUnderlineColorResolvedGradingSourceSha256: "sgr-underline-color-resolved-grading.mjs",
    sgrUnderlineColorResolvedReceiptSourceSha256: "sgr-underline-color-resolved-receipt.mjs",
});

// Frozen v24 SGR selective-reset (SGR 24, SGR 27) grading scope; future experiments must create a distinct policy and map.
export const capturedV24SourceFiles = Object.freeze({
    ...capturedV23SourceFiles,
    sgrResetContractsSourceSha256: "sgr-reset-contracts.mjs",
    sgrResetCollectorSourceSha256: "sgr-reset-collector.mjs",
    sgrResetGradingSourceSha256: "sgr-reset-grading.mjs",
    sgrResetGradingCollectorSourceSha256: "sgr-reset-grading-collector.mjs",
    sgrResetReceiptSourceSha256: "sgr-reset-receipt.mjs",
});

// Frozen v25 SGR closure (sgr.blink, sgr.overline, sgr.underline.color) grading scope; future experiments must create a distinct policy and map.
export const capturedV25SourceFiles = Object.freeze({
    ...capturedV24SourceFiles,
    sgrClosureContractsSourceSha256: "sgr-closure-contracts.mjs",
    sgrClosureCollectorSourceSha256: "sgr-closure-collector.mjs",
    sgrClosureGradingSourceSha256: "sgr-closure-grading.mjs",
    sgrClosureGradingCollectorSourceSha256: "sgr-closure-grading-collector.mjs",
    sgrClosureReceiptSourceSha256: "sgr-closure-receipt.mjs",
});

// Frozen v26 erase-in-line (erase.line.right, .left, .all) grading scope; future experiments must create a distinct policy and map.
export const capturedV26SourceFiles = Object.freeze({
    ...capturedV25SourceFiles,
    eraseLineContractsSourceSha256: "erase-line-contracts.mjs",
    eraseLineCollectorSourceSha256: "erase-line-collector.mjs",
    eraseLineGradingSourceSha256: "erase-line-grading.mjs",
    eraseLineGradingCollectorSourceSha256: "erase-line-grading-collector.mjs",
    eraseLineReceiptSourceSha256: "erase-line-receipt.mjs",
});

// Frozen v27 character-editing (editing.insert-chars, editing.delete-chars, editing.repeat-char) grading scope; future experiments must create a distinct policy and map.
export const capturedV27SourceFiles = Object.freeze({
    ...capturedV26SourceFiles,
    charEditContractsSourceSha256: "char-edit-contracts.mjs",
    charEditCollectorSourceSha256: "char-edit-collector.mjs",
    charEditGradingSourceSha256: "char-edit-grading.mjs",
    charEditGradingCollectorSourceSha256: "char-edit-grading-collector.mjs",
    charEditReceiptSourceSha256: "char-edit-receipt.mjs",
});

// Frozen v28 erase-in-display (erase.screen.below, .above, .all) grading scope; future experiments must create a distinct policy and map.
export const capturedV28SourceFiles = Object.freeze({
    ...capturedV27SourceFiles,
    eraseDisplayContractsSourceSha256: "erase-display-contracts.mjs",
    eraseDisplayCollectorSourceSha256: "erase-display-collector.mjs",
    eraseDisplayGradingSourceSha256: "erase-display-grading.mjs",
    eraseDisplayGradingCollectorSourceSha256: "erase-display-grading-collector.mjs",
    eraseDisplayReceiptSourceSha256: "erase-display-receipt.mjs",
});

// Frozen v29 line-editing (editing.insert-lines, editing.delete-lines) grading scope; future experiments must create a distinct policy and map.
export const capturedV29SourceFiles = Object.freeze({
    ...capturedV28SourceFiles,
    lineEditContractsSourceSha256: "line-edit-contracts.mjs",
    lineEditCollectorSourceSha256: "line-edit-collector.mjs",
    lineEditGradingSourceSha256: "line-edit-grading.mjs",
    lineEditGradingCollectorSourceSha256: "line-edit-grading-collector.mjs",
    lineEditReceiptSourceSha256: "line-edit-receipt.mjs",
});

// Frozen v30 charsets (charsets.utf8, .dec-special, .dec-line-drawing, .g0-g1-switching) grading scope; future experiments must create a distinct policy and map.
export const capturedV30SourceFiles = Object.freeze({
    ...capturedV29SourceFiles,
    charsetsContractsSourceSha256: "charsets-contracts.mjs",
    charsetsCollectorSourceSha256: "charsets-collector.mjs",
    charsetsGradingSourceSha256: "charsets-grading.mjs",
    charsetsGradingCollectorSourceSha256: "charsets-grading-collector.mjs",
    charsetsReceiptSourceSha256: "charsets-receipt.mjs",
});

// Frozen v31 scrolling/history (the nine pinned scrollback.* cases) grading scope; future experiments must create a distinct policy and map.
export const capturedV31SourceFiles = Object.freeze({
    ...capturedV30SourceFiles,
    scrollbackContractsSourceSha256: "scrollback-contracts.mjs",
    scrollbackModelSourceSha256: "scrollback-model.mjs",
    scrollbackCollectorSourceSha256: "scrollback-collector.mjs",
    scrollbackGradingSourceSha256: "scrollback-grading.mjs",
    scrollbackGradingCollectorSourceSha256: "scrollback-grading-collector.mjs",
    scrollbackReceiptSourceSha256: "scrollback-receipt.mjs",
});

// Frozen v32 saved-state / reset (modes.alt-screen.exit, modes.altscreen-47, modes.altscreen-1047, reset.sgr, reset.decaln) grading scope; future experiments must create a distinct policy and map.
export const capturedV32SourceFiles = Object.freeze({
    ...capturedV31SourceFiles,
    savedResetContractsSourceSha256: "saved-reset-contracts.mjs",
    savedResetCollectorSourceSha256: "saved-reset-collector.mjs",
    savedResetGradingSourceSha256: "saved-reset-grading.mjs",
    savedResetGradingCollectorSourceSha256: "saved-reset-grading-collector.mjs",
    savedResetReceiptSourceSha256: "saved-reset-receipt.mjs",
});

// Frozen v33 erase attributes / erase margins (erase.el-with-attrs, erase.ed-scroll-region) grading scope; future experiments must create a distinct policy and map.
export const capturedV33SourceFiles = Object.freeze({
    ...capturedV32SourceFiles,
    eraseAttrsRegionContractsSourceSha256: "erase-attrs-region-contracts.mjs",
    eraseAttrsRegionCollectorSourceSha256: "erase-attrs-region-collector.mjs",
    eraseAttrsRegionGradingSourceSha256: "erase-attrs-region-grading.mjs",
    eraseAttrsRegionGradingCollectorSourceSha256: "erase-attrs-region-grading-collector.mjs",
    eraseAttrsRegionReceiptSourceSha256: "erase-attrs-region-receipt.mjs",
});

// Frozen v34 attribute-rectangle (editing.deccara, editing.decrara, editing.decsace) grading scope; future experiments must create a distinct policy and map.
export const capturedV34SourceFiles = Object.freeze({
    ...capturedV33SourceFiles,
    attrRectContractsSourceSha256: "attr-rect-contracts.mjs",
    attrRectCollectorSourceSha256: "attr-rect-collector.mjs",
    attrRectGradingSourceSha256: "attr-rect-grading.mjs",
    attrRectGradingCollectorSourceSha256: "attr-rect-grading-collector.mjs",
    attrRectReceiptSourceSha256: "attr-rect-receipt.mjs",
});

// Frozen v35 cursor (cursor.cup-scroll-region, cursor.hide, cursor.shape, cursor.reverse-wrap) grading scope; future experiments must create a distinct policy and map.
export const capturedV35SourceFiles = Object.freeze({
    ...capturedV34SourceFiles,
    cursorReportSourceSha256: "cursor-report.mjs",
    cursorQualCaptureSourceSha256: "cursor-qual-capture.mjs",
    cursorQualContractsSourceSha256: "cursor-qual-contracts.mjs",
    cursorQualCollectorSourceSha256: "cursor-qual-collector.mjs",
    cursorQualGradingSourceSha256: "cursor-qual-grading.mjs",
    cursorQualGradingCollectorSourceSha256: "cursor-qual-grading-collector.mjs",
    cursorQualReceiptSourceSha256: "cursor-qual-receipt.mjs",
});

// Frozen v36 erase-character / saved-lines (erase.character, erase.screen.scrollback) grading scope; future experiments must create a distinct policy and map.
export const capturedV36SourceFiles = Object.freeze({
    ...capturedV35SourceFiles,
    eraseCharsHistoryContractsSourceSha256: "erase-chars-history-contracts.mjs",
    eraseCharsHistoryCollectorSourceSha256: "erase-chars-history-collector.mjs",
    eraseCharsHistoryGradingSourceSha256: "erase-chars-history-grading.mjs",
    eraseCharsHistoryGradingCollectorSourceSha256: "erase-chars-history-grading-collector.mjs",
    eraseCharsHistoryReceiptSourceSha256: "erase-chars-history-receipt.mjs",
});

// Frozen v37 selective-erase (DECSCA/DECSED/DECSEL) grading scope; future experiments must create a distinct policy and map.
export const capturedV37SourceFiles = Object.freeze({
    ...capturedV36SourceFiles,
    selectiveEraseContractsSourceSha256: "selective-erase-contracts.mjs",
    selectiveEraseCollectorSourceSha256: "selective-erase-collector.mjs",
    selectiveEraseGradingSourceSha256: "selective-erase-grading.mjs",
    selectiveEraseGradingCollectorSourceSha256: "selective-erase-grading-collector.mjs",
    selectiveEraseReceiptSourceSha256: "selective-erase-receipt.mjs",
});

// Frozen v38 rectangle-editing (editing.decfra, editing.decera, editing.deccra) grading scope; future experiments must create a distinct policy and map.
export const capturedV38SourceFiles = Object.freeze({
    ...capturedV37SourceFiles,
    rectEditContractsSourceSha256: "rect-edit-contracts.mjs",
    rectEditCollectorSourceSha256: "rect-edit-collector.mjs",
    rectEditGradingSourceSha256: "rect-edit-grading.mjs",
    rectEditGradingCollectorSourceSha256: "rect-edit-grading-collector.mjs",
    rectEditReceiptSourceSha256: "rect-edit-receipt.mjs",
});

// Frozen v39 column-editing (editing.sl, editing.sr, editing.decic, editing.decdc) grading scope; future experiments must create a distinct policy and map.
export const capturedV39SourceFiles = Object.freeze({
    ...capturedV38SourceFiles,
    colEditContractsSourceSha256: "col-edit-contracts.mjs",
    colEditCollectorSourceSha256: "col-edit-collector.mjs",
    colEditGradingSourceSha256: "col-edit-grading.mjs",
    colEditGradingCollectorSourceSha256: "col-edit-grading-collector.mjs",
    colEditReceiptSourceSha256: "col-edit-receipt.mjs",
});

// Frozen v40 rectangle-checksum (editing.decrqcra) grading scope; future experiments must create a distinct policy and map.
export const capturedV40SourceFiles = Object.freeze({
    ...capturedV39SourceFiles,
    checksumReportSourceSha256: "checksum-report.mjs",
    rectChecksumContractsSourceSha256: "rect-checksum-contracts.mjs",
    rectChecksumCollectorSourceSha256: "rect-checksum-collector.mjs",
    rectChecksumCaptureSourceSha256: "rect-checksum-capture.mjs",
    rectChecksumGradingSourceSha256: "rect-checksum-grading.mjs",
    rectChecksumGradingCollectorSourceSha256: "rect-checksum-grading-collector.mjs",
    rectChecksumReceiptSourceSha256: "rect-checksum-receipt.mjs",
});

// Frozen v41 left/right-margin (modes.left-right-margin) grading scope; future experiments must create a distinct policy and map.
export const capturedV41SourceFiles = Object.freeze({
    ...capturedV40SourceFiles,
    lrMarginsContractsSourceSha256: "lr-margins-contracts.mjs",
    lrMarginsCollectorSourceSha256: "lr-margins-collector.mjs",
    lrMarginsGradingSourceSha256: "lr-margins-grading.mjs",
    lrMarginsGradingCollectorSourceSha256: "lr-margins-grading-collector.mjs",
    lrMarginsReceiptSourceSha256: "lr-margins-receipt.mjs",
});

// Frozen Core-gap scope, source policy v35.
export const capturedV42SourceFiles = Object.freeze({
    ...capturedV41SourceFiles,
    coreGapsSourceSha256: "core-gaps.mjs",
    coreGapsReceiptSourceSha256: "core-gaps-receipt.mjs",
    coreGapsPinnedSha256: "core-gaps-pinned.json",
    coreIconVtSourceSha256: "core-icon-vt.cs",
    coreIconCaptureSourceSha256: "core-icon-capture.cs",
    coreIconContractSourceSha256: "core-icon-contract.cs",
    coreIconWindowSourceSha256: "core-icon-window.cs",
});

// Frozen v43 routed-input (input.x10-mouse, input.button-event-mouse, modes.application-keypad) grading scope; future experiments must create a distinct policy and map.
export const capturedV43SourceFiles = Object.freeze({
    ...capturedV42SourceFiles,
    inputPlanSourceSha256: "input-plan.mjs",
    routedInputCasesSourceSha256: "routed-input-cases.mjs",
    routedInputContractsSourceSha256: "routed-input-contracts.mjs",
    routedInputCollectorSourceSha256: "routed-input-collector.mjs",
    routedInputGradingSourceSha256: "routed-input-grading.mjs",
    routedInputGradingCollectorSourceSha256: "routed-input-grading-collector.mjs",
    routedInputReceiptSourceSha256: "routed-input-receipt.mjs",
});

// Frozen v44 runtime-color reset (OSC 104/110/111/112) grading scope; future experiments must create a distinct policy and map.
export const capturedV44SourceFiles = Object.freeze({
    ...capturedV43SourceFiles,
    colorResetContractsSourceSha256: "color-reset-contracts.mjs",
    colorResetModelSourceSha256: "color-reset-model.mjs",
    colorResetCollectorSourceSha256: "color-reset-collector.mjs",
    colorResetCaptureSourceSha256: "color-reset-capture.mjs",
    colorResetGradingSourceSha256: "color-reset-grading.mjs",
    colorResetGradingCollectorSourceSha256: "color-reset-grading-collector.mjs",
    colorResetReceiptSourceSha256: "color-reset-receipt.mjs",
});

// Frozen source scope for exact Core text/cursor fixtures.
export const capturedV45SourceFiles = Object.freeze({
    ...capturedV44SourceFiles,
    textCursorSourceSha256: 'core-text-cursor.mjs',
    textCursorReceiptSourceSha256: 'core-text-cursor-receipt.mjs',
});

// Frozen v46 OSC 133 (extensions.osc133-a, -b, -c, -d) grading scope; future experiments must create a distinct policy and map.
export const capturedV46SourceFiles = Object.freeze({
    ...capturedV45SourceFiles,
    osc133ContractsSourceSha256: "osc133-contracts.mjs",
    osc133CollectorSourceSha256: "osc133-collector.mjs",
    osc133GradingSourceSha256: "osc133-grading.mjs",
    osc133GradingCollectorSourceSha256: "osc133-grading-collector.mjs",
    osc133ReceiptSourceSha256: "osc133-receipt.mjs",
});

export const capturedV47SourceFiles = Object.freeze({
    ...capturedV46SourceFiles,
    coreEraseSourceSha256: 'core-erase.mjs',
    coreEraseReceiptSourceSha256: 'core-erase-receipt.mjs',
});

export const capturedV48SourceFiles = Object.freeze({
    ...capturedV47SourceFiles,
    tabStopsSourceSha256: 'core-tab-stops.mjs',
    tabStopsReceiptSourceSha256: 'core-tab-stops-receipt.mjs',
});

export const capturedV49SourceFiles = Object.freeze({
    ...capturedV48SourceFiles,
    coreWrapContractSourceSha256: 'core-wrap-contract.mjs',
    coreWrapCollectorSourceSha256: 'core-wrap-collector.mjs',
    coreWrapObserverSourceSha256: 'core-wrap-observer.mjs',
    coreWrapGeometrySourceSha256: 'core-wrap-geometry.mjs',
    coreWrapReceiptSourceSha256: 'core-wrap-receipt.mjs',
});

export const capturedV50SourceFiles = Object.freeze({
    ...capturedV49SourceFiles,
    sgrRasterSourceSha256: 'core-sgr-raster.mjs',
    sgrRasterReceiptSourceSha256: 'core-sgr-raster-receipt.mjs',
});

export const capturedV51SourceFiles = Object.freeze({
    ...capturedV50SourceFiles,
    modeReportSourceSha256: 'mode-report.mjs',
    reverseWideSourceSha256: 'core-reverse-wide.mjs',
    reverseWideReceiptSourceSha256: 'core-reverse-wide-receipt.mjs',
});

export const capturedV52SourceFiles = Object.freeze({
    ...capturedV51SourceFiles,
    osc78ContractsSourceSha256: 'osc78-contracts.mjs',
    osc78CollectorSourceSha256: 'osc78-collector.mjs',
    osc78GradingSourceSha256: 'osc78-grading.mjs',
    osc78GradingCollectorSourceSha256: 'osc78-grading-collector.mjs',
    osc78ReceiptSourceSha256: 'osc78-receipt.mjs',
});

// Frozen v53 title-stack (device.xtwinops-22, device.xtwinops-23) grading scope; future experiments must create a distinct policy and map.
export const capturedV53SourceFiles = Object.freeze({
    ...capturedV52SourceFiles,
    titleStackContractsSourceSha256: 'title-stack-contracts.mjs',
    titleStackCollectorSourceSha256: 'title-stack-collector.mjs',
    titleStackGradingSourceSha256: 'title-stack-grading.mjs',
    titleStackGradingCollectorSourceSha256: 'title-stack-grading-collector.mjs',
    titleStackReceiptSourceSha256: 'title-stack-receipt.mjs',
});

// Fixed successor of the frozen v53 scope for OSC title setters.
export const capturedV54SourceFiles = Object.freeze({
    ...capturedV53SourceFiles,
    oscTitleContractsSourceSha256: 'osc-title-contracts.mjs',
    oscTitleCollectorSourceSha256: 'osc-title-collector.mjs',
    oscTitleGradingSourceSha256: 'osc-title-grading.mjs',
    oscTitleGradingCollectorSourceSha256: 'osc-title-grading-collector.mjs',
    oscTitleReceiptSourceSha256: 'osc-title-receipt.mjs',
    oscTitleTabLabelsSourceSha256: 'osc-title-tab-labels.cs',
});

// Fixed successor of the frozen OSC title scope for insert/replace mode.
export const capturedV55SourceFiles = Object.freeze({
    ...capturedV54SourceFiles,
    insertReplaceContractsSourceSha256: 'insert-replace-contracts.mjs',
    insertReplaceCollectorSourceSha256: 'insert-replace-collector.mjs',
    insertReplaceGradingSourceSha256: 'insert-replace-grading.mjs',
    insertReplaceGradingCollectorSourceSha256: 'insert-replace-grading-collector.mjs',
    insertReplaceReceiptSourceSha256: 'insert-replace-receipt.mjs',
    insertReplacePinnedSourceSha256: 'insert-replace-pinned.mjs',
    insertReplaceReplaySourceSha256: 'insert-replace-pinned-replay.mjs',
    insertReplaceVtWriteSourceSha256: 'irm-vt-write.cs',
    insertReplaceVtModesSourceSha256: 'irm-vt-modes.cs',
    insertReplaceVtMarginsSourceSha256: 'irm-vt-margins.cs',
    insertReplaceObserverContractSourceSha256: 'irm-observer-contract.cs',
});

// Frozen v56 XTGETTCAP (device.xtgettcap) grading scope; future experiments must create a distinct policy and map.
export const capturedV56SourceFiles = Object.freeze({
    ...capturedV55SourceFiles,
    xtgettcapReportSourceSha256: 'xtgettcap-report.mjs',
    xtgettcapContractsSourceSha256: 'xtgettcap-contracts.mjs',
    xtgettcapCaptureSourceSha256: 'xtgettcap-capture.mjs',
    xtgettcapCollectorSourceSha256: 'xtgettcap-collector.mjs',
    xtgettcapGradingSourceSha256: 'xtgettcap-grading.mjs',
    xtgettcapGradingCollectorSourceSha256: 'xtgettcap-grading-collector.mjs',
    xtgettcapReceiptSourceSha256: 'xtgettcap-receipt.mjs',
    xtgettcapReceiptCoreSourceSha256: 'xtgettcap-receipt-core.mjs',
});

/** Frozen v57 modifyOtherKeys (input.modify-other-keys, levels 1 and 2) grading scope; future experiments must create a distinct policy and map. */
export const capturedV57SourceFiles = Object.freeze({
    ...capturedV56SourceFiles,
    modifyOtherKeysCasesSourceSha256: 'modify-other-keys-cases.mjs',
    modifyOtherKeysContractsSourceSha256: 'modify-other-keys-contracts.mjs',
    modifyOtherKeysCollectorSourceSha256: 'modify-other-keys-collector.mjs',
    modifyOtherKeysGradingSourceSha256: 'modify-other-keys-grading.mjs',
    modifyOtherKeysGradingCollectorSourceSha256: 'modify-other-keys-grading-collector.mjs',
    modifyOtherKeysReceiptSourceSha256: 'modify-other-keys-receipt.mjs',
});

// Frozen v58 OSC 9 (extensions.osc9-progress, extensions.notifications) grading scope; future experiments must create a distinct policy and map.
export const capturedV58SourceFiles = Object.freeze({
    ...capturedV57SourceFiles,
    osc9ContractsSourceSha256: 'osc9-contracts.mjs',
    osc9CollectorSourceSha256: 'osc9-collector.mjs',
    osc9GradingSourceSha256: 'osc9-grading.mjs',
    osc9GradingCollectorSourceSha256: 'osc9-grading-collector.mjs',
    osc9ReceiptSourceSha256: 'osc9-receipt.mjs',
    osc9FocusSourceSha256: 'osc9-focus.mjs',
});

// Frozen v59 ASCII-reflow grading scope; future experiments must create a distinct policy and map.
export const capturedV59SourceFiles = Object.freeze({
    ...capturedV58SourceFiles,
    asciiReflowContractsSourceSha256: 'ascii-reflow-contracts.mjs',
    asciiReflowObserverSourceSha256: 'ascii-reflow-observer.mjs',
    asciiReflowCollectorSourceSha256: 'ascii-reflow-collector.mjs',
    asciiReflowGradingSourceSha256: 'ascii-reflow-grading.mjs',
    asciiReflowGradingCollectorSourceSha256: 'ascii-reflow-grading-collector.mjs',
    asciiReflowReceiptSourceSha256: 'ascii-reflow-receipt.mjs',
});

// Frozen v60 OSC 777 / OSC 99 notification grading scope; future experiments must create a distinct policy and map.
export const capturedV60SourceFiles = Object.freeze({
    ...capturedV59SourceFiles,
    notifyContractsSourceSha256: 'notify-contracts.mjs',
    notifyCollectorSourceSha256: 'notify-collector.mjs',
    notifyGradingSourceSha256: 'notify-grading.mjs',
    notifyGradingCollectorSourceSha256: 'notify-grading-collector.mjs',
    notifyReceiptSourceSha256: 'notify-receipt.mjs',
});

// Fixed successor of the frozen v60 scope for the selection-color qualification.
export const capturedV61SourceFiles = Object.freeze({
    ...capturedV60SourceFiles,
    selectionColorsContractsSourceSha256: 'selection-colors-contracts.mjs',
    selectionColorsModelSourceSha256: 'selection-colors-model.mjs',
    selectionColorsCollectorSourceSha256: 'selection-colors-collector.mjs',
    selectionColorsCaptureSourceSha256: 'selection-colors-capture.mjs',
    selectionColorsGradingSourceSha256: 'selection-colors-grading.mjs',
    selectionColorsGradingCollectorSourceSha256: 'selection-colors-grading-collector.mjs',
    selectionColorsReceiptSourceSha256: 'selection-colors-receipt.mjs',
});

// Frozen v62 device-query (XTGETXRES, XTREPORTCOLORS, CSI 20 t / 21 t) grading scope; future experiments must create a distinct policy and map.
export const capturedV62SourceFiles = Object.freeze({
    ...capturedV61SourceFiles,
    devqReportSourceSha256: 'devq-report.mjs',
    devqContractsSourceSha256: 'devq-contracts.mjs',
    devqCaptureSourceSha256: 'devq-capture.mjs',
    devqCollectorSourceSha256: 'devq-collector.mjs',
    devqGradingSourceSha256: 'devq-grading.mjs',
    devqGradingCollectorSourceSha256: 'devq-grading-collector.mjs',
    devqReceiptSourceSha256: 'devq-receipt.mjs',
});

// Frozen v63 xterm color-stack (XTPUSHCOLORS, XTPOPCOLORS) grading scope; future experiments must create a distinct policy and map.
export const capturedV63SourceFiles = Object.freeze({
    ...capturedV62SourceFiles,
    xcsReportSourceSha256: 'xcs-report.mjs',
    xcsContractsSourceSha256: 'xcs-contracts.mjs',
    xcsCaptureSourceSha256: 'xcs-capture.mjs',
    xcsCollectorSourceSha256: 'xcs-collector.mjs',
    xcsGradingSourceSha256: 'xcs-grading.mjs',
    xcsGradingCollectorSourceSha256: 'xcs-grading-collector.mjs',
    xcsReceiptSourceSha256: 'xcs-receipt.mjs',
});

// Frozen v64 xterm SGR-stack (XTPUSHSGR, XTPOPSGR) grading scope; future experiments must create a distinct policy and map.
export const capturedV64SourceFiles = Object.freeze({
    ...capturedV63SourceFiles,
    xssReportSourceSha256: 'xss-report.mjs',
    xssContractsSourceSha256: 'xss-contracts.mjs',
    xssCaptureSourceSha256: 'xss-capture.mjs',
    xssCollectorSourceSha256: 'xss-collector.mjs',
    xssGradingSourceSha256: 'xss-grading.mjs',
    xssGradingCollectorSourceSha256: 'xss-grading-collector.mjs',
    xssReceiptSourceSha256: 'xss-receipt.mjs',
});

// Frozen v65 extended mouse encodings (modes.utf8-mouse-1005, input.urxvt-mouse; input plan v4) grading scope; future experiments must create a distinct policy and map.
export const capturedV65SourceFiles = Object.freeze({
    ...capturedV64SourceFiles,
    mexCasesSourceSha256: 'mex-cases.mjs',
    mexContractsSourceSha256: 'mex-contracts.mjs',
    mexCollectorSourceSha256: 'mex-collector.mjs',
    mexGradingSourceSha256: 'mex-grading.mjs',
    mexGradingCollectorSourceSha256: 'mex-grading-collector.mjs',
    mexReceiptSourceSha256: 'mex-receipt.mjs',
});

// Frozen v66 xterm saved-mode (XTSAVE, XTRESTORE) grading scope; future experiments must create a distinct policy and map.
export const capturedV66SourceFiles = Object.freeze({
    ...capturedV65SourceFiles,
    xsvReportSourceSha256: 'xsv-report.mjs',
    xsvContractsSourceSha256: 'xsv-contracts.mjs',
    xsvCaptureSourceSha256: 'xsv-capture.mjs',
    xsvCollectorSourceSha256: 'xsv-collector.mjs',
    xsvGradingSourceSha256: 'xsv-grading.mjs',
    xsvGradingCollectorSourceSha256: 'xsv-grading-collector.mjs',
    xsvReceiptSourceSha256: 'xsv-receipt.mjs',
});

// Frozen v67 kitty color-control (OSC 21) grading scope; future experiments must create a distinct policy and map.
export const capturedV67SourceFiles = Object.freeze({
    ...capturedV66SourceFiles,
    kclReportSourceSha256: 'kcl-report.mjs',
    kclContractsSourceSha256: 'kcl-contracts.mjs',
    kclCaptureSourceSha256: 'kcl-capture.mjs',
    kclCollectorSourceSha256: 'kcl-collector.mjs',
    kclGradingSourceSha256: 'kcl-grading.mjs',
    kclGradingCollectorSourceSha256: 'kcl-grading-collector.mjs',
    kclReceiptSourceSha256: 'kcl-receipt.mjs',
});

// Frozen v68 OSC 633 lifecycle qualification source scope.
export const capturedOsc633ForkV61Files = Object.freeze({
    ...capturedV67SourceFiles,
    osc633ContractsSourceSha256: 'osc633-contracts.mjs',
    osc633CollectorSourceSha256: 'osc633-collector.mjs',
    osc633GradingSourceSha256: 'osc633-grading.mjs',
    osc633GradingCollectorSourceSha256: 'osc633-grading-collector.mjs',
    osc633ReceiptSourceSha256: 'osc633-receipt.mjs',
    collectionScopeSourceSha256: 'collection-scope.mjs',
});

// Frozen v68 SGR pixel-coordinate mouse (input.pixel-mouse; input plan v5) grading scope; future experiments must create a distinct policy and map.
export const capturedV68SourceFiles = Object.freeze({
    ...capturedV67SourceFiles,
    pxmCasesSourceSha256: 'pxm-cases.mjs',
    pxmContractsSourceSha256: 'pxm-contracts.mjs',
    pxmCollectorSourceSha256: 'pxm-collector.mjs',
    pxmGradingSourceSha256: 'pxm-grading.mjs',
    pxmGradingCollectorSourceSha256: 'pxm-grading-collector.mjs',
    pxmReceiptSourceSha256: 'pxm-receipt.mjs',
});

// Integrated v62 contains the frozen SGR-pixel source scope and the OSC 633 additions.
export const capturedV69SourceFiles = Object.freeze({
    ...capturedV68SourceFiles,
    osc633ContractsSourceSha256: 'osc633-contracts.mjs',
    osc633CollectorSourceSha256: 'osc633-collector.mjs',
    osc633GradingSourceSha256: 'osc633-grading.mjs',
    osc633GradingCollectorSourceSha256: 'osc633-grading-collector.mjs',
    osc633ReceiptSourceSha256: 'osc633-receipt.mjs',
    collectionScopeSourceSha256: 'collection-scope.mjs',
});

// Integrated v63 adds the Kitty animation measurement suite to the frozen v69 scope.
export const capturedV70SourceFiles = Object.freeze({
    ...capturedV69SourceFiles,
    kittyAnimationInputsSourceSha256: 'kitty-animation-inputs.mjs',
    kittyAnimationCasesSourceSha256: 'kitty-animation-cases.mjs',
    kittyAnimationOracleSourceSha256: 'kitty-animation-oracle.mjs',
    kittyAnimationSuiteSourceSha256: 'kitty-animation.mjs',
    kittyAnimationCaptureSourceSha256: 'kitty-animation-capture.mjs',
    kittyAnimationCollectorSourceSha256: 'kitty-animation-collector.mjs',
    kittyAnimationReceiptSourceSha256: 'kitty-animation-receipt.mjs',
});

// Frozen pre-integration OSC cell-size fork, measured under the same v63 policy.
export const capturedCellSizeForkV70Files = Object.freeze({
    ...capturedV69SourceFiles,
    cellSizeContractsSourceSha256: "cell-size-contracts.mjs",
    cellSizeFrameSourceSha256: "cell-size-frame.mjs",
    cellSizeGradingCollectorSourceSha256: "cell-size-grading-collector.mjs",
    cellSizeCollectorSourceSha256: "cell-size-collector.mjs",
    cellSizeCaptureSourceSha256: "cell-size-capture.mjs",
    cellSizeGradingSourceSha256: "cell-size-grading.mjs",
    cellSizeReceiptSourceSha256: "cell-size-receipt.mjs",
    cellSizeParserReferenceSha256: "cell-size-parser.cs",
    cellSizeMetricsReferenceSha256: "cell-size-metrics.cs",
    cellSizeFramingReferenceSha256: "cell-size-framing.cs",
    cellSizeFontReferenceSha256: "cell-size-font.cs",
    cellSizeGeometryContractReferenceSha256: "cell-size-observer-contract.cs",
});

// Combined graph: both previously measured forks remain immutable.
export const capturedV71SourceFiles = Object.freeze({
    ...capturedV70SourceFiles,
    ...capturedCellSizeForkV70Files,
});

// The rxvt special-color batch adds its seven scripts to the integrated graph.
export const capturedOsc5ForkV63Files = Object.freeze({
    ...capturedV69SourceFiles,
    osc5ReportSourceSha256: 'osc5-report.mjs',
    osc5ContractsSourceSha256: 'osc5-contracts.mjs',
    osc5CaptureSourceSha256: 'osc5-capture.mjs',
    osc5CollectorSourceSha256: 'osc5-collector.mjs',
    osc5GradingSourceSha256: 'osc5-grading.mjs',
    osc5GradingCollectorSourceSha256: 'osc5-grading-collector.mjs',
    osc5ReceiptSourceSha256: 'osc5-receipt.mjs',
});

export const capturedOsc5ForkV64Files = Object.freeze({
    ...capturedV70SourceFiles,
    osc5ReportSourceSha256: 'osc5-report.mjs',
    osc5ContractsSourceSha256: 'osc5-contracts.mjs',
    osc5CaptureSourceSha256: 'osc5-capture.mjs',
    osc5CollectorSourceSha256: 'osc5-collector.mjs',
    osc5GradingSourceSha256: 'osc5-grading.mjs',
    osc5GradingCollectorSourceSha256: 'osc5-grading-collector.mjs',
    osc5ReceiptSourceSha256: 'osc5-receipt.mjs',
});

// Live v65 integrates all three measured families; frozen graphs above remain independent.
export const capturedV72SourceFiles = Object.freeze({
    ...capturedV71SourceFiles,
    osc5ReportSourceSha256: 'osc5-report.mjs',
    osc5ContractsSourceSha256: 'osc5-contracts.mjs',
    osc5CaptureSourceSha256: 'osc5-capture.mjs',
    osc5CollectorSourceSha256: 'osc5-collector.mjs',
    osc5GradingSourceSha256: 'osc5-grading.mjs',
    osc5GradingCollectorSourceSha256: 'osc5-grading-collector.mjs',
    osc5ReceiptSourceSha256: 'osc5-receipt.mjs',
});

// The OSC 3008 context batch adds its five scripts to the integrated v65 graph; the frozen v72 graph above stays exactly as published.
export const capturedV73SourceFiles = Object.freeze({
    ...capturedV72SourceFiles,
    osc3008ContractsSourceSha256: 'osc3008-contracts.mjs',
    osc3008CollectorSourceSha256: 'osc3008-collector.mjs',
    osc3008GradingSourceSha256: 'osc3008-grading.mjs',
    osc3008GradingCollectorSourceSha256: 'osc3008-grading-collector.mjs',
    osc3008ReceiptSourceSha256: 'osc3008-receipt.mjs',
});

// The rxvt OSC 720 batch adds its six scripts to the integrated v66 graph; the frozen v73 graph above stays exactly as published.
export const capturedV74SourceFiles = Object.freeze({
    ...capturedV73SourceFiles,
    osc720ContractsSourceSha256: 'osc720-contracts.mjs',
    osc720CollectorSourceSha256: 'osc720-collector.mjs',
    osc720ActionSourceSha256: 'osc720-action.mjs',
    osc720GradingSourceSha256: 'osc720-grading.mjs',
    osc720GradingCollectorSourceSha256: 'osc720-grading-collector.mjs',
    osc720ReceiptSourceSha256: 'osc720-receipt.mjs',
});

// OSC 22 adds five runtime modules after the frozen OSC 720 v74 graph.
export const capturedV75SourceFiles = Object.freeze({
    ...capturedV74SourceFiles,
    osc22ContractsSourceSha256: 'osc22-contracts.mjs',
    osc22CollectorSourceSha256: 'osc22-collector.mjs',
    osc22GradingSourceSha256: 'osc22-grading.mjs',
    osc22GradingCollectorSourceSha256: 'osc22-grading-collector.mjs',
    osc22ReceiptSourceSha256: 'osc22-receipt.mjs',
});

// Font queries add six modules; the v75 graph remains frozen.
export const capturedV76SourceFiles = Object.freeze({
    ...capturedV75SourceFiles,
    fontSizeContractsSourceSha256: 'font-size-contracts.mjs',
    fontSizeGradingCollectorSourceSha256: 'font-size-grading-collector.mjs',
    fontSizeCollectorSourceSha256: 'font-size-collector.mjs',
    fontSizeCaptureSourceSha256: 'font-size-capture.mjs',
    fontSizeGradingSourceSha256: 'font-size-grading.mjs',
    fontSizeReceiptSourceSha256: 'font-size-receipt.mjs',
});

// The iTerm2 feature-reporting batch adds the shared query-probe machinery and its seven suite scripts to
// the integrated v69 graph; the frozen v76 graph above stays exactly as published.
export const capturedV77SourceFiles = Object.freeze({
    ...capturedV76SourceFiles,
    probeSuiteSharedSourceSha256: 'probe-suite-shared.mjs',
    capsReportSourceSha256: 'caps-report.mjs',
    capsContractsSourceSha256: 'caps-contracts.mjs',
    capsCaptureSourceSha256: 'caps-capture.mjs',
    capsCollectorSourceSha256: 'caps-collector.mjs',
    capsGradingSourceSha256: 'caps-grading.mjs',
    capsGradingCollectorSourceSha256: 'caps-grading-collector.mjs',
    capsReceiptSourceSha256: 'caps-receipt.mjs',
});

// The xterm pointer-color batch adds its seven suite scripts to the integrated v70 graph; the frozen v77 graph above stays exactly as published.
export const capturedV78SourceFiles = Object.freeze({
    ...capturedV77SourceFiles,
    pclReportSourceSha256: 'pcl-report.mjs',
    pclContractsSourceSha256: 'pcl-contracts.mjs',
    pclCaptureSourceSha256: 'pcl-capture.mjs',
    pclCollectorSourceSha256: 'pcl-collector.mjs',
    pclGradingSourceSha256: 'pcl-grading.mjs',
    pclGradingCollectorSourceSha256: 'pcl-grading-collector.mjs',
    pclReceiptSourceSha256: 'pcl-receipt.mjs',
});

// The DECCOLM batch adds its seven suite scripts to the integrated v71 graph; the frozen v78 graph above stays exactly as published.
export const capturedV79SourceFiles = Object.freeze({
    ...capturedV78SourceFiles,
    dclReportSourceSha256: 'dcl-report.mjs',
    dclContractsSourceSha256: 'dcl-contracts.mjs',
    dclCaptureSourceSha256: 'dcl-capture.mjs',
    dclCollectorSourceSha256: 'dcl-collector.mjs',
    dclGradingSourceSha256: 'dcl-grading.mjs',
    dclGradingCollectorSourceSha256: 'dcl-grading-collector.mjs',
    dclReceiptSourceSha256: 'dcl-receipt.mjs',
});

export const capturedV80SourceFiles = Object.freeze({
    ...capturedV79SourceFiles,
    modernCursorSourceSha256: 'modern-cursor.mjs',
    modernCursorReceiptSourceSha256: 'modern-cursor-receipt.mjs',
});

export const capturedV81SourceFiles = Object.freeze({
    ...capturedV80SourceFiles,
    modernModesSourceSha256: 'modern-modes.mjs',
    modernModesReceiptSourceSha256: 'modern-modes-receipt.mjs',
});

// The DECSCLM batch adds its seven suite scripts to the integrated v74 graph (policy v80). Policies v75-v78 are the frozen focused forks of v74 below: they never carried these scripts and stay exactly as published.
export const capturedV82SourceFiles = Object.freeze({
    ...capturedV81SourceFiles,
    dslReportSourceSha256: 'dsl-report.mjs',
    dslContractsSourceSha256: 'dsl-contracts.mjs',
    dslCaptureSourceSha256: 'dsl-capture.mjs',
    dslCollectorSourceSha256: 'dsl-collector.mjs',
    dslGradingSourceSha256: 'dsl-grading.mjs',
    dslGradingCollectorSourceSha256: 'dsl-grading-collector.mjs',
    dslReceiptSourceSha256: 'dsl-receipt.mjs',
});

export const capturedSixelReportsSourceFiles = Object.freeze({
    ...capturedV82SourceFiles,
    sixelReportsContractsSourceSha256: 'sixel-reports-contracts.mjs',
    sixelReportsOwnedSourceSha256: 'sixel-reports-owned.mjs',
    sixelReportsWorkerSourceSha256: 'sixel-reports-worker.mjs',
});

export const capturedOsc66SourceFiles = Object.freeze({
    ...capturedV82SourceFiles,
    osc66ContractsSourceSha256: 'osc66-contracts.mjs',
    osc66OwnedSourceSha256: 'osc66-owned.mjs',
    osc66WorkerSourceSha256: 'osc66-worker.mjs',
});

export const capturedKittyGraphicsSourceFiles = Object.freeze({
    ...capturedV82SourceFiles,
    kittyGraphicsOwnedSourceSha256: 'kitty-graphics-owned.mjs',
    kittyGraphicsWorkerSourceSha256: 'kitty-graphics-worker.mjs',
});

export const capturedUnicodeBoundarySourceFiles = Object.freeze({
    ...capturedV82SourceFiles,
    unicodeBoundaryContractsSourceSha256: 'unicode-boundary-contracts.mjs',
    unicodeBoundaryOwnedSourceSha256: 'unicode-boundary-owned.mjs',
    unicodeBoundaryWorkerSourceSha256: 'unicode-boundary-worker.mjs',
    unicodeWidthContractsSourceSha256: 'unicode-width-contracts.mjs',
});

export const capturedUnicodeWidthSourceFiles = Object.freeze({
    ...capturedV82SourceFiles,
    unicodeWidthContractsSourceSha256: 'unicode-width-contracts.mjs',
    unicodeWidthOwnedSourceSha256: 'unicode-width-owned.mjs',
    unicodeWidthWorkerSourceSha256: 'unicode-width-worker.mjs',
});

// Only the blink scope captures this extension. Full collections keep their integrated graph and receipt.
export const capturedBlinkSourceFiles = Object.freeze({
    ...capturedV81SourceFiles,
    modernBlinkSourceSha256: 'modern-blink.mjs',
    modernBlinkNativeSourceSha256: 'modern-blink-native.mjs',
    modernBlinkWindowSourceSha256: 'modern-blink-window.jxa',
    modernBlinkOwnedSourceSha256: 'modern-blink-owned.mjs',
    modernBlinkWorkerSourceSha256: 'modern-blink-worker.mjs',
});

// Only the viewport scope extends the full graph with these measured scripts.
export const capturedViewportSourceFiles = Object.freeze({
    ...capturedV81SourceFiles,
    modernViewportSourceSha256: 'modern-viewport.mjs',
    modernViewportOwnedSourceSha256: 'modern-viewport-owned.mjs',
    modernViewportWorkerSourceSha256: 'modern-viewport-worker.mjs',
});

export const capturedOscTitlesSourceFiles = Object.freeze({
    ...capturedV81SourceFiles,
    modernOscTitlesSourceSha256: 'modern-osc-titles.mjs',
    modernOscTitlesOwnedSourceSha256: 'modern-osc-titles-owned.mjs',
    modernOscTitlesWorkerSourceSha256: 'modern-osc-titles-worker.mjs',
});

export const capturedOscCwdSourceFiles = Object.freeze({
    ...capturedV81SourceFiles,
    modernOscCwdSourceSha256: 'modern-osc7-cwd.mjs',
    modernOscCwdOwnedSourceSha256: 'modern-osc7-cwd-owned.mjs',
    modernOscCwdWorkerSourceSha256: 'modern-osc7-cwd-worker.mjs',
    modernOscCwdNativeSourceSha256: 'modern-osc7-cwd-native.swift',
    modernOscCwdTabHeadersSourceSha256: 'modern-osc7-cwd-tab-headers.cs',
});

export const capturedChildEnvironmentSourceFiles = Object.freeze({
    ...capturedV81SourceFiles,
    modernChildEnvironmentSourceSha256: 'modern-child-environment.mjs',
    modernChildEnvironmentOwnedSourceSha256: 'modern-child-environment-owned.mjs',
    modernChildEnvironmentWorkerSourceSha256: 'modern-child-environment-worker.mjs',
});

/** Bind the private temporary root, selected inventory and manifest environment pin before trusting any captured source. */
export function readSourceSnapshot(root) {
    if (realpathSync(root) !== process.env.NTILDE_TERMINFO_SOURCE_SNAPSHOT
        || dirname(root) !== realpathSync(tmpdir()) || !root.split(/[\\/]/).at(-1).startsWith('ntilde-owned-sources-')) throw new Error('Expected the private captured source tree.');
    const expected = {
        [capturedSixelReportsSourcePolicy]: capturedSixelReportsSourceFiles,
        [capturedOsc66SourcePolicy]: capturedOsc66SourceFiles,
        [capturedKittyGraphicsSourcePolicy]: capturedKittyGraphicsSourceFiles,
        [capturedUnicodeBoundarySourcePolicy]: capturedUnicodeBoundarySourceFiles,
        [capturedUnicodeWidthSourcePolicy]: capturedUnicodeWidthSourceFiles,
        [capturedBlinkSourcePolicy]: capturedBlinkSourceFiles,
        [capturedViewportSourcePolicy]: capturedViewportSourceFiles,
        [capturedOscTitlesSourcePolicy]: capturedOscTitlesSourceFiles,
        [capturedChildEnvironmentSourcePolicy]: capturedChildEnvironmentSourceFiles,
        [capturedOscCwdSourcePolicy]: capturedOscCwdSourceFiles,
    }[sourceSnapshotPolicy] ?? capturedV82SourceFiles;
    if (JSON.stringify(ownedSourceFiles) !== JSON.stringify(expected)) throw new Error('Invalid selected source inventory.');
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

