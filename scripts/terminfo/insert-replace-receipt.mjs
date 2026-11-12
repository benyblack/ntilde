import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { adaptInsertReplaceCollector, insertReplaceAdapterId } from './insert-replace-grading-collector.mjs';
import { insertReplaceContractsVersion, insertReplaceSuiteId } from './insert-replace-contracts.mjs';
import { buildInsertReplaceReport } from './insert-replace-grading.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const digest = value => /^[a-f\d]{64}$/.test(value ?? '');
const files = ['irm-observations.json', 'irm-run.json', 'irm-snapshots.jsonl', 'collector-irm.mjs'];

/** An explicit artifact or declaration can never be silently treated as absent. */
export function includesInsertReplaceEvidence(output, evidence, context, firstPolicy) {
    const policy = Number(/^readonly-owned-source-graph-v(\d+)$/.exec(context.sourceSnapshotPolicy ?? '')?.[1] ?? 0);
    if (!Number.isSafeInteger(firstPolicy) || firstPolicy < 1) throw new Error('Missing IRM first source policy.');
    return policy >= firstPolicy || context.insertReplaceAdapterId !== undefined || context.insertReplaceSuiteId !== undefined
        || context.insertReplaceBundleSha256 !== undefined || context.insertReplaceContractsVersion !== undefined
        || evidence.insertReplaceEvidence !== undefined || files.some(file => lstatSync(join(output, file), { throwIfNoEntry: false }) !== undefined);
}

/** Check retained bytes, predecessor binding, adapter derivation and the complete report. */
export function checkInsertReplaceEvidence(output, evidence, runId, context) {
    const binding = evidence.insertReplaceEvidence;
    if (!binding || binding.runId !== runId || binding.suiteId !== insertReplaceSuiteId || binding.file !== 'irm-observations.json'
        || binding.runFile !== 'irm-run.json' || binding.provisionalUntil !== 'cell-receipt.json'
        || binding.contractsVersion !== insertReplaceContractsVersion || context.insertReplaceContractsVersion !== binding.contractsVersion
        || context.insertReplaceAdapterId !== insertReplaceAdapterId || context.insertReplaceSuiteId !== insertReplaceSuiteId
        || ![binding.sha256, binding.runSha256, binding.adapterBundleSha256].every(digest)) throw new Error('Invalid IRM receipt binding.');
    if (!evidence.lrMarginsEvidence || !digest(context.lrMarginsBundleSha256)) throw new Error('IRM evidence requires its left/right-margin predecessor.');
    for (const file of [...files, 'collector-lrmm.mjs']) {
        if (!lstatSync(join(output, file)).isFile()) throw new Error(`IRM artifact is not a regular file: ${file}`);
    }
    const reportBytes = readFileSync(join(output, binding.file)), runBytes = readFileSync(join(output, binding.runFile));
    const adapterBytes = readFileSync(join(output, 'collector-irm.mjs')), upstream = readFileSync(join(output, 'collector-lrmm.mjs'));
    if (hash(reportBytes) !== binding.sha256 || hash(runBytes) !== binding.runSha256 || hash(adapterBytes) !== binding.adapterBundleSha256
        || context.insertReplaceBundleSha256 !== binding.adapterBundleSha256 || hash(upstream) !== context.lrMarginsBundleSha256
        || !Buffer.from(adaptInsertReplaceCollector(upstream)).equals(adapterBytes)) throw new Error('IRM retained bytes or layered derivation changed.');
    const report = JSON.parse(reportBytes), run = JSON.parse(runBytes);
    if (report.adapter?.bundleSha256 !== binding.adapterBundleSha256 || report.adapter?.upstreamBundleSha256 !== context.upstreamBundleSha256) {
        throw new Error('IRM report identity differs from the receipt.');
    }
    const text = readFileSync(join(output, 'irm-snapshots.jsonl'), 'utf8').trim();
    if (!text) throw new Error('IRM journal is empty.');
    const frames = text.split('\n').map(line => JSON.parse(line));
    const derived = buildInsertReplaceReport(frames, run, runId, report.adapter, { contractsVersion: binding.contractsVersion });
    if (JSON.stringify(report) !== JSON.stringify(derived) || JSON.stringify(report.snapshots) !== JSON.stringify(frames)
        || binding.counts?.[report.effect.outcome] !== 1 || Object.keys(binding.counts).length !== 1) {
        throw new Error('IRM report does not match retained evidence.');
    }
}
