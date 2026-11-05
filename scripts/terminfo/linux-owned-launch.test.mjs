import test from 'node:test';
import assert from 'node:assert/strict';
import { launchWithIo, copyLaunchInput, copyLaunchObservation } from './linux-launch/core.mjs';
const run = 'a'.repeat(32), sha = 'b'.repeat(64);
const file = { dev: '1', ino: '2', rdev: '0', uid: '1000', mode: '33216', size: '100', mtimeNs: '1', ctimeNs: '2' };
const dir = path => ({ path, device: { dev: '1', ino: '3', uid: '1000', mode: '16832' } });
const artifact = () => ({ directory: dir('/private/app'), sha256: { app: sha, managed: sha, pty: sha } });
const fixture = () => ({ runId: run, uid: 1000, scratch: dir('/private/run'), appDataDirectory: dir('/private/run/appdata'),
    initialSettingsSha256: sha, locale: 'C.UTF-8', appVersion: '0.13.0' });
const python = () => ({ path: '/usr/bin/python3', sha256: sha });
const observation = () => ({ childPid: null, forked: false, pidfdOpened: false, preExecReady: false, gateReleased: false, execPipeClosed: false,
    admitted: false, exitObserved: false, childReaped: false, preExecChildReaped: false, appReaped: false, wait: null,
    childErrno: null, attempts: [], pidfdClosed: false, childPipesClosed: false, appLogClosed: false, appLogBytes: 0,
    aborted: false, errors: [] });
/** Model the fixed helper/provider boundaries deterministically, including actual-release facts after refusal. */
function environment(change = {}) {
    let clock = 1000000000n, phase = 'python', state = observation(), finished = 0;
    const calls = [], processReads = [];
    const helperArgv = ['/usr/bin/python3.14', '-I', '-S', '-B', '/proc/self/fd/4', '--run-id=' + run];
    const appArgv = ['/private/app/Ntilde', '--ntilde-terminfo-owner=' + run, '--ntilde-terminfo-private-clipboard=' + run];
    const channel = { resources: () => ({ helperPid: 301, helperArgv, python: { path: helperArgv[0], file },
        app: { path: appArgv[0], file }, appArgv }), snapshot: () => ({ closeConfirmed: finished > 0, observation: structuredClone(state) }),
        check() { change.check?.(); },
        async request(command) {
            calls.push(command.op); await change.before?.(command, state);
            if (command.op === 'prepare') Object.assign(state, { childPid: 302, forked: true, pidfdOpened: true, preExecReady: true });
            if (command.op === 'release') { state.gateReleased = true; state.execPipeClosed = true; phase = 'app'; }
            if (command.op === 'admitApp') state.admitted = true;
            if (['stop', 'abort'].includes(command.op)) Object.assign(state, { exitObserved: true, childReaped: true,
                appReaped: state.admitted, wait: { source: 'P_PIDFD', pid: 302, uid: 1000, code: 1, status: 0 },
                attempts: [{ signal: 'TERM', sent: true, errno: null }], pidfdClosed: true, childPipesClosed: true, appLogClosed: true });
            change.after?.(command, state);
            return { clockNs: '2000000000', observation: structuredClone(state) };
        }, async finish() { finished++; change.finish?.(); return structuredClone(state); } };
    const io = { now: () => clock, currentUid: () => 1000, selfPid: () => 9,
        open: async () => channel,
        process(pid) {
            processReads.push(pid);
            const helper = pid === 301, app = phase === 'app' && !helper;
            const result = { platform: 'linux', pid, ppid: helper ? 9 : 301, startTimeTicks: helper ? '77' : '88', processGroupId: pid, sessionId: pid, controllingDevice: '0',
                executable: app ? '/private/app/Ntilde' : '/usr/bin/python3.14', executableFile: { ...file },
                argv: [...(app ? appArgv : helperArgv)], uids: [1000, 1000, 1000, 1000] };
            change.process?.(result, phase); return result;
        } };
    return { io, calls, processReads, state: () => state, finished: () => finished, advance: ns => { clock += ns; },
        launch: () => launchWithIo(io, artifact(), fixture(), python(), { deadlineNs: clock + 4000000000n, lifetimeMs: 30000 }),
        deadline: () => ({ deadlineNs: clock + 4000000000n }) };
}

test('only exact private artifact/fixture/plain-data expectations are admitted', () => {
    assert.equal(copyLaunchInput(artifact(), fixture(), python(), 1000).fixture.runId, run);
    for (const mutate of [v => { v.runId = 'wrong'; }, v => { v.uid = 1001; }, v => { v.locale = 'C'; },
        v => { v.appDataDirectory.path = '/personal'; }, v => { v.scratch.path = '/private/../personal'; },
        v => { v.scratch.device.mode = '16877'; }, v => { v.extra = true; }]) {
        const value = fixture(); mutate(value); assert.throws(() => copyLaunchInput(artifact(), value, python(), 1000));
    }
    const accessor = fixture(); Object.defineProperty(accessor, 'runId', { get() { throw new Error('Accessor ran'); }, enumerable: true });
    assert.throws(() => copyLaunchInput(artifact(), accessor, python(), 1000), /accessor/);
    assert.throws(() => copyLaunchInput({ ...artifact(), argv: ['other'] }, fixture(), python(), 1000));
    assert.throws(() => copyLaunchInput(artifact(), fixture(), { path: '/tmp/python', sha256: sha }, 1000));
});

test('fork/provider/exec/provider/admission sequence preserves original generation and parent', async () => {
    const env = environment(); const lease = await env.launch();
    assert.deepEqual(env.calls, ['clock', 'prepare', 'observe', 'release', 'observe', 'admitApp']);
    assert.deepEqual(env.processReads, [301, 302, 301, 302]);
    assert.equal(lease.snapshot().acceptedPreExec.child.startTimeTicks, '88');
    assert.equal(lease.snapshot().acceptedApp.child.executable, '/private/app/Ntilde');
    assert.equal(lease.snapshot().observation.appReaped, false);
    const result = await lease.stop(env.deadline());
    assert.equal(result.observation.appReaped, true); assert.equal(result.observation.wait.status, 0);
    assert.strictEqual(await lease.stop(env.deadline()), result);
    assert.equal(env.calls.filter(v => v === 'stop').length, 1); assert.equal(env.finished(), 1);
});

for (const stage of ['python', 'app']) for (const defect of ['pid', 'birth', 'parent', 'uid', 'argv', 'executable', 'file', 'session', 'group', 'ctty']) {
    test('refuses ' + stage + ' original identity defect: ' + defect, async () => {
        const env = environment({ process(value, current) {
            if (value.pid === 301 || current !== stage) return;
            if (defect === 'pid') value.pid++;
            if (defect === 'birth') value.startTimeTicks = stage === 'app' ? '99' : '';
            if (defect === 'parent') value.ppid++;
            if (defect === 'uid') value.uids[2]++;
            if (defect === 'argv') value.argv = ['lookalike'];
            if (defect === 'executable') value.executable = '/other';
            if (defect === 'file') value.executableFile.ino++;
            if (defect === 'session') value.sessionId++;
            if (defect === 'group') value.processGroupId++;
            if (defect === 'ctty') value.controllingDevice = '123';
        } });
        await assert.rejects(env.launch(), error => {
            assert.equal(error.linuxLaunch.aborted, true);
            if (stage === 'python') assert.equal(error.linuxLaunch.observation.gateReleased, false);
            else assert.equal(error.linuxLaunch.observation.gateReleased, true);
            return true;
        });
        assert.equal(env.calls.includes('admitApp'), false);
    });
}

test('helper replacement across pre/post exec refuses App admission', async () => {
    const env = environment({ process(value, phase) { if (value.pid === 301 && phase === 'app') value.startTimeTicks = 'changed'; } });
    await assert.rejects(env.launch()); assert.equal(env.calls.includes('admitApp'), false);
});

test('CLOEXEC pipe EOF cannot replace independent App process facts', async () => {
    const env = environment({ process(value, phase) { if (value.pid === 302 && phase === 'app') value.executable = '/usr/bin/python3.14'; } });
    await assert.rejects(env.launch()); assert.equal(env.calls.includes('admitApp'), false);
});

test('original birth, UID, executable and argv are rechecked before normal stop', async () => {
    let drift = false; const env = environment({ process(value) { if (drift && value.pid === 302) value.startTimeTicks = '999'; } });
    const lease = await env.launch(); drift = true;
    await assert.rejects(lease.stop(env.deadline()), error => { assert.equal(error.linuxLaunch.aborted, true); return true; });
    assert.equal(env.calls.includes('stop'), false); assert.equal(env.finished(), 1);
});

test('abort uses only fixed original-target operation and cannot become a successful settlement', async () => {
    const env = environment(); const lease = await env.launch();
    const first = await lease.abortCleanup(env.deadline()).catch(error => error);
    assert.equal(first.linuxLaunch.aborted, true);
    assert.equal(first.linuxLaunch.observation.appReaped, true);
    await assert.rejects(lease.stop(env.deadline()), error => error === first);
    assert.equal(env.calls.filter(op => op === 'abort').length, 1);
});

test('consuming native wait is necessary; readiness/EOF/ECHILD cannot manufacture App reaping', () => {
    const state = { ...observation(), childPid: 302, forked: true, pidfdOpened: true, preExecReady: true, gateReleased: true, admitted: true,
        exitObserved: true, appReaped: true };
    assert.throws(() => copyLaunchObservation(state));
    assert.throws(() => copyLaunchObservation({ ...state, childReaped: true, wait: { source: 'ECHILD', pid: 302, uid: 1000, code: 1, status: 0 } }));
    assert.throws(() => copyLaunchObservation({ ...state, childReaped: true, wait: { source: 'waitpid-preexec', pid: 302, uid: null, code: null, status: 0 } }));
});

test('helper close error is retained with primary identity failure and cannot settle cleanly', async () => {
    const env = environment({ process(value, phase) { if (value.pid === 302 && phase === 'app') value.ppid = 777; },
        finish() { throw new Error('Controlled close failure'); } });
    await assert.rejects(env.launch(), error => { assert.equal(error.linuxLaunch.aborted, true); assert.equal(env.finished(), 1); return true; });
});

test('expired initial deadline cannot open any helper', async () => {
    const env = environment(); let opened = false; env.io.open = () => { opened = true; throw new Error('Must not open'); };
    await assert.rejects(launchWithIo(env.io, artifact(), fixture(), python(), { deadlineNs: 1n, lifetimeMs: 30000 }));
    assert.equal(opened, false);
});

test('overlap makes the in-flight observation fail after actual source facts are retained', async () => {
    let release; let hold = false; const blocked = new Promise(resolve => { release = resolve; });
    const env = environment({ async before(command) { if (hold && command.op === 'observe') await blocked; } });
    const lease = await env.launch(); hold = true; const first = lease.observe(env.deadline());
    await Promise.resolve(); await Promise.resolve();
    await assert.rejects(lease.observe(env.deadline()), /Overlapping/); release();
    await assert.rejects(first, /Overlapping/);
    await assert.rejects(lease.abortCleanup(env.deadline()));
});

test('budget exhausted during pre-exec provider read never sends release', async () => {
    const env = environment({ process(value, stage) { if (value.pid === 302 && stage === 'python') env.advance(4000000001n); } });
    await assert.rejects(env.launch(), error => {
        assert.equal(error.linuxLaunch.aborted, true);
        assert.equal(error.linuxLaunch.observation.gateReleased, false);
        return true;
    });
    assert.equal(env.calls.includes('release'), false);
});

test('a late release response retains actual release facts while refusing admission and retry', async () => {
    const env = environment({ after(command) { if (command.op === 'release') env.advance(4000000001n); } });
    await assert.rejects(env.launch(), error => {
        assert.equal(error.linuxLaunch.aborted, true);
        assert.equal(error.linuxLaunch.observation.gateReleased, true);
        assert.equal(error.linuxLaunch.acceptedApp, null);
        return true;
    });
    assert.equal(env.calls.filter(op => op === 'release').length, 1);
    assert.equal(env.calls.includes('admitApp'), false);
});

for (const deadlineNs of [1n, 'malformed']) {
    test('invalid settlement deadline closes once and remains aborted on repeated cleanup: ' + deadlineNs, async () => {
        const env = environment(); const lease = await env.launch();
        const first = await lease.stop({ deadlineNs }).catch(error => error);
        assert.equal(env.finished(), 1);
        assert.equal(first.linuxLaunch.aborted, true);
        assert.equal(first.linuxLaunch.helper.closeConfirmed, true);
        await assert.rejects(lease.abortCleanup(env.deadline()), error => error === first);
        assert.equal(env.finished(), 1);
        assert.equal(env.calls.includes('stop'), false);
    });
}

test('normal settlement cannot accept independent exit without an actual original-reference signal attempt', async () => {
    const env = environment({ after(command, state) { if (command.op === 'stop') state.attempts = []; } });
    const lease = await env.launch();
    await assert.rejects(lease.stop(env.deadline()), error => {
        assert.equal(error.linuxLaunch.aborted, true);
        assert.equal(error.linuxLaunch.observation.appReaped, true);
        assert.deepEqual(error.linuxLaunch.observation.attempts, []);
        return true;
    });
});
