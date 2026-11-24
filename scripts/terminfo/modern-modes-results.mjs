import { existsSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { extractOwnedArchive, verifyOwnedRun } from '../terminfo-core-results.mjs';
import { buildModernCursorPublication, cursorPublication } from './modern-cursor-results.mjs';
import { modernRoot } from './modern-results.mjs';
import { digest, parseJson, pinnedJson, requireModern, same } from './modern-inputs.mjs';
import { modernModeRequirements, modernModesSuiteId } from './modern-modes.mjs';

export const modesPublication = 'docs/qa/terminfo/2026-11-24-modern-modes-results';
const manifestSha256 = '9d5189fe693a99b4f7da8b3493c1637f863ac0c5a9f4791772cdc89ee04216b8';

export function verifyModernModesRun(directory, manifest) {
    requireModern(existsSync(join(directory, 'cell-receipt.json')) && !existsSync(join(directory, 'failure.json'))
        && !existsSync(join(directory, 'cleanup-failure.json')), 'missing or failed mode collection receipt');
    const { context, receipt } = verifyOwnedRun(directory, manifest);
    const binding = receipt.modernModesEvidence;
    requireModern(binding?.suiteId === modernModesSuiteId && binding.runId === context.runId, 'missing mode suite receipt binding');
    return { context, receipt, report: parseJson(join(directory, binding.file)) };
}

function readModesMeasurement(base) {
    const directory = join(base, modesPublication);
    const manifest = pinnedJson(join(directory, 'manifest.json'), manifestSha256);
    const scratch = mkdtempSync(join(tmpdir(), 'ntilde-modern-modes-'));
    try {
        for (const attempt of manifest.attempts) {
            requireModern(/^attempt-\d+\.zip$/.test(attempt.archive) && attempt.reason
                && digest(readFileSync(join(directory, attempt.archive))) === manifest.archives[attempt.archive], 'changed mode attempt archive');
        }
        extractOwnedArchive(directory, manifest, scratch, join(directory, 'owned-run.zip'));
        const { context, receipt, report } = verifyModernModesRun(scratch, manifest);
        const measurement = { ...manifest.measurement, startedAt: context.startedAt, appVersion: context.appVersion,
            appVersionSource: context.appVersionSource, executableSha256: context.executableSha256, assemblySha256: context.assemblySha256,
            sourceSnapshotManifestSha256: context.sourceSnapshotManifestSha256, receiptCompletedAt: receipt.completedAt,
            fixtureRemoved: receipt.fixtureRemoved, geometry: report.geometry, publication: modesPublication, manifestSha256,
            archiveSha256: manifest.archives['owned-run.zip'], attempts: manifest.attempts };
        return { report, measurement };
    } finally { rmSync(scratch, { recursive: true, force: true }); }
}

export function buildModernModesPublication(base = modernRoot) {
    const predecessor = buildModernCursorPublication(base);
    const { report, measurement } = readModesMeasurement(base);
    requireModern(same(report.cases.map(result => result.id), Object.values(modernModeRequirements)), 'unexpected mode claim set');
    const results = new Map(report.cases.map(result => [result.id, result]));
    const rows = predecessor.rows.map(row => {
        const result = results.get(row.id);
        if (!result) return row;
        requireModern(row.conclusion === 'inconclusive', 'mode claim already graded ' + row.id);
        return { ...row, conclusion: result.conclusion, blocker: result.conclusion === 'inconclusive' ? result.outcome : null,
            criterionPath: 'fenced DECRPM App protocol exchange', observed: result,
            evidence: { file: 'mmod-observations.json', sourceId: String(result.mode), runId: report.runId,
                recordSha256: digest(JSON.stringify(result)) } };
    });
    const totals = Object.fromEntries(['pass', 'fail', 'inconclusive'].map(verdict => [verdict, rows.filter(row => row.conclusion === verdict).length]));
    return { ...predecessor, schemaVersion: 3, totals, ownedCriterionPassPercentage: totals.pass / predecessor.requirementsCount * 100,
        evidencePolicy: 'The four-cursor Modern assessment is rederived from its original inputs. A fresh owned App run grades two further frozen criteria through their protocol state exchanges.',
        predecessor: { publication: cursorPublication, totals: predecessor.totals }, modesMeasurement: measurement, rows };
}

function readableMode(row) {
    const result = row.observed;
    const lines = [`### Mode ${result.mode}`, '', `\`${row.id}\` — **${row.conclusion}** (${result.outcome}).`, '',
        '| Stage | Exact framed request | Exact framed reply | DECRPM states | Elapsed ms |',
        '| --- | --- | --- | --- | --- |'];
    for (const item of result.exchanges) {
        const states = item.replies.map(reply => reply.pm ?? 'absent').join(' → ');
        lines.push(`| ${item.role} | \`${JSON.stringify(item.request)}\` | \`${JSON.stringify(item.response)}\` | ${states} | ${item.elapsedMs.toFixed(2)} |`);
    }
    return [...lines, ''];
}

export function readableModernModes(report) {
    const { totals, modesMeasurement: m, predecessor } = report;
    const rows = report.rows.filter(row => row.evidence?.runId === m.runId);
    const previous = predecessor.totals;
    return [
        '# Ntilde Modern assessment — synchronized output and color scheme reporting', '',
        `**${totals.pass} pass / ${totals.fail} fail / ${totals.inconclusive} inconclusive across ${report.requirementsCount} frozen requirements.**`, '',
        `The [four-cursor assessment](../2026-11-23-modern-cursor-results/report.md) had ${previous.pass} pass / ${previous.fail} fail / ${previous.inconclusive} inconclusive. The [original Modern publication](../2026-11-15-modern-results/report.md) retains the frozen criteria, source snapshot, and upstream attribution.`, '',
        'These exchanges measure the App protocol state of modes 2026 and 2031 in one disposable direct-local macOS pane. The frozen criteria have separate library arms. Frame presentation and unsolicited color-scheme notifications are outside this measurement. Core, stock collector results, and the qualification inventory retain their existing grades.', '',
        '## Protocol exchanges', '',
        'Each query ends at a DA barrier. The reset-state branch sends DECSET, the first query and barrier, DECRST, and the final query and barrier in one PTY write. The 2026 branch requires the complete exchange inside the App’s 200 ms synchronized-output window.', '',
        ...rows.flatMap(readableMode),
        '## Measurement and provenance', '',
        `Owned run \`${m.runId}\`, started ${m.startedAt}. App source \`${m.appRevision}\`; measured scripts \`${m.scriptsRevision}\`; App version \`${m.appVersion}\` (${m.appVersionSource}). Geometry ${m.geometry.rows}×${m.geometry.cols}; source policy \`${m.sourcePolicy}\`; receipt ${m.receiptSchema}, completed ${m.receiptCompletedAt}.`, '',
        `The [owned run](owned-run.zip) has SHA-256 \`${m.archiveSha256}\`. It retains the worker exchanges in \`mmod-run.json\`, the pane-bound journal in \`mmod-snapshots.jsonl\`, and the derived traces in \`mmod-observations.json\`. The receipt verifies the source manifest, mode exchanges, restored main-screen projection, and process and fixture cleanup.`, '',
        ...m.attempts.flatMap(attempt => [`Retained attempt [${attempt.archive}](${attempt.archive}). ${attempt.reason}`, '']),
        '## Reproduce', '',
        'Run the independent receipt and report derivation offline with Node 24+ and Python 3.', '',
        '```sh', 'node scripts/terminfo-modern-results.mjs --check --modes', '```', '',
        'The [machine-readable report](report.json) includes every frozen criterion, observation path, stock verdict, and current blocker.', '',
        'For a fresh capture, build the Release App at the recorded App revision with the build wrapper and [prepare the collector](../../QA_TERMINFO.md) at the measured scripts revision. Run the standalone sidecar on an awake macOS desktop. The launcher acquires the desktop collection lock.', '',
        '```sh',
        'scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar',
        `node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version ${m.appVersion} --scope osc633`,
        '```', '',
    ].join('\n');
}

export const modernModesOutputPaths = base => ({ json: join(base, modesPublication, 'report.json'), markdown: join(base, modesPublication, 'report.md') });
