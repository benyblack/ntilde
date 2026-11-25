#!/usr/bin/env node
import { createHash } from 'node:crypto';
import { chmodSync, existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, dirname, isAbsolute, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { acquireDesktopLease, desktopLockAuditPath, desktopLockWaitMs } from './terminfo/desktop-lock.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const files = ['desktop-lock.mjs', 'linux-process-core.mjs', 'linux-process-provider.mjs', 'modern-x11-mouse.mjs',
    'modern-x11-mouse-collect.mjs', 'modern-x11-mouse-native.py', 'modern-x11-mouse-worker.py'];
const digest = bytes => createHash('sha256').update(bytes).digest('hex');

function copySources(scratch, output, keyboard) {
    const dir = join(scratch, 'sources'), retained = join(output, 'sources');
    mkdirSync(dir, { mode: 0o700 });
    mkdirSync(retained, { mode: 0o700 });
    const selected = [...files];
    if (keyboard) selected.push('modern-x11-keyboard.mjs');
    if (keyboard === '--application-keypad') selected.push('modern-x11-keypad.mjs');
    const policy = keyboard === '--application-keypad' ? 'owned-modern-x11-keypad-v1'
        : keyboard ? 'owned-modern-x11-keyboard-v1' : 'owned-modern-x11-mouse-v1';
    const manifest = Object.fromEntries(selected.map(name => {
        const path = join(here, 'terminfo', name);
        if (!lstatSync(path).isFile()) throw new Error('Expected regular scoped collector source.');
        const bytes = readFileSync(path);
        for (const root of [dir, retained]) writeFileSync(join(root, name), bytes, { flag: 'wx', mode: 0o400 });
        return [name, digest(bytes)];
    }));
    writeFileSync(join(output, 'source-manifest.json'), JSON.stringify({ policy, files: manifest,
        launcherSha256: digest(readFileSync(fileURLToPath(import.meta.url))) }, null, 2) + '\n', { flag: 'wx', mode: 0o600 });
    writeFileSync(join(output, 'launcher.mjs'), readFileSync(fileURLToPath(import.meta.url)), { flag: 'wx', mode: 0o400 });
    return { dir, manifest };
}

function readAudit(path) {
    try { return readFileSync(path, 'utf8').trimEnd().split('\n'); }
    catch (error) {
        if (error.code !== 'ENOENT') throw error;
        return [];
    }
}

function captureLease(lease, output) {
    const path = desktopLockAuditPath(lease.lockPath);
    const current = readAudit(path);
    const entries = [...new Set([...readAudit(path + '.1'), ...current])]
        .filter(line => line.includes(lease.leaseId)).filter(line => JSON.parse(line).lease === lease.leaseId);
    writeFileSync(join(output, 'desktop-lease.jsonl'), entries.join('\n') + '\n', { flag: 'wx', mode: 0o600 });
    const expected = lease.launchAttempted ? ['acquired', 'launch-attempt', 'cleanup-verified', 'released'] : ['acquired', 'released'];
    if (JSON.stringify(entries.map(line => JSON.parse(line).event)) !== JSON.stringify(expected)) throw new Error('Incomplete X11 desktop lease audit.');
}

function validateInvocation() {
    const [target, appVersion, selection, ...extra] = process.argv.slice(2);
    if (![undefined, '--modify-other-keys', '--application-keypad'].includes(selection) || extra.length || !target || !isAbsolute(target) || !/^\d[\dA-Za-z.+_-]*$/.test(appVersion ?? '')
        || process.platform !== 'linux' || Number(process.versions.node.split('.')[0]) < 24) {
        throw new Error('Usage on Linux with Node 24+: node scripts/terminfo-modern-x11-owned.mjs /absolute/private/publish/Ntilde APP_VERSION [--modify-other-keys|--application-keypad]');
    }
    const executable = realpathSync(target);
    const info = lstatSync(executable);
    if (target !== executable || basename(executable) !== 'Ntilde' || !info.isFile() || info.uid !== process.getuid()
        || (info.mode & 0o022) !== 0 || lstatSync(dirname(executable)).uid !== process.getuid()) throw new Error('Expected the owned regular Ntilde sidecar.');
    return { executable, appVersion, keyboard: selection };
}

function finishLease(lease, scratch, sources, output) {
    if (lease.launchAttempted && !lease.cleanupVerified) {
        lease.retain('Owned X11 launch or cleanup was not verified.');
        throw new Error('Desktop lock retained for ' + lease.leaseId);
    }
    if (sources) chmodSync(sources.dir, 0o700);
    if (scratch) {
        rmSync(scratch, { recursive: true, force: true });
        if (existsSync(scratch)) throw new Error('Disposable X11 fixture remains.');
        writeFileSync(join(output, 'fixture-cleanup.json'), JSON.stringify({
            runId: lease.leaseId, scratch, removedAt: new Date().toISOString(),
        }) + '\n', { flag: 'wx', mode: 0o600 });
    }
    lease.release();
    if (output) captureLease(lease, output);
}

function assertUnchanged(sources) {
    if (readdirSync(sources.dir).length !== Object.keys(sources.manifest).length) throw new Error('Measured source membership changed.');
    for (const name of Object.keys(sources.manifest)) {
        if (digest(readFileSync(join(sources.dir, name))) !== sources.manifest[name]) throw new Error('Measured source changed.');
    }
}

async function runOwnedFixture(lease, outputRoot, invocation) {
    let scratch, output, sources, failure, verdicts;
    const signals = ['SIGINT', 'SIGTERM', 'SIGHUP'];
    let interrupted;
    const onSignal = signal => { interrupted ??= signal; };
    for (const signal of signals) process.on(signal, onSignal);
    try {
        output = mkdtempSync(join(outputRoot, 'modern-x11-'));
        console.log('Owned X11 result directory ' + output);
        scratch = realpathSync(mkdtempSync(join(tmpdir(), 'ntilde-x11-')));
        sources = copySources(scratch, output, invocation.keyboard);
        chmodSync(sources.dir, 0o500);
        const { collect } = await import(pathToFileURL(join(sources.dir, 'modern-x11-mouse-collect.mjs')).href);
        verdicts = await collect({ scratch, output, ...invocation, sources: sources.dir, lease, aborted: () => interrupted !== undefined });
        if (interrupted) throw new Error('Interrupted by ' + interrupted);
        assertUnchanged(sources);
    } catch (error) { failure = error; }
    try { finishLease(lease, scratch, sources, output); }
    catch (error) { failure = failure ? new AggregateError([failure, error], 'run and cleanup failed') : error; }
    finally { for (const signal of signals) process.removeListener(signal, onSignal); }
    if (!failure) {
        console.log(JSON.stringify({ runId: lease.leaseId, output, verdicts }));
        return;
    }
    if (output) writeFileSync(join(output, 'failure.json'), JSON.stringify({
        errors: failure instanceof AggregateError ? failure.errors.map(error => error.message) : [failure.message],
    }) + '\n', { mode: 0o600 });
    throw failure;
}

async function main() {
    const invocation = validateInvocation();
    const repo = realpathSync(join(here, '..'));
    const outputRoot = join(repo, 'artifacts', 'terminfo');
    mkdirSync(outputRoot, { recursive: true, mode: 0o700 });
    const lease = await acquireDesktopLease({ entrypoint: 'terminfo-modern-x11-owned.mjs', worktree: repo, waitMs: desktopLockWaitMs() });
    await runOwnedFixture(lease, outputRoot, invocation);
}
try { await main(); }
catch (error) { console.error(error); process.exitCode = 1; }
