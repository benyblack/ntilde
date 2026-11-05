// Source-built private ELF/process tests only; never launch the actual App or a desktop.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import * as fs from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { launchLinuxOwnedApp, createLinuxLaunchDeadline } from './linux-owned-launch.mjs';
import { launchWithIo, copyLaunchInput } from './linux-launch/core.mjs';
import { conservativeDeadline } from './linux-lifecycle/core.mjs';
import { linuxOutputBudget } from './linux-owned-output-core.mjs';
import { openLaunchChannel, helperSha256 } from './linux-launch/platform.mjs';
import { readLinuxProcessFacts, readLinuxOwnedChildFacts } from './linux-process-provider.mjs';
const source = fileURLToPath(new URL('./linux-launch/fixture.c', import.meta.url));
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const stable = stat => Object.fromEntries(['dev', 'ino', 'uid', 'mode'].map(key => [key, stat[key].toString()]));
const directory = path => ({ path, device: stable(fs.lstatSync(path, { bigint: true })) });
const phase = () => ({ deadlineNs: createLinuxLaunchDeadline(4000) });
/** Create a source-known fixture input exclusively; it never overwrites an existing file. */
function privateFile(path, bytes) { fs.writeFileSync(path, bytes, { flag: 'wx', mode: 0o600 }); return digest(bytes); }
/** Identify the already installed trusted compiler without installing or downloading tools. */
function compilerPin() {
    const path = fs.realpathSync('/usr/bin/cc'); const stat = fs.statSync(path, { bigint: true });
    if (stat.uid !== 0n || stat.mode & 0o022n || stat.size > 32n * 1024n * 1024n || !stat.isFile()) throw new Error('Expected the already installed root-owned fixture compiler.');
    return { path, sha256: digest(fs.readFileSync(path)) };
}
/** Build only the retained private C fixture with fixed flags and bounded compiler output. */
function compile(path, mode, compiler) {
    const result = spawnSync(compiler.path, ['-std=c11', '-O2', '-Wall', '-Wextra', '-Werror', '-DMODE=' + mode, source, '-o', path],
        { env: { PATH: '/usr/bin:/bin', LC_ALL: 'C.UTF-8' }, encoding: 'utf8', timeout: 30000, maxBuffer: 4096 });
    if (result.status !== 0 || result.error) throw new Error('Private fixture compile failed: ' + result.stderr);
    fs.chmodSync(path, 0o700); return digest(fs.readFileSync(path));
}
/** Create a private ELF role and explicit opaque test companion pins, never a real App artifact. */
function prepare(root, name, mode, compiler) {
    const scratch = join(root, name); fs.mkdirSync(scratch, { mode: 0o700 });
    const app = join(scratch, 'app'), appdata = join(scratch, 'appdata');
    fs.mkdirSync(app, { mode: 0o700 }); fs.mkdirSync(appdata, { mode: 0o700 });
    const appSha = compile(join(app, 'Ntilde'), mode, compiler);
    // Explicitly test-owned opaque companion pins, not real managed/runtime qualification.
    const managed = privateFile(join(app, 'Ntilde.dll'), Buffer.from('private fixture companion: managed\n'));
    const pty = privateFile(join(app, 'librusty_pty.so'), Buffer.from('private fixture companion: native\n'));
    const settings = privateFile(join(appdata, 'settings.json'), Buffer.from('{"privateFixture":true}\n'));
    const runId = digest(Buffer.from(name)).slice(0, 32);
    return { artifact: { directory: directory(app), sha256: { app: appSha, managed, pty } },
        fixture: { runId, uid: process.getuid(), scratch: directory(scratch), appDataDirectory: directory(appdata),
            initialSettingsSha256: settings, locale: 'C.UTF-8', appVersion: '0.13.0' },
        log: join(scratch, 'app-launch.log') };
}
/** Observe only the known fixture log until a bounded source-defined marker appears. */
async function awaitLog(path, predicate) {
    const until = process.hrtime.bigint() + 3000000000n;
    /** Poll only this known log; another sample cannot renew the original cutoff. */
    function sample() {
        if (process.hrtime.bigint() >= until) throw new Error('Private fixture did not publish its bounded expected log.');
        const data = fs.existsSync(path) ? fs.readFileSync(path, 'utf8') : '';
        if (predicate(data)) return data;
        return new Promise(resolve_ => setTimeout(resolve_, 5)).then(sample);
    }
    return sample();
}
/** Use the public production boundary, or the explicit internal gate-observation seam for its real control. */
function launch(input, python, internal = false) {
    const options = { ...phase(), lifetimeMs: 30000 };
    if (!internal) return launchLinuxOwnedApp(input.artifact, input.fixture, python, options);
    const process_ = (pid, timeoutMs) => {
        const facts = readLinuxProcessFacts(pid, { timeoutMs });
        if (facts.ppid !== process.pid && facts.executable === fs.realpathSync(python.path)) {
            const log = fs.readFileSync(input.log, 'utf8');
            assert.equal(log.includes('READY_PRIVATE_ELF'), false, 'App executed before public-provider admission.');
        }
        return facts;
    };
    return launchWithIo({ now: () => process.hrtime.bigint(), currentUid: () => process.getuid(), selfPid: () => process.pid,
        process: process_, open: openLaunchChannel }, input.artifact, input.fixture, python, options);
}
/** Observe an expected-fatal fixture until its original cutoff without renewing or retrying actions. */
async function observeFailure(lease, until) {
    if (process.hrtime.bigint() >= until) return null;
    try { await lease.observe(phase()); }
    catch (error) { return error; }
    await new Promise(resolve_ => setTimeout(resolve_, 5));
    return observeFailure(lease, until);
}
/** Retain genuine consuming wait and sticky refusal for early exit/output overflow. */
async function rejectedFixture(input, python, mode) {
    let lease, failure, snapshot;
    try {
        lease = await launch(input, python);
        failure = await observeFailure(lease, process.hrtime.bigint() + 4000000000n);
    } catch (error) { failure = error; snapshot = error.linuxLaunch; }
    finally {
        if (lease) {
            const error = await lease.abortCleanup(phase()).catch(error => error);
            snapshot = error.linuxLaunch ?? lease.snapshot();
        }
    }
    assert.ok(failure, 'Expected the source-owned fixture to abort on early exit/overflow.');
    assert.equal(snapshot.aborted, true);
    assert.equal(snapshot.observation?.childReaped, true);
    assert.ok(snapshot.observation.appLogBytes <= 1048576);
    if (mode !== 4) assert.equal(snapshot.observation.wait.status, mode === 3 ? 7 : 0);
    return snapshot;
}
/** Exercise owned ELF/PTY/kernel/EOF scenarios and an independent held sentinel; never launch Ntilde or a desktop. */
export async function runLinuxLaunchSelfTest(parent = tmpdir()) {
    if (process.platform !== 'linux') throw new Error('The actual launch fixture requires Linux.');
    const root = fs.realpathSync(fs.mkdtempSync(join(parent, 'ntilde-linux-launch-'))); fs.chmodSync(root, 0o700);
    const compiler = compilerPin(), pythonPath = fs.realpathSync('/usr/bin/python3');
    const python = { path: '/usr/bin/python3', sha256: digest(fs.readFileSync(pythonPath)) };
    const results = [], fixtures = [];
    const retain = () => fs.writeFileSync(join(root, 'selftest-progress.json'), JSON.stringify({ results, fixtures, helperSha256 }, null, 2) + '\n', { mode: 0o600 });
    /** Require genuine original-App-role wait/resource closure for one live private fixture. */
    async function live(name, mode, internal = false) {
        const input = prepare(root, name, mode, compiler); fixtures.push(input);
        let lease;
        try {
            lease = await launch(input, python, internal);
            assert.equal(lease.snapshot().observation.admitted, true);
            await awaitLog(input.log, text => text.includes('READY_PRIVATE_ELF'));
            await lease.observe(phase());
            if (mode === 5) {
                const log = await awaitLog(input.log, value => value.includes('fixtureWorkerPid'));
                const record = JSON.parse(log.split('\n').find(line => line.startsWith('{')));
                const app = lease.snapshot().acceptedApp.child;
                const facts = readLinuxOwnedChildFacts({ parentPid: app.pid, childPid: record.fixtureWorkerPid, uid: process.getuid(),
                    runId: input.fixture.runId, executable: app.executable,
                    argv: [app.executable, '--ntilde-terminfo-worker=' + input.fixture.runId] }, { timeoutMs: 4000 });
                assert.equal(facts.child.pty.descriptors[1].path, record.pty); assert.equal(facts.child.pty.descriptors.length, 2);
            }
            const result = await lease.stop(phase());
            assert.equal(result.observation.appReaped, true); assert.equal(result.observation.wait.source, 'P_PIDFD');
            assert.equal(result.observation.wait.pid, result.acceptedApp.child.pid);
            assert.equal(result.helper.closeConfirmed, true);
            assert.equal(result.observation.attempts.some(item => item.signal === 'KILL'), mode === 1);
            assert.equal(result.observation.wait.status, mode === 1 ? 9 : 0);
            results.push({ name, status: 'passed', observation: result.observation, acceptedApp: result.acceptedApp, helper: result.helper }); retain();
        } finally {
            if (lease && !lease.snapshot().observation.appReaped) await lease.abortCleanup(phase()).catch(() => {});
        }
    }
    const sentinelInput = prepare(root, 'independent-sentinel', 0, compiler); fixtures.push(sentinelInput);
    const sentinel = await launch(sentinelInput, python);
    const sentinelBefore = sentinel.snapshot().acceptedApp;
    let sentinelAfter, sentinelClosed, primary;
    try {
        await live('ordinary-source-owned', 0, true);
        await live('term-resistant', 1);
        await live('private-pty-owner', 5);
        /** Keep fixed cases serial so private cleanup cannot hide another case's exit. */
        async function refusal(name, mode) {
            const input = prepare(root, name, mode, compiler); fixtures.push(input);
            const snapshot = await rejectedFixture(input, python, mode);
            results.push({ name, status: 'expected-refusal', snapshot }); retain();
        }
        await refusal('instant-zero', 2);
        await refusal('instant-seven', 3);
        await refusal('log-overflow', 4);
        const blocked = prepare(root, 'eof-before-exec', 0, compiler); fixtures.push(blocked);
        const firstPhase = linuxOutputBudget(phase(), () => process.hrtime.bigint());
        const channel = await openLaunchChannel(copyLaunchInput(blocked.artifact, blocked.fixture, python, process.getuid()), firstPhase);
        const clock = await channel.request({ op: 'clock' }, firstPhase);
        const prepared = await channel.request({ op: 'prepare', deadlineNs: conservativeDeadline(clock.clockNs, process.hrtime.bigint(), firstPhase.deadlineNs),
            lifetimeDeadlineNs: (BigInt(clock.clockNs) + 30000000000n).toString() }, firstPhase);
        assert.equal(prepared.observation.gateReleased, false);
        const beforeExec = await channel.finish(firstPhase);
        const eofBefore = { name: 'coordinator-eof-before-exec', status: 'observed', observation: beforeExec, helper: channel.snapshot() };
        results.push(eofBefore); retain();
        assert.equal(beforeExec.aborted, true); assert.equal(beforeExec.preExecChildReaped, true);
        assert.equal(beforeExec.appReaped, false); assert.equal(beforeExec.gateReleased, false);
        assert.equal(fs.readFileSync(blocked.log, 'utf8').includes('READY_PRIVATE_ELF'), false);
        eofBefore.status = 'expected-abort'; retain();

        const afterInput = prepare(root, 'eof-after-admission', 0, compiler); fixtures.push(afterInput);
        let captured;
        const afterLease = await launchWithIo({ now: () => process.hrtime.bigint(), currentUid: () => process.getuid(), selfPid: () => process.pid,
            process: (pid, timeoutMs) => readLinuxProcessFacts(pid, { timeoutMs }),
            open: async (...args) => { captured = await openLaunchChannel(...args); return captured; } },
            afterInput.artifact, afterInput.fixture, python, { ...phase(), lifetimeMs: 30000 });
        const afterExec = await captured.finish(linuxOutputBudget(phase(), () => process.hrtime.bigint()));
        assert.equal(afterExec.aborted, true); assert.equal(afterExec.appReaped, true);
        assert.equal(afterExec.wait.source, 'P_PIDFD'); assert.equal(afterLease.snapshot().aborted, true);
        results.push({ name: 'coordinator-eof-after-admission', status: 'expected-abort', observation: afterExec, helper: captured.snapshot() }); retain();
    } catch (error) { primary = error; }
    finally {
        try {
            sentinelAfter = (await sentinel.observe(phase())).acceptedApp;
            assert.equal(sentinelAfter.child.startTimeTicks, sentinelBefore.child.startTimeTicks);
            sentinelClosed = await sentinel.stop(phase());
            assert.equal(sentinelClosed.observation.appReaped, true);
        } catch (error) {
            await sentinel.abortCleanup(phase()).catch(() => {});
            primary = new AggregateError([primary, error].filter(Boolean), 'Private scenario/sentinel cleanup failed.');
        }
    }
    if (primary) throw primary;
    const proof = { qualification: 'source-owned-launch-fixtures-only', actualAppLaunched: false, desktopQualified: false,
        root, compiler, python, helperSha256, sentinel: { before: sentinelBefore, after: sentinelAfter, closed: sentinelClosed.observation }, fixtureSourceSha256: digest(fs.readFileSync(source)),
        fixtures: fixtures.map(value => ({ name: value.fixture.scratch.path, artifact: value.artifact, fixture: value.fixture })), results };
    fs.writeFileSync(join(root, 'selftest.json'), JSON.stringify(proof, null, 2) + '\n', { flag: 'wx', mode: 0o600 });
    return proof;
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    const output = process.argv[2] ?? 'linux-launch-foundation.json';
    if (output !== 'linux-launch-foundation.json') throw new Error('Use the fixed private fixture report filename.');
    try {
        const value = await runLinuxLaunchSelfTest();
        fs.writeFileSync(output, JSON.stringify(value, null, 2) + '\n', { flag: 'wx', mode: 0o600 });
        console.log(JSON.stringify({ qualification: value.qualification, scenarios: value.results.length, output }));
    } catch (error) { console.error(error); process.exitCode = 1; }
}
