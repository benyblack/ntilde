import { existsSync, mkdtempSync, readdirSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, posix } from 'node:path';
import { isDeepStrictEqual as same } from 'node:util';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { assertLinuxOwnedChild, linuxControllingDevice } from './linux-process-core.mjs';
import { buildModernChildEnvironmentPublication, childEnvironmentPublication } from './modern-child-environment-results.mjs';
import { digest, parseJson, pinnedJson, requireModern } from './modern-inputs.mjs';
import { modernRoot } from './modern-results.mjs';
import { ids, replayMouse, x11SocketPath } from './modern-x11-mouse.mjs';

export const x11MousePublication = 'docs/qa/terminfo/2026-11-24-modern-x11-mouse-results';
const manifestSha256 = 'f6a7a4370b13aa4b108ef73d25f1aa7f4884452d615f02fd982aa676251f08a0';
const criterionSha256 = {
    [ids[0]]: 'd56308b4d02629905fc6f8be951b43fd27009efd1651d16040245c4e05f2bbcd',
    [ids[1]]: '1532d2ee68aefc5f657e1c306c3d72ad69e13cdc3fbbce54966f81ac7e362ab7',
    [ids[2]]: '6c2355af63988dd895196e814b3c3d26118339bf00e59337d97b88b4c9229f8b',
};
const sourceFiles = ['desktop-lock.mjs', 'linux-process-core.mjs', 'linux-process-provider.mjs', 'modern-x11-mouse.mjs',
    'modern-x11-mouse-collect.mjs', 'modern-x11-mouse-native.py', 'modern-x11-mouse-worker.py'];
const alphabetical = (a, b) => a.localeCompare(b, 'en');
const isDigest = value => typeof value === 'string' && /^[a-f\d]{64}$/.test(value);
const before = (a, b) => typeof a === 'string' && /^\d+$/.test(a) && typeof b === 'string' && /^\d+$/.test(b) && BigInt(a) < BigInt(b);

function verifySources(directory, policy, names) {
    const raw = readFileSync(join(directory, 'source-manifest.json')), manifest = JSON.parse(raw);
    const sources = join(directory, 'sources');
    requireModern(manifest.policy === policy && same(Object.keys(manifest.files), names)
        && same(readdirSync(sources).toSorted(alphabetical), names.toSorted(alphabetical))
        && manifest.launcherSha256 === digest(readFileSync(join(directory, 'launcher.mjs'))), 'wrong X11 collector source manifest');
    for (const name of names) {
        requireModern(digest(readFileSync(join(sources, name))) === manifest.files[name], 'changed X11 collector source ' + name);
    }
    return { policy: manifest.policy, sha256: digest(raw) };
}

function verifyProfile(run, profilePrefix) {
    const { settings, runId, identity, lineage: { child }, python } = run;
    const profile = settings.Profiles?.[0];
    requireModern(settings.Profiles?.length === 1 && settings.DefaultProfileId === profile.Id
        && profile.Name === profilePrefix + runId && identity.pane.profileName === profile.Name
        && profile.Type === 0 && profile.Command === python && posix.isAbsolute(profile.StartingDirectory)
        && /^ntilde-x11-[A-Za-z0-9]+$/.test(posix.basename(profile.StartingDirectory))
        && settings.SessionPersistence === 'Off' && settings.AlternateScroll === false
        && settings.CommandAssistShellIntegrationEnabled === false && settings.AgentAccessObserveEnabled === true
        && settings.AgentAccessActEnabled === false && settings.AllowOsc52ClipboardWrite === false, 'wrong X11 disposable profile');
    const scratch = profile.StartingDirectory;
    const argv = [python, '-I', '-S', '-B', posix.join(scratch, 'sources', 'modern-x11-mouse-worker.py'),
        posix.join(scratch, 'worker.json'), '--ntilde-terminfo-worker=' + runId];
    requireModern(same(child.argv, argv) && profile.Arguments === argv.slice(1).map(arg => '"' + arg + '"').join(' '), 'wrong direct X11 worker argv');
    return { argv, scratch };
}

function verifyIdentities(run, profilePrefix, markerPrefix) {
    const { appFiles, pythonSha256, runId, lineage: { parent: app, child }, identity, display, coordinatorPid, uid } = run;
    const { server } = display;
    const { argv, scratch } = verifyProfile(run, profilePrefix);
    assertLinuxOwnedChild(app, child, { parentPid: app.pid, childPid: child.pid, runId, uid, executable: run.python, argv });
    requireModern([app, child, server].every(item => item.platform === 'linux' && item.uids.length === 4 && item.uids.every(value => value === uid))
        && new Set([coordinatorPid, server.pid, app.pid, child.pid]).size === 4 && app.ppid === coordinatorPid
        && identity.appPid === app.pid && same(app.argv, [app.executable, '--ntilde-terminfo-owner=' + runId, '--ntilde-terminfo-private-clipboard=' + runId])
        && posix.basename(app.executable) === 'Ntilde' && isDigest(pythonSha256)
        && ['Ntilde', 'Ntilde.dll', 'librusty_pty.so', 'libcoreclr.so'].every(name => isDigest(appFiles[name]))
        && Object.values(appFiles).every(isDigest), 'missing isolated App identity or publish bytes');
    const [stdin, stdout] = child.pty.descriptors;
    requireModern(child.pty.selectedFd === 1 && stdin.fd === 0 && stdout.fd === 1 && /^\/dev\/pts\/\d+$/.test(stdin.path)
        && stdin.path === stdout.path && same(stdin.device, stdout.device) && stdin.device.uid === String(uid)
        && linuxControllingDevice(BigInt(stdin.device.rdev)) === child.controllingDevice, 'wrong direct PTY identity');
    requireModern(server.pid === identity.serverPid && server.ppid === coordinatorPid && server.executable === '/usr/bin/Xvfb'
        && /^:\d{1,4}$/.test(display.name) && display.socket === x11SocketPath(display.name)
        && /^[1-9]\d*$/.test(display.socketInode) && isDigest(display.executableSha256)
        && same(server.argv, [server.executable, '-displayfd', '3', '-screen', '0', '1280x800x24', '-dpi', '96',
            '-nolisten', 'tcp', '-noreset', '-auth', posix.join(scratch, 'Xauthority')])
        && identity.xtestVersion[0] === 2 && identity.marker === markerPrefix + runId
        && identity.window.width >= 400 && identity.window.height >= 300
        && Number.isSafeInteger(identity.window.id) && identity.window.id > 0, 'missing independently owned XTEST display or window');
}

function verifyCleanup(directory, run) {
    const events = readFileSync(join(directory, 'desktop-lease.jsonl'), 'utf8').trimEnd().split('\n').map(line => JSON.parse(line));
    const instants = events.map(event => Date.parse(event.at));
    const fixture = parseJson(join(directory, 'fixture-cleanup.json'));
    const removedAt = Date.parse(fixture.removedAt);
    requireModern(same(events.map(event => event.event), ['acquired', 'launch-attempt', 'cleanup-verified', 'released'])
        && events.every(event => event.pid === run.coordinatorPid && event.lease === run.runId)
        && instants.every((time, index) => Number.isFinite(time) && (index === 0 || time >= instants[index - 1]))
        && events[0].entrypoint === 'terminfo-modern-x11-owned.mjs' && events[2].by === 'marker-scan' && events[3].launch === 'attempted'
        && fixture.runId === run.runId && fixture.scratch === run.settings.Profiles[0].StartingDirectory
        && instants[2] <= removedAt && removedAt <= instants[3],
    'unproved X11 desktop lease cleanup or release');
    requireModern(run.cleanup?.socketGone === true && before(run.terminal.at, run.workerGoneAt) && before(run.workerGoneAt, run.cleanup.at)
        && [run.cleanup.app, run.cleanup.display].every(exit => exit && (Number.isSafeInteger(exit.code) || ['SIGTERM', 'SIGKILL'].includes(exit.signal))),
    'unverified X11 App, worker or display cleanup');
    return { startedAt: events[1].at, fixtureRemovedAt: fixture.removedAt, leaseReleasedAt: events[3].at };
}

export function verifyX11MouseRun(directory) {
    return verifyX11Run(directory, { policy: 'owned-modern-x11-mouse-v1', profilePrefix: 'X11 Mouse ',
        markerPrefix: 'x11-mouse-', replay: replayMouse });
}

export function verifyX11Run(directory, { policy, profilePrefix, markerPrefix, replay, extraSources = [] }) {
    requireModern(!existsSync(join(directory, 'failure.json')), 'X11 collection failed');
    const frame = readFileSync(join(directory, 'observation.json'));
    const run = JSON.parse(frame);
    requireModern(run.schemaVersion === 1 && /^[a-f\d]{32}$/.test(run.runId) && !run.failure && !run.cleanupError,
        'invalid completed X11 observation');
    const source = verifySources(directory, policy, [...sourceFiles, ...extraSources]);
    verifyIdentities(run, profilePrefix, markerPrefix);
    const results = replay(run);
    requireModern(same(results, run.results), 'X11 recorded grading does not rederive');
    const cleanup = verifyCleanup(directory, run);
    return { run, results, measurement: { ...cleanup, sha256: digest(frame), source } };
}

function verifyAttempts(directory, manifest) {
    const { attempts, archives } = manifest;
    requireModern(Array.isArray(attempts) && same(Object.keys(archives), ['owned-run.zip', ...attempts.map(item => item.archive)]),
        'wrong X11 attempt archive list');
    for (const { archive, runId, scriptsRevision, reason } of attempts) {
        requireModern(/^attempt-\d+\.zip$/.test(archive) && /^[a-f\d]{32}$/.test(runId) && /^[a-f\d]{40}$/.test(scriptsRevision)
            && reason && digest(readFileSync(join(directory, archive))) === archives[archive], 'changed retained X11 attempt');
    }
}

export function buildModernX11MousePublication(base = modernRoot) {
    const previous = buildModernChildEnvironmentPublication(base);
    const directory = join(base, x11MousePublication);
    const manifest = pinnedJson(join(directory, 'manifest.json'), manifestSha256);
    verifyAttempts(directory, manifest);
    const scratch = mkdtempSync(join(tmpdir(), 'ntilde-modern-x11-results-'));
    try {
        extractOwnedArchive(directory, manifest, scratch, join(directory, 'owned-run.zip'));
        const { run, results, measurement } = verifyX11MouseRun(scratch);
        requireModern(manifest.schemaVersion === 1 && run.runId === manifest.runId
            && [manifest.appRevision, manifest.scriptsRevision].every(revision => /^[a-f\d]{40}$/.test(revision)), 'wrong X11 run manifest');
        const byId = new Map(results.map(result => [result.id, result]));
        const rows = previous.rows.map(row => {
            const result = byId.get(row.id);
            if (!result) return row;
            requireModern(row.conclusion === 'inconclusive' && digest(row.requirement) === criterionSha256[row.id], 'changed frozen X11 App criterion');
            byId.delete(row.id);
            return { ...row, conclusion: result.conclusion, blocker: result.conclusion === 'inconclusive' ? result.reason : null,
                criterionPath: 'frozen App path with XTEST input on a disposable Linux X11 pane', observed: result,
                evidence: { file: 'observation.json', runId: run.runId, sha256: measurement.sha256 } };
        });
        requireModern(byId.size === 0 && rows.length === 69, 'missing frozen X11 App rows');
        const totals = Object.fromEntries(['pass', 'fail', 'inconclusive'].map(outcome => [outcome, rows.filter(row => row.conclusion === outcome).length]));
        return { ...previous, schemaVersion: 9, totals, ownedCriterionPassPercentage: totals.pass / rows.length * 100,
            predecessor: { publication: childEnvironmentPublication, totals: previous.totals },
            evidencePolicy: 'The child environment assessment is rederived first. A disposable Linux X11 run grades three frozen App criteria.',
            x11MouseMeasurement: { publication: x11MousePublication, manifestSha256, ...manifest, ...measurement, appVersion: run.appVersion,
                appBuildSource: 'operator-attested source archive and publish command', appExecutableSha256: run.appFiles.Ntilde,
                assemblySha256: run.appFiles['Ntilde.dll'], ptyLibrarySha256: run.appFiles['librusty_pty.so'],
                nodeVersion: run.nodeVersion, pane: run.identity.pane, window: run.identity.window, display: run.display.name,
                xtestVersion: run.identity.xtestVersion, appPid: run.lineage.parent.pid, workerPid: run.lineage.child.pid, tty: run.lineage.child.pty.descriptors[0].path }, rows };
    } finally {
        rmSync(scratch, { recursive: true, force: true });
    }
}

const displayHex = values => values.filter(Boolean).map(value => '`' + value.match(/../g).join(' ') + '`').join(' → ') || 'Silence';

export function readableModernX11Mouse(report) {
    const { totals, predecessor, x11MouseMeasurement: m } = report;
    const rows = ids.map(id => report.rows.find(row => row.id === id));
    return [
        '# Ntilde Modern X11 mouse assessment', '',
        `**${totals.pass} pass / ${totals.fail} fail / ${totals.inconclusive} inconclusive across ${report.requirementsCount} frozen requirements.**`, '',
        `The [child environment predecessor](../2026-11-24-modern-child-environment-results/report.md) recorded ${predecessor.totals.pass} pass, ${predecessor.totals.fail} fail and ${predecessor.totals.inconclusive} inconclusive. This batch applies the [frozen App criteria and controls](../modern-x11-mouse.md).`, '',
        '| Case | Fresh 1000 + 1006 click bytes | Target bytes | Modern verdict |',
        '| --- | --- | --- | --- |',
        ...rows.map(({ id, observed }) => `| \`${id}\` | ${displayHex(observed.controlHex)} | ${displayHex(observed.targetHex)} | ${observed.conclusion} |`), '',
        ...rows.map(({ id, observed }) => `- \`${id}\`. ${observed.reason}`), '',
        'The report covers App delivery through the Linux X11 backend. Each target used separate raw observation windows ending with a unique XTEST keyboard delimiter. DECRPM exchanges qualified the exact initial, control and target mode selections. The final exchange and private pane read verified the initial effective modes and primary-screen marker. The worker restored and read back its original kernel terminal mode.', '',
        '## Evidence and reproduction', '',
        `The [owned run](owned-run.zip) \`${m.runId}\` began at ${m.startedAt} on ${m.platform}. App \`${m.appVersion}\` was built from \`${m.appRevision}\` with .NET SDK ${m.dotnetSdk}. Collector \`${m.scriptsRevision}\` ran under Node ${m.nodeVersion}.`, '',
        `Private display \`${m.display}\` provided XTEST ${m.xtestVersion.join('.')}. The App PID was ${m.appPid}, the child PID ${m.workerPid} and the direct PTY \`${m.tty}\`. The owned pane measured ${m.pane.rows} rows by ${m.pane.cols} columns in X window ${m.window.id}. After the worker exited, the collector reaped its App and Xvfb processes and verified the display socket was gone. The isolated scratch tree was removed before the lease released at ${m.leaseReleasedAt}.`, '',
        `The archive retains the exact mode writes and replies, XTEST actions and coordinates, timestamped raw PTY bytes, profile, process and display identities, complete App publish tree hashes, source policy \`${m.source.policy}\` and the lease audit. [Manifest](manifest.json) · [All 69 rows](report.json).`, '',
        ...m.attempts.flatMap(attempt => [`[Retained run \`${attempt.runId}\`](${attempt.archive}). ${attempt.reason}`, '']),
        'Rederive the report from the archive with Node 24 and Python 3.', '',
        '```sh', 'node scripts/terminfo-modern-results.mjs --check --x11-mouse', '```', '',
        'To collect a fresh run, use Linux x64 with Xvfb, libX11, libXtst, libXRes and Python 3. The collector uses its own private display and the normal desktop collection lease.', '',
        '```sh',
        `# Build in an App checkout at ${m.appRevision}.`,
        'scripts/build.sh publish src/Ntilde.App -c Release -r linux-x64 --self-contained true -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar',
        'chmod -R go-w /absolute/private/sidecar',
        `# Launch from a separate collector checkout at ${m.scriptsRevision}.`,
        `node scripts/terminfo-modern-x11-owned.mjs /absolute/private/sidecar/Ntilde ${m.appVersion}`,
        '```', '',
    ].join('\n');
}

export const modernX11MouseOutputPaths = base => ({ json: join(base, x11MousePublication, 'report.json'), markdown: join(base, x11MousePublication, 'report.md') });
