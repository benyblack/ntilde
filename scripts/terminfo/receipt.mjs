import { includesMetadataEvidence, checkMetadataEvidence } from './metadata-receipt.mjs';
import { includesInputEvidence, checkInputEvidence } from './input-receipt.mjs';
import { includesQueryEvidence, checkQueryEvidence } from './query-receipt.mjs';
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
import { ownedOutputPolicy, sttyTool, validateOutputDescription, validateOutputGeometry } from './terminal-output.mjs';

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
    if (context.historySuiteId !== historySuiteId || context.terminalOutput !== ownedOutputPolicy) throw new Error('Missing or changed required v5 history identity/output policy.');
    const mode = proof.proofs?.[0]?.terminalOutput;
    if (mode?.outputProcessing !== false || mode.tool !== sttyTool || typeof mode.verifiedAt !== 'string' || !Number.isFinite(Date.parse(mode.verifiedAt))) throw new Error('Missing parent-observed private PTY output proof.');
    validateOutputDescription(mode.description);
    const report = checkEditingEvidence(output, evidence.historyEvidence, runId, 'history', historySuiteId, buildHistoryReport, 30);
    if (report.snapshots) validateOutputGeometry(mode.description, report.snapshots[0].screen.rows, report.snapshots[0].screen.cols);
}

function ownedReceiptVersion(context, { includesEditing, includesHistory, includesResize, includesSaved, includesMetadata, includesInput, includesImages, includesQueries }) {
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
    return schemaVersion;
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
    const includesEditing = context.eraseSuiteId !== undefined || context.editingSuiteId !== undefined
        || evidence.eraseEvidence !== undefined || evidence.editingEvidence !== undefined;
    const historyMarkers = ['historySuiteId', 'terminalOutput', 'historyCasesSourceSha256', 'historyCaptureSourceSha256', 'historySuiteSourceSha256', 'terminalOutputSourceSha256'];
    const includesHistory = evidence.historyEvidence !== undefined || historyMarkers.some(key => context[key] !== undefined)
        || proof.proofs?.some(entry => entry.terminalOutput !== undefined) === true;
    const includesQueries = includesQueryEvidence(output, evidence, context, proof);
    const includesImages = includesStaticImageEvidence(output, evidence, context, proof) || includesQueries;
    const includesInput = includesInputEvidence(output, evidence, context, proof) || includesImages;
    const includesMetadata = includesMetadataEvidence(output, evidence, context, proof) || includesInput;
    const includesSaved = includesSavedEvidence(output, evidence, context, proof) || includesMetadata;
    const includesResize = includesResizeEvidence(output, evidence, context, proof) || includesSaved;
    if (includesEditing || includesHistory || includesResize) checkEditingSuites(output, evidence, runId, context);
    if (includesHistory || includesResize) checkHistoryProof(output, evidence, runId, context, proof);
    if (includesResize) checkResizeEvidence(output, evidence.resizeEvidence, runId, context, proof);
    if (includesSaved) checkSavedEvidence(output, evidence.savedEvidence, runId, context, proof);
    if (includesMetadata) checkMetadataEvidence(output, evidence.metadataEvidence, runId, context, proof);
    if (includesInput) checkInputEvidence(output, evidence.inputEvidence, runId, context, proof);
    if (includesImages) checkStaticImageEvidence(output, evidence.staticImageEvidence, runId, context, proof);
    if (includesQueries) checkQueryEvidence(output, evidence.queryEvidence, runId, context, proof, evidence);
    const schemaVersion = ownedReceiptVersion(context, { includesEditing, includesHistory, includesResize, includesSaved, includesMetadata, includesInput, includesImages, includesQueries });
    const receipt = { schemaVersion, ...evidence, ownershipProofSha256: digest(proofBytes),
        contextSha256: digest(contextBytes), rawSha256: digest(readFileSync(join(output, 'raw.json'))),
        fixtureRemoved: true, completedAt: new Date().toISOString() };
    writeFileSync(join(output, 'cell-receipt.json'), JSON.stringify(receipt, null, 2) + '\n', { flag: 'wx', mode: 0o600 });
    return receipt;
}
