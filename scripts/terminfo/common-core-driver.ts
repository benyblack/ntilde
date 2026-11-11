// Parent-owned local native launch adapter. This file never assigns feature verdicts.
import { existsSync, mkdirSync, readFileSync, writeFileSync, openSync, closeSync, unlinkSync, realpathSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { homedir } from 'node:os';
import { randomBytes, randomUUID, createHash } from 'node:crypto';
import { execFileSync, spawn } from 'node:child_process';
import { fileURLToPath, pathToFileURL } from 'node:url';

const [upstreamArg, appId, outputArg, executableArg, versionArg] = process.argv.slice(2);
if (!upstreamArg || !['ntilde', 'iterm2', 'ghostty'].includes(appId) || !outputArg || (appId === 'ntilde' && (!executableArg || !versionArg || !/^\d+(?:\.\d+){1,3}(?:[-+][a-zA-Z0-9.-]+)?$/.test(versionArg)))) throw new Error('Usage: bun common-core-driver.ts <upstream> <ntilde|iterm2|ghostty> <new-output> [Ntilde-sidecar Ntilde-version]');
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const upstream = realpathSync(upstreamArg), output = resolve(outputArg);
const importUpstream = (path: string) => import(pathToFileURL(join(upstream, path)).href);
const write = (name: string, value: unknown) => writeFileSync(join(output, name), JSON.stringify(value, null, 2) + '\n', { flag: 'wx', mode: 0o600 });
const quote = (text: string) => "'" + text.replaceAll("'", "'\\''") + "'";
const payload = (directory: string, prefix = ''): Record<string, string> => Object.fromEntries(readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const relative = prefix + entry.name, path = join(directory, entry.name);
    if (entry.isDirectory()) return Object.entries(payload(path, relative + '/'));
    if (entry.isFile() || entry.isSymbolicLink()) return [[relative, sha(path)]];
    throw new Error('Unsupported sidecar payload entry: ' + relative);
}).sort(([a], [b]) => a.localeCompare(b)));
const sha = (path: string) => createHash('sha256').update(readFileSync(path)).digest('hex');
const sleep = (ms: number) => new Promise(resolve => setTimeout(resolve, ms));
const lockPath = join(homedir(), '.ntilde-qa-sidecars', '.desktop-collection.lock');
mkdirSync(dirname(lockPath), { recursive: true });
mkdirSync(output, { mode: 0o700 });
const lockFd = openSync(lockPath, 'wx', 0o600);
const runId = randomBytes(16).toString('hex');
writeFileSync(lockFd, JSON.stringify({ owner: 'codex-core-terminal-comparison', pid: process.pid, runId, startedAt: new Date().toISOString(), worktree: root }));
closeSync(lockFd);
let launch: any, windowId: number | undefined, worker: any, ownedWindow: any;
const appPath = appId === 'iterm2' ? '/Applications/iTerm.app' : appId === 'ghostty' ? '/Applications/Ghostty.app' : undefined;
try {
    const executable = appId === 'ntilde' ? realpathSync(executableArg) : join(appPath!, 'Contents/MacOS', appId === 'iterm2' ? 'iTerm2' : 'ghostty');
    const plist = appPath ? JSON.parse(String(execFileSync('/usr/bin/plutil', ['-convert', 'json', '-o', '-', join(appPath, 'Contents/Info.plist')], { timeout: 5000 }))) : undefined;
    const version = appId === 'ntilde' ? versionArg : plist?.CFBundleShortVersionString;
    if (typeof version !== 'string' || !version) throw new Error('Application version is missing.');
    const buildPayload = appId === 'ntilde' ? payload(dirname(executable)) : undefined;
    const buildPayloadSha256 = buildPayload ? createHash('sha256').update(JSON.stringify(buildPayload)).digest('hex') : undefined;
    if (buildPayload) write('build-payload.json', { files: buildPayload, sha256: buildPayloadSha256, checkoutRevisionAtCollection: String(execFileSync('git', ['rev-parse', 'HEAD'], { cwd: root })).trim() });
    const versionBasis = appId === 'ntilde' ? 'explicit build version; executable digest retained' : 'installed application Info.plist CFBundleShortVersionString';
    const { probeSuiteSnapshot, ensureSourceSuiteEnvironment } = await importUpstream('packages/admin/versions.ts');
    const suite = probeSuiteSnapshot();
    const suiteEnv = ensureSourceSuiteEnvironment();
    const requirementsPath = join(root, 'docs/qa/terminfo/2026-11-11-core-results/requirements-v1.json');
    const requirements = JSON.parse(readFileSync(requirementsPath, 'utf8'));
    const coreIds = requirements.rows.map((r: any) => r.id).sort();
    const applicableIds = coreIds.filter((id: string) => suite.probes.term.includes(id));
    const preflight = process.env.COMMON_CORE_PREFLIGHT === '1';
    const scheduledIds = preflight ? applicableIds.filter((id: string) => ['text.basic', 'cursor.move.absolute'].includes(id)) : applicableIds;
    const config = { purpose: preflight ? 'preflight-only' : 'comparison', upstream, output, appId, version, runId, coreIds, scheduledIds, probeHash: suite.probeHash };
    write('config.json', config);
    write('source.json', { upstreamRevision: String(execFileSync('git', ['rev-parse', 'HEAD'], { cwd: upstream })).trim(), suite, requirementsSha256: sha(requirementsPath), workerSha256: sha(join(root, 'scripts/terminfo/common-core-worker.ts')), driverSha256: sha(fileURLToPath(import.meta.url)), executable, executableSha256: sha(executable), buildPayloadSha256, version, versionBasis, os: String(execFileSync('/usr/bin/sw_vers', ['-productVersion'])).trim(), startedAt: new Date().toISOString() });
    const scriptPath = join(output, `worker-${runId}.sh`);
    const identityPath = join(output, 'owner-tty.txt');
    const envAssignments = { PATH: '/opt/homebrew/bin:/opt/homebrew/opt/node@24/bin:/usr/bin:/bin:/usr/sbin:/sbin', ...suiteEnv, TERMINFO_RUN_ID: runId, TERMINFO_CAPTURE_DIRECTORY: join(output, 'artifacts'), TERMINFO_TARGET_ID: appId, TERMINFO_TARGET_VERSION: version };
    const script = '#!/bin/bash -p\n' + `tty > ${quote(identityPath)}\n` + 'unset TMUX STY SSH_CONNECTION SSH_TTY __CFBundleIdentifier\n' + `exec env ${Object.entries(envAssignments).map(([k,v]) => `${k}=${quote(String(v))}`).join(' ')} ${quote(process.execPath)} --no-env-file ${quote(join(root, 'scripts/terminfo/common-core-worker.ts'))} ${quote(join(output, 'config.json'))}\n`;
    writeFileSync(scriptPath, script, { flag: 'wx', mode: 0o700 });
    if (appId === 'ghostty') {
        const { prepareCliLaunch, launchPreparedCli } = await importUpstream('packages/admin/src/app.ts');
        const prepared = prepareCliLaunch('ghostty', { id: runId, directory: output, scriptPath });
        // Launch configuration only; the child and collector independently observe the real PTY grid.
        writeFileSync(prepared.marker.value, readFileSync(prepared.marker.value, 'utf8') + 'window-width = 140\nwindow-height = 41\n', { mode: 0o600 });
        launch = launchPreparedCli(prepared, output);
    } else if (appId === 'iterm2') {
        const apple = `tell application "iTerm"
            set beforeIds to id of every window
            set w to create window with default profile command ${JSON.stringify(scriptPath)}
            if beforeIds contains (id of w) then error "iTerm reused an existing window"
            return (id of w) as string
        end tell`;
        // Retain only display-related default-profile fields, never commands, paths or environment.
        const displaySettings = String(execFileSync('/usr/bin/python3', ['-c', `import plistlib,json,sys
with open(sys.argv[1], 'rb') as f: prefs=plistlib.load(f)
profile=next(p for p in prefs['New Bookmarks'] if p['Guid']==prefs['Default Bookmark Guid'])
keys=['Normal Font','Non Ascii Font','Use Non-ASCII Font','Use Bold Font','Use Bright Bold','Cursor Type','Blinking Cursor','Transparency','Foreground Color','Background Color','Bold Color','Cursor Color']+[f'Ansi {i} Color' for i in range(16)]
print(json.dumps({'source':'iTerm default profile display fields only','fields':{k:profile[k] for k in keys if k in profile}}))`, join(homedir(), 'Library/Preferences/com.googlecode.iterm2.plist')], { timeout: 5000 }));
        write('display-settings.json', JSON.parse(displaySettings));
        write('launch-request.json', { command: 'osascript', args: ['-e', apple] });
        windowId = Number(String(execFileSync('/usr/bin/osascript', ['-e', apple], { timeout: 30000 })).trim());
        if (!Number.isSafeInteger(windowId) || windowId < 1) throw new Error('iTerm returned no newly owned window id.');
        execFileSync('/usr/bin/osascript', ['-e', `tell application "iTerm"
set w to item 1 of (every window whose id is ${windowId})
set columns of current session of w to 140
set rows of current session of w to 41
activate
end tell`], { timeout: 15000 });
    } else {
        const appData = join(output, 'appdata');
        mkdirSync(appData, { mode: 0o700 });
        const profileId = randomUUID();
        const settings = { Profiles: [{ Id: profileId, Name: `common Core ${runId}`, Command: scriptPath, Arguments: '', StartingDirectory: output, Type: 0 }], DefaultProfileId: profileId, SessionPersistence: 'Off', ShellExitPolicy: 'Always', PaneClosePolicy: 'Always', AutomaticUpdateChecks: false, QuakeModeEnabled: false, CommandAssistEnabled: false, CommandAssistShellIntegrationEnabled: false, CommandAssistPowerShellIntegrationEnabled: false };
        writeFileSync(join(appData, 'settings.json'), JSON.stringify(settings), { flag: 'wx', mode: 0o600 });
        const log = openSync(join(output, 'app-output.log'), 'wx', 0o600);
        launch = { proc: spawn(executable, [`--ntilde-terminfo-owner=${runId}`], { cwd: dirname(executable), env: { ...process.env, NTILDE_APPDATA_ROOT: appData }, stdio: ['ignore', log, log] }), executablePath: executable, identityPath, marker: { flag: '--ntilde-terminfo-owner', value: runId } };
        closeSync(log);
    }
    const readyPath = join(output, 'worker-ready.json');
    const readyDeadline = performance.now() + 60000;
    while (!existsSync(readyPath)) {
        const errorPath = join(output, 'worker-error.json');
        if (existsSync(errorPath)) throw new Error(readFileSync(errorPath, 'utf8'));
        if (performance.now() > readyDeadline) throw new Error('Disposable application did not start its worker within 60 seconds.');
        if (launch?.proc?.exitCode !== null && launch?.proc?.exitCode !== undefined) throw new Error(`Application exited with ${launch.proc.exitCode}.`);
        await sleep(100);
    }
    worker = JSON.parse(readFileSync(readyPath, 'utf8'));
    if (worker.runId !== runId || !Number.isSafeInteger(worker.pid) || worker.pid < 1 || !/^\/dev\/tty[a-zA-Z0-9._-]+$/.test(worker.tty)) throw new Error('Invalid launched-worker identity.');
    let assertion: any;
    if (appId === 'iterm2') {
        const { assertITermWindow } = await importUpstream('packages/terminfo.dev/src/iterm-window-owner.ts');
        const appPid = assertITermWindow(windowId, worker.tty, worker.pid);
        assertion = { terminalAppOwner: { asserter: 'terminfo-admin', application: 'iterm2', launchRunId: runId, workerPid: worker.pid, windowId, tabTty: worker.tty, intendedVersion: version } };
        write('native-window.json', { appPid, windowId, tty: worker.tty });
    } else {
        const { ownedCliProcessWindow } = await importUpstream('packages/admin/src/app.ts');
        const { assertOwnedTerminalWindow } = await importUpstream('packages/terminfo.dev/src/terminal-app-window.ts');
        ownedWindow = ownedCliProcessWindow(launch);
        if (!ownedWindow) throw new Error('Launched process did not yield an owned window.');
        assertOwnedTerminalWindow(ownedWindow, worker.pid);
        assertion = { processOwner: { asserter: 'terminfo-admin', launchRunId: runId, workerPid: worker.pid, provider: 'cli-process', ownerPid: ownedWindow.ownerPid, executablePath: ownedWindow.executablePath, marker: ownedWindow.marker, tty: worker.tty, intendedVersion: version } };
        write('native-window.json', ownedWindow);
    }
    // Native activation is asynchronous: do not calibrate across inactive/active title-bar colors.
    const ownerPid = appId === 'iterm2' ? JSON.parse(readFileSync(join(output, 'native-window.json'), 'utf8')).appPid : ownedWindow.ownerPid;
    for (let attempt = 0; ; attempt++) {
        const active = String(execFileSync('/usr/bin/osascript', ['-l', 'JavaScript', '-e', `ObjC.import('AppKit'); const app = $.NSRunningApplication.runningApplicationWithProcessIdentifier(${ownerPid}); !app.isNil() && app.active;`], { timeout: 5000 })).trim();
        if (active === 'true') break;
        if (attempt === 49) throw new Error('Owned application did not become active before native calibration.');
        await sleep(100);
    }
    await sleep(1000);
    write('worker-owner.json', { ...worker, assertion });
    console.log(`Collecting ${scheduledIds.length} upstream Core probes in owned ${appId} ${version}; run ${runId}`);
    const complete = join(output, 'worker-complete.json'), error = join(output, 'worker-error.json');
    // Match the upstream native macOS job's 25-minute collection bound.
    const deadline = performance.now() + 25 * 60000;
    while (!existsSync(complete)) {
        if (existsSync(error)) throw new Error(readFileSync(error, 'utf8'));
        if (performance.now() > deadline) throw new Error('Upstream Core collection exceeded 25 minutes.');
        await sleep(250);
    }
    if (buildPayloadSha256 && createHash('sha256').update(JSON.stringify(payload(dirname(executable)))).digest('hex') !== buildPayloadSha256) throw new Error('Ntilde build payload changed during collection.');
    const run = JSON.parse(readFileSync(join(output, 'raw.json'), 'utf8'));
    console.log(JSON.stringify({ subject: run.subject, counts: run.results.filter((r: any) => coreIds.includes(r.featureId)).reduce((a: any,r: any) => { a[r.status] = (a[r.status] ?? 0) + 1; return a; }, {}) }));
} catch (error) {
    write('parent-error.json', { name: error instanceof Error ? error.name : 'Error', message: String(error), stack: error instanceof Error ? error.stack : undefined });
    process.exitCode = 1;
    console.error(String(error));
} finally {
    const failures: string[] = [];
    let ownedWindowAbsent = false;
    if (windowId !== undefined) {
        try {
            const remaining = String(execFileSync('/usr/bin/osascript', ['-e', `tell application "iTerm"\nset matches to every window whose id is ${windowId}\nif (count of matches) is 1 then\nset w to item 1 of matches\nif (count of tabs of w) is not 1 then error "owned window acquired another tab"\n${worker ? `if (count of sessions of tab 1 of w) > 0 then\nif (tty of session 1 of tab 1 of w) is not ${JSON.stringify(worker.tty)} then error "owned TTY changed"\nend if` : ''}\nclose w\nend if\nset remaining to count of (every window whose id is ${windowId})\nset waited to 0\nrepeat while remaining is not 0 and waited < 50\ndelay 0.1\nset remaining to count of (every window whose id is ${windowId})\nset waited to waited + 1\nend repeat\nreturn remaining\nend tell`], { timeout: 15000 })).trim();
            if (remaining !== '0') throw new Error('Owned iTerm window survived its close request.');
            ownedWindowAbsent = true;
        } catch (error) { failures.push(String(error)); }
    } else if (launch) {
        try {
            const { ownedCliProcessWindow } = await importUpstream('packages/admin/src/app.ts');
            const { closeOwnedTerminalWindow } = await importUpstream('packages/terminfo.dev/src/terminal-app-window.ts');
            const gone = () => {
                const status = (() => { try { return String(execFileSync('/bin/ps', ['-p', String(ownedWindow.ownerPid), '-o', 'stat='], { timeout: 5000, stdio: ['ignore', 'pipe', 'ignore'] })).trim(); } catch (error: any) { if (error.status === 1) return ''; throw error; } })();
                const absent = !status || status.startsWith('Z');
                const running = String(execFileSync('/usr/bin/osascript', ['-l', 'JavaScript', '-e', `ObjC.import('AppKit'); const app = $.NSRunningApplication.runningApplicationWithProcessIdentifier(${ownedWindow.ownerPid}); app.isNil() || app.terminated;`], { timeout: 5000 })).trim();
                return absent && running === 'true';
            };
            ownedWindow ??= ownedCliProcessWindow(launch);
            if (ownedWindow) {
                // A completed worker can close its own disposable application before the parent.
                // Zombies have already exited; never send them (or a reused PID) a signal.
                if (!gone()) {
                    try { closeOwnedTerminalWindow(ownedWindow); }
                    catch (error) { if (!gone()) throw error; }
                }
                for (let i = 0; i < 50 && !gone(); i++) await sleep(100);
                if (!gone()) throw new Error('Owned application process or native application registration remains.');
                ownedWindowAbsent = true;
            }
            else throw new Error('The launched application owner could not be resolved for verified cleanup.');
        } catch (error) { failures.push(String(error)); }
    }
    let workerExited = worker === undefined;
    if (worker) {
        const { processCommand } = await importUpstream('packages/terminfo.dev/src/owned-process.ts');
        for (let attempt = 0; attempt < 50; attempt++) {
            const command = processCommand(worker.pid);
            if (!command || !command.includes(join(output, 'config.json'))) { workerExited = true; break; }
            await sleep(100);
        }
        if (!workerExited) failures.push('The recorded worker survived owned-window cleanup.');
    }
    write('cleanup.json', { runId, completedAt: new Date().toISOString(), failures, ownedWindowAbsent, workerExited });
    if (failures.length) process.exitCode = 1;
    const lock = JSON.parse(readFileSync(lockPath, 'utf8'));
    if (lock.pid === process.pid && lock.runId === runId) unlinkSync(lockPath);
}
