import { existsSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { extractOwnedArchive, verifyOwnedRun } from '../terminfo-core-results.mjs';
import { buildModernReport, modernRoot } from './modern-results.mjs';
import { digest, parseJson, pinnedJson, requireModern, same } from './modern-inputs.mjs';
import { modernCursorSuiteId } from './modern-cursor.mjs';

export const cursorPublication = 'docs/qa/terminfo/2026-11-23-modern-cursor-results';
const manifestSha256 = 'd7d729d6cdd41a0407bd61ca282cd1586fb409bbdaa46eaed7bf77efd758acfd';
const requirementTraces = {
    'cursor.horizontal-absolute': 'cha',
    'cursor.next-line': 'cnl',
    'cursor.ansi-save': 'ansi-round-trip',
    'cursor.ansi-restore': 'ansi-round-trip',
};

export function verifyModernCursorRun(directory, manifest) {
    requireModern(existsSync(join(directory, 'cell-receipt.json')) && !existsSync(join(directory, 'failure.json'))
        && !existsSync(join(directory, 'cleanup-failure.json')), 'missing or failed cursor collection receipt');
    const { context, receipt } = verifyOwnedRun(directory, manifest);
    const binding = receipt.modernCursorEvidence;
    requireModern(binding?.suiteId === modernCursorSuiteId && binding.runId === context.runId, 'missing cursor suite receipt binding');
    return { context, receipt, report: parseJson(join(directory, binding.file)) };
}

function withCursorMeasurement(base, body) {
    const dir = join(base, cursorPublication);
    const manifest = pinnedJson(join(dir, 'manifest.json'), manifestSha256);
    const scratch = mkdtempSync(join(tmpdir(), 'ntilde-modern-cursor-'));
    try {
        for (const attempt of manifest.attempts) {
            requireModern(/^attempt-\d+\.zip$/.test(attempt.archive) && attempt.reason
                && digest(readFileSync(join(dir, attempt.archive))) === manifest.archives[attempt.archive],
            'changed cursor attempt archive');
        }
        extractOwnedArchive(dir, manifest, scratch, join(dir, 'owned-run.zip'));
        const { context, receipt, report } = verifyModernCursorRun(scratch, manifest);
        const { appRevision, scriptsRevision } = manifest.measurement;
        const measurement = { runId: context.runId, startedAt: context.startedAt, appRevision, scriptsRevision,
            appVersion: context.appVersion, appVersionSource: context.appVersionSource,
            executableSha256: context.executableSha256, assemblySha256: context.assemblySha256,
            sourcePolicy: context.sourceSnapshotPolicy, sourceSnapshotManifestSha256: context.sourceSnapshotManifestSha256,
            receiptSchema: receipt.schemaVersion, receiptCompletedAt: receipt.completedAt, fixtureRemoved: receipt.fixtureRemoved,
            geometry: report.geometry, publication: cursorPublication, manifestSha256, newMeasurement: true,
            archiveSha256: manifest.archives['owned-run.zip'], attempts: manifest.attempts };
        return body(report, measurement);
    } finally { rmSync(scratch, { recursive: true, force: true }); }
}

export function applyModernCursorReport(baseline, report, measurement) {
    const ids = Object.keys(requirementTraces);
    requireModern(same(report.cases.map(item => item.id), ids), 'unexpected cursor claim set');
    const rows = baseline.rows.map(row => {
        if (!Object.hasOwn(requirementTraces, row.id)) return row;
        const result = report.cases.find(item => item.id === row.id);
        const trace = report.traces.find(item => item.traceId === result.traceId);
        requireModern(row.conclusion === 'inconclusive' && result.traceId === requirementTraces[row.id]
            && trace && ['pass', 'fail'].includes(result.conclusion), 'unexpected cursor claim ' + row.id);
        return { ...row, conclusion: result.conclusion, blocker: null, observed: trace,
            criterionPath: 'fenced CPR and paired App cursor reads',
            evidence: { file: 'mcur-observations.json', sourceId: trace.traceId, runId: report.runId,
                recordSha256: digest(JSON.stringify(trace)) } };
    });
    const totals = Object.fromEntries(['pass', 'fail', 'inconclusive'].map(verdict => [verdict, rows.filter(row => row.conclusion === verdict).length]));
    return { ...baseline, schemaVersion: 2, totals, ownedCriterionPassPercentage: totals.pass / baseline.requirementsCount * 100,
        evidencePolicy: 'Combined assessment of the original Modern evidence and one new direct-local macOS cursor collection. Each verdict applies to its frozen criterion and named observation path. The results describe the measured settings and pane. They leave the historical stock grades, Core results and qualification inventory as separate records.',
        cursorMeasurement: measurement, rows };
}

export function buildModernCursorPublication(base = modernRoot) {
    const baseline = buildModernReport(base);
    return withCursorMeasurement(base, (report, measurement) => applyModernCursorReport(baseline, report, measurement));
}

export const modernCursorOutputPaths = base => ({ json: join(base, cursorPublication, 'report.json'), markdown: join(base, cursorPublication, 'report.md') });
