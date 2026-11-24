import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { isDeepStrictEqual as equal } from 'node:util';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { digest, pinnedJson, requireModern } from './modern-inputs.mjs';
import { buildModernOscCwdPublication, oscCwdPublication } from './modern-osc7-cwd-results.mjs';
import { controls, gradeEnvironment, ids, names } from './modern-child-environment.mjs';
import { verifyChildEnvironmentRun } from './modern-child-environment-owned.mjs';
import { modernRoot } from './modern-results.mjs';

export const childEnvironmentPublication = 'docs/qa/terminfo/2026-11-24-modern-child-environment-results';
const manifestSha256 = '251b85ef537ffa2092691c78781e55bb53864e05eaec221c1c5d05ecafe705d1';
const criterionSha256 = 'e48d44e88175d05493965f90e1046b0fcdf637a23f2abd22c9a689ea36c25ec6';

export function leaseReleasedAt(directory, context, ownership) {
    const events = readFileSync(join(directory, 'desktop-lease.jsonl'), 'utf8').trimEnd().split('\n').map(line => JSON.parse(line));
    const times = events.map(event => Date.parse(event.at));
    requireModern(equal(events.map(event => event.event), ['acquired', 'launch-attempt', 'cleanup-verified', 'released'])
        && events.every(event => event.lease === context.runId && event.pid === ownership.proofs[0].app.ppid)
        && times.every((time, index) => Number.isFinite(time) && (index === 0 || time >= times[index - 1]))
        && times[0] <= Date.parse(context.startedAt) && Date.parse(context.startedAt) <= times[1]
        && times[1] <= Date.parse(ownership.proofs[0].at) && times[2] <= Date.parse(ownership.cleanedUpAt)
        && Date.parse(ownership.sourceSnapshot.removedAt) <= times[3]
        && events[0].entrypoint === 'terminfo-owned.mjs' && events[1].entrypoint === 'terminfo-owned.mjs'
        && events[2].by === 'marker-scan' && events[3].launch === 'attempted',
    'child environment desktop lease did not verify and release');
    return events.at(-1).at;
}

function readRun(directory, entry) {
    const scratch = mkdtempSync(join(tmpdir(), 'ntilde-modern-child-environment-'));
    try {
        extractOwnedArchive(directory, { archives: { 'owned-run.zip': entry.sha256 } }, scratch, join(directory, entry.archive));
        const { child, context, ownership, launch, pane, receipt } = verifyChildEnvironmentRun(scratch);
        requireModern(entry.runId === context.runId && entry.control === launch.control, 'child environment run identity differs from manifest');
        return { child, launch, measurement: { ...entry, startedAt: context.startedAt, appVersion: context.appVersion,
            appVersionSource: context.appVersionSource, executableSha256: context.executableSha256,
            appBinarySha256: launch.appBinaries, nodeVersion: context.nodeVersion, nodeSha256: launch.nodeSha256,
            sourcePolicy: context.sourceSnapshotPolicy, sourceSnapshotManifestSha256: context.sourceSnapshotManifestSha256,
            receiptSchema: receipt.schemaVersion, childOutputSha256: receipt.sha256['child-environment.json'],
            parentInputs: launch.inputs, pane, fixtureRemoved: receipt.fixtureRemoved,
            cleanedUpAt: ownership.cleanedUpAt, leaseReleasedAt: leaseReleasedAt(scratch, context, ownership) } };
    } finally {
        rmSync(scratch, { recursive: true, force: true });
    }
}

function readPair(directory, manifest) {
    requireModern(manifest.schemaVersion === 1
        && ['appRevision', 'scriptsRevision'].every(field => /^[a-f\d]{40}$/.test(manifest[field]))
        && equal(manifest.runs.map(run => run.control), controls)
        && manifest.runs.every(run => run.archive === run.control + '-run.zip'), 'wrong child environment manifest');
    const [clean, inherited] = manifest.runs.map(entry => readRun(directory, entry));
    const a = clean.measurement, b = inherited.measurement;
    requireModern(a.runId !== b.runId && clean.launch.appData !== inherited.launch.appData && a.pane.paneId !== b.pane.paneId
        && Date.parse(a.leaseReleasedAt) <= Date.parse(b.startedAt)
        && ['appVersion', 'executableSha256', 'appBinarySha256', 'nodeVersion', 'nodeSha256', 'sourcePolicy', 'sourceSnapshotManifestSha256', 'receiptSchema']
            .every(field => equal(a[field], b[field])), 'the child environment pair did not use two isolated launches of the same fixture');
    return { results: gradeEnvironment(clean.child, inherited.child), measurements: [a, b] };
}

export function buildModernChildEnvironmentPublication(base = modernRoot) {
    const previous = buildModernOscCwdPublication(base);
    const directory = join(base, childEnvironmentPublication);
    const manifest = pinnedJson(join(directory, 'manifest.json'), manifestSha256);
    const { results, measurements } = readPair(directory, manifest);
    const byId = new Map(results.map(result => [result.id, result]));
    const rows = previous.rows.map(row => {
        const result = byId.get(row.id);
        if (!result) return row;
        requireModern(row.probeStatus === 'manual' && row.conclusion === 'inconclusive' && digest(row.requirement) === criterionSha256,
            'changed frozen environment criterion: ' + row.id);
        byId.delete(row.id);
        return { ...row, conclusion: result.conclusion, blocker: result.reason ?? null,
            criterionPath: 'frozen manual catalogue criterion using the pre-shell environment of two owned direct-local PTY children',
            observed: result, evidence: measurements.map(run => ({ archive: run.archive, file: 'child-environment.json',
                runId: run.runId, sha256: run.childOutputSha256 })) };
    });
    requireModern(byId.size === 0 && rows.length === 69, 'missing frozen environment rows');
    const totals = Object.fromEntries(['pass', 'fail', 'inconclusive'].map(conclusion => [conclusion, rows.filter(row => row.conclusion === conclusion).length]));
    return { ...previous, schemaVersion: 8, totals, ownedCriterionPassPercentage: totals.pass / rows.length * 100,
        evidencePolicy: 'The OSC 7 assessment is rederived first. Two owned direct-local child environment controls grade the four frozen manual catalogue rows.',
        predecessor: { publication: oscCwdPublication, totals: previous.totals },
        childEnvironmentMeasurement: { publication: childEnvironmentPublication, manifestSha256,
            appRevision: manifest.appRevision, appRevisionSource: 'operator-attested build checkout',
            scriptsRevision: manifest.scriptsRevision, platform: manifest.platform, runs: measurements }, rows };
}

function cell({ state, value }) {
    return state === 'present' ? '<code>' + value.replaceAll('&', '&amp;').replaceAll('<', '&lt;')
        .replaceAll('>', '&gt;').replaceAll('|', '&#124;').replaceAll('\n', '&#10;') + '</code>' : state;
}

export function readableModernChildEnvironment(report) {
    const { totals, predecessor, childEnvironmentMeasurement: measurement } = report;
    const [clean, inherited] = measurement.runs;
    const results = ids.map(id => report.rows.find(row => row.id === id).observed);
    return [
        '# Ntilde Modern child environment assessment', '',
        `**${totals.pass} pass / ${totals.fail} fail / ${totals.inconclusive} inconclusive across ${report.requirementsCount} frozen requirements.**`, '',
        `The [OSC 7 predecessor](../2026-11-24-modern-osc7-cwd-results/report.md) had ${predecessor.totals.pass} pass, ${predecessor.totals.fail} fail and ${predecessor.totals.inconclusive} inconclusive. The four environment rows are manual. The [grading contract](../modern-child-environment.md) records the frozen catalogue descriptions and the controls used to interpret them.`, '',
        '| Variable | Clean App input | Clean child | Inherited App input | Inherited child | Modern verdict |',
        '| --- | --- | --- | --- | --- | --- |',
        ...results.map((result, index) => `| \`${names[index]}\` | ${cell(clean.parentInputs[names[index]])} | ${cell(result.clean)} | ${cell(inherited.parentInputs[names[index]])} | ${cell(result.inherited)} | ${result.conclusion} |`), '',
        'The first two rows assess Ntilde’s truecolor hint and terminal identity. Their clean launches measure provision, and their sentinel launches measure override. The separately audited `NO_COLOR` row measures preservation of a user setting. Each program decides whether to suppress its own ANSI colors.', '',
        results.at(-1).reason + ' The frozen reporting convention retains this VTE-specific row in the denominator.', '',
        '## Evidence and reproduction', '',
        `The sidecar was built from App checkout \`${measurement.appRevision}\` with version \`${clean.appVersion}\`. The collector used checkout \`${measurement.scriptsRevision}\`, Node ${clean.nodeVersion} and ${measurement.platform}. The observation is scoped to the disposable macOS direct-local App profile.`, '',
        ...measurement.runs.map(run => `- [${run.control} launch](${run.archive}) \`${run.runId}\` bound App PID ${run.pane.appPid}, PTY child PID ${run.pane.workerPid}, and pane \`${run.pane.paneId}\`. The desktop lease released at ${run.leaseReleasedAt} after verified cleanup.`), '',
        `Each archive retains the four controlled parent inputs, direct Node profile, raw child JSON, hashes of the App executable, App assembly, managed PTY assembly and native PTY library, process and pane proofs, source graph, restored kernel terminal mode, and lease log. Source policy \`${clean.sourcePolicy}\` and receipt schema ${clean.receiptSchema} bind the observations. [Manifest](manifest.json) · [All 69 rows](report.json).`, '',
        'Rederive from the retained bytes with Node 24 and Python 3.', '',
        '```sh', 'node scripts/terminfo-modern-results.mjs --check --child-environment', '```', '',
        'To collect fresh controls, build the recorded App checkout and run each launch from the recorded collector checkout on macOS. The launcher acquires and releases the desktop lease for each run.', '',
        '```sh',
        'scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar',
        ...controls.map(control => `NTILDE_TERMINFO_ENVIRONMENT_CONTROL=${control} node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version ${clean.appVersion} --scope modern-child-environment`),
        '```', '',
    ].join('\n');
}

export const modernChildEnvironmentOutputPaths = base => ({
    json: join(base, childEnvironmentPublication, 'report.json'),
    markdown: join(base, childEnvironmentPublication, 'report.md'),
});
