import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { digest, parseJson, pinnedJson, requireModern, same } from './modern-inputs.mjs';
import { modernRoot } from './modern-results.mjs';
import { buildModernOscTitlesPublication, oscTitlesPublication } from './modern-osc-titles-results.mjs';
import { caseId } from './modern-osc7-cwd.mjs';
import { reportFile, verifyOscCwdRun } from './modern-osc7-cwd-owned.mjs';

export const oscCwdPublication = 'docs/qa/terminfo/2026-11-24-modern-osc7-cwd-results';
const manifestSha256 = '802b51046e0f0c68158e03102aa55aa475d79835add048bafe797090a1e04092';
const criterionSha256 = '4e754b43c1c20fe5b5012ac5472461339a777f62851a92f7f2fb031dd5a4407b';

function leaseReleasedAt(directory, context) {
    const { proofs, cleanedUpAt } = parseJson(join(directory, 'ownership-proof.json'));
    const events = readFileSync(join(directory, 'desktop-lease.jsonl'), 'utf8').trimEnd().split('\n').map(line => JSON.parse(line));
    const times = events.map(event => Date.parse(event.at));
    requireModern(same(events.map(event => event.event), ['acquired', 'launch-attempt', 'cleanup-verified', 'released'])
        && events.every(event => event.lease === context.runId && event.pid === proofs[0].app.ppid)
        && times.every((time, index) => Number.isFinite(time) && (index === 0 || time >= times[index - 1]))
        && times[0] <= Date.parse(context.startedAt) && times[2] <= Date.parse(cleanedUpAt) && Date.parse(cleanedUpAt) <= times[3]
        && events[0].entrypoint === 'terminfo-owned.mjs' && events[2].by === 'marker-scan' && events[3].launch === 'attempted',
    'OSC 7 desktop lease did not verify and release for the owned fixture');
    return events.at(-1).at;
}

function verifyAttempts(directory, manifest) {
    const { attempts, archives } = manifest;
    requireModern(Array.isArray(attempts) && same(Object.keys(archives), ['owned-run.zip', ...attempts.map(item => item.archive)]),
        'OSC 7 attempt archive list differs');
    for (const { archive, runId, scriptsRevision, reason } of attempts) {
        requireModern(/^attempt-\d+\.zip$/.test(archive) && /^[a-f\d]{32}$/.test(runId) && /^[a-f\d]{40}$/.test(scriptsRevision) && reason
            && digest(readFileSync(join(directory, archive))) === archives[archive], 'changed OSC 7 attempt archive');
    }
}

function readMeasurement(directory, manifest) {
    const scratch = mkdtempSync(join(tmpdir(), 'ntilde-modern-osc7-cwd-'));
    try {
        extractOwnedArchive(directory, manifest, scratch, join(directory, 'owned-run.zip'));
        const { report, receipt } = verifyOscCwdRun(scratch);
        const context = parseJson(join(scratch, 'context.json'));
        const { measurement, sourcePolicy, receiptSchema } = manifest;
        requireModern(context.runId === measurement.runId && sourcePolicy === context.sourceSnapshotPolicy
            && receiptSchema === receipt.schemaVersion && ['appRevision', 'scriptsRevision'].every(field => /^[a-f\d]{40}$/.test(measurement[field])),
        'wrong OSC 7 manifest identity');
        return { report, measurement: { ...measurement, startedAt: context.startedAt, appVersion: context.appVersion,
            appRevisionSource: 'operator-attested build checkout', appVersionSource: context.appVersionSource,
            executableSha256: context.executableSha256, assemblySha256: context.assemblySha256, nativeHelperSha256: context.nativeHelperSha256,
            nodeVersion: context.nodeVersion, sourceSnapshotManifestSha256: context.sourceSnapshotManifestSha256,
            fixtureRemoved: receipt.fixtureRemoved, leaseReleasedAt: leaseReleasedAt(scratch, context) } };
    } finally {
        rmSync(scratch, { recursive: true, force: true });
    }
}

export function buildModernOscCwdPublication(base = modernRoot) {
    const previous = buildModernOscTitlesPublication(base);
    const directory = join(base, oscCwdPublication);
    const manifest = pinnedJson(join(directory, 'manifest.json'), manifestSha256);
    requireModern(manifest.schemaVersion === 1, 'wrong OSC 7 manifest version');
    verifyAttempts(directory, manifest);
    const { report, measurement } = readMeasurement(directory, manifest);
    const observed = report.result;
    const rows = previous.rows.map(row => {
        if (row.id !== caseId) return row;
        requireModern(row.conclusion === 'inconclusive' && digest(row.requirement) === criterionSha256 && observed.id === caseId,
            'OSC 7 frozen criterion missing or changed');
        return { ...row, conclusion: observed.conclusion, blocker: observed.conclusion === 'inconclusive' ? observed.reason : null,
            criterionPath: 'frozen App criterion using public macOS horizontal tab hover tooltip readback',
            observed, evidence: { file: reportFile, sourceId: caseId, runId: report.runId, recordSha256: digest(JSON.stringify(observed)) } };
    });
    const totals = Object.fromEntries(['pass', 'fail', 'inconclusive'].map(conclusion => [conclusion, rows.filter(row => row.conclusion === conclusion).length]));
    return { ...previous, schemaVersion: 7, totals, ownedCriterionPassPercentage: totals.pass / rows.length * 100,
        evidencePolicy: 'The OSC title assessment is rederived first. One owned direct-local App run grades the frozen OSC 7 App requirement.',
        predecessor: { publication: oscTitlesPublication, totals: previous.totals },
        oscCwdMeasurement: { publication: oscCwdPublication, manifestSha256, archiveSha256: manifest.archives['owned-run.zip'],
            sourcePolicy: manifest.sourcePolicy, receiptSchema: manifest.receiptSchema, ...measurement, pane: report.pane, window: report.window,
            attempts: manifest.attempts }, rows };
}

function table(result) {
    const cell = value => value ? '`' + value + '`' : 'unset';
    return [
        '| Stage | Public tooltip path | Private pane metadata |',
        '| --- | --- | --- |',
        ...Object.entries(result.stages).map(([stage, value]) => `| ${stage} | ${cell(value.publicPath)} | ${cell(value.metadataPath)} |`),
    ];
}

export function readableModernOscCwd(report) {
    const { totals, predecessor, oscCwdMeasurement: m } = report;
    const { observed } = report.rows.find(row => row.id === caseId);
    return [
        '# Ntilde Modern OSC 7 current directory assessment', '',
        `**${totals.pass} pass / ${totals.fail} fail / ${totals.inconclusive} inconclusive across ${report.requirementsCount} frozen requirements.**`, '',
        `The [OSC title assessment](../2026-11-24-modern-osc-titles-results/report.md) had ${predecessor.totals.pass} pass / ${predecessor.totals.fail} fail / ${predecessor.totals.inconclusive} inconclusive. This batch updates one row of the [original Modern assessment](../2026-11-15-modern-results/report.md).`, '',
        `## \`${caseId}\` — ${observed.conclusion}`, '',
        observed.reason, '',
        `The disposable worker launched with its reported directory unset. It emitted \`${observed.incomingUri}\` as fixture setup. The App showed the expected path, and a fresh hover verified the incoming path before the first control. The two targets had the same basename and different parent directories. The checked OSC 9999 carried a different URI-shaped payload and left the second target path in place. Restoration replayed the exact fixture URI and reopened the tooltip for a separate verification.`, '',
        ...table(observed), '',
        'The App’s tab tooltip now adds the active pane’s reported full directory on a second line. Each DA-fenced action was followed by a fresh native hover and a screenshot of the owned tooltip window. Apple Vision transcribed the captured pixels. Read-only pane metadata corroborates the decoded path. The result covers the path shown on the macOS horizontal tab tooltip. The child’s working directory and the headless OSC 633 P control are separate measurements.', '',
        'The [earlier OSC 7 metadata qualification](../2026-11-11-osc7-osc8-qualification.md) established decoded pane metadata. This batch adds the full-path tooltip and uses its native pixels for the frozen App criterion.', '',
        '## Evidence and reproduction', '',
        `[Owned run](owned-run.zip) \`${m.runId}\` started ${m.startedAt} on ${m.platform} with Node ${m.nodeVersion}. The App \`${m.appVersion}\` came from build checkout \`${m.appRevision}\`. The collector checkout was \`${m.scriptsRevision}\`.`, '',
        `The ${m.pane.rows} × ${m.pane.cols} direct-local pane used an isolated profile with persistence off. The archive retains the initial foreground proof. The journal retains exact writes, raw DA replies, native window identities and ordered hover samples, private corroboration, screenshot hashes and raw Vision lines with bounds. The [manifest](manifest.json) pins the archive. Source policy \`${m.sourcePolicy}\` and receipt schema ${m.receiptSchema} bind the run, settings, App and native reader identities, kernel terminal mode restoration and verified fixture cleanup. The archive also includes the desktop lease release. [All 69 rows](report.json).`, '',
        ...m.attempts.flatMap(attempt => [`[Retained attempt ${attempt.runId}](${attempt.archive}). ${attempt.reason}`, '']),
        'Rederive the report with Node 24 and Python 3.', '',
        '```sh', 'node scripts/terminfo-modern-results.mjs --check --osc7-cwd', '```', '',
        `Build the App at \`${m.appRevision}\` and launch the collector from \`${m.scriptsRevision}\` on an awake macOS desktop with screen capture, native pointer events and System Events access. The launcher acquires and releases the repository desktop lease.`, '',
        '```sh',
        'scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar',
        `node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version ${m.appVersion} --scope modern-osc7-cwd`,
        '```', '',
        'On macOS, independently transcribe an extracted native screenshot with the retained Swift source.', '',
        '```sh',
        'swiftc -o ./observe-tab-tooltip sources/modern-osc7-cwd-native.swift',
        './observe-tab-tooltip --ocr tooltip-target-b.png',
        '```', '',
    ].join('\n');
}

export const modernOscCwdOutputPaths = base => ({ json: join(base, oscCwdPublication, 'report.json'), markdown: join(base, oscCwdPublication, 'report.md') });
