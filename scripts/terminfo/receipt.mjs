import { createHash } from 'node:crypto';
import { lstatSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

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

/** Finalize all required suite bindings only after verified cleanup; never overwrite a receipt. */
export function completeOwnedReceipt(output, scratch, evidence) {
    const proofBytes = readFileSync(join(output, 'ownership-proof.json'));
    const proof = JSON.parse(proofBytes);
    if (!proof.completed || !proof.cleanedUpAt || proof.cleanupError || fixtureRemains(scratch)) {
        throw new Error('Owned cell evidence has no completed cleanup.');
    }
    const contextBytes = readFileSync(join(output, 'context.json'));
    const runId = JSON.parse(contextBytes).runId;
    if (!/^[a-f\d]{32}$/.test(runId ?? '')) throw new Error('Invalid owned receipt run identity.');
    checkEvidence(output, evidence, 'cell', runId);
    for (const prefix of suites) checkEvidence(output, evidence[prefix + 'Evidence'], prefix, runId);
    const receipt = { schemaVersion: 1, ...evidence, ownershipProofSha256: digest(proofBytes),
        contextSha256: digest(contextBytes), rawSha256: digest(readFileSync(join(output, 'raw.json'))),
        fixtureRemoved: true, completedAt: new Date().toISOString() };
    writeFileSync(join(output, 'cell-receipt.json'), JSON.stringify(receipt, null, 2) + '\n', { flag: 'wx', mode: 0o600 });
    return receipt;
}
