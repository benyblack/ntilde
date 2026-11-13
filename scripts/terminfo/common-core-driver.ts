// Parent-owned local native launch adapter. This file never assigns feature verdicts.
import { pollCommonCore } from './common-core-poll.mjs';
import {
    spawnCommonCoreApplication,
    commonCoreCollectionReady,
    assertCommonCoreLaunchKnown,
} from './common-core-process.mjs';
import { publishCommonCoreJson } from './common-core-handshake.mjs';
import { terminateCommonCoreOwner } from './common-core-cleanup.mjs';
import { commonCoreGit, assertCleanCommonCoreSource } from './common-core-tools.mjs';
import { invalidatedWindowCases } from './common-core-retest.mjs';
import {
    existsSync,
    mkdirSync,
    readFileSync,
    writeFileSync,
    openSync,
    closeSync,
    realpathSync,
    readdirSync,
} from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { homedir } from 'node:os';
import { acquireDesktopLease, desktopLockWaitMs } from './desktop-lock.mjs';
import { randomUUID, createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { fileURLToPath, pathToFileURL } from 'node:url';

const [upstreamArg, appId, outputArg, executableArg, versionArg] = process.argv.slice(2);
if (
    !upstreamArg ||
    !['ntilde', 'iterm2', 'ghostty'].includes(appId) ||
    !outputArg ||
    (appId === 'ntilde' &&
        (!executableArg || !versionArg || !/^\d+(?:\.\d+){1,3}(?:[-+][a-zA-Z0-9.-]+)?$/.test(versionArg)))
)
    throw new Error(
        'Usage: bun common-core-driver.ts <upstream> <ntilde|iterm2|ghostty> <new-output> [Ntilde-sidecar Ntilde-version]',
    );
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const upstream = realpathSync(upstreamArg),
    output = resolve(outputArg);
/** Load the native harness from its explicit source root. */
const importUpstream = (path: string) => import(pathToFileURL(join(upstream, path)).href);
/** Atomically publish a parent record without overwriting prior evidence. */
const write = (name: string, value: unknown) => publishCommonCoreJson(output, name, value);
/** Quote one literal argument for the private POSIX bootstrap. */
const quote = (text: string) => "'" + text.replaceAll("'", String.raw`'\''`) + "'";
/** Record every file in the supplied managed sidecar payload. */
const payload = (directory: string, prefix = ''): Record<string, string> =>
    Object.fromEntries(
        readdirSync(directory, { withFileTypes: true })
            .flatMap((entry) => {
                const relative = prefix + entry.name,
                    path = join(directory, entry.name);
                if (entry.isDirectory()) return Object.entries(payload(path, relative + '/'));
                if (entry.isFile() || entry.isSymbolicLink()) return [[relative, sha(path)]];
                throw new Error('Unsupported sidecar payload entry: ' + relative);
            })
            .sort(([a], [b]) => a.localeCompare(b)),
    );
/** Hash the exact executable or script bytes. */
const sha = (path: string) => createHash('sha256').update(readFileSync(path)).digest('hex');
/** Yield between bounded process and handshake checks. */
const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));
/** Require both process exit and native application-registration absence. */
function nativeProcessAbsent(pid: number) {
    let status: string;
    try {
        status = String(
            execFileSync('/bin/ps', ['-p', String(pid), '-o', 'stat='], {
                timeout: 5000,
                stdio: ['ignore', 'pipe', 'ignore'],
            }),
        ).trim();
    } catch (error: any) {
        if (error.status === 1) {
            status = '';
        } else {
            throw error;
        }
    }
    const absent = !status || status.startsWith('Z');
    const script = `ObjC.import('AppKit'); const app = $.NSRunningApplication.runningApplicationWithProcessIdentifier(${pid}); app.isNil() || app.terminated;`;
    const registration = String(
        execFileSync('/usr/bin/osascript', ['-l', 'JavaScript', '-e', script], { timeout: 5000 }),
    ).trim();
    return absent && registration === 'true';
}
// Exclusive desktop lease before any output, launch or window action; ownership-checked release only after verified cleanup.
const lease = await acquireDesktopLease({
    entrypoint: 'common-core-driver.ts',
    worktree: root,
    waitMs: desktopLockWaitMs(),
    // Record only the markers this driver really puts on its processes: the worker command line always, the App only for Ntilde.
    markerKinds: appId === 'ntilde' ? ['owner', 'worker'] : ['worker'],
});
const runId = lease.leaseId;
try {
    mkdirSync(output, { mode: 0o700 });
} catch (error) {
    lease.release(); // nothing was launched
    throw error;
}
let applicationState: any;
let launchAttempted = false;
/** Persist the launch intent in the lease first: a failed write stops the launch before it can happen. */
function markLaunchAttempt() {
    lease.noteLaunchAttempt();
    launchAttempted = true;
}
let launch: any, windowId: number | undefined, worker: any, ownedWindow: any;
const nativeApps: Record<string, string> = { iterm2: '/Applications/iTerm.app', ghostty: '/Applications/Ghostty.app' };
const appPath = nativeApps[appId];
try {
    const upstreamRevision = assertCleanCommonCoreSource(upstream);
    const nativeExecutable = appId === 'iterm2' ? 'iTerm2' : 'ghostty';
    const executable =
        appId === 'ntilde' ? realpathSync(executableArg) : join(appPath!, 'Contents/MacOS', nativeExecutable);
    const plist = appPath
        ? JSON.parse(
              String(
                  execFileSync(
                      '/usr/bin/plutil',
                      ['-convert', 'json', '-o', '-', join(appPath, 'Contents/Info.plist')],
                      { timeout: 5000 },
                  ),
              ),
          )
        : undefined;
    const version = appId === 'ntilde' ? versionArg : plist?.CFBundleShortVersionString;
    if (typeof version !== 'string' || !version) throw new Error('Application version is missing.');
    const buildPayload = appId === 'ntilde' ? payload(dirname(executable)) : undefined;
    const buildPayloadSha256 = buildPayload
        ? createHash('sha256').update(JSON.stringify(buildPayload)).digest('hex')
        : undefined;
    if (buildPayload)
        write('build-payload.json', {
            files: buildPayload,
            sha256: buildPayloadSha256,
            checkoutRevisionAtCollection: String(
                execFileSync(commonCoreGit(), ['rev-parse', 'HEAD'], { cwd: root }),
            ).trim(),
        });
    const harnessNames = [
        'common-core-driver.ts',
        'common-core-worker.ts',
        'common-core-retest.mjs',
        'common-core-tools.mjs',
        'common-core-handshake.mjs',
        'common-core-process.mjs',
        'common-core-poll.mjs',
        'common-core-cleanup.mjs',
    ];
    const harnessDirectory = join(output, 'harness-sources');
    mkdirSync(harnessDirectory, { mode: 0o700 });
    const harnessSources = Object.fromEntries(
        harnessNames.map((name) => {
            const bytes = readFileSync(join(root, 'scripts/terminfo', name));
            writeFileSync(join(harnessDirectory, name), bytes, { flag: 'wx', mode: 0o600 });
            return [name, createHash('sha256').update(bytes).digest('hex')];
        }),
    );
    const workerBundleSources = Object.fromEntries(
        ['common-core-worker.ts', 'common-core-handshake.mjs', 'common-core-poll.mjs'].map((name) => [
            name,
            harnessSources[name],
        ]),
    );
    const workerBundleSha256 = createHash('sha256').update(JSON.stringify(workerBundleSources)).digest('hex');
    writeFileSync(join(output, 'launch-driver.ts'), readFileSync(join(harnessDirectory, 'common-core-driver.ts')), {
        flag: 'wx',
        mode: 0o600,
    });
    writeFileSync(join(output, 'collection-worker.ts'), readFileSync(join(harnessDirectory, 'common-core-worker.ts')), {
        flag: 'wx',
        mode: 0o600,
    });
    const versionBasis =
        appId === 'ntilde'
            ? 'explicit build version; executable digest retained'
            : 'installed application Info.plist CFBundleShortVersionString';
    const { probeSuiteSnapshot, ensureSourceSuiteEnvironment } = await importUpstream('packages/admin/versions.ts');
    const suite = probeSuiteSnapshot();
    const suiteEnv = ensureSourceSuiteEnvironment();
    const requirementsPath = join(root, 'docs/qa/terminfo/2026-11-11-core-results/requirements-v1.json');
    const requirements = JSON.parse(readFileSync(requirementsPath, 'utf8'));
    const coreIds = requirements.rows.map((r: any) => r.id).sort();
    const applicableIds = coreIds.filter((id: string) => suite.probes.term.includes(id));
    const preflight = process.env.COMMON_CORE_PREFLIGHT === '1';
    let scheduledIds = preflight
        ? applicableIds.filter((id: string) => ['text.basic', 'cursor.move.absolute'].includes(id))
        : applicableIds;
    let retest: any;
    if (process.env.COMMON_CORE_RETEST_FROM) {
        if (preflight) throw new Error('A focused retest cannot also be a preflight.');
        const baselineDirectory = realpathSync(process.env.COMMON_CORE_RETEST_FROM);
        const baselineBytes = readFileSync(join(baselineDirectory, 'raw.json'));
        const baseline = JSON.parse(baselineBytes.toString());
        const baselineSource = JSON.parse(readFileSync(join(baselineDirectory, 'source.json'), 'utf8'));
        if (
            baseline.subject?.id !== appId ||
            baseline.subject?.version !== version ||
            baseline.probeHash !== suite.probeHash ||
            baseline.sourceRevision !==
                String(execFileSync(commonCoreGit(), ['rev-parse', 'HEAD'], { cwd: upstream })).trim() ||
            baselineSource.workerSha256 !== sha(join(root, 'scripts/terminfo/common-core-worker.ts')) ||
            baselineSource.workerBundleSha256 !== workerBundleSha256 ||
            baselineSource.executableSha256 !== sha(executable) ||
            baselineSource.buildPayloadSha256 !== buildPayloadSha256
        )
            throw new Error('Retest application, payload or upstream identity differs from the baseline.');
        scheduledIds = invalidatedWindowCases(baseline, coreIds);
        if (scheduledIds.some((id: string) => !applicableIds.includes(id)))
            throw new Error('Retest case has no current upstream application callback.');
        retest = {
            baselineRunId: baseline.runId,
            baselineRawSha256: createHash('sha256').update(baselineBytes).digest('hex'),
            reason: 'all Core cases invalidated by native-window movement',
        };
    }
    let purpose = 'comparison';
    if (preflight) purpose = 'preflight-only';
    if (retest) purpose = 'focused-retest';
    const config = {
        purpose,
        retest,
        upstream,
        output,
        appId,
        version,
        runId,
        coreIds,
        scheduledIds,
        probeHash: suite.probeHash,
    };
    write('config.json', config);
    write('source.json', {
        upstreamRevision: String(execFileSync(commonCoreGit(), ['rev-parse', 'HEAD'], { cwd: upstream })).trim(),
        suite,
        requirementsSha256: sha(requirementsPath),
        workerSha256: sha(join(root, 'scripts/terminfo/common-core-worker.ts')),
        workerBundleSha256,
        harnessSources,
        driverSha256: sha(fileURLToPath(import.meta.url)),
        retestSelectorSha256: sha(join(root, 'scripts/terminfo/common-core-retest.mjs')),
        handshakeSha256: sha(join(root, 'scripts/terminfo/common-core-handshake.mjs')),
        executable,
        executableSha256: sha(executable),
        buildPayloadSha256,
        version,
        versionBasis,
        os: String(execFileSync('/usr/bin/sw_vers', ['-productVersion'])).trim(),
        startedAt: new Date().toISOString(),
    });
    const scriptPath = join(output, `worker-${runId}.sh`);
    const identityPath = join(output, 'owner-tty.txt');
    const envAssignments = {
        PATH: '/opt/homebrew/bin:/opt/homebrew/opt/node@24/bin:/usr/bin:/bin:/usr/sbin:/sbin',
        ...suiteEnv,
        TERMINFO_RUN_ID: runId,
        TERMINFO_CAPTURE_DIRECTORY: join(output, 'artifacts'),
        TERMINFO_TARGET_ID: appId,
        TERMINFO_TARGET_VERSION: version,
    };
    const assignmentText = Object.entries(envAssignments)
        .map(([key, value]) => key + '=' + quote(String(value)))
        .join(' ');
    const script =
        '#!/bin/bash -p\n' +
        `tty > ${quote(identityPath)}\n` +
        'unset TMUX STY SSH_CONNECTION SSH_TTY __CFBundleIdentifier\n' +
        `exec env ${assignmentText} ${quote(process.execPath)} --no-env-file ${quote(join(harnessDirectory, 'common-core-worker.ts'))} ${quote(join(output, 'config.json'))} ${quote('--ntilde-terminfo-worker=' + runId)}\n`;
    writeFileSync(scriptPath, script, { flag: 'wx', mode: 0o700 });
    if (appId === 'ghostty') {
        const { prepareCliLaunch, launchPreparedCli } = await importUpstream('packages/admin/src/app.ts');
        const prepared = prepareCliLaunch('ghostty', { id: runId, directory: output, scriptPath });
        // Launch configuration only; the child and collector independently observe the real PTY grid.
        writeFileSync(
            prepared.marker.value,
            readFileSync(prepared.marker.value, 'utf8') + 'window-width = 140\nwindow-height = 41\n',
            { mode: 0o600 },
        );
        markLaunchAttempt();
        launch = launchPreparedCli(prepared, output);
    } else if (appId === 'iterm2') {
        const apple = `tell application "iTerm"
            set beforeIds to id of every window
            set w to create window with default profile command ${JSON.stringify(scriptPath)}
            if beforeIds contains (id of w) then error "iTerm reused an existing window"
            return (id of w) as string
        end tell`;
        // Retain only display-related default-profile fields, never commands, paths or environment.
        const displaySettings = String(
            execFileSync(
                '/usr/bin/python3',
                [
                    '-c',
                    `import plistlib,json,sys
with open(sys.argv[1], 'rb') as f: prefs=plistlib.load(f)
profile=next(p for p in prefs['New Bookmarks'] if p['Guid']==prefs['Default Bookmark Guid'])
keys=['Normal Font','Non Ascii Font','Use Non-ASCII Font','Use Bold Font','Use Bright Bold','Cursor Type','Blinking Cursor','Transparency','Foreground Color','Background Color','Bold Color','Cursor Color']+[f'Ansi {i} Color' for i in range(16)]
print(json.dumps({'source':'iTerm default profile display fields only','fields':{k:profile[k] for k in keys if k in profile}}))`,
                    join(homedir(), 'Library/Preferences/com.googlecode.iterm2.plist'),
                ],
                { timeout: 5000 },
            ),
        );
        write('display-settings.json', JSON.parse(displaySettings));
        write('launch-request.json', { command: 'osascript', args: ['-e', apple] });
        markLaunchAttempt();
        const launchedId = Number(String(execFileSync('/usr/bin/osascript', ['-e', apple], { timeout: 30000 })).trim());
        if (!Number.isSafeInteger(launchedId) || launchedId < 1)
            throw new Error('iTerm returned no newly owned window id.');
        windowId = launchedId;
        execFileSync(
            '/usr/bin/osascript',
            [
                '-e',
                `tell application "iTerm"
set w to item 1 of (every window whose id is ${windowId})
set columns of current session of w to 140
set rows of current session of w to 41
activate
end tell`,
            ],
            { timeout: 15000 },
        );
    } else {
        const appData = join(output, 'appdata');
        mkdirSync(appData, { mode: 0o700 });
        const profileId = randomUUID();
        const settings = {
            Profiles: [
                {
                    Id: profileId,
                    Name: `common Core ${runId}`,
                    Command: scriptPath,
                    Arguments: '',
                    StartingDirectory: output,
                    Type: 0,
                },
            ],
            DefaultProfileId: profileId,
            SessionPersistence: 'Off',
            ShellExitPolicy: 'Always',
            PaneClosePolicy: 'Always',
            AutomaticUpdateChecks: false,
            QuakeModeEnabled: false,
            CommandAssistEnabled: false,
            CommandAssistShellIntegrationEnabled: false,
            CommandAssistPowerShellIntegrationEnabled: false,
        };
        writeFileSync(join(appData, 'settings.json'), JSON.stringify(settings), { flag: 'wx', mode: 0o600 });
        const log = openSync(join(output, 'app-output.log'), 'wx', 0o600);
        markLaunchAttempt();
        applicationState = spawnCommonCoreApplication(executable, [`--ntilde-terminfo-owner=${runId}`], {
            cwd: dirname(executable),
            env: { ...process.env, NTILDE_APPDATA_ROOT: appData },
            stdio: ['ignore', log, log],
        });
        launch = {
            proc: applicationState.proc,
            executablePath: executable,
            identityPath,
            marker: { flag: '--ntilde-terminfo-owner', value: runId },
        };
        closeSync(log);
    }
    const readyPath = join(output, 'worker-ready.json');
    await pollCommonCore(
        () => {
            if (applicationState?.error) throw applicationState.error;
            const errorPath = join(output, 'worker-error.json');
            if (existsSync(errorPath)) throw new Error(readFileSync(errorPath, 'utf8'));
            const exitCode = launch?.proc?.exitCode;
            if (exitCode != null) throw new Error('Application exited with ' + exitCode);
            return existsSync(readyPath);
        },
        { timeoutMs: 60000, message: 'Disposable application did not start its worker within 60 seconds.' },
    );
    worker = JSON.parse(readFileSync(readyPath, 'utf8'));
    if (
        worker.runId !== runId ||
        !Number.isSafeInteger(worker.pid) ||
        worker.pid < 1 ||
        !/^\/dev\/tty[a-zA-Z0-9._-]+$/.test(worker.tty)
    )
        throw new Error('Invalid launched-worker identity.');
    let assertion: any;
    if (appId === 'iterm2') {
        const { assertITermWindow } = await importUpstream('packages/terminfo.dev/src/iterm-window-owner.ts');
        const appPid = assertITermWindow(windowId, worker.tty, worker.pid);
        assertion = {
            terminalAppOwner: {
                asserter: 'terminfo-admin',
                application: 'iterm2',
                launchRunId: runId,
                workerPid: worker.pid,
                windowId,
                tabTty: worker.tty,
                intendedVersion: version,
            },
        };
        write('native-window.json', { appPid, windowId, tty: worker.tty });
    } else {
        const { ownedCliProcessWindow } = await importUpstream('packages/admin/src/app.ts');
        const { assertOwnedTerminalWindow } = await importUpstream('packages/terminfo.dev/src/terminal-app-window.ts');
        ownedWindow = ownedCliProcessWindow(launch);
        if (!ownedWindow) throw new Error('Launched process did not yield an owned window.');
        assertOwnedTerminalWindow(ownedWindow, worker.pid);
        assertion = {
            processOwner: {
                asserter: 'terminfo-admin',
                launchRunId: runId,
                workerPid: worker.pid,
                provider: 'cli-process',
                ownerPid: ownedWindow.ownerPid,
                executablePath: ownedWindow.executablePath,
                marker: ownedWindow.marker,
                tty: worker.tty,
                intendedVersion: version,
            },
        };
        write('native-window.json', ownedWindow);
    }
    // Native activation is asynchronous: do not calibrate across inactive/active title-bar colors.
    const ownerPid =
        appId === 'iterm2'
            ? JSON.parse(readFileSync(join(output, 'native-window.json'), 'utf8')).appPid
            : ownedWindow.ownerPid;
    await pollCommonCore(
        () =>
            String(
                execFileSync(
                    '/usr/bin/osascript',
                    [
                        '-l',
                        'JavaScript',
                        '-e',
                        `ObjC.import('AppKit'); const app = $.NSRunningApplication.runningApplicationWithProcessIdentifier(${ownerPid}); !app.isNil() && app.active;`,
                    ],
                    { timeout: 5000 },
                ),
            ).trim() === 'true',
        { timeoutMs: 5000, message: 'Owned application did not become active before native calibration.' },
    );
    await sleep(1000);
    write('worker-owner.json', { ...worker, assertion });
    console.log(`Collecting ${scheduledIds.length} upstream Core probes in owned ${appId} ${version}; run ${runId}`);
    const complete = join(output, 'worker-complete.json'),
        error = join(output, 'worker-error.json');
    // Match the upstream native macOS job's 25-minute collection bound.
    const { processCommand } = await importUpstream('packages/terminfo.dev/src/owned-process.ts');
    await pollCommonCore(
        () =>
            commonCoreCollectionReady({
                completed: () => existsSync(complete),
                failure: () =>
                    applicationState?.error ?? (existsSync(error) ? new Error(readFileSync(error, 'utf8')) : null),
                running: () => processCommand(worker.pid)?.includes(join(output, 'config.json')) === true,
            }),
        { timeoutMs: 25 * 60000, intervalMs: 1000, message: 'Upstream Core collection exceeded 25 minutes.' },
    );
    assertCleanCommonCoreSource(upstream, upstreamRevision);
    const stableHarness = harnessNames.every(
        (name) =>
            sha(join(root, 'scripts/terminfo', name)) === harnessSources[name] &&
            sha(join(harnessDirectory, name)) === harnessSources[name],
    );
    if (!stableHarness) {
        throw new Error('Launch/worker source changed during collection.');
    }
    if (
        buildPayloadSha256 &&
        createHash('sha256')
            .update(JSON.stringify(payload(dirname(executable))))
            .digest('hex') !== buildPayloadSha256
    )
        throw new Error('Ntilde build payload changed during collection.');
    const run = JSON.parse(readFileSync(join(output, 'raw.json'), 'utf8'));
    console.log(
        JSON.stringify({
            subject: run.subject,
            counts: run.results
                .filter((r: any) => coreIds.includes(r.featureId))
                .reduce((a: any, r: any) => {
                    a[r.status] = (a[r.status] ?? 0) + 1;
                    return a;
                }, {}),
        }),
    );
} catch (error) {
    write('parent-error.json', {
        name: error instanceof Error ? error.name : 'Error',
        message: String(error),
        stack: error instanceof Error ? error.stack : undefined,
    });
    process.exitCode = 1;
    console.error(String(error));
} finally {
    await cleanupLaunch().catch((error) => {
        process.exitCode = 1;
        console.error('Cleanup could not finalize its evidence: ' + String(error));
    });
}

/** Close only the newly recorded iTerm window behind a current single-session TTY witness. */
function closeITermWindow() {
    let witness = 'error "owned worker witness absent"';
    if (worker) {
        witness = [
            'if (count of sessions of tab 1 of w) is not 1 then error "owned session witness absent"',
            'if (tty of session 1 of tab 1 of w) is not ' +
                JSON.stringify(worker.tty) +
                ' then error "owned TTY changed"',
        ].join('\n');
    }
    const script = `tell application "iTerm"
set matches to every window whose id is ${windowId}
if (count of matches) is 1 then
set w to item 1 of matches
if (count of tabs of w) is not 1 then error "owned window acquired another tab"
${witness}
close w
end if
set remaining to count of (every window whose id is ${windowId})
set waited to 0
repeat while remaining is not 0 and waited < 50
delay 0.1
set remaining to count of (every window whose id is ${windowId})
set waited to waited + 1
end repeat
return remaining
end tell`;
    const remaining = String(execFileSync('/usr/bin/osascript', ['-e', script], { timeout: 15000 })).trim();
    if (remaining !== '0') {
        throw new Error('Owned iTerm window survived its close request.');
    }
}

/** Close the resolved CLI owner, allowing a natural exit before the parent reaches cleanup. */
async function closeCLIWindow() {
    const { ownedCliProcessWindow } = await importUpstream('packages/admin/src/app.ts');
    const { closeOwnedTerminalWindow } = await importUpstream('packages/terminfo.dev/src/terminal-app-window.ts');
    ownedWindow ??= ownedCliProcessWindow(launch);
    if (!ownedWindow) {
        throw new Error('Launched application owner could not be resolved for cleanup.');
    }
    if (!nativeProcessAbsent(ownedWindow.ownerPid)) {
        try {
            closeOwnedTerminalWindow(ownedWindow);
        } catch (error) {
            if (!nativeProcessAbsent(ownedWindow.ownerPid)) {
                throw error;
            }
        }
    }
    await pollCommonCore(() => nativeProcessAbsent(ownedWindow.ownerPid), {
        timeoutMs: 5000,
        message: 'Owned application registration remains.',
    });
}

/** Recover a startup failure only through the fresh executable/marker pair, never a guessed window. */
async function closeMarkedStartupOwner() {
    const { findOwnerPids } = await importUpstream('packages/terminfo.dev/src/terminal-app-window.ts');
    const { processExecutable, processCommand, argvCarriesMarker } = await importUpstream(
        'packages/terminfo.dev/src/owned-process.ts',
    );
    const owners = findOwnerPids(launch.executablePath, launch.marker);
    if (owners.length !== 1) {
        throw new Error('Startup cleanup cannot identify exactly one marked launch owner.');
    }
    const pid = owners[0];
    await terminateCommonCoreOwner({
        exited: () => nativeProcessAbsent(pid),
        matches: () =>
            processExecutable(pid, 'startup cleanup') === realpathSync(launch.executablePath) &&
            argvCarriesMarker(processCommand(pid)?.split(/\s+/) ?? [], launch.marker),
        signal: (signal: NodeJS.Signals) => process.kill(pid, signal),
        pause: sleep,
    });
    if (!nativeProcessAbsent(pid)) {
        throw new Error('Startup owner remains after cleanup.');
    }
}

/** Keep the original refusal if both normal and marked-owner startup cleanup fail. */
async function closeCLIWithFallback() {
    try {
        await closeCLIWindow();
    } catch (error) {
        const primaryError = String(error);
        try {
            await closeMarkedStartupOwner();
        } catch (error) {
            throw new Error('Owned cleanup failed: ' + primaryError + '; startup fallback: ' + String(error));
        }
    }
}

/** Select the cleanup authority recorded by this launch, refusing any absent ownership witness. */
async function closeRunOwner() {
    if (windowId !== undefined) {
        closeITermWindow();
        return true;
    }
    if (applicationState?.error && !applicationState.spawned) {
        return true;
    }
    if (!launch) {
        assertCommonCoreLaunchKnown(launchAttempted);
        return false;
    }
    await closeCLIWithFallback();
    return true;
}

/** Confirm that the recorded worker no longer executes this run's configuration. */
async function confirmWorkerExit() {
    if (!worker) {
        return;
    }
    const { processCommand } = await importUpstream('packages/terminfo.dev/src/owned-process.ts');
    await pollCommonCore(
        () => {
            const command = processCommand(worker.pid);
            return !command?.includes(join(output, 'config.json'));
        },
        { timeoutMs: 5000, message: 'Recorded worker survived owned-window cleanup.' },
    );
}

/** Publish cleanup evidence before releasing the lease; failures retain it (marked cleanup-failed) for inspection. */
function finishCleanupRecord(failures: string[], ownedWindowAbsent: boolean, workerExited: boolean) {
    let ownLock = true;
    try {
        lease.assertHeld();
    } catch (error) {
        ownLock = false;
        failures.push('Collection lock ownership changed before cleanup completed: ' + String(error));
    }
    write('cleanup.json', { runId, completedAt: new Date().toISOString(), failures, ownedWindowAbsent, workerExited });
    if (failures.length) {
        process.exitCode = 1;
    }
    if (!ownLock) {
        return;
    }
    if (failures.length) {
        lease.retain(failures.join('; '));
    } else {
        lease.release();
    }
}

/** Finish both cleanup authorities without replacing a primary collection error. */
async function cleanupLaunch() {
    const failures: string[] = [];
    let ownedWindowAbsent = false,
        workerExited = false;
    try {
        ownedWindowAbsent = await closeRunOwner();
    } catch (error) {
        failures.push(String(error));
    }
    try {
        if (!worker && launchAttempted && !ownedWindowAbsent) {
            throw new Error('Worker exit is unverified after an unconfirmed launch.');
        }
        await confirmWorkerExit();
        workerExited = true;
    } catch (error) {
        failures.push(String(error));
    }
    // The lease may be released only when the existing checks held AND no process carrying its run markers survives.
    if (failures.length === 0) {
        try {
            await lease.verifyCleanup();
        } catch (error) {
            failures.push(String(error));
        }
    }
    finishCleanupRecord(failures, ownedWindowAbsent, workerExited);
}
