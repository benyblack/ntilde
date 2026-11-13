// Desktop-collection lease: record format, atomic exclusion across independent processes, refusal of every ambiguous
// state, verified-cleanup release rules and explicit conservative recovery. Uses a private temporary lock path and
// injected process facts; it never touches the shared ~/.ntilde-qa-sidecars lock and launches no App.
import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir, userInfo } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import {
    DesktopLockRefusal, acquireDesktopLease, assessDesktopHolder, canonicalDesktopLockPath, createDesktopLeaseBroker, desktopLockAuditPath,
    desktopLockWaitMs, formatOwnerRecord, inspectDesktopLock, parseOwnerRecord, readDesktopLockState, recoverDesktopLease,
} from './desktop-lock.mjs';

const helperUrl = pathToFileURL(join(import.meta.dirname, 'desktop-lock.mjs')).href;
const posix = process.platform !== 'win32';
const lease32 = 'a'.repeat(32);

function sandbox(action) {
    const root = mkdtempSync(join(tmpdir(), 'nt-desklock-'));
    try { return action({ root, lockPath: join(root, '.desktop-collection.lock') }); }
    finally { rmSync(root, { recursive: true, force: true }); }
}
async function sandboxAsync(action) {
    const root = mkdtempSync(join(tmpdir(), 'nt-desklock-'));
    try { return await action({ root, lockPath: join(root, '.desktop-collection.lock') }); }
    finally { rmSync(root, { recursive: true, force: true }); }
}
/** Deterministic clock and sleeper: sleeping advances time, so waits and grace periods never take wall-clock time. */
function fakeTime(start = 1_800_000_000_000) {
    const clock = { value: start };
    return { now: () => clock.value, sleep: async ms => { clock.value += ms; }, clock };
}
const record = (overrides = {}) => ({ lease: lease32, pid: 4242, birth: 'Fri Nov 13 10:00:00 2026', uid: 501, worktree: '/tmp/work tree/ntilde',
    entrypoint: 'terminfo-owned.mjs', since: '2026-11-13T10:00:01.000Z', status: 'held', launch: 'none',
    markers: [`--ntilde-terminfo-owner=${lease32}`], ...overrides });
const plant = (lockPath, overrides) => { mkdirSync(lockPath); writeFileSync(join(lockPath, 'owner'), formatOwnerRecord(record(overrides))); };
const liveDeps = (extra = {}) => ({ processAlive: () => true, processBirth: () => record().birth, parentPid: () => 1, listProcesses: () => [], pid: () => 999_001, ...extra });
const goneDeps = (extra = {}) => liveDeps({ processAlive: () => false, ...extra });

test('the canonical lock is the passwd home path and no environment value can redirect it', () => {
    const expected = join(userInfo().homedir, '.ntilde-qa-sidecars', '.desktop-collection.lock');
    assert.equal(canonicalDesktopLockPath(), expected);
    const result = spawnSync(process.execPath, ['--input-type=module', '-e',
        `import { canonicalDesktopLockPath as p } from ${JSON.stringify(helperUrl)}; process.stdout.write(p());`],
    { encoding: 'utf8', env: { ...process.env, HOME: '/nonexistent-home', USERPROFILE: '/nonexistent-home', NTILDE_TERMINFO_DESKTOP_LOCK_PATH: '/tmp/elsewhere' } });
    assert.equal(result.stdout, expected);
});

test('owner records round-trip exactly and every malformed or foreign spelling is rejected', () => {
    const original = record({ status: 'cleanup-failed', launch: 'attempted', failure: 'worker survived; see log' });
    const line = formatOwnerRecord(original);
    assert.ok(line.endsWith('\n') && !line.slice(0, -1).includes('\n'));
    assert.deepEqual(parseOwnerRecord(line), { kind: 'owned', record: original });
    const legacy = parseOwnerRecord('owner=other-session pid=123 since=2026-11-13T10:00:00Z\n');
    assert.equal(legacy.kind, 'legacy-manual');
    for (const bad of ['', '\n', 'garbage', line.replace('schema=1', 'schema=2'), line.replace(lease32, 'xyz'), line + 'x=1\n', line.trim() + ' extra=1\n',
        line.replace('status=cleanup-failed', 'status=idle'), line.replace(/pid=\d+/, 'pid=0'), line.replace(' markers=', ' markers=--evil%20x,'), line + line,
        line.replace('since=', 'since=not-a-date%20'), 'schema=1 schema=1\n', 'a'.repeat(5000), line.replace('\n', '\0\n')]) {
        assert.notEqual(parseOwnerRecord(bad).kind, 'owned', JSON.stringify(bad).slice(0, 80));
    }
    assert.throws(() => formatOwnerRecord(record({ failure: 'x'.repeat(5000) })), /too large/);
});

test('acquire is atomic, writes the record before returning and release is verified and removes everything', () => sandboxAsync(async ({ lockPath }) => {
    const lease = await acquireDesktopLease({ lockPath, entrypoint: 'unit.mjs', worktree: '/work/unit', deps: { sleep: async () => {} } });
    const state = readDesktopLockState(lockPath);
    assert.equal(state.kind, 'owned');
    assert.equal(state.record.lease, lease.leaseId); assert.equal(state.record.pid, process.pid); assert.equal(state.record.status, 'held');
    assert.equal(state.record.launch, 'none'); assert.equal(state.record.markers.length, 3);
    assert.equal(lstatSync(lockPath).isDirectory(), true);
    assert.equal(lease.assertHeld().lease, lease.leaseId);
    lease.release();
    assert.equal(existsSync(lockPath), false);
    assert.deepEqual(readdirSync(join(lockPath, '..')).filter(name => name.includes('release-')), []);
    assert.throws(() => lease.assertHeld(), /already released/);
    const events = readFileSync(desktopLockAuditPath(lockPath), 'utf8').trim().split('\n').map(JSON.parse).map(entry => entry.event);
    assert.deepEqual(events, ['acquired', 'released']);
}));

test('a held lock refuses a second contender in the same process and never launches', () => sandboxAsync(async ({ lockPath }) => {
    const first = await acquireDesktopLease({ lockPath, entrypoint: 'a.mjs', worktree: '/w' });
    await assert.rejects(acquireDesktopLease({ lockPath, entrypoint: 'b.mjs', worktree: '/w' }), error => {
        assert.ok(error instanceof DesktopLockRefusal); assert.match(error.message, /No App was launched/);
        assert.equal(error.diagnostic.state, 'owned');
        // This very process holds it: a nested acquisition is named as such instead of deadlocking.
        assert.equal(error.diagnostic.ancestor, posix ? true : error.diagnostic.ancestor);
        return true;
    });
    assert.equal(first.assertHeld().lease, first.leaseId);
    first.release();
}));

/** A contender process that waits at a file barrier, then tries once to enter the launch section. */
const contenderSource = (extra = '') => `
import { acquireDesktopLease } from ${JSON.stringify(helperUrl)};
import { existsSync, readdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
const [lockPath, dir, id, expectRefused] = process.argv.slice(2);
const until = async (condition, label) => { for (let i = 0; i < 800; i++) { if (condition()) return; await new Promise(r => setTimeout(r, 25)); } throw new Error('timeout: ' + label); };
await until(() => existsSync(join(dir, 'go')), 'go');
let lease;
try { lease = await acquireDesktopLease({ lockPath, entrypoint: 'contender', worktree: dir }); }
catch (error) { writeFileSync(join(dir, 'refused-' + id), error.diagnostic?.state ?? error.message); process.exit(3); }
writeFileSync(join(dir, 'entered-' + id), String(process.pid)); // stands for "launch an App"
${extra}
await until(() => readdirSync(dir).filter(name => name.startsWith('refused-')).length >= Number(expectRefused), 'losers');
lease.release();
`;
const runNode = (file, args) => new Promise(resolve => {
    const child = spawn(process.execPath, [file, ...args], { stdio: ['ignore', 'pipe', 'pipe'] });
    let stderr = ''; child.stderr.on('data', chunk => { stderr += chunk; });
    child.on('close', code => resolve({ code, stderr }));
});

test('six independent processes race: exactly one enters the launch section, the rest are refused and launch nothing', { skip: !posix && 'child-process race uses ps-free paths but is timing-heavy on Windows' }, () => sandboxAsync(async ({ root, lockPath }) => {
    const script = join(root, 'contender.mjs'); writeFileSync(script, contenderSource());
    const runs = [1, 2, 3, 4, 5, 6].map(id => runNode(script, [lockPath, root, String(id), '5']));
    writeFileSync(join(root, 'go'), '');
    const results = await Promise.all(runs);
    const entered = readdirSync(root).filter(name => name.startsWith('entered-')), refused = readdirSync(root).filter(name => name.startsWith('refused-'));
    assert.equal(entered.length, 1, JSON.stringify(results));
    assert.equal(refused.length, 5);
    assert.deepEqual(results.map(result => result.code).sort(), [0, 3, 3, 3, 3, 3]);
    for (const name of refused) assert.match(readFileSync(join(root, name), 'utf8'), /^(owned|partial)$/);
    assert.equal(existsSync(lockPath), false, 'the winner released after its section');
}));

test('a refused contender never reaches launch while a holder process keeps the lease, and gets in only after release', () => sandboxAsync(async ({ root, lockPath }) => {
    const holder = join(root, 'holder.mjs');
    writeFileSync(holder, contenderSource("writeFileSync(join(dir, 'holding'), 'x');\nawait until(() => existsSync(join(dir, 'drop')), 'drop');"));
    const holderRun = runNode(holder, [lockPath, root, 'holder', '0']);
    writeFileSync(join(root, 'go'), '');
    for (let i = 0; i < 400 && !existsSync(join(root, 'holding')); i++) await new Promise(resolve => setTimeout(resolve, 25));
    assert.ok(existsSync(join(root, 'holding')));
    await assert.rejects(acquireDesktopLease({ lockPath, entrypoint: 'late.mjs', worktree: root }), DesktopLockRefusal);
    const diagnostic = inspectDesktopLock(lockPath);
    assert.equal(diagnostic.state, 'owned'); assert.equal(diagnostic.holder, posix ? 'alive' : diagnostic.holder);
    // Waiting is bounded by its own duration and still refuses afterwards.
    const time = fakeTime();
    await assert.rejects(acquireDesktopLease({ lockPath, entrypoint: 'late.mjs', worktree: root, waitMs: 5000, pollMs: 1000, deps: { ...time } }), DesktopLockRefusal);
    assert.ok(time.clock.value - 1_800_000_000_000 >= 5000 || !posix);
    writeFileSync(join(root, 'drop'), '');
    assert.equal((await holderRun).code, 0);
    const next = await acquireDesktopLease({ lockPath, entrypoint: 'late.mjs', worktree: root });
    next.release();
}));

test('delegation cannot bypass ownership: a child of the holder is refused whatever the environment says and cannot release or recover the parent lease', { skip: !posix && 'ancestor walk uses ps' }, () => sandboxAsync(async ({ root, lockPath }) => {
    const child = join(root, 'child.mjs');
    writeFileSync(child, `
import { acquireDesktopLease, recoverDesktopLease } from ${JSON.stringify(helperUrl)};
const lockPath = process.argv[2]; const out = {};
try { await acquireDesktopLease({ lockPath, entrypoint: 'child', worktree: '/c' }); out.acquired = true; }
catch (error) { out.refused = error.diagnostic.state; out.ancestor = error.diagnostic.ancestor; }
try { recoverDesktopLease({ lockPath, leaseId: process.env.PARENT_LEASE, confirmDesktopIdle: true }); out.recovered = true; }
catch (error) { out.recoverRefused = error.message; }
process.stdout.write(JSON.stringify(out));`);
    const lease = await acquireDesktopLease({ lockPath, entrypoint: 'parent', worktree: root });
    const result = spawnSync(process.execPath, [child, lockPath], { encoding: 'utf8', env: { ...process.env, PARENT_LEASE: lease.leaseId,
        NTILDE_TERMINFO_DESKTOP_LEASE: lease.leaseId, NTILDE_TERMINFO_OWNER: 'delegated', NTILDE_TERMINFO_DESKTOP_LOCK_HELD: '1' } });
    const out = JSON.parse(result.stdout);
    assert.equal(out.acquired, undefined); assert.equal(out.refused, 'owned'); assert.equal(out.ancestor, true);
    assert.match(out.recoverRefused, /holder is alive/);
    assert.equal(lease.assertHeld().lease, lease.leaseId, 'the parent lease is untouched');
    lease.release();
}));

test('partial, malformed, legacy and foreign lock state is refused and left untouched, never read as idle', () => sandboxAsync(async ({ root, lockPath }) => {
    const time = fakeTime();
    const refuse = async (setup, state) => {
        setup();
        const before = readdirSync(root).filter(name => !name.endsWith('.log')).sort().join();
        await assert.rejects(acquireDesktopLease({ lockPath, entrypoint: 'x.mjs', worktree: root, deps: { ...time, ...liveDeps() } }), error => error.diagnostic.state === state);
        assert.equal(readdirSync(root).filter(name => !name.endsWith('.log')).sort().join(), before, state + ' state must stay untouched');
        rmSync(lockPath, { recursive: true, force: true });
    };
    await refuse(() => mkdirSync(lockPath), 'partial');
    await refuse(() => { mkdirSync(lockPath); writeFileSync(join(lockPath, 'owner'), 'schema=1 owner=ntilde-terminfo lease=zzz\n'); }, 'malformed');
    await refuse(() => { mkdirSync(lockPath); writeFileSync(join(lockPath, 'owner'), '\0\0\0'); }, 'malformed');
    await refuse(() => { mkdirSync(lockPath); mkdirSync(join(lockPath, 'owner')); }, 'malformed');
    await refuse(() => { mkdirSync(lockPath); writeFileSync(join(lockPath, 'owner'), 'owner=other-session pid=1 since=2026-11-13T10:00Z\n'); }, 'legacy-manual');
    await refuse(() => writeFileSync(lockPath, JSON.stringify({ owner: 'codex-core-terminal-comparison', pid: 1, runId: 'abc', startedAt: 'x' })), 'legacy-file');
    await refuse(() => writeFileSync(lockPath, 'not json'), 'legacy-file');
    if (posix) await refuse(() => symlinkSync(root, lockPath), 'foreign');
    // A partial directory is first given a short grace for a mid-acquisition peer, then refused; time is fake here.
    mkdirSync(lockPath);
    const started = time.clock.value;
    await assert.rejects(acquireDesktopLease({ lockPath, entrypoint: 'x.mjs', worktree: root, deps: { ...time } }), DesktopLockRefusal);
    assert.ok(time.clock.value - started >= 3000);
}));

test('a legacy holder is waited for up to the bounded wait, then refused; an unhealthy lease is refused at once', () => sandboxAsync(async ({ root, lockPath }) => {
    mkdirSync(lockPath); writeFileSync(join(lockPath, 'owner'), 'owner=other pid=1 since=2026-11-13T10:00Z\n');
    const time = fakeTime(), started = time.clock.value;
    await assert.rejects(acquireDesktopLease({ lockPath, entrypoint: 'w.mjs', worktree: root, waitMs: 3000, pollMs: 1000, deps: { ...time } }), DesktopLockRefusal);
    assert.ok(time.clock.value - started >= 3000);
    rmSync(lockPath, { recursive: true });
    plant(lockPath, { status: 'cleanup-failed', failure: 'worker survived' });
    const before = time.clock.value;
    await assert.rejects(acquireDesktopLease({ lockPath, entrypoint: 'w.mjs', worktree: root, waitMs: 60000, deps: { ...time, ...liveDeps() } }), error => error.diagnostic.state === 'owned' && error.diagnostic.record.status === 'cleanup-failed');
    assert.equal(time.clock.value, before, 'no waiting for a lease that cannot clear itself');
}));

test('an abort signal stops a waiting contender before it takes anything', () => sandboxAsync(async ({ root, lockPath }) => {
    await assert.rejects(acquireDesktopLease({ lockPath, entrypoint: 'w.mjs', worktree: root, aborted: () => true }), /Interrupted before the desktop lease/);
    assert.equal(existsSync(lockPath), false);
}));

test('invalid entrypoints, worktrees and waits are refused before any directory is created', () => sandboxAsync(async ({ root, lockPath }) => {
    for (const options of [{ entrypoint: '../x', worktree: root }, { entrypoint: 'x', worktree: '' }, { entrypoint: 'x', worktree: root, waitMs: -1 },
        { entrypoint: 'x', worktree: root, waitMs: 4_000_000 }, { entrypoint: 'x', worktree: root, pollMs: 0 }])
        await assert.rejects(acquireDesktopLease({ lockPath, ...options }), /Invalid desktop lease/);
    assert.equal(existsSync(lockPath), false);
    assert.equal(desktopLockWaitMs({}), 0);
    assert.equal(desktopLockWaitMs({ NTILDE_TERMINFO_DESKTOP_LOCK_WAIT_SECONDS: '90' }), 90000);
    for (const bad of ['-1', '1.5', '3601', 'abc', '99999']) assert.throws(() => desktopLockWaitMs({ NTILDE_TERMINFO_DESKTOP_LOCK_WAIT_SECONDS: bad }), /whole number of seconds/);
}));

test('changed ownership blocks release and never removes or rewrites the foreign lock', () => sandboxAsync(async ({ root, lockPath }) => {
    const lease = await acquireDesktopLease({ lockPath, entrypoint: 'o.mjs', worktree: root });
    const foreign = formatOwnerRecord(record({ lease: 'b'.repeat(32), pid: process.pid }));
    writeFileSync(join(lockPath, 'owner'), foreign);
    assert.throws(() => lease.release(), /ownership changed/);
    assert.throws(() => lease.retain('x'), /ownership changed/);
    assert.throws(() => lease.noteLaunchAttempt(), /ownership changed/);
    assert.equal(readFileSync(join(lockPath, 'owner'), 'utf8'), foreign);
    writeFileSync(join(lockPath, 'owner'), 'garbage\n');
    assert.throws(() => lease.release(), /ownership changed/);
    rmSync(lockPath, { recursive: true });
    assert.throws(() => lease.release(), /ownership changed/);
    assert.deepEqual(readdirSync(root).filter(name => name.includes('release-')), []);
}));

test('a recorded launch with no verified cleanup can neither release nor be reported clean; retention keeps every contender out', () => sandboxAsync(async ({ root, lockPath }) => {
    const lease = await acquireDesktopLease({ lockPath, entrypoint: 'l.mjs', worktree: root, deps: { listProcesses: () => [] } });
    lease.noteLaunchAttempt();
    assert.equal(readDesktopLockState(lockPath).record.launch, 'attempted');
    assert.throws(() => lease.release(), /no verified cleanup/);
    assert.equal(existsSync(lockPath), true);
    const broker = createDesktopLeaseBroker({ lockPath, entrypoint: 'b.mjs', worktree: root });
    const facade = await broker.acquire().catch(error => error);
    assert.ok(facade instanceof DesktopLockRefusal, 'the broker cannot overtake a held lock');
    lease.retain('Launch attempted but cleanup was not verified');
    const state = readDesktopLockState(lockPath);
    assert.equal(state.record.status, 'cleanup-failed'); assert.match(state.record.failure, /not verified/);
    await assert.rejects(acquireDesktopLease({ lockPath, entrypoint: 'next.mjs', worktree: root, waitMs: 600000 }), error => error.diagnostic.record.status === 'cleanup-failed');
    assert.throws(() => lease.release(), /no verified cleanup/);
}));

test('cleanup is verified only by a clean, successful scan for this lease\'s markers; survivors and scan failures keep it unverified', () => sandboxAsync(async ({ root, lockPath }) => {
    const time = fakeTime();
    let rows = [];
    const lease = await acquireDesktopLease({ lockPath, entrypoint: 'v.mjs', worktree: root, deps: { ...time, listProcesses: () => rows } });
    lease.noteLaunchAttempt();
    const [owner, worker] = lease.record.markers;
    rows = [{ pid: 777, command: `/path/Ntilde ${owner} --other` }];
    await assert.rejects(lease.verifyCleanup(), /survive cleanup \(pids 777\)/);
    rows = [{ pid: 778, command: `node worker.mjs ${worker}` }];
    await assert.rejects(lease.verifyCleanup(), /pids 778/);
    assert.equal(lease.cleanupVerified, false);
    assert.throws(() => lease.release(), /no verified cleanup/);
    // Other leases' markers and substring look-alikes are not this lease's processes.
    rows = [{ pid: 779, command: `Ntilde --ntilde-terminfo-owner=${'c'.repeat(32)}` }, { pid: 780, command: `grep ${owner}x` }, { pid: process.pid, command: owner }];
    await lease.verifyCleanup();
    assert.equal(lease.cleanupVerified, true);
    lease.release();
    assert.equal(existsSync(lockPath), false);
    const failing = await acquireDesktopLease({ lockPath, entrypoint: 'v.mjs', worktree: root, deps: { ...time, listProcesses: () => { throw new Error('ps denied'); } } });
    failing.noteLaunchAttempt();
    await assert.rejects(failing.verifyCleanup(), /scan failed, so cleanup is unverified/);
    assert.equal(failing.cleanupVerified, false);
    assert.throws(() => failing.release(), /no verified cleanup/);
}));

test('the broker facade cannot release or retain; finish() releases only after no launch or verified cleanup and never throws', () => sandboxAsync(async ({ root, lockPath }) => {
    const options = { lockPath, entrypoint: 'broker.mjs', worktree: root, deps: { listProcesses: () => [] } };
    // No launch: preparation failure releases.
    let broker = createDesktopLeaseBroker(options);
    assert.deepEqual(broker.finish(), { held: false });
    let facade = await broker.acquire();
    assert.deepEqual(Object.keys(facade).sort(), ['assertHeld', 'leaseId', 'noteLaunchAttempt', 'verifyCleanup']);
    assert.equal(Object.isFrozen(facade), true);
    await assert.rejects(broker.acquire(), /already requested/);
    assert.equal(broker.finish().released, true);
    assert.equal(existsSync(lockPath), false);
    // Launch without verified cleanup is retained, with the failure recorded.
    broker = createDesktopLeaseBroker(options); facade = await broker.acquire(); facade.noteLaunchAttempt();
    const outcome = broker.finish();
    assert.deepEqual([outcome.released, outcome.retained], [false, true]);
    assert.equal(readDesktopLockState(lockPath).record.status, 'cleanup-failed');
    rmSync(lockPath, { recursive: true });
    // Launch plus verified cleanup releases.
    broker = createDesktopLeaseBroker(options); facade = await broker.acquire(); facade.noteLaunchAttempt(); await facade.verifyCleanup();
    assert.equal(broker.finish().released, true);
    // finish() reports (not throws) when ownership changed under it.
    broker = createDesktopLeaseBroker(options); facade = await broker.acquire();
    writeFileSync(join(lockPath, 'owner'), 'owner=thief pid=1 since=2026-11-13T10:00Z\n');
    const changed = broker.finish();
    assert.equal(changed.released, false); assert.match(changed.error, /ownership changed/);
    assert.match(readFileSync(join(lockPath, 'owner'), 'utf8'), /thief/);
}));

test('holder assessment: a dead pid, a reused pid and an unknowable pid are three different, all non-idle, answers', () => {
    const rec = record();
    assert.deepEqual(assessDesktopHolder(rec, liveDeps()), { holder: 'alive', reason: 'pid-and-start-match' });
    assert.deepEqual(assessDesktopHolder(rec, goneDeps()), { holder: 'gone', reason: 'pid-absent' });
    assert.deepEqual(assessDesktopHolder(rec, liveDeps({ processBirth: () => 'Sat Nov 14 11:11:11 2026' })), { holder: 'gone', reason: 'pid-reused' });
    assert.equal(assessDesktopHolder(rec, liveDeps({ processBirth: () => { throw new Error('no ps'); } })).holder, 'ambiguous');
    assert.equal(assessDesktopHolder(rec, liveDeps({ processAlive: () => { throw Object.assign(new Error('x'), { code: 'EIO' }); } })).holder, 'ambiguous');
});

test('a stale or reused holder pid is refused, never taken over; its advice points at explicit recovery only', () => sandboxAsync(async ({ root, lockPath }) => {
    plant(lockPath);
    for (const deps of [goneDeps(), liveDeps({ processBirth: () => 'Sat Nov 14 11:11:11 2026' }), liveDeps({ processBirth: () => { throw new Error('no ps'); } })]) {
        await assert.rejects(acquireDesktopLease({ lockPath, entrypoint: 's.mjs', worktree: root, waitMs: 60000, deps: { ...fakeTime(), ...deps } }), error => error.diagnostic.state === 'owned' && error.diagnostic.holder !== 'alive');
        assert.equal(readDesktopLockState(lockPath).record.lease, lease32);
    }
    const diagnostic = inspectDesktopLock(lockPath, goneDeps());
    assert.match(diagnostic.advice, /recover --lease a{32}/); assert.equal(diagnostic.holder, 'gone');
    assert.equal(inspectDesktopLock(join(root, 'absent')).state, 'idle');
}));

test('recovery needs the exact lease, a gone holder, no surviving marked process and, after a launch, the operator\'s word; it preserves the evidence', () => sandboxAsync(async ({ root, lockPath }) => {
    const base = { lockPath, leaseId: lease32 };
    assert.throws(() => recoverDesktopLease({ ...base, leaseId: 'short', deps: goneDeps() }), /exact 32-hex/);
    assert.throws(() => recoverDesktopLease({ ...base, deps: goneDeps() }), /this lock is absent/);
    plant(lockPath);
    assert.throws(() => recoverDesktopLease({ ...base, leaseId: 'b'.repeat(32), deps: goneDeps() }), /different lease/);
    assert.throws(() => recoverDesktopLease({ ...base, deps: liveDeps() }), /holder is alive/);
    assert.throws(() => recoverDesktopLease({ ...base, deps: liveDeps({ processBirth: () => { throw new Error('x'); } }) }), /holder is ambiguous/);
    assert.throws(() => recoverDesktopLease({ ...base, deps: goneDeps({ listProcesses: () => [{ pid: 9, command: `Ntilde --ntilde-terminfo-owner=${lease32}` }] }) }), /survive \(pids 9\)/);
    assert.throws(() => recoverDesktopLease({ ...base, deps: goneDeps({ listProcesses: () => { throw new Error('ps'); } }) }), /scan failed/);
    // A launched lease also needs --confirm-desktop-idle; an unlaunched dead holder does not.
    rmSync(lockPath, { recursive: true }); plant(lockPath, { launch: 'attempted' });
    assert.throws(() => recoverDesktopLease({ ...base, deps: goneDeps() }), /confirm-desktop-idle/);
    rmSync(lockPath, { recursive: true }); plant(lockPath, { status: 'cleanup-failed', failure: 'x' });
    assert.throws(() => recoverDesktopLease({ ...base, deps: goneDeps() }), /confirm-desktop-idle/);
    // Extra entries and marker-less leases are never auto-recovered.
    writeFileSync(join(lockPath, 'surprise'), 'x');
    assert.throws(() => recoverDesktopLease({ ...base, confirmDesktopIdle: true, deps: goneDeps() }), /extra entries/);
    rmSync(lockPath, { recursive: true }); plant(lockPath, { markers: [] });
    assert.throws(() => recoverDesktopLease({ ...base, confirmDesktopIdle: true, deps: goneDeps() }), /no process markers/);
    rmSync(lockPath, { recursive: true }); plant(lockPath, { launch: 'attempted' });
    const result = recoverDesktopLease({ ...base, confirmDesktopIdle: true, deps: goneDeps() });
    assert.equal(result.recovered, lease32);
    assert.equal(existsSync(lockPath), false);
    assert.match(readFileSync(join(result.evidence, 'owner'), 'utf8'), /launch=attempted/);
    const next = await acquireDesktopLease({ lockPath, entrypoint: 'after.mjs', worktree: root });
    next.release();
    const events = readFileSync(desktopLockAuditPath(lockPath), 'utf8').trim().split('\n').map(JSON.parse);
    assert.ok(events.some(entry => entry.event === 'recovered' && entry.confirmedIdle === true && entry.holderReason === 'pid-absent'));
}));

test('recovery re-verifies what it moved: a lease swapped in between the checks and the rename is restored, never discarded', () => sandboxAsync(async ({ root, lockPath }) => {
    const rival = lease => formatOwnerRecord(record({ lease, pid: process.pid, birth: 'unavailable' }));
    plant(lockPath);
    // Simulate the race with a dependency that runs after the holder/scan checks, immediately before the rename.
    const racing = goneDeps({ listProcesses: () => {
        rmSync(lockPath, { recursive: true }); mkdirSync(lockPath); writeFileSync(join(lockPath, 'owner'), rival('b'.repeat(32)));
        return [];
    } });
    assert.throws(() => recoverDesktopLease({ lockPath, leaseId: lease32, deps: racing }), /changed during recovery; it was restored untouched/);
    assert.equal(readDesktopLockState(lockPath).record.lease, 'b'.repeat(32));
    assert.deepEqual(readdirSync(root).filter(name => name.includes('.recovered-')), []);
}));

test('a driver-style lease (unlaunched, dead holder) recovers without confirmation, and a launched one needs it', () => sandboxAsync(async ({ lockPath }) => {
    plant(lockPath, { launch: 'none' });
    assert.equal(recoverDesktopLease({ lockPath, leaseId: lease32, deps: goneDeps() }).recovered, lease32);
    plant(lockPath, { launch: 'attempted', markers: [`--ntilde-terminfo-owner=${lease32}`, `--ntilde-terminfo-worker=${lease32}`, `--ntilde-terminfo-private-clipboard=${lease32}`] });
    assert.throws(() => recoverDesktopLease({ lockPath, leaseId: lease32, deps: goneDeps() }), /confirm-desktop-idle/);
    assert.equal(recoverDesktopLease({ lockPath, leaseId: lease32, confirmDesktopIdle: true, deps: goneDeps() }).recovered, lease32);
}));

test('a really dead holder process (not a fake) is classified gone and still refuses contenders', { skip: !posix && 'needs ps' }, () => sandboxAsync(async ({ root, lockPath }) => {
    const dead = spawnSync(process.execPath, ['-e', '']);
    plant(lockPath, { pid: dead.pid });
    const diagnostic = inspectDesktopLock(lockPath);
    assert.ok(['gone', 'ambiguous'].includes(diagnostic.holder));
    await assert.rejects(acquireDesktopLease({ lockPath, entrypoint: 'd.mjs', worktree: root }), DesktopLockRefusal);
    assert.equal(readDesktopLockState(lockPath).record.pid, dead.pid);
}));

test('a real marked process is found by the scan and clears once it exits', { skip: !posix && 'needs ps' }, () => sandboxAsync(async ({ root, lockPath }) => {
    const lease = await acquireDesktopLease({ lockPath, entrypoint: 'p.mjs', worktree: root });
    lease.noteLaunchAttempt();
    const child = spawn(process.execPath, ['-e', 'setInterval(()=>{},1000)', '--', lease.record.markers[0]], { stdio: 'ignore' });
    await new Promise(resolve => child.once('spawn', resolve));
    await assert.rejects(lease.verifyCleanup(), /survive cleanup/); // real ps; bounded 3 s scan budget
    child.kill('SIGKILL');
    await new Promise(resolve => child.once('exit', resolve));
    await lease.verifyCleanup();
    lease.release();
    assert.equal(existsSync(lockPath), false);
}));

test('offline commands never import the lock: only the launcher, the common-core driver, the lock tooling and its test fixture reference it', () => {
    const scripts = new URL('../', import.meta.url);
    const walk = dir => readdirSync(dir, { withFileTypes: true }).flatMap(entry => entry.isDirectory() ? (entry.name === 'node_modules' ? [] : walk(new URL(entry.name + '/', dir))) : [new URL(entry.name, dir)]);
    const referencing = walk(scripts).filter(url => /\.(?:mjs|ts|py|sh|ps1)$/.test(url.pathname) && !/\.test\./.test(url.pathname))
        .filter(url => /desktop-lock|desktop-collection\.lock|acquireDesktopLease|desktopLeaseKey|ntilde\.terminfo\.desktopLease/.test(readFileSync(url, 'utf8')))
        .map(url => url.pathname.slice(scripts.pathname.length)).sort();
    assert.deepEqual(referencing, ['terminfo-desktop-lock.mjs', 'terminfo-owned-runner.mjs', 'terminfo-owned.mjs', 'terminfo/common-core-driver.ts',
        'terminfo/desktop-lock.mjs', 'terminfo/fixtures/desktop-lock-fixture.mjs']);
});
