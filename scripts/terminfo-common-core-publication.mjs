#!/usr/bin/env node
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { dirname, join, resolve, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { verifiedEvidenceFile, verifyCommonCoreArchive, readCommonCoreRaw } from './terminfo/common-core-evidence.mjs';
import { verifyCommonCoreSource } from './terminfo/common-core-source.mjs';
import { buildWindowMovementRetest } from './terminfo/common-core-retest-report.mjs';
import { buildCommonCoreComparison, comparisonStatusLabels } from './terminfo/common-core-comparison.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const publication = join(root, 'docs/qa/terminfo/2026-11-11-common-core-comparison');
/** Hash publication bytes without normalizing raw evidence. */
const digest = (bytes) => createHash('sha256').update(bytes).digest('hex');
const mode = process.argv[2];
if (!['--check', '--write'].includes(mode) || process.argv.length !== 3) {
    throw new Error('Usage: node scripts/terminfo-common-core-publication.mjs <--check|--write>');
}
const manifestBytes = readFileSync(join(publication, 'manifest.json'));
const manifest = JSON.parse(manifestBytes);
if (manifest.schemaVersion !== 1 || !Array.isArray(manifest.subjects))
    throw new Error('Invalid common-harness publication manifest.');
/** Read a pinned file or exact raw member from its verified archive. */
const verifiedFile = (entry) => {
    const file = verifiedEvidenceFile(publication, entry);
    if (entry.member === undefined) return file.bytes;
    if (entry.member !== 'raw.json') throw new Error('Unsupported archived evidence member.');
    const bytes = readCommonCoreRaw(file.path);
    if (digest(bytes) !== entry.contentSha256) throw new Error('Archived raw record digest differs.');
    return bytes;
};
for (const file of manifest.artifacts ?? []) verifiedFile(file);
const attempts = JSON.parse(verifiedFile((manifest.artifacts ?? []).find((entry) => entry.path === 'attempts.json')));
for (const attempt of attempts.attempts ?? []) verifiedFile(attempt.archive);
const requirementsBytes = verifiedFile(manifest.requirements);
/** Bind subject metadata and measured script snapshots to the owned-run archive. */
const loadSubject = (subject) => {
    const files = Object.fromEntries(
        Object.entries(subject.files).map(([name, entry]) => [name + 'Bytes', verifiedFile(entry)]),
    );
    const archive = verifiedEvidenceFile(publication, subject.archive);
    const members = verifyCommonCoreArchive(archive.path, files.rawBytes);
    const source = JSON.parse(files.sourceBytes);
    if (members['launch-driver.ts'] !== source.driverSha256 || members['collection-worker.ts'] !== source.workerSha256)
        throw new Error(`${subject.id} measured script snapshots differ from source identity.`);
    for (const [name, entry] of Object.entries(subject.files)) {
        const member = entry.member ?? entry.path.split('/').at(-1);
        if (members[member] !== (entry.contentSha256 ?? entry.sha256))
            throw new Error(`Archived ${name} differs from published ${entry.path}`);
    }
    if (source.workerBundleSha256) {
        const bundle = Object.fromEntries(
            ['common-core-worker.ts', 'common-core-handshake.mjs', 'common-core-poll.mjs'].map((name) => [
                name,
                source.harnessSources?.[name],
            ]),
        );
        if (digest(Buffer.from(JSON.stringify(bundle))) !== source.workerBundleSha256) {
            throw new Error('Shared worker helper identity differs.');
        }
    }
    for (const [name, digest] of Object.entries(source.harnessSources ?? {})) {
        if (members['harness-sources/' + name] !== digest) {
            throw new Error('Retained harness helper differs: ' + name);
        }
    }
    if (source.retestSelectorSha256 && members['retest-selector.mjs'] !== source.retestSelectorSha256)
        throw new Error('Retest selector snapshot differs.');
    return { id: subject.id, label: subject.label, ...files };
};
const subjects = manifest.subjects.map(loadSubject);
const retests = (manifest.retests ?? []).map(loadSubject);
if (new Set(retests.map((retest) => retest.id)).size !== retests.length)
    throw new Error('Only one explicitly recorded movement retest per application is supported.');
const report = buildCommonCoreComparison({ requirementsBytes, subjects });
const sourceProof = JSON.parse(readFileSync(join(publication, 'upstream-source.json'), 'utf8'));
if (sourceProof.adapterRevision !== report.method.sourceRevision || sourceProof.probeDefinitionsUnchanged !== true)
    throw new Error('Retained upstream source differs from the measured comparison.');
verifyCommonCoreSource(
    publication,
    sourceProof,
    verifiedEvidenceFile(publication, manifest.subjects[0].files.source).path,
    [...subjects, ...retests],
);
report.method.upstreamBaseRevision = sourceProof.baseRevision;
const sourcePath = 'upstream-source.json';
if (!(manifest.artifacts ?? []).some((entry) => entry.path === sourcePath))
    throw new Error('Source proof is not digest-bound to the manifest.');
report.manifestSha256 = digest(manifestBytes);
const retestReports = retests.map((retest) =>
    buildWindowMovementRetest({
        comparison: report,
        baseline: subjects.find((subject) => subject.id === retest.id),
        retest,
    }),
);
for (const retest of retestReports) {
    const subject = report.subjects.find((subject) => subject.id === retest.subjectId);
    subject.focusedRetest = { ...retest, results: undefined };
    for (const row of report.rows) if (retest.results[row.id]) row.results[subject.id].retest = retest.results[row.id];
}
const caseOutputs = [...subjects, ...retests].map((subject, index) => {
    const isRetest = index >= subjects.length;
    const evidenceDirectory = isRetest ? `${subject.id}/movement-retest` : subject.id;
    const raw = JSON.parse(subject.rawBytes);
    const cases = {
        runId: raw.runId,
        rawSha256: digest(subject.rawBytes),
        fullRunArchive: 'owned-run.zip',
        records: report.rows
            .filter((row) => !isRetest || JSON.parse(subject.configBytes).scheduledIds.includes(row.id))
            .map((row) => {
                const result = raw.results.find((result) => result.featureId === row.id);
                return {
                    featureId: row.id,
                    status: result?.status ?? 'not-scheduled',
                    attempt: result?.attempt ?? null,
                    assertionType: result?.assertionType ?? null,
                    reason: result?.reason ?? null,
                    tests: result?.tests ?? [],
                    assertions: raw.assertions.filter((assertion) => assertion.featureId === row.id),
                    rawReplyRef: result?.rawReplyRef ?? null,
                    rawReplySha256:
                        result?.rawReplyRef && raw.rawReplies[result.rawReplyRef] !== undefined
                            ? digest(Buffer.from(raw.rawReplies[result.rawReplyRef]))
                            : null,
                    screenshotRefs: [
                        ...new Set(
                            (result?.frames ?? [])
                                .filter((frame) => frame.kind === 'pixels')
                                .flatMap((frame) => (frame.ref ? [frame.ref] : [])),
                        ),
                    ],
                };
            }),
    };
    const content = JSON.stringify(cases, null, 2) + '\n',
        lines = content.split('\n');
    for (const row of report.rows) {
        const record = isRetest ? row.results[subject.id].retest : row.results[subject.id];
        if (record) {
            record.evidenceLine =
                lines.findIndex((line) => line.trim() === `"featureId": ${JSON.stringify(row.id)},`) + 1;
            record.evidencePath = `${evidenceDirectory}/cases.json`;
        }
    }
    return [join(publication, evidenceDirectory, 'cases.json'), content];
});
const json = JSON.stringify(report, null, 2) + '\n';
const table = report.subjects
    .map(
        (s) =>
            `| ${s.label} ${s.version} | ${s.counts.supported} | ${s.counts.unsupported} | ${s.counts['probe-todo']} | ${s.counts['probe-invalid']} | ${s.counts.untestable + s.counts['not-scheduled']} |`,
    )
    .join('\n');
/** Escape readable Markdown table text without changing outcomes. */
const cell = (value) =>
    String(value ?? '')
        .replaceAll('|', String.raw`\|`)
        .replaceAll('\n', ' ');
const limits = report.subjects
    .map(
        (s) =>
            `- **${s.label}:** capture ${s.measurementLimits.captureOutcome ?? 'not recorded'}${s.measurementLimits.captureRefusal ? ' — ' + s.measurementLimits.captureRefusal : ''}. Disposable ownership receipt: ${s.measurementLimits.disposableOwnership ?? 'not recorded'} (a separate run-scoped process proof may authorize writes). Withheld probes: ${s.measurementLimits.withheldProbeCount}; invalidated by native bounds changes: ${s.measurementLimits.invalidWindowBoundsCount}.`,
    )
    .join('\n');
const retestMarkdown = retestReports.length
    ? '\n## Focused movement retest\n\nThe original full-run results remain retained above. One unattempted manual position is labeled not tested while its original raw status is preserved. Every movement-invalidated case was retested with the same upstream collector, shared worker, executable/payload identity and grid. Latest combined counts include every retest outcome, including any unsupported or inconclusive result.\n\n' +
      retestReports
          .map(
              (retest) =>
                  `- ${retest.subjectId}: ${retest.counts.supported}/${retest.caseIds.length} supported in retest \`${retest.runId}\`; latest combined evidence ${retest.currentCounts.supported} supported / ${retest.currentCounts.unsupported} unsupported / ${retest.currentCounts['probe-invalid']} invalid / ${retest.currentCounts['probe-todo']} not concluded / ${retest.currentCounts.untestable} untestable / ${retest.currentCounts['not-scheduled']} not tested. Original run \`${retest.baselineRunId}\` remains retained.\n`,
          )
          .join('') +
      '\n| Retested case | New outcome |\n|---|---|\n' +
      retestReports
          .flatMap((retest) =>
              retest.caseIds.map((id) => `| ${id} | ${comparisonStatusLabels[retest.results[id].status]} |`),
          )
          .join('\n') +
      '\n'
    : '';
const markdown =
    `# Common-harness Core comparison\n\nThese are fresh application runs through the same upstream terminal probes. Ntilde's [separate 80/80 detailed qualification](../2026-11-11-core-reverse-wide-results/report.md) is not substituted into this comparison.\n\n` +
    `Upstream/adapter source: \`${report.method.sourceRevision}\`; suite \`${report.method.probeHash}\`. Measured macOS version: ${report.method.os}; PTY geometry: ${report.method.geometry.rows} × ${report.method.geometry.cols}.\n\n` +
    `| Application | Supported | Unsupported | Not concluded | Invalid measurement | Untestable / not tested |\n|---|---:|---:|---:|---:|---:|\n${table}\n\n` +
    `All counts use the same 80 feature IDs. The exact upstream application probe decides each result. A missing observer, invalid measurement or untestable framework API is not a terminal failure. Defaults and capture conditions are retained with the evidence. Observer availability differs across applications, so these counts are not a capability ranking.\n\n` +
    `The local adapter adds native launch/ownership and preserves measured Retina screenshots without resampling. Probe definitions and grading rules are unchanged; the upstream revision, adapter patch and sources are retained. Historical website scores are not mixed with these runs.\n\n${limits}\n\n` +
    `Reproduce the published projection: \`node scripts/terminfo-common-core-publication.mjs --check\`. A new native collection additionally needs the recorded applications, upstream dependencies and desktop access.\n\n` +
    `| Feature | ${report.subjects.map((s) => cell(s.label)).join(' | ')} |\n|---|${report.subjects.map(() => '---').join('|')}|\n` +
    report.rows
        .map(
            (row) =>
                `| [${row.id}](${row.url}) | ${report.subjects.map((s) => comparisonStatusLabels[row.results[s.id].status]).join(' | ')} |`,
        )
        .join('\n') +
    '\n' +
    retestMarkdown;
const site = {
    schemaVersion: report.schemaVersion,
    requirementCount: report.requirementCount,
    method: report.method,
    manifestSha256: report.manifestSha256,
    subjects: report.subjects,
    evidencePath: 'docs/qa/terminfo/2026-11-11-common-core-comparison',
    rows: report.rows.map((row) => ({
        ...row,
        results: Object.fromEntries(
            Object.entries(row.results).map(([id, result]) => [
                id,
                {
                    evidencePath: result.evidencePath,
                    retest: result.retest ?? null,
                    evidenceLine: result.evidenceLine,
                    status: result.status,
                    method: result.method,
                    reason: result.reason,
                    scheduled: result.scheduled,
                },
            ]),
        ),
    })),
};
const outputs = [
    ...caseOutputs,
    [join(publication, 'report.json'), json],
    [join(publication, 'report.md'), markdown],
    [join(root, 'site/src/data/common-core-comparison.json'), JSON.stringify(site, null, 2) + '\n'],
];
for (const [path, expected] of outputs) {
    if (mode === '--write') {
        mkdirSync(dirname(path), { recursive: true });
        writeFileSync(path, expected);
    } else if (readFileSync(path, 'utf8').replaceAll('\r\n', '\n') !== expected)
        throw new Error(`Derived comparison differs: ${relative(root, path)}`);
}
console.log(
    `Common-harness comparison ${mode === '--check' ? 'verified' : 'written'}: ${report.rows.length} requirements, ${report.subjects.length} applications.`,
);
