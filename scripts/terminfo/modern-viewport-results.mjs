import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { blinkPublication, buildModernBlinkPublication } from './modern-blink-results.mjs';
import { digest, parseJson, pinnedJson, requireModern, same } from './modern-inputs.mjs';
import { modernRoot } from './modern-results.mjs';
import { requirementId } from './modern-viewport.mjs';
import { verifyViewportRun } from './modern-viewport-owned.mjs';

export const viewportPublication = 'docs/qa/terminfo/2026-11-24-modern-viewport-results';
const manifestSha256 = '5c2ba2cb063274bc092da427b509b9cac4a005ded4e55f5d8a155514064e3261';

function verifyLease(directory, runId) {
    const events = readFileSync(join(directory, 'desktop-lease.jsonl'), 'utf8').trimEnd().split('\n').map(line => JSON.parse(line));
    requireModern(same(events.map(event => event.event), ['acquired', 'launch-attempt', 'cleanup-verified', 'released'])
        && events.every((event, index) => event.lease === runId && event.pid === events[0].pid
            && Number.isFinite(Date.parse(event.at)) && (!index || Date.parse(event.at) >= Date.parse(events[index - 1].at)))
        && events[0].entrypoint === 'terminfo-owned.mjs' && events[2].by === 'marker-scan', 'viewport lease did not verify and release');
    return events.at(-1).at;
}

function readMeasurement(directory, manifest, archive, measurement) {
    const scratch = mkdtempSync(join(tmpdir(), 'ntilde-modern-viewport-'));
    try {
        const archiveSha256 = manifest.archives[archive];
        extractOwnedArchive(directory, { archives: { 'owned-run.zip': archiveSha256 } }, scratch, join(directory, archive));
        const { report, receipt } = verifyViewportRun(scratch);
        const context = parseJson(join(scratch, 'context.json'));
        requireModern(measurement.runId === context.runId && manifest.sourcePolicy === context.sourceSnapshotPolicy
            && manifest.receiptSchema === receipt.schemaVersion && /^[a-f\d]{40}$/.test(measurement.appRevision)
            && /^[a-f\d]{40}$/.test(measurement.scriptsRevision), 'wrong viewport run or receipt');
        return { report, measurement: { ...measurement, archive, archiveSha256, startedAt: context.startedAt, appVersion: context.appVersion,
            appVersionSource: context.appVersionSource, executableSha256: context.executableSha256, assemblySha256: context.assemblySha256,
            nodeVersion: context.nodeVersion, sourceSnapshotManifestSha256: context.sourceSnapshotManifestSha256,
            fixtureRemoved: receipt.fixtureRemoved, leaseReleasedAt: verifyLease(scratch, context.runId) } };
    } finally {
        rmSync(scratch, { recursive: true, force: true });
    }
}

export function buildModernViewportPublication(base = modernRoot) {
    const previous = buildModernBlinkPublication(base);
    const directory = join(base, viewportPublication);
    const manifest = pinnedJson(join(directory, 'manifest.json'), manifestSha256);
    requireModern(manifest.schemaVersion === 1, 'unsupported viewport manifest');
    for (const [name, sha] of Object.entries(manifest.archives)) {
        requireModern(/^(?:baseline-run|owned-run|attempt-\d+)\.zip$/.test(name)
            && digest(readFileSync(join(directory, name))) === sha, 'changed viewport archive');
    }
    const baseline = readMeasurement(directory, manifest, 'baseline-run.zip', manifest.baseline);
    const measured = readMeasurement(directory, manifest, 'owned-run.zip', manifest.measurement);
    const { report } = measured;
    requireModern(baseline.report.runId !== report.runId && baseline.measurement.assemblySha256 !== measured.measurement.assemblySha256,
        'baseline and fixed App are not independent');
    const target = previous.rows.find(row => row.id === requirementId);
    requireModern(target?.conclusion === 'inconclusive'
        && digest(target.requirement) === 'b39d030c7fb9c7fe57d5617fb1f857d577e5bb64af3616c75a1b4280e3256103', 'viewport criterion missing or changed');
    const rows = previous.rows.map(row => row.id === requirementId ? {
        ...row, conclusion: report.conclusion, blocker: report.conclusion === 'inconclusive' ? report.phases.filter(phase => phase.conclusion === 'inconclusive').map(phase => phase.reason).join('; ') : null,
        criterionPath: 'frozen manual macOS App viewport criterion on a direct-local pane',
        observed: report, evidence: { file: 'viewport-observation.json', sourceId: requirementId, runId: report.runId, recordSha256: digest(JSON.stringify(report)) },
    } : row);
    const totals = Object.fromEntries(['pass', 'fail', 'inconclusive'].map(verdict => [verdict, rows.filter(row => row.conclusion === verdict).length]));
    return { ...previous, schemaVersion: 5, totals, ownedCriterionPassPercentage: totals.pass / previous.requirementsCount * 100,
        evidencePolicy: 'The blink assessment is rederived from its retained evidence. Two independent owned phases grade the same frozen manual macOS App viewport requirement.',
        predecessor: { publication: blinkPublication, totals: previous.totals },
        viewportMeasurement: { publication: viewportPublication, manifestSha256, sourcePolicy: manifest.sourcePolicy,
            receiptSchema: manifest.receiptSchema, ...measured.measurement, attempts: manifest.attempts ?? [],
            baseline: { ...baseline.measurement, observed: baseline.report } }, rows };
}

function view({ visibleRows, offset, extent }) {
    const parts = [visibleRows[0], visibleRows.findLast(Boolean)].map(label => label?.split('-').at(-1) ?? 'blank');
    return `${parts.join('–')} (${offset}/${extent})`;
}

function table(report) {
    return [
        '| Phase | Verdict | Live bottom | Old anchor | After output | Restored bottom |',
        '| --- | --- | --- | --- | --- | --- |',
        ...report.phases.map(phase => `| ${phase.phase} | ${phase.conclusion} | ${view(phase.bottom)} | ${view(phase.anchor)} | ${view(phase.output)} | ${view(phase.restored)} |`),
    ];
}

export function readableModernViewport(report) {
    const { totals, predecessor, viewportMeasurement: m } = report;
    const { observed } = report.rows.find(row => row.id === requirementId);
    const { viewRows, viewCols } = observed.geometry.cellGeometry;
    const { windowWidthDip, windowHeightDip } = observed.geometry.fontGeometry;
    return [
        '# Ntilde Modern assessment for holding the viewport on output', '',
        `**${totals.pass} pass / ${totals.fail} fail / ${totals.inconclusive} inconclusive across ${report.requirementsCount} frozen requirements.**`, '',
        `The [blink assessment](../2026-11-24-modern-blink-results/report.md) had ${predecessor.totals.pass} pass / ${predecessor.totals.fail} fail / ${predecessor.totals.inconclusive} inconclusive. The [original Modern publication](../2026-11-15-modern-results/report.md) retains the frozen catalogue and criteria.`, '',
        `**\`${requirementId}\` earns a ${observed.conclusion} in the macOS App assessment.** The manual criterion requires the same old rows at the same visible positions after both plain and synchronized output. The separate Linux XTEST wheel and pixel-capture path remains unmeasured by this run.`, '',
        '## Fixed App', '',
        ...table(observed), '',
        'The table shows the numbered row IDs and the distance from the live bottom over the maximum offset. The restored view ends with a blank row below its marker.', '',
        ...observed.phases.map(phase => `- The ${phase.phase} marker at the restored bottom was \`${phase.marker}\`.`), '',
        `Each phase started from a fresh seed of ${3 * viewRows} numbered lines. The pane held a ${viewRows}-row by ${viewCols}-column grid in a ${windowWidthDip} × ${windowHeightDip} DIP window. Both phases retained every seeded row with zero evictions. Smooth scrolling was disabled for settled wheel steps. The default wheel step was three rows per notch and the default MaxHistory was 10,000. Session persistence was off.`, '',
        'The collector used the OSC 720 generated App wheel route. Paired readbacks combined the App viewport offset, the retained history and the live rows to identify every visible row. The child wrote the plain marker with CRLF. For the synchronized phase it wrote the marker and CRLF in one write between `ESC [ ? 2026 h` and `ESC [ ? 2026 l`. The collector sent no keyboard input or PTY queries between the anchor and post-output snapshots.', '',
        '## Baseline and fix', '',
        ...table(m.baseline.observed), '',
        'The [baseline run](baseline-run.zip) delivered both markers and drifted one row in each phase. Both baseline phases are inconclusive under the frozen criterion. The App had kept a constant distance from the growing bottom. The fix tracks the history tail and advances the offset with appended rows so the visible anchor stays in place. App regressions cover plain and synchronized output, a smooth wheel gesture, a return to the live bottom during continuous output, eviction of an older page and a cleared history generation.', '',
        '## Evidence and reproduction', '',
        `[Fixed App run](owned-run.zip) \`${m.runId}\` started ${m.startedAt} on ${m.platform} with Node ${m.nodeVersion}. App ${m.appVersion} came from \`${m.appRevision}\`. The measured scripts were \`${m.scriptsRevision}\`. Baseline App \`${m.baseline.appRevision}\` and scripts \`${m.baseline.scriptsRevision}\` produced run \`${m.baseline.runId}\`.`, '',
        `The [manifest](manifest.json) pins the archives. Source policy \`${m.sourcePolicy}\` and receipt schema ${m.receiptSchema} bind the raw snapshots, wheel actions, exact PTY bytes, settings and geometry to the owned App and child. The runs verified restoration to the blank main pane with no history, the original PTY mode, fixture shutdown and desktop lease release. The [JSON report](report.json) retains the full visible row vectors and all 69 Modern rows.`, '',
        ...m.attempts.flatMap(attempt => [`[Retained attempt ${attempt.runId}](${attempt.archive}). ${attempt.reason}`, '']),
        'Rederive the reports from the retained archives with Node 24 and Python 3.', '',
        '```sh', 'node scripts/terminfo-modern-results.mjs --check --viewport', '```', '',
        'For a fresh measurement, build the recorded App revision and run the collector from the measured scripts revision on an awake macOS desktop. The launcher acquires and releases the repository desktop lease.', '',
        '```sh',
        'scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar',
        `node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version ${m.appVersion} --scope modern-viewport`,
        '```', '',
    ].join('\n');
}

export const modernViewportOutputPaths = base => ({ json: join(base, viewportPublication, 'report.json'), markdown: join(base, viewportPublication, 'report.md') });
