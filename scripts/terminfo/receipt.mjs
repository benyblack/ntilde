import { createHash } from 'node:crypto';
import { lstatSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { buildPixelReport } from './pixels.mjs';
import { pixelSuiteId } from './pixels-cases.mjs';

import { buildColorReport } from './colors.mjs';
import { colorSuiteId } from './color-cases.mjs';

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
    let schemaVersion = 1;
    if (context.pixelSuiteId) schemaVersion = 2;
    if (context.colorSuiteId) schemaVersion = 3;
    const receipt = { schemaVersion, ...evidence, ownershipProofSha256: digest(proofBytes),
        contextSha256: digest(contextBytes), rawSha256: digest(readFileSync(join(output, 'raw.json'))),
        fixtureRemoved: true, completedAt: new Date().toISOString() };
    writeFileSync(join(output, 'cell-receipt.json'), JSON.stringify(receipt, null, 2) + '\n', { flag: 'wx', mode: 0o600 });
    return receipt;
}
