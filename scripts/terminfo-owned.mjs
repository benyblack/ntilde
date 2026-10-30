#!/usr/bin/env node
// Launches a private macOS desktop instance; never attaches to an existing pane.
import { spawn, execFileSync } from 'node:child_process';
import { createHash, randomBytes, randomUUID, timingSafeEqual } from 'node:crypto';
import { chmodSync, closeSync, existsSync, mkdirSync, mkdtempSync, openSync, readFileSync, realpathSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:net';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { adapterId, adaptCollector, upstreamBundleSha256, upstreamReceipt, validateCollectorReceipt } from './terminfo/collector.mjs';
import { assertProcessOwnership, matchesRecordedWorker, processRecord } from './terminfo/ownership.mjs';
import { createPrivateScratch, createProcessBudget, installLockedCollector, readCollectorLockfile, resolveOwnedExecutable } from './terminfo/runtime.mjs';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const writeJson = (path, value) => writeFileSync(path, JSON.stringify(value, null, 2) + '\n', { flag: 'wx', mode: 0o600 });
const quote = value => "'" + value.replaceAll("'", String.raw`'\''`) + "'";

function validateInvocation(args) {
    if (args.length !== 4 || args[0] !== '--executable' || args[2] !== '--app-version'
        || !/^\d[\dA-Za-z.+_-]*$/.test(args[3])) throw new Error('Use --help for the required executable and version arguments.');
    if (process.platform !== 'darwin') throw new Error('The owned adapter currently supports macOS only.');
    if (Number(process.versions.node.split('.')[0]) < 24) throw new Error('Node 24+ is required.');
    return resolveOwnedExecutable(args[1]);
}

function readPreparedCollector(tool) {
    const pkg = join(tool, 'node_modules', 'terminfo.dev');
    const original = join(pkg, 'dist', 'terminfo.bundle.mjs');
    if (!existsSync(original)) throw new Error('Prepare the pinned collector first; see docs/qa/QA_TERMINFO.md.');
    if (JSON.parse(readFileSync(join(pkg, 'package.json'))).version !== '4.0.0') throw new Error('Expected terminfo.dev 4.0.0.');
    const source = readFileSync(original, 'utf8');
    const receiptBytes = readFileSync(join(pkg, 'dist', 'terminfo.bundle.receipt.json'));
    validateCollectorReceipt(source, JSON.parse(receiptBytes));
    const lockBytes = readCollectorLockfile(tool);
    return { source, receiptBytes, lockBytes, manifestBytes: readFileSync(join(tool, 'package.json')) };
}

function prepareCollector(tool) {
    const { source, receiptBytes, lockBytes } = readPreparedCollector(tool);
    const ownerModule = join(repo, 'scripts', 'terminfo', 'ownership.mjs');
    const adapted = adaptCollector(source, ownerModule);
    const adapterBundleSha256 = hash(adapted);
    // Keep external package resolution beside the original bundle. A derived
    // file is content-addressed; the downloaded original is never overwritten.
    const bundle = join(tool, 'node_modules', 'terminfo.dev', 'dist', `ntilde-${adapterBundleSha256}.mjs`);
    if (!existsSync(bundle)) writeFileSync(bundle, adapted, { flag: 'wx', mode: 0o600 });
    if (hash(readFileSync(bundle)) !== adapterBundleSha256) throw new Error('Derived adapter bundle digest mismatch.');

    return { ownerModule, bundle, adapterBundleSha256, receiptBytes, lockBytes };
}

function collectionSummary(output, appVersion) {
    const rawPath = join(output, 'raw.json');
    const raw = JSON.parse(readFileSync(rawPath));
    if (raw.schemaVersion !== 2 || raw.target?.id !== 'ntilde' || raw.target?.version !== appVersion
        || raw.collectorAdapter?.id !== adapterId || raw.probeHash !== upstreamReceipt.probeHash
        || raw.sourceRevision !== upstreamReceipt.collectorRevision) throw new Error('Unexpected collected identity or suite.');
    const counts = {};
    const reasons = {};
    for (const observation of raw.observations) {
        counts[observation.outcome] = (counts[observation.outcome] ?? 0) + 1;
        if (observation.reason) reasons[observation.reason] = (reasons[observation.reason] ?? 0) + 1;
    }
    return { runId: raw.runId, suiteId: raw.suiteId, suiteComplete: raw.suiteComplete,
        ungradedDiagnostics: raw.ungradedDiagnostics, measuredAt: raw.measuredAt, target: raw.target,
        adapter: raw.collectorAdapter, observations: raw.observations.length, counts, reasons, rawSha256: hash(readFileSync(rawPath)) };
}

function terminateOwnedWorker(recorded, runId) {
    if (!recorded) return;
    try {
        const current = processRecord(recorded.pid);
        const command = execFileSync('/bin/ps', ['-p', String(recorded.pid), '-o', 'command='], {
            encoding: 'utf8', timeout: 2000, maxBuffer: 16384,
        });
        if (matchesRecordedWorker(current, recorded, command, runId)) process.kill(recorded.pid, 'SIGTERM');
    } catch {}
}

async function main() {
    const args = process.argv.slice(2);
    if (args.length === 1 && ['--help', '-h'].includes(args[0])) {
        console.log(`Usage: node scripts/terminfo-owned.mjs --executable /absolute/path/Ntilde --app-version VERSION

Requires macOS, Node 24+ and an uninstalled sidecar outside .app bundles.
Packaged apps are refused because a staged update can restart outside the fixture.
Requires an npm installation (or NTILDE_TERMINFO_NPM_CLI) to recreate locked dependencies.
Prepare terminfo.dev@4.0.0 under
artifacts/terminfo/tool (see docs/qa/QA_TERMINFO.md). Launches a disposable
Ntilde process with private settings and runs a locally extended collector.
Results go to artifacts/terminfo/owned-*. No existing sessions are used.
The collector extension and its digest are recorded; this is not stock CLI data.`);
        return;
    }
    const executable = validateInvocation(args);
    const input = readPreparedCollector(join(repo, 'artifacts', 'terminfo', 'tool'));

    let resolveDone;
    let rejectDone;
    const done = new Promise((resolve, reject) => { resolveDone = resolve; rejectDone = reject; });
    done.catch(() => {});
    let interruption;
    const interrupted = signal => {
        interruption ??= new Error(`Interrupted by ${signal}.`);
        process.exitCode = 1;
        rejectDone(interruption);
    };
    const onInt = () => interrupted('SIGINT');
    const onTerm = () => interrupted('SIGTERM');
    let scratch;
    let output;
    // Persistent handlers also protect against a second signal during teardown.
    process.on('SIGINT', onInt);
    process.on('SIGTERM', onTerm);
    try {
        output = mkdtempSync(join(repo, 'artifacts', 'terminfo', 'owned-'));
        scratch = createPrivateScratch();
        if (interruption) throw interruption;
        const privateTool = join(scratch, 'collector');
        installLockedCollector(privateTool, input.manifestBytes, input.lockBytes);
        const { ownerModule, bundle, adapterBundleSha256, receiptBytes, lockBytes } = prepareCollector(privateTool);
        const appData = join(scratch, 'appdata');
        mkdirSync(appData, { mode: 0o700 });
        const runId = randomBytes(16).toString('hex');
        const token = randomBytes(32).toString('hex');
        const configPath = join(scratch, 'worker.json');
        const workerScript = join(repo, 'scripts', 'terminfo', 'worker.mjs');
        const config = { runId, token, appData, output, executable, bundle,
            socket: join(scratch, 'owner.sock'), adapterBundleSha256, upstreamBundleSha256 };
        writeJson(configPath, config);
        const command = join(scratch, 'worker.sh');
        writeFileSync(command, `#!/bin/sh\nexec ${quote(process.execPath)} ${quote(workerScript)} ${quote(configPath)} --ntilde-terminfo-worker=${runId}\n`, { flag: 'wx', mode: 0o700 });
        const profileId = randomUUID();
        const settings = {
            Profiles: [{ Id: profileId, Name: 'terminfo disposable worker', Command: command, Arguments: '', StartingDirectory: scratch, Type: 0 }],
            DefaultProfileId: profileId, SessionPersistence: 'Off', ShellExitPolicy: 'Always', PaneClosePolicy: 'Always',
            QuakeModeEnabled: false, AutomaticUpdateChecks: false, AllowOsc52ClipboardWrite: false,
            BellAudioEnabled: false, BellVisualEnabled: false, SmoothScrolling: false,
            CommandAssistEnabled: false, CommandAssistShellIntegrationEnabled: false,
            CommandAssistPowerShellIntegrationEnabled: false, AgentAccessObserveEnabled: false, AgentAccessActEnabled: false,
        };
        writeJson(join(appData, 'settings.json'), settings);
        writeJson(join(output, 'initial-settings.json'), settings);
        writeFileSync(join(output, 'upstream-suite-receipt.json'), receiptBytes, { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'package-lock.json'), lockBytes, { flag: 'wx', mode: 0o600 });

        let app;
        let workerIdentity;
        let completed = false;
        const proofs = [];
        const image = statSync(executable);
        const context = {
            adapter: adapterId, adapterBundleSha256, upstreamBundleSha256, appVersion: args[3],
            appVersionSource: 'operator supplied; not independently verified',
            executable, executableSha256: hash(readFileSync(executable)),
            assemblySha256: existsSync(join(dirname(executable), 'Ntilde.dll')) ? hash(readFileSync(join(dirname(executable), 'Ntilde.dll'))) : null,
            nodeVersion: process.version, dependencyInstall: 'isolated npm ci --ignore-scripts', startedAt: new Date().toISOString(), runId,
            adapterSourceSha256: hash(readFileSync(fileURLToPath(import.meta.url))),
            ownershipSourceSha256: hash(readFileSync(ownerModule)), workerSourceSha256: hash(readFileSync(workerScript)),
            runtimeSourceSha256: hash(readFileSync(join(repo, 'scripts', 'terminfo', 'runtime.mjs'))),
            clipboard: 'disabled; no clipboard fixture', pixels: 'no pixel capture adapter',
        };
        const server = createServer(socket => {
            let buffer = '';
            socket.setTimeout(5000, () => socket.destroy());
            socket.on('error', () => {});
            socket.on('data', chunk => {
                buffer += chunk;
                if (buffer.length > 8192) return socket.destroy();
                if (!buffer.includes('\n')) return;
                socket.removeAllListeners('data');
                try {
                    const request = JSON.parse(buffer.slice(0, buffer.indexOf('\n')));
                    if (typeof request.token !== 'string' || request.token.length !== token.length
                        || !timingSafeEqual(Buffer.from(request.token), Buffer.from(token)) || request.runId !== runId) {
                        throw new Error('Invalid run capability.');
                    }
                    if (!app?.pid || app.exitCode !== null || app.signalCode !== null) throw new Error('Owned app is no longer live.');
                    const currentImage = statSync(executable);
                    if (image.ino !== currentImage.ino || image.size !== currentImage.size || image.mtimeMs !== currentImage.mtimeMs) throw new Error('Owned executable changed during collection.');
                    // Three serial queries must fit inside the existing 5s reply timeout.
                    const queryTimeout = createProcessBudget(4000);
                    const owner = processRecord(app.pid, queryTimeout());
                    const worker = processRecord(request.workerPid, queryTimeout());
                    assertProcessOwnership({ app: { ...owner, executable: realpathSync(owner.executable) }, worker,
                        ownerPid: app.pid, executable, uid: process.getuid(), runId, tty: request.tty,
                        appCommand: execFileSync('/bin/ps', ['-p', String(app.pid), '-o', 'command='], { encoding: 'utf8', timeout: queryTimeout(), maxBuffer: 16384 }) });
                    if (workerIdentity && workerIdentity.pid !== worker.pid) throw new Error('Another worker tried to use this run.');
                    workerIdentity ??= worker;
                    if (request.kind === 'prove') {
                        // Retain the measured proof, never the capability token.
                        if (proofs.length === 0) proofs.push({ app: owner, worker, tty: request.tty, at: new Date().toISOString() });
                    } else if (request.kind === 'done') {
                        completed = true;
                        resolveDone();
                    } else if (request.kind === 'failed') {
                        rejectDone(new Error(request.error ?? 'Worker failed.'));
                    } else throw new Error('Unknown owner request.');
                    socket.end(JSON.stringify({ ok: true, ownerPid: app.pid }) + '\n');
                } catch (error) {
                    socket.end(JSON.stringify({ ok: false, error: error.message }) + '\n');
                }
            });
        });
        let deadline;
        let logFd;
        try {
            await Promise.race([done, new Promise((resolve, reject) => {
                server.once('error', reject);
                server.listen(config.socket, resolve);
            })]);
            if (interruption) throw interruption;
            chmodSync(config.socket, 0o600);
            const env = { ...process.env, NTILDE_APPDATA_ROOT: appData, TERMINFO_RUN_ID: runId,
                TERM_PROGRAM: 'Ntilde', TERM_PROGRAM_VERSION: args[3], PATH: dirname(process.execPath) + ':' + process.env.PATH };
            for (const name of ['__CFBundleIdentifier', 'TMUX', 'STY', 'SSH_CONNECTION', 'SSH_TTY',
                'TERMINFO_CAPTURE_DIRECTORY', 'TERMINFO_CLIPBOARD_FIXTURE_RECEIPT', 'TERMINFO_RUNTIME_PROVENANCE']) delete env[name];
            logFd = openSync(join(output, 'app-output.log'), 'wx', 0o600);
            app = spawn(executable, [`--ntilde-terminfo-owner=${runId}`], { env, cwd: dirname(executable), stdio: ['ignore', logFd, logFd] });
            app.once('error', rejectDone);
            app.once('exit', (code, signal) => { if (!completed) rejectDone(new Error(`Owned app exited before collection (${code ?? signal}).`)); });
            context.ownerPid = app.pid;
            writeJson(join(output, 'context.json'), context);
            deadline = setTimeout(() => rejectDone(new Error('Owned collection exceeded 5 minutes.')), 300000);
            console.log(`Collecting in disposable Ntilde pid ${app.pid}; results: ${output}`);
            await done;
            const summary = collectionSummary(output, args[3]);
            writeJson(join(output, 'summary.json'), summary);
            console.log(JSON.stringify(summary, null, 2));
        } catch (error) {
            writeJson(join(output, 'failure.json'), { error: error.message, at: new Date().toISOString() });
            throw new Error(`${error.message} Evidence retained in ${output}`);
        } finally {
            clearTimeout(deadline);
            terminateOwnedWorker(workerIdentity, runId);
            // Only terminate the specific app process we launched.
            if (app?.pid && app.exitCode === null && app.signalCode === null) {
                app.kill('SIGTERM');
                await new Promise(resolve => {
                    const timer = setTimeout(() => app.kill('SIGKILL'), 2000);
                    app.once('exit', () => { clearTimeout(timer); resolve(); });
                });
            }
            if (logFd !== undefined) closeSync(logFd);
            await new Promise(resolve => server.close(() => resolve()));
            writeJson(join(output, 'ownership-proof.json'), { proofs, completed, cleanedUpAt: new Date().toISOString() });
        }
    } catch (error) {
        if (output && !existsSync(join(output, 'failure.json'))) {
            writeJson(join(output, 'failure.json'), { error: error.message, at: new Date().toISOString() });
        }
        throw error;
    } finally {
        try {
            // Also handles preparation failures before the app or server is started.
            if (scratch) rmSync(scratch, { recursive: true, force: true });
        } finally {
            process.removeListener('SIGINT', onInt);
            process.removeListener('SIGTERM', onTerm);
        }
    }
    if (interruption) {
        writeJson(join(output, 'failure.json'), { error: interruption.message, at: new Date().toISOString() });
        throw interruption;
    }
}

try {
    await main();
} catch (error) {
    console.error(`[terminfo-owned] ${error.message}`);
    process.exitCode = 1;
}
