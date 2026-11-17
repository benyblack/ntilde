import assert from 'node:assert/strict';
import { test } from 'node:test';
import { assertPersistentDaemon, assertProcessOwnership, discoverPersistentDaemons, environmentHas, isDaemonCommand, readPersistentDaemon, terminateRecordedDaemon } from './ownership.mjs';

const runId = 'a'.repeat(32), appData = '/private/ntilde-qa/nt-abc123/appdata', executable = '/sidecar/Ntilde';
const daemonFacts = () => ({
    record: { pid: 300, ppid: 1, uid: 501, tty: '??', executable },
    text: `${executable} mux serve --idle-exit-minutes 10 NTILDE_APPDATA_ROOT=${appData} TERMINFO_RUN_ID=${runId} HOME=/Users/test`,
    birth: 'Tue Nov 17 16:07:30 2026', appBirth: 'Tue Nov 17 16:07:29 2026',
    descriptor: { pid: 300, processName: 'Ntilde', endpoint: appData + '/mux/sock' },
});
const persistentProof = () => ({
    app: { pid: 100, uid: 501, executable }, worker: { pid: 301, ppid: 300, uid: 501, tty: 'ttys012' },
    ownerPid: 100, uid: 501, executable, runId, appCommand: `${executable} --ntilde-terminfo-owner=${runId}`, tty: '/dev/ttys012',
    daemon: daemonFacts(), appData,
});

test('environmentHas matches one exact NAME=value assignment as a whole, spaces included', () => {
    assert.equal(environmentHas('cmd A=1 B=2', 'A', '1'), true);
    assert.equal(environmentHas('cmd A=1 A=1', 'A', '1'), false);
    assert.equal(environmentHas('cmd A=12', 'A', '1'), false);
    assert.equal(environmentHas('cmd XA=1', 'A', '1'), false);
    assert.equal(environmentHas('cmd', 'A', '1'), false);
    assert.equal(environmentHas('cmd A=/Users/some one/x B=2', 'A', '/Users/some one/x'), true);
    assert.equal(environmentHas('cmd A=/Users/some one/x', 'A', '/Users/some one/x'), true);
    assert.equal(environmentHas('cmd A=/Users/some one/xy B=2', 'A', '/Users/some one/x'), false);
});

test('isDaemonCommand requires the exact launched executable (spaces included) serving', () => {
    assert.equal(isDaemonCommand('/a b/Ntilde mux serve --x', '/a b/Ntilde'), true);
    assert.equal(isDaemonCommand('/a b/Ntilde mux serve', '/a b/Ntilde'), true);
    assert.equal(isDaemonCommand('/a b/Ntilde mux servers', '/a b/Ntilde'), false);
    assert.equal(isDaemonCommand('/a b/Ntilde mux ls', '/a b/Ntilde'), false);
    assert.equal(isDaemonCommand('/a b/NtildeX mux serve', '/a b/Ntilde'), false);
});

test('the daemon of a sidecar whose path contains spaces is accepted', () => {
    const spaced = '/Users/some one/sidecar/Ntilde', proof = persistentProof(), root = '/Users/some one/.ntilde-qa/nt-abc/appdata';
    proof.executable = proof.app.executable = proof.daemon.record.executable = spaced;
    proof.appCommand = `${spaced} --ntilde-terminfo-owner=${runId}`;
    proof.appData = root;
    proof.daemon.text = `${spaced} mux serve NTILDE_APPDATA_ROOT=${root} TERMINFO_RUN_ID=${runId} HOME=/Users/some one`;
    proof.daemon.descriptor.endpoint = root + '/mux/sock';
    assert.doesNotThrow(() => assertProcessOwnership(proof));
});

test('accepts the worker of the owned multiplexer daemon', () => {
    assert.doesNotThrow(() => assertProcessOwnership(persistentProof()));
});

test('a persistent worker must not be mistaken for a direct child of the app', () => {
    const proof = persistentProof();
    delete proof.daemon;
    assert.throws(() => assertProcessOwnership(proof), /direct child of the owned app/);
});

for (const [name, change, message] of [
    ['a worker under another parent', p => { p.worker.ppid = 999; }, /owned multiplexer daemon/],
    ['a daemon of another user', p => { p.daemon.record.uid = 502; }, /another user/],
    ['another executable', p => { p.daemon.record.executable = '/other/Ntilde'; }, /launched executable/],
    ['a command that is not mux serve', p => { p.daemon.text = p.daemon.text.replace('mux serve', 'mux ls'); }, /launched executable/],
    ['no serve arguments', p => { p.daemon.text = `${executable} mux NTILDE_APPDATA_ROOT=${appData} TERMINFO_RUN_ID=${runId}`; }, /launched executable/],
    ['another app-data root', p => { p.daemon.text = p.daemon.text.replace(appData, '/private/ntilde-qa/nt-other/appdata'); }, /environment differs/],
    ['another run identity', p => { p.daemon.text = p.daemon.text.replace(runId, 'b'.repeat(32)); }, /environment differs/],
    ['a repeated root', p => { p.daemon.text += ' NTILDE_APPDATA_ROOT=' + appData; }, /environment differs/],
    ['a descriptor naming another pid', p => { p.daemon.descriptor.pid = 301; }, /descriptor/],
    ['a descriptor endpoint outside the private root', p => { p.daemon.descriptor.endpoint = '/tmp/elsewhere/sock'; }, /descriptor/],
    ['a descriptor endpoint beside the root', p => { p.daemon.descriptor.endpoint = appData + '-sibling/sock'; }, /descriptor/],
    ['a daemon older than the app', p => { p.daemon.birth = 'Tue Nov 17 16:07:28 2026'; }, /predates/],
    ['an unreadable birth', p => { p.daemon.birth = 'garbage'; }, /predates/],
]) {
    test(`refuses ${name}`, () => {
        const proof = persistentProof();
        change(proof);
        assert.throws(() => assertProcessOwnership(proof), message);
    });
}

test('assertPersistentDaemon is the only place the daemon relation is decided', () => {
    const { app, worker, daemon } = persistentProof();
    assert.doesNotThrow(() => assertPersistentDaemon({ app, worker, daemon, executable, uid: 501, runId, appData }));
    assert.throws(() => assertPersistentDaemon({ app, worker, daemon, executable: '/other', uid: 501, runId, appData }), /launched executable/);
});

test('readPersistentDaemon refuses a descriptor that is not a regular file', () => {
    const facts = { record: () => ({ pid: 300, ppid: 1, uid: 501, tty: '??', executable }), birth: () => 'Tue Nov 17 16:07:30 2026',
        canonical: path => path, run: () => 'text', status: () => ({ isFile: () => false, isSymbolicLink: () => true }), read: () => '{}' };
    assert.throws(() => readPersistentDaemon(300, 100, appData, 1000, facts), /regular file/);
});

test('readPersistentDaemon returns the daemon facts and the descriptor it wrote', () => {
    const facts = { record: () => ({ pid: 300, ppid: 1, uid: 501, tty: '??', executable }), birth: pid => (pid === 300 ? 'Tue Nov 17 16:07:30 2026' : 'Tue Nov 17 16:07:29 2026'),
        canonical: path => path, run: () => `${executable} mux serve x NTILDE_APPDATA_ROOT=${appData}\n`, status: () => ({ isFile: () => true, isSymbolicLink: () => false }),
        read: () => JSON.stringify({ pid: 300, processName: 'Ntilde', endpoint: appData + '/mux/sock' }) };
    const facts2 = readPersistentDaemon(300, 100, appData, 1000, facts);
    assert.deepEqual(facts2.descriptor, { pid: 300, processName: 'Ntilde', endpoint: appData + '/mux/sock' });
    assert.equal(facts2.birth, 'Tue Nov 17 16:07:30 2026');
    assert.equal(facts2.appBirth, 'Tue Nov 17 16:07:29 2026');
});

const context = { executable, appData, runId };
test('cleanup asks for nothing when no daemon is recorded and none is found', async () => {
    let asked = false;
    await terminateRecordedDaemon(undefined, context, { stop: () => { asked = true; }, discover: () => [] });
    await terminateRecordedDaemon({ pid: 2 ** 22 + 12345, uid: 501, executable }, context, { stop: () => { asked = true; }, discover: () => [] });
    assert.equal(asked, false);
});

const unixOnly = { skip: process.platform === 'win32' ? 'Requires Unix process inspection and user ids' : false };
test('cleanup refuses to signal a live process that is not this run\'s daemon', unixOnly, async () => {
    let asked = false;
    await assert.rejects(terminateRecordedDaemon({ pid: process.pid, uid: process.getuid(), executable: process.execPath }, { executable: process.execPath, appData, runId },
        { stop: () => { asked = true; }, discover: () => [] }), /identity changed/);
    assert.equal(asked, false);
});

test('cleanup fails when a scan still finds a daemon after every candidate was handled', async () => {
    await assert.rejects(terminateRecordedDaemon(undefined, context, { stop: () => {}, discover: () => [{ pid: 2 ** 22 + 777, uid: 501, executable }] }), /survived cleanup/);
});

test('a daemon that never published its descriptor is still found by its environment', () => {
    const listing = [`  300   501 ${executable} mux serve NTILDE_APPDATA_ROOT=${appData} TERMINFO_RUN_ID=${runId} HOME=/Users/test`,
        `  301   501 ${executable} --ntilde-terminfo-owner=${runId} NTILDE_APPDATA_ROOT=${appData} TERMINFO_RUN_ID=${runId}`,
        `  302   502 ${executable} mux serve NTILDE_APPDATA_ROOT=${appData} TERMINFO_RUN_ID=${runId}`,
        `  303   501 ${executable} mux serve NTILDE_APPDATA_ROOT=/other/appdata TERMINFO_RUN_ID=${runId}`,
        `  304   501 ${executable} mux serve NTILDE_APPDATA_ROOT=${appData} TERMINFO_RUN_ID=${'b'.repeat(32)}`,
        '  not a process line'].join('\n');
    assert.deepEqual(discoverPersistentDaemons(context, { run: () => listing, uid: 501 }), [{ pid: 300, uid: 501, executable }]);
});

test('a failed process scan is a failed cleanup, never an empty result', async () => {
    assert.throws(() => discoverPersistentDaemons(context, { run: () => { throw new Error('ps failed'); }, uid: 501 }), /ps failed/);
    await assert.rejects(terminateRecordedDaemon(undefined, context, { stop: () => {}, discover: () => { throw new Error('ps failed'); } }), /ps failed/);
});
