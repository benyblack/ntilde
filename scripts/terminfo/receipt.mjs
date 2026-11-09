import { includesPrivateClipboardEvidence, checkPrivateClipboardEvidence } from './private-clipboard-receipt.mjs';
import { includesMetadataEvidence, checkMetadataEvidence } from './metadata-receipt.mjs';
import { includesInputEvidence, checkInputEvidence } from './input-receipt.mjs';
import { includesQueryEvidence, checkQueryEvidence } from './query-receipt.mjs';
import { includesAsciiSgrEvidence, checkAsciiSgrEvidence } from './ascii-sgr-receipt.mjs';
import { includesSgrColorsEvidence, checkSgrColorsEvidence } from './sgr-colors-receipt.mjs';
import { includesSgrRgbEvidence, checkSgrRgbEvidence } from './sgr-rgb-receipt.mjs';
import { includesSgrAttrsEvidence, checkSgrAttrsEvidence } from './sgr-attrs-receipt.mjs';
import { includesSgrPaletteEvidence, checkSgrPaletteEvidence } from './sgr-palette-receipt.mjs';
import { includesSgrTruecolorEvidence, checkSgrTruecolorEvidence } from './sgr-truecolor-receipt.mjs';
import { includesTruecolorFindingEvidence, checkTruecolorFindingEvidence } from './truecolor-literal-finding-receipt.mjs';
import { includesSgrUnderlineEvidence, checkSgrUnderlineEvidence } from './sgr-underline-receipt.mjs';
import { includesSgrUnderlineColorEvidence, checkSgrUnderlineColorEvidence } from './sgr-underline-color-receipt.mjs';
import { includesUnderlineResolvedEvidence, checkUnderlineResolvedEvidence } from './sgr-underline-color-resolved-receipt.mjs';
import { includesSgrResetEvidence, checkSgrResetEvidence } from './sgr-reset-receipt.mjs';
import { includesSgrClosureEvidence, checkSgrClosureEvidence } from './sgr-closure-receipt.mjs';
import { includesEraseLineEvidence, checkEraseLineEvidence } from './erase-line-receipt.mjs';
import { includesCharEditEvidence, checkCharEditEvidence } from './char-edit-receipt.mjs';
import { includesEraseDisplayEvidence, checkEraseDisplayEvidence } from './erase-display-receipt.mjs';
import { includesLineEditEvidence, checkLineEditEvidence } from './line-edit-receipt.mjs';
import { includesScrollbackEvidence, checkScrollbackEvidence } from './scrollback-receipt.mjs';
import { includesStaticImageEvidence, checkStaticImageEvidence } from './static-images-receipt.mjs';
import { includesSavedEvidence, checkSavedEvidence } from './saved-receipt.mjs';
import { includesResizeEvidence, checkResizeEvidence } from './resize-receipt.mjs';
import { createHash } from 'node:crypto';
import { lstatSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { buildPixelReport } from './pixels.mjs';
import { pixelSuiteId } from './pixels-cases.mjs';

import { buildColorReport } from './colors.mjs';
import { colorSuiteId } from './color-cases.mjs';

import { buildEraseReport, buildCellEditingReport } from './editing.mjs';
import { eraseSuiteId, editingSuiteId } from './editing-cases.mjs';
import { historySuiteId } from './history-cases.mjs';
import { buildHistoryReport } from './history.mjs';
import { privateClipboardOutputPolicy, ownedOutputPolicy, sttyTool, validateOutputDescription, validateOutputGeometry } from './terminal-output.mjs';

const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const suites = ['sgr', 'rgb', 'rendition', 'cursor'];

/** Refuse any remaining fixture entry, including a dangling symlink. */
function fixtureRemains(scratch) {
    try { lstatSync(scratch); return true; }
    catch (error) {
        if (error.code === 'ENOENT') {
            return false;
        }
        throw error;
    }
}

/** Bind every required report or unavailable marker to this run and its exact retained bytes. */
function checkEvidence(output, evidence, prefix, runId) {
    const allowedFiles = [`${prefix}-observations.json`];
    if (prefix !== 'cell') allowedFiles.push(`${prefix}-unavailable.json`);
    if (!evidence || evidence.runId !== runId || typeof evidence.suiteId !== 'string'
        || !allowedFiles.includes(evidence.file)
        || !/^[a-f\d]{64}$/.test(evidence.sha256 ?? '')
        || digest(readFileSync(join(output, evidence.file))) !== evidence.sha256) throw new Error('Invalid owned suite receipt binding.');
}


/** Re-derive the supplemental pixel evidence from retained bytes and the complete journal. */
function checkPixelEvidence(output, evidence, runId, suiteId) {
    if (suiteId !== pixelSuiteId) throw new Error('Unexpected pixel suite identity.');
    checkEvidence(output, evidence, 'pixel', runId);
    const pixels = JSON.parse(readFileSync(join(output, evidence.file)));
    if (pixels.runId !== runId || pixels.suiteId !== pixelSuiteId) throw new Error('Invalid pixel report identity.');
    if (evidence.file === 'pixel-observations.json') {
        const derived = buildPixelReport(pixels.snapshots, runId, output);
        if (JSON.stringify(pixels) !== JSON.stringify(derived)) throw new Error('Pixel observations do not match retained evidence.');
        const journal = readFileSync(join(output, 'pixel-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
        if (JSON.stringify(journal) !== JSON.stringify(pixels.snapshots)) throw new Error('Pixel journal does not match the report.');
    } else if (pixels.status !== 'unavailable' || pixels.reason !== 'screenDetailsUnavailable' || pixels.fixtureStarted !== false) {
        throw new Error('Invalid pixel unavailable marker.');
    }
}

function checkColorEvidence(output, evidence, runId, suiteId) {
    if (suiteId !== colorSuiteId) throw new Error('Unexpected color suite identity.');
    checkEvidence(output, evidence, 'color', runId);
    const colors = JSON.parse(readFileSync(join(output, evidence.file)));
    if (colors.runId !== runId || colors.suiteId !== colorSuiteId) throw new Error('Invalid color report identity.');
    if (evidence.file === 'color-observations.json') {
        const derived = buildColorReport(colors.snapshots, runId, output);
        if (JSON.stringify(colors) !== JSON.stringify(derived)) throw new Error('Color observations do not match retained evidence.');
        const journal = readFileSync(join(output, 'color-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
        if (JSON.stringify(journal) !== JSON.stringify(colors.snapshots)) throw new Error('Color journal does not match the report.');
    } else if (colors.status !== 'unavailable' || colors.reason !== 'screenDetailsUnavailable' || colors.fixtureStarted !== false) {
        throw new Error('Invalid color unavailable marker.');
    }
}

/** Re-derive both new complete-state/history reports without weakening any historical receipt. */
function checkEditingEvidence(output, evidence, runId, prefix, suiteId, buildReport, count) {
    checkEvidence(output, evidence, prefix, runId);
    const report = JSON.parse(readFileSync(join(output, evidence.file)));
    if (report.runId !== runId || report.suiteId !== suiteId || evidence.suiteId !== suiteId) throw new Error('Invalid editing report identity.');
    const journalText = readFileSync(join(output, `${prefix}-snapshots.jsonl`), 'utf8').trim();
    if (evidence.file === `${prefix}-observations.json`) {
        const derived = buildReport(report.snapshots, runId);
        if (JSON.stringify(report) !== JSON.stringify(derived)) throw new Error('Editing observations do not match retained evidence.');
        const journal = journalText.split('\n').map(line => JSON.parse(line));
        if (JSON.stringify(journal) !== JSON.stringify(report.snapshots)) throw new Error('Editing journal does not match the report.');
        const counts = report.observations.reduce((values, item) => ({ ...values, [item.result]: (values[item.result] ?? 0) + 1 }), {});
        if (JSON.stringify(counts) !== JSON.stringify(evidence.counts)) throw new Error('Editing receipt counts do not match the report.');
    } else if (report.status !== 'unavailable' || report.reason !== 'screenDetailsUnavailable'
        || report.fixtureStarted !== false || report.plannedExperiments !== count || journalText !== ''
        || evidence.status !== 'unavailable' || evidence.reason !== report.reason || JSON.stringify(evidence.counts) !== '{}') {
        throw new Error('Invalid editing unavailable marker.');
    }
    return report;
}
function checkEditingSuites(output, evidence, runId, context) {
    if (context.eraseSuiteId !== eraseSuiteId || context.editingSuiteId !== editingSuiteId
        || context.pixelSuiteId !== pixelSuiteId || context.colorSuiteId !== colorSuiteId) throw new Error('Missing or changed required v4 suite identities.');
    checkEditingEvidence(output, evidence.eraseEvidence, runId, 'erase', eraseSuiteId, buildEraseReport, 13);
    checkEditingEvidence(output, evidence.editingEvidence, runId, 'editing', editingSuiteId, buildCellEditingReport, 22);
}

/** Require the parent-observed transport and matching geometry before accepting v5 history evidence. */
function checkHistoryProof(output, evidence, runId, context, proof) {
    if (context.historySuiteId !== historySuiteId || ![ownedOutputPolicy, privateClipboardOutputPolicy].includes(context.terminalOutput)) throw new Error('Missing or changed required v5 history identity/output policy.');
    const mode = proof.proofs?.[0]?.terminalOutput;
    if (mode?.outputProcessing !== false || mode.tool !== sttyTool || typeof mode.verifiedAt !== 'string' || !Number.isFinite(Date.parse(mode.verifiedAt))) throw new Error('Missing parent-observed private PTY output proof.');
    validateOutputDescription(mode.description);
    const report = checkEditingEvidence(output, evidence.historyEvidence, runId, 'history', historySuiteId, buildHistoryReport, 30);
    if (report.snapshots) validateOutputGeometry(mode.description, report.snapshots[0].screen.rows, report.snapshots[0].screen.cols);
}

/** Select the newest required schema from admitted declarations; private evidence cannot fall back to any frozen legacy version. */
function ownedReceiptVersion(context, { includesEditing, includesHistory, includesResize, includesSaved, includesMetadata, includesInput, includesImages, includesQueries, includesPrivateClipboard, includesAsciiSgr, includesSgrColors, includesSgrRgb, includesSgrAttrs, includesSgrPalette, includesSgrTruecolor, includesTruecolorFinding, includesSgrUnderline, includesSgrUnderlineColor, includesUnderlineResolved, includesSgrReset, includesSgrClosure, includesEraseLine, includesCharEdit, includesEraseDisplay, includesLineEdit, includesScrollback }) {
    let schemaVersion = 1;
    if (context.pixelSuiteId) schemaVersion = 2;
    if (context.colorSuiteId) schemaVersion = 3;
    if (includesEditing) schemaVersion = 4;
    if (includesHistory) schemaVersion = 5;
    if (includesResize) schemaVersion = 6;
    if (includesSaved) schemaVersion = 7;
    if (includesMetadata) schemaVersion = 8;
    if (includesInput) schemaVersion = 9;
    if (includesImages) schemaVersion = 10;
    if (includesQueries) schemaVersion = 11;
    if (includesPrivateClipboard) schemaVersion = 12;
    if (includesAsciiSgr) schemaVersion = 13;
    if (includesSgrColors) schemaVersion = 14;
    if (includesSgrRgb) schemaVersion = 15;
    if (includesSgrAttrs) schemaVersion = 16;
    if (includesSgrPalette) schemaVersion = 17;
    if (includesSgrTruecolor) schemaVersion = 18;
    if (includesTruecolorFinding) schemaVersion = 19;
    if (includesSgrUnderline) schemaVersion = 20;
    if (includesSgrUnderlineColor) schemaVersion = 21;
    if (includesUnderlineResolved) schemaVersion = 22;
    if (includesSgrReset) schemaVersion = 23;
    if (includesSgrClosure) schemaVersion = 24;
    if (includesEraseLine) schemaVersion = 25;
    if (includesCharEdit) schemaVersion = 26;
    if (includesEraseDisplay) schemaVersion = 27;
    if (includesLineEdit) schemaVersion = 28;
    if (includesScrollback) schemaVersion = 29;
    return schemaVersion;
}

/** Which optional evidence suffixes a run declares, newest last. */
function admittedSuffixes(output, evidence, context, proof) {
    const includesEditing = context.eraseSuiteId !== undefined || context.editingSuiteId !== undefined
        || evidence.eraseEvidence !== undefined || evidence.editingEvidence !== undefined;
    const historyMarkers = ['historySuiteId', 'terminalOutput', 'historyCasesSourceSha256', 'historyCaptureSourceSha256', 'historySuiteSourceSha256', 'terminalOutputSourceSha256'];
    const includesHistory = evidence.historyEvidence !== undefined || historyMarkers.some(key => context[key] !== undefined)
        || proof.proofs?.some(entry => entry.terminalOutput !== undefined) === true;
    const includesPrivateClipboard = includesPrivateClipboardEvidence(output, evidence, context, proof);
    const includesAsciiSgr = includesAsciiSgrEvidence(output, evidence, context);
    const includesSgrColors = includesSgrColorsEvidence(output, evidence, context);
    const includesSgrRgb = includesSgrRgbEvidence(output, evidence, context);
    const includesSgrAttrs = includesSgrAttrsEvidence(output, evidence, context);
    const includesSgrPalette = includesSgrPaletteEvidence(output, evidence, context);
    const includesSgrTruecolor = includesSgrTruecolorEvidence(output, evidence, context);
    const includesTruecolorFinding = includesTruecolorFindingEvidence(output, evidence, context);
    const includesSgrUnderline = includesSgrUnderlineEvidence(output, evidence, context);
    const includesSgrUnderlineColor = includesSgrUnderlineColorEvidence(output, evidence, context);
    const includesUnderlineResolved = includesUnderlineResolvedEvidence(output, evidence, context);
    const includesSgrReset = includesSgrResetEvidence(output, evidence, context);
    const includesSgrClosure = includesSgrClosureEvidence(output, evidence, context);
    const includesEraseLine = includesEraseLineEvidence(output, evidence, context);
    const includesCharEdit = includesCharEditEvidence(output, evidence, context);
    const includesEraseDisplay = includesEraseDisplayEvidence(output, evidence, context);
    const includesLineEdit = includesLineEditEvidence(output, evidence, context);
    const includesScrollback = includesScrollbackEvidence(output, evidence, context);
    const includesQueries = includesQueryEvidence(output, evidence, context, proof) || includesPrivateClipboard;
    const includesImages = includesStaticImageEvidence(output, evidence, context, proof) || includesQueries;
    const includesInput = includesInputEvidence(output, evidence, context, proof) || includesImages;
    const includesMetadata = includesMetadataEvidence(output, evidence, context, proof) || includesInput;
    const includesSaved = includesSavedEvidence(output, evidence, context, proof) || includesMetadata;
    const includesResize = includesResizeEvidence(output, evidence, context, proof) || includesSaved;
    return { includesEditing, includesHistory, includesResize, includesSaved, includesMetadata,
        includesInput, includesImages, includesQueries, includesPrivateClipboard, includesAsciiSgr, includesSgrColors, includesSgrRgb, includesSgrAttrs, includesSgrPalette, includesSgrTruecolor, includesTruecolorFinding, includesSgrUnderline, includesSgrUnderlineColor, includesUnderlineResolved, includesSgrReset, includesSgrClosure, includesEraseLine, includesCharEdit, includesEraseDisplay, includesLineEdit, includesScrollback };
}

/** Validate every admitted optional suffix after the mandatory core suites. */
function checkAdmittedSuffixes(output, evidence, runId, context, proof, suffixes) {
    if (suffixes.includesEditing || suffixes.includesHistory || suffixes.includesResize) checkEditingSuites(output, evidence, runId, context);
    if (suffixes.includesHistory || suffixes.includesResize) checkHistoryProof(output, evidence, runId, context, proof);
    if (suffixes.includesResize) checkResizeEvidence(output, evidence.resizeEvidence, runId, context, proof);
    if (suffixes.includesSaved) checkSavedEvidence(output, evidence.savedEvidence, runId, context, proof);
    if (suffixes.includesMetadata) checkMetadataEvidence(output, evidence.metadataEvidence, runId, context, proof);
    if (suffixes.includesInput) checkInputEvidence(output, evidence.inputEvidence, runId, context, proof);
    if (suffixes.includesImages) checkStaticImageEvidence(output, evidence.staticImageEvidence, runId, context, proof);
    if (suffixes.includesQueries) checkQueryEvidence(output, evidence.queryEvidence, runId, context, proof, evidence);
    if (suffixes.includesPrivateClipboard) checkPrivateClipboardEvidence(output, evidence.privateClipboardEvidence, runId, context, proof, evidence);
    if (suffixes.includesAsciiSgr) checkAsciiSgrEvidence(output, evidence, runId, context);
    if (suffixes.includesSgrColors) checkSgrColorsEvidence(output, evidence, runId, context);
    if (suffixes.includesSgrRgb) checkSgrRgbEvidence(output, evidence, runId, context);
    if (suffixes.includesSgrAttrs) checkSgrAttrsEvidence(output, evidence, runId, context);
    if (suffixes.includesSgrPalette) checkSgrPaletteEvidence(output, evidence, runId, context);
    if (suffixes.includesSgrTruecolor) checkSgrTruecolorEvidence(output, evidence, runId, context);
    if (suffixes.includesTruecolorFinding) checkTruecolorFindingEvidence(output, evidence, runId, context);
    if (suffixes.includesSgrUnderline) checkSgrUnderlineEvidence(output, evidence, runId, context);
    if (suffixes.includesSgrUnderlineColor) checkSgrUnderlineColorEvidence(output, evidence, runId, context);
    if (suffixes.includesUnderlineResolved) checkUnderlineResolvedEvidence(output, evidence, runId, context);
    if (suffixes.includesSgrReset) checkSgrResetEvidence(output, evidence, runId, context);
    if (suffixes.includesSgrClosure) checkSgrClosureEvidence(output, evidence, runId, context);
    if (suffixes.includesEraseLine) checkEraseLineEvidence(output, evidence, runId, context);
    if (suffixes.includesCharEdit) checkCharEditEvidence(output, evidence, runId, context);
    if (suffixes.includesEraseDisplay) checkEraseDisplayEvidence(output, evidence, runId, context);
    if (suffixes.includesLineEdit) checkLineEditEvidence(output, evidence, runId, context);
    if (suffixes.includesScrollback) checkScrollbackEvidence(output, evidence, runId, context);
}

/** Finalize all required suite bindings only after verified cleanup; never overwrite a receipt. */
export function completeOwnedReceipt(output, scratch, evidence) {
    const proofBytes = readFileSync(join(output, 'ownership-proof.json'));
    const proof = JSON.parse(proofBytes);
    if (!proof.completed || !proof.cleanedUpAt || proof.cleanupError || fixtureRemains(scratch)) {
        throw new Error('Owned cell evidence has no completed cleanup.');
    }
    const contextBytes = readFileSync(join(output, 'context.json'));
    const context = JSON.parse(contextBytes);
    const runId = context.runId;
    if (!/^[a-f\d]{32}$/.test(runId ?? '')) throw new Error('Invalid owned receipt run identity.');
    checkEvidence(output, evidence, 'cell', runId);
    for (const prefix of suites) checkEvidence(output, evidence[prefix + 'Evidence'], prefix, runId);
    if (context.pixelSuiteId !== undefined) checkPixelEvidence(output, evidence.pixelEvidence, runId, context.pixelSuiteId);
    if (context.colorSuiteId !== undefined) checkColorEvidence(output, evidence.colorEvidence, runId, context.colorSuiteId);
    const suffixes = admittedSuffixes(output, evidence, context, proof);
    checkAdmittedSuffixes(output, evidence, runId, context, proof, suffixes);
    const schemaVersion = ownedReceiptVersion(context, suffixes);
    const receipt = { schemaVersion, ...evidence, ownershipProofSha256: digest(proofBytes),
        contextSha256: digest(contextBytes), rawSha256: digest(readFileSync(join(output, 'raw.json'))),
        fixtureRemoved: true, completedAt: new Date().toISOString() };
    writeFileSync(join(output, 'cell-receipt.json'), JSON.stringify(receipt, null, 2) + '\n', { flag: 'wx', mode: 0o600 });
    return receipt;
}
