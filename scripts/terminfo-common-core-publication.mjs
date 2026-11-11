#!/usr/bin/env node
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { dirname, join, resolve, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { verifiedEvidenceFile, verifyCommonCoreArchive } from './terminfo/common-core-evidence.mjs';
import { buildCommonCoreComparison, comparisonStatusLabels } from './terminfo/common-core-comparison.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const publication = join(root, 'docs/qa/terminfo/2026-11-11-common-core-comparison');
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const mode = process.argv[2];
if (!['--check', '--write'].includes(mode) || process.argv.length !== 3) {
    throw new Error('Usage: node scripts/terminfo-common-core-publication.mjs <--check|--write>');
}
const manifestBytes = readFileSync(join(publication, 'manifest.json'));
const manifest = JSON.parse(manifestBytes);
if (manifest.schemaVersion !== 1 || !Array.isArray(manifest.subjects)) throw new Error('Invalid common-harness publication manifest.');
const verifiedFile = entry => verifiedEvidenceFile(publication, entry).bytes;
for (const file of manifest.artifacts ?? []) verifiedFile(file);
const attempts = JSON.parse(verifiedFile((manifest.artifacts ?? []).find(entry => entry.path === 'attempts.json')));
for (const attempt of attempts.attempts ?? []) verifiedFile(attempt.archive);
const requirementsBytes = verifiedFile(manifest.requirements);
const subjects = manifest.subjects.map(subject => {
    const files = Object.fromEntries(Object.entries(subject.files).map(([name, entry]) => [name + 'Bytes', verifiedFile(entry)]));
    const archive = verifiedEvidenceFile(publication, subject.archive);
    const members = verifyCommonCoreArchive(archive.path, files.rawBytes);
    const source = JSON.parse(files.sourceBytes);
    if (members['launch-driver.ts'] !== source.driverSha256 || members['collection-worker.ts'] !== source.workerSha256) throw new Error(`${subject.id} measured script snapshots differ from source identity.`);
    for (const [name, entry] of Object.entries(subject.files)) {
        const member = entry.path.split('/').at(-1);
        if (members[member] !== entry.sha256) throw new Error(`Archived ${name} differs from published ${entry.path}`);
    }
    return { id: subject.id, label: subject.label, ...files };
});
const report = buildCommonCoreComparison({ requirementsBytes, subjects });
const sourceProof = JSON.parse(readFileSync(join(publication, 'upstream-source.json'), 'utf8'));
if (sourceProof.adapterRevision !== report.method.sourceRevision || sourceProof.probeDefinitionsUnchanged !== true) throw new Error('Retained upstream source differs from the measured comparison.');
for (const subject of subjects) {
    if (JSON.parse(subject.identityBytes).checkerSha256 !== sourceProof.identityGuardSha256) throw new Error(`${subject.id} used a different identity guard.`);
}
report.method.upstreamBaseRevision = sourceProof.baseRevision;
const sourcePath = 'upstream-source.json';
if (!(manifest.artifacts ?? []).some(entry => entry.path === sourcePath)) throw new Error('Source proof is not digest-bound to the manifest.');
report.manifestSha256 = digest(manifestBytes);
const json = JSON.stringify(report, null, 2) + '\n';
const table = report.subjects.map(s => `| ${s.label} ${s.version} | ${s.counts.supported} | ${s.counts.unsupported} | ${s.counts['probe-todo']} | ${s.counts['probe-invalid']} | ${s.counts.untestable + s.counts['not-scheduled']} |`).join('\n');
const cell = value => String(value ?? '').replaceAll('|', '\\|').replaceAll('\n', ' ');
const limits = report.subjects.map(s => `- **${s.label}:** capture ${s.measurementLimits.captureOutcome ?? 'not recorded'}${s.measurementLimits.captureRefusal ? ' — ' + s.measurementLimits.captureRefusal : ''}. Disposable ownership receipt: ${s.measurementLimits.disposableOwnership ?? 'not recorded'} (a separate run-scoped process proof may authorize writes).`).join('\n');
const markdown = `# Common-harness Core comparison\n\nThese are fresh application runs through the same upstream terminal probes. Ntilde's separate 80/80 detailed qualification is not substituted into this comparison.\n\n` +
    `Upstream/adapter source: \`${report.method.sourceRevision}\`; suite \`${report.method.probeHash}\`. Measured macOS version: ${report.method.os}; PTY geometry: ${report.method.geometry.rows} × ${report.method.geometry.cols}.\n\n` +
    `| Application | Supported | Unsupported | Not concluded | Invalid measurement | Untestable / not tested |\n|---|---:|---:|---:|---:|---:|\n${table}\n\n` +
    `All counts use the same 80 feature IDs. The exact upstream application probe decides each result. A missing observer, invalid measurement or untestable framework API is not a terminal failure. Defaults and capture conditions are retained with the evidence. Observer availability differs across applications, so these counts are not a capability ranking.\n\n` +
    `The local adapter adds native launch/ownership and preserves measured Retina screenshots without resampling. Probe definitions and grading rules are unchanged; the upstream revision, adapter patch and sources are retained. Historical website scores are not mixed with these runs.\n\n${limits}\n\n` +
    `Reproduce the published projection: \`node scripts/terminfo-common-core-publication.mjs --check\`. A new native collection additionally needs the recorded applications, upstream dependencies and desktop access.\n\n` +
    `| Feature | ${report.subjects.map(s => cell(s.label)).join(' | ')} |\n|---|${report.subjects.map(() => '---').join('|')}|\n` +
    report.rows.map(row => `| [${row.id}](${row.url}) | ${report.subjects.map(s => comparisonStatusLabels[row.results[s.id].status]).join(' | ')} |`).join('\n') + '\n';
const site = { schemaVersion: report.schemaVersion, requirementCount: report.requirementCount, method: report.method,
    manifestSha256: report.manifestSha256, subjects: report.subjects,
    evidencePath: 'docs/qa/terminfo/2026-11-11-common-core-comparison',
    rows: report.rows.map(row => ({ ...row, results: Object.fromEntries(Object.entries(row.results).map(([id, result]) => [id,
        { evidenceLine: result.evidenceLine, status: result.status, method: result.method, reason: result.reason, scheduled: result.scheduled }])) })) };
const outputs = [[join(publication, 'report.json'), json], [join(publication, 'report.md'), markdown],
    [join(root, 'site/src/data/common-core-comparison.json'), JSON.stringify(site, null, 2) + '\n']];
for (const [path, expected] of outputs) {
    if (mode === '--write') { mkdirSync(dirname(path), { recursive: true }); writeFileSync(path, expected); }
    else if (readFileSync(path, 'utf8') !== expected) throw new Error(`Derived comparison differs: ${relative(root, path)}`);
}
console.log(`Common-harness comparison ${mode === '--check' ? 'verified' : 'written'}: ${report.rows.length} requirements, ${report.subjects.length} applications.`);
