import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { verifyBlinkRun, receiptFile } from './modern-blink-owned.mjs';
import { buildModernModesPublication, modesPublication } from './modern-modes-results.mjs';
import { digest, parseJson, pinnedJson, requireModern, same } from './modern-inputs.mjs';
import { modernRoot } from './modern-results.mjs';

export const blinkPublication = 'docs/qa/terminfo/2026-11-24-modern-blink-results';
const manifestSha256 = '0ffd970ad5aa21a5a4d3af635286df808c006c5f47d8180f513a41642554c068';

function verifyLease(directory, runId) {
    const events = readFileSync(join(directory, 'desktop-lease.jsonl'), 'utf8').trimEnd().split('\n').map(line => JSON.parse(line));
    requireModern(same(events.map(event => event.event), ['acquired', 'launch-attempt', 'cleanup-verified', 'released'])
        && events.every((event, index) => event.lease === runId && event.pid === events[0].pid
            && Number.isFinite(Date.parse(event.at)) && (!index || Date.parse(event.at) >= Date.parse(events[index - 1].at)))
        && events[0].entrypoint === 'terminfo-owned.mjs' && events[2].by === 'marker-scan', 'desktop lease did not verify and release');
    return events.at(-1).at;
}

function readBlinkMeasurement(base) {
    const directory = join(base, blinkPublication);
    const manifest = pinnedJson(join(directory, 'manifest.json'), manifestSha256);
    const scratch = mkdtempSync(join(tmpdir(), 'ntilde-modern-blink-'));
    try {
        for (const attempt of manifest.attempts) {
            requireModern(/^attempt-\d+\.zip$/.test(attempt.archive) && attempt.reason
                && digest(readFileSync(join(directory, attempt.archive))) === manifest.archives[attempt.archive], 'changed blink attempt archive');
        }
        extractOwnedArchive(directory, manifest, scratch, join(directory, 'owned-run.zip'));
        const report = verifyBlinkRun(scratch, true);
        const context = parseJson(join(scratch, 'context.json'));
        const receipt = parseJson(join(scratch, receiptFile));
        const m = manifest.measurement;
        requireModern(m.runId === context.runId && m.sourcePolicy === context.sourceSnapshotPolicy
            && m.receiptSchema === receipt.schemaVersion && manifest.schemaVersion === 1, 'wrong blink run or receipt');
        const measurement = { ...m, startedAt: context.startedAt, appVersion: context.appVersion, appVersionSource: context.appVersionSource,
            executableSha256: context.executableSha256, assemblySha256: context.assemblySha256, nodeVersion: context.nodeVersion,
            sourceSnapshotManifestSha256: context.sourceSnapshotManifestSha256, fixtureRemoved: receipt.fixtureRemoved,
            leaseReleasedAt: verifyLease(scratch, m.runId), publication: blinkPublication, manifestSha256,
            archiveSha256: manifest.archives['owned-run.zip'], attempts: manifest.attempts };
        return { report, measurement };
    } finally {
        rmSync(scratch, { recursive: true, force: true });
    }
}

export function buildModernBlinkPublication(base = modernRoot) {
    const previous = buildModernModesPublication(base);
    const { report, measurement } = readBlinkMeasurement(base);
    requireModern(previous.rows.filter(row => row.id === report.id && row.conclusion === 'inconclusive').length === 1,
        'blink criterion missing or graded in predecessor');
    const rows = previous.rows.map(row => row.id === report.id ? {
        ...row, conclusion: report.conclusion, blocker: report.conclusion === 'inconclusive' ? report.reason : null,
        criterionPath: 'live macOS App window with fenced direct-local PTY setup', observed: report,
        evidence: { file: 'blink-observation.json', sourceId: report.id, runId: report.runId, recordSha256: digest(JSON.stringify(report)) },
    } : row);
    const totals = Object.fromEntries(['pass', 'fail', 'inconclusive'].map(verdict => [verdict, rows.filter(row => row.conclusion === verdict).length]));
    return { ...previous, schemaVersion: 4, totals, ownedCriterionPassPercentage: totals.pass / previous.requirementsCount * 100,
        evidencePolicy: 'The Modern modes assessment is rederived from its original inputs. An owned native-window series grades the frozen SGR 5 visual criterion.',
        predecessor: { publication: modesPublication, totals: previous.totals }, blinkMeasurement: measurement, rows };
}

export function readableModernBlink(report) {
    const { totals, predecessor, blinkMeasurement: m } = report;
    const { observed: b } = report.rows.find(row => row.id === 'sgr.blink');
    const previous = predecessor.totals;
    const coords = ({ x, y, width, height }) => `(${x}, ${y}, ${width}, ${height})`;
    const [id, , x, y, width, height] = b.geometry;
    const appearances = new Map();
    const label = hash => {
        if (hash === null) return 'unusable';
        if (!appearances.has(hash)) appearances.set(hash, String.fromCharCode(65 + appearances.size));
        return appearances.get(hash);
    };
    return [
        '# Ntilde Modern assessment for SGR 5 blink', '',
        `**${totals.pass} pass / ${totals.fail} fail / ${totals.inconclusive} inconclusive across ${report.requirementsCount} frozen requirements.**`, '',
        `The [Modern modes assessment](../2026-11-24-modern-modes-results/report.md) had ${previous.pass} pass / ${previous.fail} fail / ${previous.inconclusive} inconclusive. The [original Modern publication](../2026-11-15-modern-results/report.md) retains the frozen criteria and upstream attribution.`, '',
        `**sgr.blink: ${b.conclusion}.** ${b.reason}.`, '',
        `The sidecar wrote SGR 5 followed by X on row 4 and left an unstyled X on row 7. It hid the caret and parked it at row 12, column 1. It made no terminal writes during the measured series. Both crops came from the separate plain-X calibration. ${b.controlMatchesCalibration ? `All ${b.frames.length} control crops matched that calibration.` : 'The control crops diverged from that calibration.'}`, '',
        `The sampler targeted ${b.intervalMs} ms intervals. Each native screenshot falls within its recorded command interval. The no-change rule uses the shortest possible overall span and the longest possible interframe gap.`, '',
        ...(b.spanMs === undefined ? [] : [`The measured series spans at least ${b.spanMs.toFixed(2)} ms with gaps no greater than ${b.maxGapMs.toFixed(2)} ms.`, '']),
        ...(b.returnFrames ? [`The styled crop changed and returned across frames ${b.returnFrames.join(' → ')} (zero-based). The live compositor used the App’s usual clock. The stored blink flag (${b.corroboration.storedBlink}) and DECRQSS reply (\`${JSON.stringify(b.corroboration.decrqss)}\`) corroborate the setup.`, ''] : []),
        `Native window ${id} held at (${x}, ${y}, ${width}, ${height}) in macOS points. The original PNGs are ${b.imageSize.width} × ${b.imageSize.height} pixels. The styled crop is ${coords(b.crops.styled)} and the control is ${coords(b.crops.control)}, in window-local pixels (x, y, width, height).`, '',
        '| Frame | Capture start (monotonic ms) | Capture end (monotonic ms) | Styled appearance |', '| --- | ---: | ---: | --- |',
        ...b.frames.map((frame, index) => `| ${index} | ${frame.startMs.toFixed(2)} | ${frame.endMs.toFixed(2)} | ${label(frame.styled)} |`), '',
        '## Evidence and reproduction', '',
        `[Owned run](owned-run.zip) \`${m.runId}\` started ${m.startedAt} on ${m.platform}, with Node ${m.nodeVersion}. App ${m.appVersion} was built from \`${m.appRevision}\`. The measured scripts were \`${m.scriptsRevision}\`. The [manifest](manifest.json) names source policy \`${m.sourcePolicy}\`, receipt schema ${m.receiptSchema}, and the archive digests.`, '',
        'The archive retains the original PNGs, pane snapshots, exact fixture bytes and DA fences, native window bounds and timestamps, App executable and assembly hashes, captured source graph, PTY restoration, cleanup receipt, and desktop lease release events. The [JSON report](report.json) contains all 69 rows and the derived crop digests.', '',
        ...m.attempts.flatMap(attempt => [`[Retained attempt ${attempt.runId}](${attempt.archive}). ${attempt.reason}`, '']),
        'Check the archived evidence and rederive the reports offline with Node 24 and Python 3.', '',
        '```sh', 'node scripts/terminfo-modern-results.mjs --check --blink', '```', '',
        'For a fresh capture, build the recorded App revision and run the collector from the measured scripts revision on an awake macOS desktop with Screen Recording permission. The launcher acquires and releases the desktop lease.', '',
        '```sh',
        'scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar',
        `node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version ${m.appVersion} --scope modern-blink`,
        '```', '',
    ].join('\n');
}

export const modernBlinkOutputPaths = base => ({ json: join(base, blinkPublication, 'report.json'), markdown: join(base, blinkPublication, 'report.md') });
