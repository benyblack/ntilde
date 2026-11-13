// Cooperative exclusive lease over the shared desktop-collection lock.
//
// Pure Node/BCL on purpose: the trusted launcher (scripts/terminfo-owned.mjs) loads these exact bytes by a pinned
// SHA-256 before it imports anything measured, and the common-core driver and the offline status/recovery CLI import it
// directly. It is not part of the measured source graph, so it never changes a receipt or source policy.
//
// Boundary: this is *cooperative* coordination between sessions of one user. It does not defend against a hostile
// same-user process that ignores it, deletes the directory or forges a record.
//
// Layout (kept compatible with the older manual `mkdir` protocol, which only ever tested the directory's existence):
//   ~/.ntilde-qa-sidecars/.desktop-collection.lock/        directory created atomically by mkdir
//   ~/.ntilde-qa-sidecars/.desktop-collection.lock/owner   one line of percent-encoded key=value pairs
// An absent directory is the only idle state. Partial, malformed, foreign, legacy-file, legacy-manual and dead-holder
// states are all "held": they refuse a contender and are never taken over automatically.
import { execFileSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { appendFileSync, lstatSync, mkdirSync, readFileSync, readdirSync, renameSync, rmdirSync, statSync, unlinkSync, writeFileSync } from 'node:fs';
import { userInfo } from 'node:os';
import { dirname, join } from 'node:path';

export const desktopLockSchema = 1;
const ownerFile = 'owner';
const maxOwnerBytes = 4096;
const maxFieldBytes = 1024;
const maxWaitMs = 3600 * 1000;
const partialGraceMs = 3000;
const cleanupScanBudgetMs = 3000;
const auditLimitBytes = 512 * 1024;
const leasePattern = /^[a-f0-9]{32}$/;
const markerPattern = /^--ntilde-terminfo-(?:owner|worker|private-clipboard)=[a-f0-9]{32}$/;

/** The only lock location: the passwd home, never $HOME or another environment value, so worktrees always share it. */
export function canonicalDesktopLockPath() {
    return join(userInfo().homedir, '.ntilde-qa-sidecars', '.desktop-collection.lock');
}
export const desktopLockAuditPath = lockPath => join(dirname(lockPath), '.desktop-collection.log');

/** A refusal carries a bounded diagnostic and never a recovery that guessed ownership. */
export class DesktopLockRefusal extends Error {
    constructor(message, diagnostic) {
        super(message);
        this.name = 'DesktopLockRefusal';
        this.diagnostic = diagnostic;
    }
}

/** Seconds a live, healthy holder is waited for (default 0 = refuse at once). A duration only: it can never skip exclusion. */
export function desktopLockWaitMs(environment = process.env) {
    const value = environment.NTILDE_TERMINFO_DESKTOP_LOCK_WAIT_SECONDS;
    if (value === undefined || value === '') return 0;
    if (!/^\d{1,4}$/.test(value) || Number(value) > maxWaitMs / 1000) throw new Error('NTILDE_TERMINFO_DESKTOP_LOCK_WAIT_SECONDS must be a whole number of seconds from 0 to 3600.');
    return Number(value) * 1000;
}

const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
const psEnvironment = () => ({ ...process.env, LC_ALL: 'C', LANG: 'C', TZ: 'UTC' });

/** Real process facts. Every one may throw: a thrown fact is "unknown", never "idle". */
export const defaultDesktopLockDeps = Object.freeze({
    pid: () => process.pid,
    uid: () => (process.getuid ? process.getuid() : -1),
    now: () => Date.now(),
    sleep,
    processAlive(pid) {
        try { process.kill(pid, 0); return true; }
        catch (error) {
            if (error.code === 'ESRCH') return false;
            if (error.code === 'EPERM') return true;
            throw error;
        }
    },
    processBirth(pid) {
        const value = execFileSync('/bin/ps', ['-p', String(pid), '-o', 'lstart='], {
            encoding: 'utf8', timeout: 2000, maxBuffer: 256, env: psEnvironment(), stdio: ['ignore', 'pipe', 'ignore'],
        }).trim();
        if (!/^[A-Za-z]{3} [A-Za-z]{3} +\d{1,2} \d{2}:\d{2}:\d{2} \d{4}$/.test(value)) throw new Error('Process start time is not available.');
        return value;
    },
    parentPid(pid) {
        const value = execFileSync('/bin/ps', ['-p', String(pid), '-o', 'ppid='], {
            encoding: 'utf8', timeout: 2000, maxBuffer: 64, env: psEnvironment(), stdio: ['ignore', 'pipe', 'ignore'],
        }).trim();
        if (!/^\d+$/.test(value)) throw new Error('Parent process is not available.');
        return Number(value);
    },
    listProcesses() {
        const text = execFileSync('/bin/ps', ['-axo', 'pid=,command='], {
            encoding: 'utf8', timeout: 5000, maxBuffer: 32 * 1024 * 1024, env: psEnvironment(), stdio: ['ignore', 'pipe', 'ignore'],
        });
        const rows = [];
        for (const line of text.split('\n')) {
            const trimmed = line.trimStart(), gap = trimmed.indexOf(' ');
            if (gap > 0 && /^\d+$/.test(trimmed.slice(0, gap))) rows.push({ pid: Number(trimmed.slice(0, gap)), command: trimmed.slice(gap).trim() });
        }
        return rows;
    },
});

const withDeps = deps => ({ ...defaultDesktopLockDeps, ...deps });
const bounded = (value, limit = 160) => String(value).replaceAll(/[^\x20-\x7E]/g, '?').slice(0, limit);

// ---------------------------------------------------------------------------------------------------------------
// Owner record: one line of `key=value` pairs, values percent-encoded so it stays strictly parseable.

const recordKeys = ['schema', 'owner', 'lease', 'pid', 'birth', 'uid', 'worktree', 'entrypoint', 'since', 'status', 'launch', 'markers', 'failure'];
const recordKeySet = new Set(recordKeys);

/** Serialize an owner record; only the declared keys are written. */
export function formatOwnerRecord(record) {
    const values = {
        schema: String(desktopLockSchema), owner: 'ntilde-terminfo', lease: record.lease, pid: String(record.pid), birth: record.birth,
        uid: String(record.uid), worktree: record.worktree, entrypoint: record.entrypoint, since: record.since,
        status: record.status, launch: record.launch,
        markers: record.markers.length ? record.markers.map(encodeURIComponent).join(',') : undefined,
        failure: record.failure,
    };
    const parts = [];
    for (const key of recordKeys) {
        const value = values[key];
        if (value === undefined) continue;
        parts.push(key + '=' + (['markers', 'schema', 'pid', 'uid', 'lease', 'status', 'launch', 'owner'].includes(key) ? value : encodeURIComponent(value)));
    }
    const line = parts.join(' ') + '\n';
    if (Buffer.byteLength(line) > maxOwnerBytes) throw new Error('Desktop lease record is too large.');
    return line;
}

function decodeValue(value) {
    try { return decodeURIComponent(value); } catch { return undefined; }
}

/** Parse an owner file strictly. Anything not exactly a v1 record or a recognizable legacy manual line is malformed. */
export function parseOwnerRecord(text) {
    if (typeof text !== 'string' || Buffer.byteLength(text) > maxOwnerBytes || text.includes('\0')) return { kind: 'malformed', reason: 'size-or-content' };
    const line = text.endsWith('\n') ? text.slice(0, -1) : text;
    if (!line || line.includes('\n') || line.includes('\r')) return { kind: 'malformed', reason: 'not-one-line' };
    const fields = {};
    for (const token of line.split(' ')) {
        const match = /^([a-z]+)=(.*)$/.exec(token);
        if (!match || Object.hasOwn(fields, match[1])) return { kind: 'malformed', reason: 'token' };
        fields[match[1]] = match[2];
    }
    if (!Object.hasOwn(fields, 'schema')) {
        // The retired manual protocol wrote `owner=<session> pid=... since=...`; it is informational and always "held".
        if (Object.hasOwn(fields, 'owner')) return { kind: 'legacy-manual', fields: Object.fromEntries(Object.entries(fields).slice(0, 8).map(([k, v]) => [bounded(k, 32), bounded(v)])) };
        return { kind: 'malformed', reason: 'unrecognized' };
    }
    return parseOwnedFields(fields);
}

/** Validate a schema-1 field map into a record; any unknown key, bad value or oversize field makes it malformed. */
function parseOwnedFields(fields) {
    const unknown = Object.keys(fields).filter(key => !recordKeySet.has(key));
    if (unknown.length || fields.schema !== String(desktopLockSchema) || fields.owner !== 'ntilde-terminfo') return { kind: 'malformed', reason: 'schema' };
    const decoded = {};
    for (const key of ['birth', 'worktree', 'entrypoint', 'since', 'failure']) {
        if (fields[key] === undefined) continue;
        decoded[key] = decodeValue(fields[key]);
        if (decoded[key] === undefined || Buffer.byteLength(decoded[key]) > maxFieldBytes) return { kind: 'malformed', reason: 'value' };
    }
    const markers = fields.markers === undefined ? [] : fields.markers.split(',').map(decodeValue);
    const pid = Number(fields.pid), uid = Number(fields.uid);
    const identityOk = leasePattern.test(fields.lease ?? '') && /^\d+$/.test(fields.pid ?? '') && Number.isSafeInteger(pid) && pid >= 1
        && /^-?\d+$/.test(fields.uid ?? '') && Boolean(decoded.birth) && Boolean(decoded.worktree) && /^[A-Za-z0-9._-]{1,64}$/.test(decoded.entrypoint ?? '');
    const stateOk = Number.isFinite(Date.parse(decoded.since ?? '')) && ['held', 'cleanup-failed'].includes(fields.status)
        && (fields.launch === undefined || ['none', 'attempted'].includes(fields.launch));
    const markersOk = markers.length <= 8 && markers.every(marker => typeof marker === 'string' && markerPattern.test(marker));
    if (!identityOk || !stateOk || !markersOk) return { kind: 'malformed', reason: 'fields' };
    return { kind: 'owned', record: {
        lease: fields.lease, pid, birth: decoded.birth, uid, worktree: decoded.worktree, entrypoint: decoded.entrypoint, since: decoded.since,
        status: fields.status, launch: fields.launch ?? 'none', markers, ...(decoded.failure === undefined ? {} : { failure: decoded.failure }),
    } };
}

// ---------------------------------------------------------------------------------------------------------------
// State inspection (read-only).

/** Classify what currently occupies the lock path without ever treating unreadable state as idle. */
export function readDesktopLockState(lockPath) {
    let info;
    try { info = lstatSync(lockPath); }
    catch (error) { return error.code === 'ENOENT' ? { kind: 'absent' } : { kind: 'unreadable', reason: bounded(error.code ?? error.message) }; }
    if (info.isSymbolicLink()) return { kind: 'foreign', reason: 'symlink' };
    if (info.isFile()) {
        // The pre-lease common-core driver created a regular JSON file here. It still means a collection owns the desktop.
        let summary = null;
        try {
            const parsed = JSON.parse(readFileSync(lockPath, 'utf8').slice(0, maxOwnerBytes));
            summary = Object.fromEntries(['owner', 'pid', 'runId', 'startedAt', 'status'].filter(key => parsed?.[key] !== undefined).map(key => [key, bounded(parsed[key])]));
        } catch { /* unreadable legacy file stays conservative */ }
        return { kind: 'legacy-file', summary };
    }
    if (!info.isDirectory()) return { kind: 'foreign', reason: 'not-a-directory-or-file' };
    let entries;
    try { entries = readdirSync(lockPath); }
    catch (error) { return { kind: 'unreadable', reason: bounded(error.code ?? error.message) }; }
    if (!entries.includes(ownerFile)) return { kind: 'partial', entries: entries.slice(0, 8).map(entry => bounded(entry, 64)) };
    let text;
    try {
        const ownerPath = join(lockPath, ownerFile), ownerInfo = lstatSync(ownerPath);
        if (!ownerInfo.isFile() || ownerInfo.size > maxOwnerBytes) return { kind: 'malformed', reason: 'owner-not-a-small-regular-file' };
        text = readFileSync(ownerPath, 'utf8');
    } catch (error) { return { kind: 'unreadable', reason: bounded(error.code ?? error.message) }; }
    const parsed = parseOwnerRecord(text);
    return { ...parsed, extraEntries: entries.length - 1 };
}

/** Decide whether the recorded holder process is demonstrably alive, demonstrably gone, or unknowable. */
export function assessDesktopHolder(record, depsInput) {
    const deps = withDeps(depsInput);
    let alive;
    try { alive = deps.processAlive(record.pid); }
    catch (error) { return { holder: 'ambiguous', reason: 'liveness-unavailable:' + bounded(error.code ?? error.message, 40) }; }
    if (!alive) return { holder: 'gone', reason: 'pid-absent' };
    let birth;
    try { birth = deps.processBirth(record.pid); }
    catch { return { holder: 'ambiguous', reason: 'birth-unavailable' }; }
    return birth === record.birth ? { holder: 'alive', reason: 'pid-and-start-match' } : { holder: 'gone', reason: 'pid-reused' };
}

/** Whether `candidate` is this process or one of its ancestors (bounded walk; unknown means "not proven"). */
export function isSelfOrAncestor(candidate, depsInput) {
    const deps = withDeps(depsInput);
    let current = deps.pid();
    for (let step = 0; step < 32; step++) {
        if (current === candidate) return true;
        if (current <= 1) return false;
        try { current = deps.parentPid(current); } catch { return false; }
    }
    return false;
}

const summarizeRecord = record => ({
    lease: record.lease, pid: record.pid, entrypoint: record.entrypoint, worktree: bounded(record.worktree, 200),
    since: record.since, status: record.status, launch: record.launch,
    ...(record.failure === undefined ? {} : { failure: bounded(record.failure, 200) }),
});

/** Bounded, offline-safe diagnostic. Never mutates; the recovery advice never guesses ownership. */
export function inspectDesktopLock(lockPathInput, depsInput) {
    const lockPath = lockPathInput ?? canonicalDesktopLockPath();
    const state = readDesktopLockState(lockPath);
    if (state.kind === 'absent') return { state: 'idle', lockPath, advice: 'No lock directory exists. Live validation of idleness still needs the coordinating sessions, because legacy collectors may not use the lock.' };
    if (state.kind === 'owned') {
        const assessment = assessDesktopHolder(state.record, depsInput);
        const ancestor = assessment.holder === 'alive' && isSelfOrAncestor(state.record.pid, depsInput);
        let advice = 'Held by a live collection. Wait for it; never delete this lock.';
        if (state.record.status === 'cleanup-failed') advice = 'The owning collection could not verify cleanup. Inspect the owned App/worker, then recover with the explicit command below.';
        else if (assessment.holder === 'gone') advice = 'The recorded holder is gone but its descendants may survive. Run `node scripts/terminfo-desktop-lock.mjs recover --lease ' + state.record.lease + '` to verify and preserve evidence.';
        else if (assessment.holder === 'ambiguous') advice = 'Holder liveness could not be established. Treat as held.';
        if (ancestor) advice = 'Held by this process or one of its ancestors: nested desktop collections are refused. Run entrypoints sequentially.';
        return { state: 'owned', holder: assessment.holder, holderReason: assessment.reason, ancestor, record: summarizeRecord(state.record), extraEntries: state.extraEntries, lockPath, advice };
    }
    const advice = {
        partial: 'A lock directory exists without an owner record (an interrupted acquisition or a legacy collector between mkdir and its owner write). Treated as held; resolve with the owning session or inspect manually.',
        malformed: 'The owner record is not a recognizable v1 lease. Treated as held; inspect manually.',
        'legacy-manual': 'Held under the retired manual protocol. Coordinate with that session; this tool cannot verify or recover it.',
        'legacy-file': 'A regular file from an older collector occupies the lock path. Treated as held; coordinate with its session.',
        foreign: 'Something unexpected occupies the lock path. Treated as held.',
        unreadable: 'The lock could not be read. Treated as held.',
    }[state.kind];
    return { state: state.kind, lockPath, detail: bounded(JSON.stringify({ reason: state.reason, fields: state.fields, summary: state.summary, entries: state.entries }), 400), advice };
}

// ---------------------------------------------------------------------------------------------------------------
// Audit log: an append-only record of real acquisitions, refusals and releases, so later audits have facts to cite.

function audit(lockPath, event, depsInput) {
    const deps = withDeps(depsInput);
    try {
        const path = desktopLockAuditPath(lockPath);
        try { if (statSync(path).size > auditLimitBytes) renameSync(path, path + '.1'); } catch { /* absent or racing: append anyway */ }
        // Every caller passes bounded fields, so a line stays small and always valid JSON.
        appendFileSync(path, JSON.stringify({ at: new Date(deps.now()).toISOString(), pid: deps.pid(), ...event }) + '\n', { mode: 0o600 });
    } catch (error) {
        console.error('[desktop-lock] audit log write failed: ' + bounded(error.code ?? error.message, 80));
    }
}

// ---------------------------------------------------------------------------------------------------------------
// The lease.

function replaceOwner(lockPath, lease, record) {
    const temp = join(lockPath, ownerFile + '.tmp-' + lease);
    writeFileSync(temp, formatOwnerRecord(record), { flag: 'wx', mode: 0o600 });
    try { renameSync(temp, join(lockPath, ownerFile)); }
    catch (error) { try { unlinkSync(temp); } catch { /* retained temp is bounded diagnostics only */ } throw error; }
}

export class DesktopLease {
    #lockPath; #record; #deps; launchAttempted = false; cleanupVerified = false; released = false;
    constructor(lockPath, record, deps) { this.#lockPath = lockPath; this.#record = record; this.#deps = deps; }
    get leaseId() { return this.#record.lease; }
    get lockPath() { return this.#lockPath; }
    get record() { return { ...this.#record, markers: [...this.#record.markers] }; }

    /** Re-read the lock and require that it is still exactly this lease held by this process. */
    assertHeld() {
        if (this.released) throw new Error('Desktop lease was already released.');
        const state = readDesktopLockState(this.#lockPath);
        if (state.kind !== 'owned' || state.record.lease !== this.#record.lease || state.record.pid !== this.#record.pid || state.record.birth !== this.#record.birth)
            throw new Error('Desktop collection lock ownership changed; this process no longer holds it.');
        return state.record;
    }

    /** Persist "a launch is about to be attempted" before it happens; failing to persist stops the launch. */
    noteLaunchAttempt() {
        const current = this.assertHeld();
        if (current.status !== 'held') throw new Error('Desktop lease is no longer healthy; refusing to launch.');
        this.#record = { ...this.#record, launch: 'attempted' };
        replaceOwner(this.#lockPath, this.#record.lease, this.#record);
        this.launchAttempted = true;
        audit(this.#lockPath, { event: 'launch-attempt', lease: this.#record.lease, entrypoint: this.#record.entrypoint }, this.#deps);
    }

    /** The caller verified cleanup by its own evidence (used by the common-core driver). */
    attestCleanup(source) {
        this.assertHeld();
        this.cleanupVerified = true;
        audit(this.#lockPath, { event: 'cleanup-verified', lease: this.#record.lease, by: bounded(source, 80) }, this.#deps);
    }

    /** Verify no process carrying this lease's run markers survives. A failed scan is a failed verification. */
    async verifyCleanup() {
        this.assertHeld();
        if (this.#record.markers.length === 0) throw new Error('This lease records no process markers; use attestCleanup with explicit evidence.');
        const survivors = await scanUntilClear(this.#record.markers, this.#deps, this.#deps.now() + cleanupScanBudgetMs);
        if (survivors.length > 0) throw new Error('Owned processes survive cleanup (pids ' + survivors.slice(0, 8).join(',') + '); the desktop lease is retained.');
        this.cleanupVerified = true;
        audit(this.#lockPath, { event: 'cleanup-verified', lease: this.#record.lease, by: 'marker-scan' }, this.#deps);
    }

    /** Keep the lock held and record why; the next contender is refused until an explicit recovery. */
    retain(reason) {
        this.assertHeld();
        this.#record = { ...this.#record, status: 'cleanup-failed', failure: bounded(reason, 400) };
        replaceOwner(this.#lockPath, this.#record.lease, this.#record);
        audit(this.#lockPath, { event: 'retained', lease: this.#record.lease, reason: bounded(reason, 300) }, this.#deps);
    }

    /** Ownership-checked release. Refuses outright while an attempted launch has no verified cleanup. */
    release() {
        if (this.launchAttempted && !this.cleanupVerified) throw new Error('Refusing to release the desktop lease: the launch has no verified cleanup.');
        this.assertHeld();
        const tombstone = this.#lockPath + '.release-' + this.#record.lease;
        renameSync(this.#lockPath, tombstone);
        const after = parseOwnerRecord(safeRead(join(tombstone, ownerFile)));
        if (after.kind !== 'owned' || after.record.lease !== this.#record.lease || after.record.pid !== this.#record.pid) {
            try { renameSync(tombstone, this.#lockPath); }
            catch { throw new Error('Desktop lock ownership changed during release and could not be restored; evidence kept at ' + tombstone + '.'); }
            throw new Error('Desktop lock ownership changed during release; the lock was restored untouched.');
        }
        this.released = true;
        try { unlinkSync(join(tombstone, ownerFile)); rmdirSync(tombstone); }
        catch (error) { console.error('[desktop-lock] released, but the tombstone ' + tombstone + ' could not be removed: ' + bounded(error.code ?? error.message, 80)); }
        audit(this.#lockPath, { event: 'released', lease: this.#record.lease, entrypoint: this.#record.entrypoint, launch: this.#record.launch }, this.#deps);
    }
}

function safeRead(path) {
    try { return readFileSync(path, 'utf8'); } catch { return ''; }
}

/** Poll for run-marker survivors until none remain or the budget ends; a failed scan is a failed verification. */
async function scanUntilClear(markers, deps, deadline) {
    let survivors;
    try { survivors = findMarkedProcesses(markers, deps); }
    catch (error) { throw new Error('Owned process scan failed, so cleanup is unverified: ' + bounded(error.code ?? error.message, 80)); }
    if (survivors.length === 0 || deps.now() >= deadline) return survivors;
    await deps.sleep(100);
    return scanUntilClear(markers, deps, deadline);
}

/** Pids whose command line carries any of the exact run markers (never the scanner itself). */
export function findMarkedProcesses(markers, depsInput) {
    const deps = withDeps(depsInput), self = deps.pid();
    return deps.listProcesses().filter(row => row.pid !== self
        && row.command.split(/\s+/).some(token => markers.includes(token))).map(row => row.pid);
}

const markerKinds = ['owner', 'worker', 'private-clipboard'];
/** The run markers a lease records: all three by default, or only the kinds an entrypoint really puts on its processes. */
const markersFor = (lease, kinds = markerKinds) => kinds.map(kind => `--ntilde-terminfo-${kind}=${lease}`);

/** A contender waits only for a verified live, healthy, non-ancestor holder; legacy and unverifiable states refuse at once. */
function isWaitableHolder(state, deps) {
    if (state.kind !== 'owned' || state.record.status !== 'held') return false;
    const assessment = assessDesktopHolder(state.record, deps);
    return assessment.holder === 'alive' && !isSelfOrAncestor(state.record.pid, deps);
}

function refuseContender(lockPath, entrypoint, deps) {
    const diagnostic = inspectDesktopLock(lockPath, deps);
    audit(lockPath, { event: 'refused', entrypoint, state: diagnostic.state, holder: diagnostic.holder, holderLease: diagnostic.record?.lease, holderPid: diagnostic.record?.pid }, deps);
    return new DesktopLockRefusal('Desktop collection lock is unavailable (' + diagnostic.state + (diagnostic.holder ? ', holder ' + diagnostic.holder : '')
        + (diagnostic.ancestor ? ', held by an ancestor of this process' : '') + '). No App was launched. ' + diagnostic.advice, diagnostic);
}

/** The directory was just created atomically: publish the owner record immediately, or give the directory back. */
function publishLease(lockPath, options, deps, start) {
    let birth = 'unavailable';
    try { birth = deps.processBirth(deps.pid()); } catch { /* an unprovable identity leaves later holders "ambiguous", i.e. conservative */ }
    const lease = randomBytes(16).toString('hex');
    const record = { lease, pid: deps.pid(), birth, uid: deps.uid(), worktree: options.worktree, entrypoint: options.entrypoint, since: new Date(deps.now()).toISOString(),
        status: 'held', launch: 'none', markers: markersFor(lease, options.markerKinds) };
    try { writeFileSync(join(lockPath, ownerFile), formatOwnerRecord(record), { flag: 'wx', mode: 0o600 }); }
    catch (error) {
        try { rmdirSync(lockPath); } catch { /* an unremovable empty directory stays "partial" = held */ }
        throw error;
    }
    audit(lockPath, { event: 'acquired', lease, entrypoint: options.entrypoint, worktree: bounded(options.worktree, 300), waitedMs: deps.now() - start }, deps);
    return new DesktopLease(lockPath, record, deps);
}

/** One contention decision: retry after a pause, or refuse. A partial directory first gets a short grace for a mid-acquisition peer. */
function decideContention(context) {
    const { lockPath, deps, waitMs, pollMs, start } = context;
    const state = readDesktopLockState(lockPath);
    if (state.kind === 'absent') return { retryAfterMs: 0 }; // released between mkdir and the read
    const now = deps.now();
    if (state.kind === 'partial') {
        context.partialSince ??= now;
        if (now - context.partialSince < partialGraceMs) return { retryAfterMs: Math.min(100, pollMs) };
    } else context.partialSince = undefined;
    if (now - start < waitMs && isWaitableHolder(state, deps)) return { retryAfterMs: Math.min(pollMs, waitMs - (now - start)) };
    return undefined;
}

async function acquireLoop(context) {
    const { lockPath, options, deps } = context;
    if (options.aborted?.()) throw new Error('Interrupted before the desktop lease was acquired.');
    try { mkdirSync(lockPath, { mode: 0o700 }); }
    catch (error) {
        if (error.code !== 'EEXIST') throw error;
        const decision = decideContention(context);
        if (!decision) throw refuseContender(lockPath, options.entrypoint, deps);
        await deps.sleep(decision.retryAfterMs);
        return acquireLoop(context);
    }
    return publishLease(lockPath, options, deps, context.start);
}

/**
 * Acquire exclusive ownership of the desktop. Returns only after `mkdir` succeeded and the owner record is written.
 * `waitMs` bounds how long a *live, verified, healthy* holder is waited for; every other occupied state refuses at once.
 */
export async function acquireDesktopLease(options) {
    const deps = withDeps(options.deps);
    const lockPath = options.lockPath ?? canonicalDesktopLockPath();
    const { entrypoint, worktree } = options;
    const waitMs = options.waitMs ?? 0, pollMs = options.pollMs ?? 1000;
    if (!/^[A-Za-z0-9._-]{1,64}$/.test(entrypoint ?? '') || typeof worktree !== 'string' || !worktree || Buffer.byteLength(worktree) > maxFieldBytes)
        throw new Error('Invalid desktop lease entrypoint or worktree.');
    if (!Number.isSafeInteger(waitMs) || waitMs < 0 || waitMs > maxWaitMs || !Number.isSafeInteger(pollMs) || pollMs < 1) throw new Error('Invalid desktop lease wait.');
    if (options.markerKinds !== undefined && (!Array.isArray(options.markerKinds) || options.markerKinds.length === 0 || new Set(options.markerKinds).size !== options.markerKinds.length
        || !options.markerKinds.every(kind => markerKinds.includes(kind)))) throw new Error('Invalid desktop lease marker kinds.');
    mkdirSync(dirname(lockPath), { recursive: true, mode: 0o700 });
    return acquireLoop({ lockPath, options, deps, waitMs, pollMs, start: deps.now(), partialSince: undefined });
}

/**
 * The launcher-owned broker. The measured runner may acquire (once, lazily, just before its first desktop-affecting
 * action) and report launch/cleanup facts through a facade that cannot release or retain; only `finish()`, called by
 * the trusted launcher after the runner returns, decides between release and retention.
 */
export function createDesktopLeaseBroker(options) {
    let lease, acquiring = false;
    const facade = Object.freeze({
        get leaseId() { return lease.leaseId; },
        assertHeld: () => lease.assertHeld(),
        noteLaunchAttempt: () => lease.noteLaunchAttempt(),
        verifyCleanup: () => lease.verifyCleanup(),
    });
    return {
        /** Resolve the facade; a second call is refused, never re-entrant. */
        async acquire() {
            if (lease || acquiring) throw new Error('The desktop lease was already requested by this process.');
            acquiring = true;
            try { lease = await acquireDesktopLease(options); }
            finally { acquiring = false; }
            return facade;
        },
        get acquired() { return lease !== undefined; },
        /** Never throws: reports whether the lock was released or deliberately retained, with the reason. */
        finish() {
            if (!lease) return { held: false };
            const cleanOutcome = !lease.launchAttempted || lease.cleanupVerified;
            try {
                if (cleanOutcome) { lease.release(); return { held: true, released: true, leaseId: lease.leaseId }; }
                lease.retain('Launch attempted but cleanup was not verified (unknown outcome or surviving owned process).');
                return { held: true, released: false, retained: true, leaseId: lease.leaseId };
            } catch (error) {
                return { held: true, released: false, retained: !cleanOutcome, leaseId: lease.leaseId, error: error.message };
            }
        },
    };
}

// ---------------------------------------------------------------------------------------------------------------
// Explicit, conservative recovery. Never automatic, never a delete: the lock is renamed aside with its evidence.

export function recoverDesktopLease(options) {
    const deps = withDeps(options.deps);
    const lockPath = options.lockPath ?? canonicalDesktopLockPath();
    if (!leasePattern.test(options.leaseId ?? '')) throw new Error('Supply the exact 32-hex lease id from the lock status.');
    const state = readDesktopLockState(lockPath);
    if (state.kind !== 'owned') throw new Error('Recovery only handles a well-formed v1 lease; this lock is ' + state.kind + '. Resolve it with its owning session or manually.');
    if (state.record.lease !== options.leaseId) throw new Error('The lock now belongs to a different lease; refusing to recover it.');
    if (state.extraEntries > 0) throw new Error('The lock directory holds unexpected extra entries; refusing automatic recovery.');
    const assessment = assessDesktopHolder(state.record, deps);
    if (assessment.holder !== 'gone') throw new Error('The recorded holder is ' + assessment.holder + ' (' + assessment.reason + '); refusing recovery.');
    if (state.record.markers.length === 0) throw new Error('The lease records no process markers, so surviving descendants cannot be excluded; refusing recovery.');
    let survivors;
    try { survivors = findMarkedProcesses(state.record.markers, deps); }
    catch { throw new Error('The process scan failed, so surviving descendants cannot be excluded; refusing recovery.'); }
    if (survivors.length) throw new Error('Processes carrying this lease\'s markers survive (pids ' + survivors.slice(0, 8).join(',') + '); stop them through their owner first.');
    // Windows owned by a dead App have no process to scan for: a cleanup-failed or launched lease needs the operator's word.
    if ((state.record.status === 'cleanup-failed' || state.record.launch === 'attempted') && options.confirmDesktopIdle !== true)
        throw new Error('This lease attempted a launch. Confirm no owned App window remains and pass --confirm-desktop-idle.');
    const aside = lockPath + '.recovered-' + state.record.lease + '-' + new Date(deps.now()).toISOString().replaceAll(/[^0-9TZ]/g, '') + '-' + randomBytes(3).toString('hex');
    renameSync(lockPath, aside);
    // The checks above are not atomic with the rename: a concurrent recovery plus a new collection could have put a different
    // live lease at this path in between. Verify what was actually moved and give a foreign lock straight back.
    const moved = parseOwnerRecord(safeRead(join(aside, ownerFile)));
    if (moved.kind !== 'owned' || moved.record.lease !== state.record.lease) {
        try { renameSync(aside, lockPath); }
        catch { throw new Error('A different lock was moved aside during recovery and could not be restored; evidence kept at ' + aside + '.'); }
        throw new Error('The lock changed during recovery; it was restored untouched.');
    }
    audit(lockPath, { event: 'recovered', lease: state.record.lease, holderPid: state.record.pid, holderReason: assessment.reason, confirmedIdle: options.confirmDesktopIdle === true, evidence: bounded(aside, 300) }, deps);
    return { recovered: state.record.lease, evidence: aside };
}
