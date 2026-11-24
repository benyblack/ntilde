import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { digest, parseJson, pinnedJson, requireModern, same } from './modern-inputs.mjs';
import { modernRoot } from './modern-results.mjs';
import { buildModernViewportPublication, viewportPublication } from './modern-viewport-results.mjs';
import { osc0, osc2 } from './modern-osc-titles.mjs';
import { reportFile, verifyOscTitlesRun } from './modern-osc-titles-owned.mjs';

export const oscTitlesPublication = 'docs/qa/terminfo/2026-11-24-modern-osc-titles-results';
const manifestSha256 = '4c5462d37677b3c3573a5c2981380fe665dfd8d72d4b7d338a295e3391b4fa03';
const criteria = {
    [osc2]: 'e25978542e8966ea9c3645b0a3b2f7c6693e2fd767817d655cf35af8d26096c7',
    [osc0]: '1b28b0fac150294d922f1107919fe644ca0e62c094fcc7b419784f2a532b6bb2',
};

function leaseReleasedAt(directory, context) {
    const { proofs, cleanedUpAt } = parseJson(join(directory, 'ownership-proof.json'));
    const events = readFileSync(join(directory, 'desktop-lease.jsonl'), 'utf8').trimEnd().split('\n').map(line => JSON.parse(line));
    requireModern(same(events.map(event => event.event), ['acquired', 'launch-attempt', 'cleanup-verified', 'released']),
        'missing OSC titles desktop lease lifecycle');
    const times = events.map(event => Date.parse(event.at));
    requireModern(events.every(event => event.lease === context.runId && event.pid === proofs[0].app.ppid)
        && times.every((time, index) => Number.isFinite(time) && (index === 0 || time >= times[index - 1]))
        && times[0] <= Date.parse(context.startedAt) && times[2] <= Date.parse(cleanedUpAt) && Date.parse(cleanedUpAt) <= times[3]
        && events[0].entrypoint === 'terminfo-owned.mjs' && events[2].by === 'marker-scan' && events[3].launch === 'attempted',
    'OSC titles lease did not verify and release for this fixture');
    return events.at(-1).at;
}

function verifyAttempts(directory, manifest) {
    const attempts = manifest.attempts ?? [];
    requireModern(same(Object.keys(manifest.archives), ['owned-run.zip', ...attempts.map(attempt => attempt.archive)]),
        'extra OSC title attempt archives');
    for (const { archive, runId, reason } of attempts) {
        requireModern(/^attempt-\d+\.zip$/.test(archive) && /^[a-f\d]{32}$/.test(runId) && reason
            && digest(readFileSync(join(directory, archive))) === manifest.archives[archive], 'changed OSC title attempt archive');
    }
    return attempts;
}

function readMeasurement(directory, manifest) {
    const scratch = mkdtempSync(join(tmpdir(), 'ntilde-modern-osc-titles-'));
    try {
        extractOwnedArchive(directory, manifest, scratch, join(directory, 'owned-run.zip'));
        const { report, receipt } = verifyOscTitlesRun(scratch);
        const context = parseJson(join(scratch, 'context.json'));
        const { measurement, sourcePolicy, receiptSchema } = manifest;
        requireModern(context.runId === measurement.runId && sourcePolicy === context.sourceSnapshotPolicy
            && receiptSchema === receipt.schemaVersion && ['appRevision', 'scriptsRevision'].every(field => /^[a-f\d]{40}$/.test(measurement[field])),
        'wrong OSC title manifest identity');
        return { report, measurement: { ...measurement, startedAt: context.startedAt, appVersion: context.appVersion,
            appRevisionSource: 'operator-attested build checkout', appVersionSource: context.appVersionSource,
            executableSha256: context.executableSha256, assemblySha256: context.assemblySha256,
            nodeVersion: context.nodeVersion, sourceSnapshotManifestSha256: context.sourceSnapshotManifestSha256,
            fixtureRemoved: receipt.fixtureRemoved, leaseReleasedAt: leaseReleasedAt(scratch, context) } };
    } finally {
        rmSync(scratch, { recursive: true, force: true });
    }
}

export function buildModernOscTitlesPublication(base = modernRoot) {
    const previous = buildModernViewportPublication(base);
    const directory = join(base, oscTitlesPublication);
    const manifest = pinnedJson(join(directory, 'manifest.json'), manifestSha256);
    requireModern(manifest.schemaVersion === 1, 'wrong OSC titles manifest version');
    const attempts = verifyAttempts(directory, manifest);
    const { report, measurement } = readMeasurement(directory, manifest);
    const rows = previous.rows.map(row => {
        if (!Object.hasOwn(criteria, row.id)) return row;
        requireModern(row.conclusion === 'inconclusive' && digest(row.requirement) === criteria[row.id], 'OSC title criterion missing or changed');
        const observed = report.cases.find(item => item.id === row.id);
        requireModern(observed, 'OSC title measurement missing');
        return { ...row, conclusion: observed.conclusion, blocker: observed.conclusion === 'inconclusive' ? observed.reason : null,
            criterionPath: 'frozen App criterion using fenced CSI 21 t title and CSI 20 t icon label readback',
            observed, evidence: { file: reportFile, sourceId: row.id, runId: report.runId, recordSha256: digest(JSON.stringify(observed)) } };
    });
    const totals = Object.fromEntries(['pass', 'fail', 'inconclusive'].map(conclusion => [conclusion, rows.filter(row => row.conclusion === conclusion).length]));
    return { ...previous, schemaVersion: 6, totals, ownedCriterionPassPercentage: totals.pass / rows.length * 100,
        evidencePolicy: 'The viewport assessment is rederived first. One owned direct-local App run grades each frozen OSC title requirement.',
        predecessor: { publication: viewportPublication, totals: previous.totals },
        oscTitlesMeasurement: { publication: oscTitlesPublication, manifestSha256, archiveSha256: manifest.archives['owned-run.zip'],
            sourcePolicy: manifest.sourcePolicy, receiptSchema: manifest.receiptSchema, ...measurement, pane: report.pane, attempts }, rows };
}

const codeLabel = value => `\`${value}\``;

function show(reading) {
    if (reading.value === null) return reading.stable ? 'unobserved' : 'unstable';
    if (reading.value === '') return 'empty';
    return codeLabel(reading.value);
}

function observationTable(result) {
    return [
        '| Stage | Requested label | Title reply | Icon label reply |',
        '| --- | --- | --- | --- |',
        ...Object.entries(result.stages).map(([stage, { title, icon }]) => {
            const requested = result.requested[stage];
            return `| ${stage} | ${requested ? codeLabel(requested) : '—'} | ${show(title)} | ${show(icon)} |`;
        }),
    ];
}

export function readableModernOscTitles(report) {
    const { totals, predecessor, oscTitlesMeasurement: m } = report;
    const cases = report.rows.filter(row => Object.hasOwn(criteria, row.id));
    return [
        '# Ntilde Modern assessment for OSC 2 and OSC 0', '',
        `**${totals.pass} pass / ${totals.fail} fail / ${totals.inconclusive} inconclusive across ${report.requirementsCount} frozen requirements.**`, '',
        `The [viewport assessment](../2026-11-24-modern-viewport-results/report.md) had ${predecessor.totals.pass} pass / ${predecessor.totals.fail} fail / ${predecessor.totals.inconclusive} inconclusive. The [original Modern publication](../2026-11-15-modern-results/report.md) retains the criteria. This batch changes only the two rows below.`, '',
        ...cases.flatMap(row => [
            `## \`${row.id}\` — ${row.conclusion}`, '',
            row.observed.reason, '',
            ...observationTable(row.observed), '',
            `Incoming title restoration was ${row.observed.restoration.title} and incoming icon label restoration was ${row.observed.restoration.icon}.`, '',
        ]),
        'Each read uses public CSI 21 t and CSI 20 t in a DA-fenced exchange, then repeats the queries in the opposite order. The CSI 21 t reader reports the requested window title as OSC l and the CSI 20 t reader reports the requested icon label as OSC L. Empty denotes a complete reply with an empty payload. The icon result measures a label through the protocol. Native icon presentation is outside the measurement.', '',
        'The child read both incoming labels before any setter, saved them with CSI 22 ; 0 t and restored them with CSI 23 ; 0 t after each case. The unhandled OSC 9999 was checked against the App dispatch source. Each case used fresh distinguishable payloads. The OSC 0 icon reader had a separate OSC 1 control and retained its value through the OSC 2 title control.', '',
        '## Evidence and reproduction', '',
        `[Owned run](owned-run.zip) \`${m.runId}\` started ${m.startedAt} on ${m.platform} with Node ${m.nodeVersion}. The operator recorded App ${m.appVersion} from build checkout \`${m.appRevision}\`. The measured scripts were \`${m.scriptsRevision}\`.`, '',
        `The ${m.pane.rows} × ${m.pane.cols} direct-local fixture enabled AllowTitleReport in its disposable settings. The [manifest](manifest.json) pins the archive. The journal retains exact writes, ordered raw replies and DA barriers for both reads of each label. Source policy \`${m.sourcePolicy}\` and receipt schema ${m.receiptSchema} bind the measured executable and assembly hashes, collector source graph, settings, pane observations and verified fixture cleanup. The archive includes label and kernel terminal restoration and desktop lease release events. The [JSON report](report.json) retains all 69 rows.`, '',
        'The App behavior already existed. The [OSC title qualification](../2026-11-12-osc-title-qualification.md) and [XTWINOPS query qualification](../2026-11-15-device-queries-qualification.md) retain their own evidence and outcomes. These observations apply to the frozen macOS App criteria. Stock and common-harness results retain their original scope.', '',
        ...m.attempts.flatMap(attempt => [`[Retained attempt ${attempt.runId}](${attempt.archive}). ${attempt.reason}`, '']),
        'Rederive the reports with Node 24 and Python 3.', '',
        '```sh', 'node scripts/terminfo-modern-results.mjs --check --osc-titles', '```', '',
        `For a fresh measurement, run the build from the App checkout at \`${m.appRevision}\`. Then switch to the separate collector checkout at \`${m.scriptsRevision}\` and run the launcher on an awake macOS desktop. The launcher holds the repository desktop lease through verified cleanup.`, '',
        '```sh',
        'scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar',
        `node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version ${m.appVersion} --scope modern-osc-titles`,
        '```', '',
    ].join('\n');
}

export const modernOscTitlesOutputPaths = base => ({ json: join(base, oscTitlesPublication, 'report.json'), markdown: join(base, oscTitlesPublication, 'report.md') });
