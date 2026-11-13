// The trusted launcher owns the desktop lease for the owned runner, and the driver/runner source keeps its ordering
// invariants. Fixture launchers run with a patched private lock path and fixture runners: no real lock, App or window.
import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { installPrivateLockLauncher } from './fixtures/desktop-lock-fixture.mjs';
import { join } from 'node:path';
import { acquireDesktopLease, desktopLockAuditPath, readDesktopLockState, recoverDesktopLease } from './desktop-lock.mjs';

const posix = process.platform !== 'win32';
const real = path => new URL(path, import.meta.url);
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const leaseKey = "Symbol.for('ntilde.terminfo.desktopLease')";

const header = `
import { spawn } from 'node:child_process';
import { existsSync, writeFileSync } from 'node:fs';
const broker = globalThis[${leaseKey}];
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
const dir = process.env.FIXTURE_DIR;
`;
const runners = {
    clean: `${header}
const lease = await broker.acquire(); lease.noteLaunchAttempt(); await lease.verifyCleanup(); console.log('fixture:clean');`,
    prep: `${header}
await broker.acquire(); throw new Error('prep boom');`,
    never: `${header}
console.log('fixture:offline-help');`,
    spawnFailure: `${header}
const lease = await broker.acquire(); lease.noteLaunchAttempt();
const child = spawn('/definitely/not/an/app', [], { stdio: 'ignore' }); await new Promise(resolve => child.once('error', resolve));
await lease.verifyCleanup(); console.log('fixture:spawn-failed-known');`,
    survivor: `${header}
const lease = await broker.acquire(); lease.noteLaunchAttempt();
const marker = '--ntilde-terminfo-owner=' + lease.leaseId;
const child = spawn(process.execPath, ['-e', 'setInterval(()=>{},1000)', '--', marker], { stdio: 'ignore' });
await new Promise(resolve => child.once('spawn', resolve)); writeFileSync(dir + '/child.pid', String(child.pid)); child.unref();
try { await lease.verifyCleanup(); } catch (error) { console.error('fixture: ' + error.message); process.exitCode = 1; }`,
    launchesIfAllowed: `${header}
const lease = await broker.acquire(); writeFileSync(dir + '/launched', 'yes'); lease.noteLaunchAttempt(); await lease.verifyCleanup();`,
    signalled: `${header}
const lease = await broker.acquire(); lease.noteLaunchAttempt();
const marker = '--ntilde-terminfo-owner=' + lease.leaseId;
const child = spawn(process.execPath, ['-e', "const fs=require('fs');setInterval(()=>{if(fs.existsSync(process.argv.at(-2)))process.exit(0)},50)", '--', dir + '/stop', marker], { stdio: 'ignore' });
await new Promise(resolve => child.once('spawn', resolve)); writeFileSync(dir + '/ready', String(process.pid));
await new Promise(resolve => child.once('exit', resolve)); await lease.verifyCleanup();`,
    tampered: `${header}
await broker.acquire(); writeFileSync(dir + '/launched', 'yes');`,
};

async function fixture(action) {
    const root = mkdtempSync(join(tmpdir(), 'nt-desklaunch-'));
    try {
        const lockPath = join(root, 'sidecars', '.desktop-collection.lock');
        mkdirSync(join(root, 'repo/scripts/terminfo'), { recursive: true }); mkdirSync(join(root, 'sidecars'));
        installPrivateLockLauncher(fileURLToPath(real('../../')), join(root, 'repo'), lockPath);
        copyFileSync(real('./source-imports.mjs'), join(root, 'repo/scripts/terminfo/source-imports.mjs'));
        writeFileSync(join(root, 'repo/scripts/terminfo/owned-source-inventory.json'), JSON.stringify({ runnerSourceSha256: 'terminfo-owned-runner.mjs',
            sourceInventorySha256: 'owned-source-inventory.json', sourceImportsSourceSha256: 'source-imports.mjs' }));
        const useRunner = name => writeFileSync(join(root, 'repo/scripts/terminfo-owned-runner.mjs'), runners[name]);
        const launch = (name, env = {}, args = []) => {
            useRunner(name);
            return spawn(process.execPath, [join(root, 'repo/scripts/terminfo-owned.mjs'), ...args], { env: { ...process.env, FIXTURE_DIR: root, ...env }, stdio: ['ignore', 'pipe', 'pipe'] });
        };
        const finish = child => new Promise(resolve => {
            let stdout = '', stderr = '';
            child.stdout.on('data', chunk => { stdout += chunk; }); child.stderr.on('data', chunk => { stderr += chunk; });
            child.on('close', code => resolve({ code, stdout, stderr }));
        });
        return await action({ root, lockPath, launch, run: async (name, env, args) => finish(launch(name, env, args)), finish, useRunner });
    } finally { rmSync(root, { recursive: true, force: true }); }
}
const auditEvents = lockPath => readFileSync(desktopLockAuditPath(lockPath), 'utf8').trim().split('\n').map(JSON.parse).map(entry => entry.event);
/** Gone means absent or a zombie: a container's PID 1 may never reap an orphan, and a zombie runs nothing. */
function processGone(pid) {
    try { process.kill(pid, 0); } catch { return true; }
    const state = spawnSync('/bin/ps', ['-o', 'stat=', '-p', String(pid)], { encoding: 'utf8' });
    return state.status !== 0 || state.stdout.trim().startsWith('Z');
}
const waitFor = async (condition, label) => { for (let i = 0; i < 400; i++) { if (condition()) return; await new Promise(resolve => setTimeout(resolve, 25)); } throw new Error('timeout: ' + label); };

test('the pinned bootstrap digest names the exact lock helper bytes and the helper imports only node builtins', () => {
    const launcher = readFileSync(real('../terminfo-owned.mjs'), 'utf8');
    const pinned = /const desktopLockSha256 = '([a-f0-9]{64})';/.exec(launcher)?.[1];
    assert.equal(pinned, sha256(Buffer.from(readFileSync(real('./desktop-lock.mjs'), 'utf8').replaceAll('\r\n', '\n'))), 'update desktopLockSha256 in scripts/terminfo-owned.mjs whenever desktop-lock.mjs changes');
    const helper = readFileSync(real('./desktop-lock.mjs'), 'utf8');
    for (const match of helper.matchAll(/^import .* from '([^']+)';$/gm)) assert.match(match[1], /^node:[a-z_/]+$/);
    assert.ok(!/^\s*(?:await\s+)?import\s*\(/m.test(helper), 'no dynamic imports');
});

test('a clean launch holds the lease through the run and releases it after verified cleanup, with audit facts', { skip: !posix && 'uses ps' }, () => fixture(async ({ lockPath, run }) => {
    const result = await run('clean');
    assert.equal(result.code, 0, result.stderr); assert.match(result.stderr, /released after verified cleanup/);
    assert.equal(existsSync(lockPath), false);
    assert.deepEqual(auditEvents(lockPath), ['acquired', 'launch-attempt', 'cleanup-verified', 'released']);
}));

test('a preparation failure before any launch releases the lease and preserves the primary error', { skip: !posix && 'uses ps' }, () => fixture(async ({ lockPath, run }) => {
    const result = await run('prep');
    assert.equal(result.code, 1); assert.match(result.stderr, /prep boom/); assert.match(result.stderr, /released after verified cleanup/);
    assert.equal(existsSync(lockPath), false);
}));

test('a spawn failure with a clean marker scan is a known, released outcome', { skip: !posix && 'uses ps' }, () => fixture(async ({ lockPath, run }) => {
    const result = await run('spawnFailure');
    assert.equal(result.code, 0, result.stderr); assert.equal(existsSync(lockPath), false);
}));

test('a surviving owned process keeps the lease, refuses the next collector before it launches, and needs explicit recovery', { skip: !posix && 'uses ps' }, () => fixture(async ({ root, lockPath, run }) => {
    const first = await run('survivor');
    assert.equal(first.code, 1); assert.match(first.stderr, /DESKTOP LOCK RETAINED/); assert.match(first.stderr, /survive cleanup/);
    const survivor = Number(readFileSync(join(root, 'child.pid'), 'utf8'));
    try {
        const state = readDesktopLockState(lockPath);
        assert.equal(state.record.status, 'cleanup-failed'); assert.equal(state.record.launch, 'attempted');
        // Even with a "this collector already holds it" style environment, the next collector is refused before its runner launches anything.
        const second = await run('launchesIfAllowed', { NTILDE_TERMINFO_DESKTOP_LEASE: state.record.lease, NTILDE_TERMINFO_DESKTOP_LOCK_HELD: '1' });
        assert.equal(second.code, 1); assert.match(second.stderr, /Desktop collection lock is unavailable \(owned/); assert.match(second.stderr, /No App was launched/);
        assert.equal(existsSync(join(root, 'launched')), false);
        // The launcher process has exited but its descendant has not: a dead parent alone does not free the desktop.
        assert.throws(() => recoverDesktopLease({ lockPath, leaseId: state.record.lease, confirmDesktopIdle: true }), /survive \(pids/);
    } finally { try { process.kill(survivor, 'SIGKILL'); } catch { /* already gone */ } }
    await waitFor(() => processGone(survivor), 'survivor exit');
    const leaseId = readDesktopLockState(lockPath).record.lease;
    assert.throws(() => recoverDesktopLease({ lockPath, leaseId }), /confirm-desktop-idle/);
    recoverDesktopLease({ lockPath, leaseId, confirmDesktopIdle: true });
    assert.equal(existsSync(lockPath), false);
    const third = await run('clean');
    assert.equal(third.code, 0, third.stderr);
    assert.ok(auditEvents(lockPath).includes('retained') && auditEvents(lockPath).includes('refused') && auditEvents(lockPath).includes('recovered'));
}));

test('help and other offline runner paths never take the lease and work while another session holds it', { skip: !posix && 'uses ps' }, () => fixture(async ({ root, lockPath, run }) => {
    const holder = await acquireDesktopLease({ lockPath, entrypoint: 'other-session', worktree: root });
    try {
        const result = await run('never', {}, ['--help']);
        assert.equal(result.code, 0, result.stderr); assert.match(result.stdout, /fixture:offline-help/);
        assert.equal(readDesktopLockState(lockPath).record.lease, holder.leaseId);
        assert.ok(!auditEvents(lockPath).includes('refused'));
    } finally { holder.release(); }
}));

test('a CRLF checkout of the helper still matches the pinned LF digest and loads', { skip: !posix && 'uses ps' }, () => fixture(async ({ root, lockPath, run }) => {
    const helper = join(root, 'repo/scripts/terminfo/desktop-lock.mjs');
    writeFileSync(helper, readFileSync(helper, 'utf8').replaceAll('\n', '\r\n'));
    const result = await run('clean');
    assert.equal(result.code, 0, result.stderr); assert.equal(existsSync(lockPath), false);
}));

test('a changed lock helper is refused by the pinned digest before any lock directory exists', { skip: !posix && 'uses ps' }, () => fixture(async ({ root, lockPath, run }) => {
    writeFileSync(join(root, 'repo/scripts/terminfo/desktop-lock.mjs'), readFileSync(real('./desktop-lock.mjs'), 'utf8') + '\n// tampered\n');
    const result = await run('tampered');
    assert.equal(result.code, 1); assert.match(result.stderr, /changed trusted desktop lock helper/);
    assert.equal(existsSync(lockPath), false); assert.equal(existsSync(join(root, 'launched')), false);
}));

test('repeated signals during cleanup do not kill the launcher: the lease is released only after the owned process is gone', { skip: !posix && 'POSIX signals and ps' }, () => fixture(async ({ root, lockPath, launch, finish }) => {
    const child = launch('signalled'); const done = finish(child);
    await waitFor(() => existsSync(join(root, 'ready')), 'runner ready');
    child.kill('SIGTERM'); child.kill('SIGTERM'); child.kill('SIGHUP');
    await new Promise(resolve => setTimeout(resolve, 400));
    assert.equal(child.exitCode, null, 'the launcher survived repeated signals');
    assert.equal(readDesktopLockState(lockPath).record.status, 'held', 'still held while the owned process lives');
    writeFileSync(join(root, 'stop'), '');
    const result = await done;
    assert.equal(result.code, 1, result.stderr); assert.match(result.stderr, /released after verified cleanup/);
    assert.equal(existsSync(lockPath), false);
}));

test('the runner takes the lease before any output, App or window action, and reports launch and cleanup through it', () => {
    const runner = readFileSync(real('../terminfo-owned-runner.mjs'), 'utf8');
    const at = text => { const index = runner.indexOf(text); assert.ok(index > 0, text); return index; };
    assert.ok(at('lease = await acquireDesktopLease(() => interruption);') < at("output = mkdtempSync(join(repo, 'artifacts', 'terminfo', 'owned-')"));
    assert.ok(at('lease = await acquireDesktopLease(() => interruption);') < at('app = spawn(executable'));
    assert.ok(at('lease.noteLaunchAttempt();') < at('app = spawn(executable'));
    assert.ok(at('const runId = lease.leaseId;') > at('lease = await acquireDesktopLease(() => interruption);'));
    const cleanup = runner.slice(at('async function cleanupOwnedRun'), at('function retainPreparationFailure'));
    assert.ok(cleanup.includes('await lease.verifyCleanup()') && cleanup.includes('workerError ?? appError ?? survivorError'));
    assert.ok(cleanup.indexOf('if (!workerError && !appError)') > 0 && cleanup.indexOf('if (!workerError && !appError)') < cleanup.indexOf('await lease.verifyCleanup()'),
        'a failed worker or App termination never reaches the scan that could mark cleanup verified');
    assert.ok(runner.includes("process.on('SIGHUP', onHup)") && runner.includes("process.removeListener('SIGHUP', onHup)") && /\['SIGINT', 'SIGTERM', 'SIGHUP'\]\.includes\(process\.env\.NTILDE_TERMINFO_PENDING_SIGNAL\)/.test(runner));
    assert.equal(runner.match(/\bspawn\(/g).length, 1, 'one App spawn site, and it is the guarded one');
    assert.ok(!/process\.env\.[A-Z_]*(?:LOCK|LEASE)/.test(runner) && !/process\.env\[[^\]]*(?:LOCK|LEASE)/.test(runner), 'no environment value can stand in for the lease');
    assert.ok(!/\.release\(\)|\.retain\(/.test(runner), 'only the launcher releases or retains');
    assert.ok(runner.includes('must be started through scripts/terminfo-owned.mjs'));
});

test('the launcher releases only through the broker after the runner returns, and signals stay handled until then', () => {
    const launcher = readFileSync(real('../terminfo-owned.mjs'), 'utf8');
    const settle = launcher.indexOf('settleDesktopLease(broker)'), remove = launcher.indexOf("process.removeListener('SIGINT', onInt)");
    assert.ok(settle > 0 && remove > settle, 'listeners are removed only after the lease is settled');
    assert.ok(launcher.indexOf("process.on('SIGHUP', onHup)") > 0);
    assert.ok(/NTILDE_TERMINFO_PENDING_SIGNAL/.test(launcher));
    assert.ok(!/process\.env\.[A-Z_]*(?:LOCK|LEASE)/.test(launcher.replaceAll('NTILDE_TERMINFO_DESKTOP_LOCK_WAIT_SECONDS', '')));
});

test('the common-core driver acquires before creating output and persists every launch intent before the launch it guards', () => {
    const driver = readFileSync(real('./common-core-driver.ts'), 'utf8');
    const at = text => { const index = driver.indexOf(text); assert.ok(index > 0, text); return index; };
    assert.ok(at('await acquireDesktopLease({') < at('mkdirSync(output'));
    const lastLaunchMark = text => driver.lastIndexOf('markLaunchAttempt();', at(text));
    for (const launch of ['launch = launchPreparedCli(', "execFileSync('/usr/bin/osascript', ['-e', apple]", 'applicationState = spawnCommonCoreApplication(executable']) {
        const mark = lastLaunchMark(launch);
        assert.ok(mark > 0 && at(launch) - mark < 400, launch + ' must directly follow markLaunchAttempt()');
    }
    assert.equal(driver.match(/launchAttempted = true/g).length, 1);
    assert.ok(at('await lease.verifyCleanup()') < at('finishCleanupRecord(failures, ownedWindowAbsent, workerExited);'), 'the marker scan runs before the record is finished and the lease released');
    assert.ok(!driver.includes('attestCleanup'), 'the driver no longer self-attests cleanup');
    assert.ok(at('if (failures.length === 0) {') < at('await lease.verifyCleanup()'), 'a failed earlier check never reaches the scan that could mark cleanup verified');
    assert.ok(driver.indexOf('lease.release()') < at('const upstreamRevision'), 'the only earlier release is the pre-launch output failure');
    assert.ok(at('lease.retain(failures.join') > 0);
    assert.ok(!/\.desktop-collection\.lock/.test(driver), 'the driver no longer hand-rolls its own lock file');
    assert.ok(/markerKinds: appId === 'ntilde' \? \['owner', 'worker'\] : \['worker'\]/.test(driver) && driver.includes("'--ntilde-terminfo-worker=' + runId"),
        'the driver records only the markers it puts on its processes, and the worker command line carries its marker');
});
