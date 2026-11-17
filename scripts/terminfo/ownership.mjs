import { execFileSync, spawnSync } from 'node:child_process';
import { fstatSync, lstatSync, readFileSync, realpathSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { createConnection } from 'node:net';
import { isatty } from 'node:tty';

export function processRecord(pid, timeout = 2000) {
    if (!Number.isSafeInteger(pid) || pid < 2) throw new Error('Invalid process ID.');
    const text = execFileSync('/bin/ps', ['-p', String(pid), '-o', 'pid=,ppid=,uid=,tty=,comm='], {
        encoding: 'utf8', timeout, maxBuffer: 8192,
    }).trim();
    return parseProcessRecord(text, pid);
}

/** UTC/C-locale, second-resolution corroboration only; not immutable PID identity or a held lease. */
export function processBirth(pid, timeout = 2000, run = execFileSync) {
    if (!Number.isSafeInteger(pid) || pid < 2) throw new Error('Invalid process ID.');
    const value = run('/bin/ps', ['-p', String(pid), '-o', 'lstart='], {
        encoding: 'utf8', timeout, maxBuffer: 256,
        env: { ...process.env, LC_ALL: 'C', LANG: 'C', TZ: 'UTC' },
    }).trim();
    if (!/^[A-Za-z]{3} [A-Za-z]{3} +\d{1,2} \d{2}:\d{2}:\d{2} \d{4}$/.test(value)) throw new Error('Could not corroborate process start time.');
    return value;
}
/** Read the canonical private macOS character-device identity; this is not a held descriptor or PTY lease. */
export function outputDevice(tty) {
    if (typeof tty !== 'string' || !/^\/dev\/ttys\d+$/.test(tty)) throw new Error('Invalid private PTY.');
    const stat = statSync(tty, { bigint: true });
    if (!stat.isCharacterDevice()) throw new Error('Private PTY is not a device.');
    return device(stat);
}

export function parseProcessRecord(text, pid = '?') {
    const fields = [];
    let offset = 0;
    for (const match of text.matchAll(/\S+/g)) {
        fields.push(match[0]);
        offset = match.index + match[0].length;
        if (fields.length === 4) break;
    }
    const executable = text.slice(offset).trim();
    if (fields.length !== 4 || !fields.slice(0, 3).every(field => /^\d+$/.test(field)) || !executable) {
        throw new Error(`Could not identify process ${pid}.`);
    }
    return { pid: Number(fields[0]), ppid: Number(fields[1]), uid: Number(fields[2]), tty: fields[3], executable };
}

/**
 * Whether a user-owned process's `ps eww` text (command line, then environment) carries exactly one `NAME=` assignment and it is `NAME=value`.
 * The value is matched as a whole against the known expected one, so a path with spaces is neither split nor guessed.
 */
export function environmentHas(text, name, value) {
    const needle = ` ${name}=`, first = text.indexOf(needle);
    if (first < 0 || text.includes(needle, first + 1)) return false;
    const end = first + needle.length + value.length;
    return text.startsWith(value, first + needle.length) && (end === text.length || text[end] === ' ');
}

/** Whether the command line is exactly the launched executable (spaces included) serving: `<executable> mux serve`, then arguments or the environment. */
export function isDaemonCommand(text, executable) {
    const prefix = `${executable} mux serve`;
    return text.startsWith(prefix) && (text.length === prefix.length || text[prefix.length] === ' ');
}

const daemonBirthMs = birth => Date.parse(birth + ' UTC');

/**
 * Read the facts that identify the multiplexer daemon hosting a persistent collection's worker: its process record, command line and
 * environment, both start times and the descriptor it wrote inside the run's private app-data root. Observations only; the verdict is
 * assertPersistentDaemon's.
 */
export function readPersistentDaemon(daemonPid, appPid, appData, timeout = 2000, {
    record = processRecord, birth = processBirth, canonical = realpathSync, run = execFileSync, status = lstatSync, read = readFileSync,
} = {}) {
    const daemon = record(daemonPid, timeout);
    const text = run('/bin/ps', ['eww', '-p', String(daemonPid), '-o', 'command='], { encoding: 'utf8', timeout, maxBuffer: 262144 }).trim();
    const path = join(appData, 'mux', 'mux-endpoint.json');
    const stat = status(path);
    if (!stat.isFile() || stat.isSymbolicLink()) throw new Error('The multiplexer descriptor is not a regular file.');
    const descriptor = JSON.parse(read(path, 'utf8'));
    return { record: { ...daemon, executable: canonical(daemon.executable) }, text, birth: birth(daemonPid, timeout), appBirth: birth(appPid, timeout),
        descriptor: { pid: descriptor.pid, processName: descriptor.processName, endpoint: descriptor.endpoint } };
}

/**
 * A persistent collection's worker is a child of the multiplexer daemon, which the App starts detached, so the worker is never the App's
 * own child. The same owner is proved instead by everything a foreign daemon could not have: the exact launched executable running
 * `mux serve`, started no earlier than the owned App, with this run's private app-data root and run identity in its environment, and
 * advertising itself through a descriptor inside that private root.
 */
export function assertPersistentDaemon({ app, worker, daemon, executable, uid, runId, appData }) {
    if (worker.ppid !== daemon.record.pid) throw new Error('Worker is not a direct child of the owned multiplexer daemon.');
    if (daemon.record.uid !== uid || app.uid !== uid) throw new Error('Multiplexer daemon belongs to another user.');
    if (daemon.record.executable !== executable || !isDaemonCommand(daemon.text, executable)) throw new Error('Multiplexer daemon is not the launched executable serving.');
    if (!environmentHas(daemon.text, 'NTILDE_APPDATA_ROOT', appData) || !environmentHas(daemon.text, 'TERMINFO_RUN_ID', runId)) {
        throw new Error('Multiplexer daemon environment differs from this run.');
    }
    const { descriptor } = daemon;
    if (descriptor.pid !== daemon.record.pid || typeof descriptor.endpoint !== 'string' || !descriptor.endpoint.startsWith(appData + '/')) {
        throw new Error('Multiplexer descriptor does not name this daemon inside the private app-data root.');
    }
    const [started, appStarted] = [daemonBirthMs(daemon.birth), daemonBirthMs(daemon.appBirth)];
    if (!Number.isFinite(started) || !Number.isFinite(appStarted) || started < appStarted) throw new Error('Multiplexer daemon predates the owned app.');
}

export function assertProcessOwnership({ app, worker, ownerPid, executable, uid, runId, appCommand, tty, daemon, appData }) {
    if (!/^[a-f0-9]{32}$/.test(runId)) throw new Error('Invalid launch run ID.');
    if (app.pid !== ownerPid) throw new Error('Worker is not a direct child of the owned app.');
    if (daemon === undefined) {
        if (worker.ppid !== app.pid) throw new Error('Worker is not a direct child of the owned app.');
    } else assertPersistentDaemon({ app, worker, daemon, executable, uid, runId, appData });
    if (app.uid !== uid || worker.uid !== uid) throw new Error('App or worker belongs to another user.');
    if (app.executable !== executable) throw new Error('App executable differs from the launched executable.');
    if (!appCommand.split(/\s+/).includes(`--ntilde-terminfo-owner=${runId}`)) throw new Error('App has no matching run marker.');
    if (!/^tty[a-zA-Z0-9._-]+$/.test(worker.tty) || tty !== `/dev/${worker.tty}`) {
        throw new Error('Worker controlling TTY differs from its claimed output.');
    }
}

export function matchesRecordedWorker(current, recorded, command, runId) {
    if (!current || !recorded || !/^[a-f\d]{32}$/.test(runId)) return false;
    return current.pid === recorded.pid && current.uid === recorded.uid
        && current.executable === recorded.executable
        && /^tty[a-zA-Z0-9._-]+$/.test(recorded.tty)
        // A PTY hangup can detach the controlling terminal before cleanup runs.
        && (current.tty === recorded.tty || current.tty === '??')
        && command.split(/\s+/).includes(`--ntilde-terminfo-worker=${runId}`);
}

export function workerExists(pid) {
    try { process.kill(pid, 0); return true; }
    catch (error) {
        if (error.code === 'ESRCH') { return false; }
        throw error;
    }
}

function verifyRecordedWorker(recorded, runId) {
    if (!workerExists(recorded.pid)) return false;
    try {
        const current = processRecord(recorded.pid, 500);
        const command = execFileSync('/bin/ps', ['-p', String(recorded.pid), '-o', 'command='], {
            encoding: 'utf8', timeout: 500, maxBuffer: 16384,
        });
        if (!matchesRecordedWorker(current, recorded, command, runId)) {
            throw new Error('Worker identity changed; refusing to signal an unverified process.');
        }
        return true;
    } catch (error) {
        // The process can exit between the existence check and either query.
        if (!workerExists(recorded.pid)) return false;
        throw error;
    }
}

function waitForWorkerExit(pid) {
    const deadline = performance.now() + 2000;
    return new Promise((resolve, reject) => {
        const check = () => {
            try {
                if (!workerExists(pid)) return resolve(true);
                if (performance.now() >= deadline) return resolve(false);
                setTimeout(check, 25);
            } catch (error) { reject(error); }
        };
        check();
    });
}

export async function terminateRecordedWorker(recorded, runId) {
    if (!recorded) return;
    for (const signal of ['SIGTERM', 'SIGKILL']) {
        if (!verifyRecordedWorker(recorded, runId)) return;
        try { process.kill(recorded.pid, signal); }
        catch (error) {
            if (error.code === 'ESRCH') { return; }
            throw error;
        }
        if (await waitForWorkerExit(recorded.pid)) return;
    }
    throw new Error(`Owned worker ${recorded.pid} did not exit after SIGKILL.`);
}

/**
 * Exchange one newline-delimited capability request on the configured owner socket.
 * The five-second socket timeout and per-kind response cap reject missing/oversized replies;
 * only the explicit initial-unavailability codes survive a rejected owner response.
 */
/** The observation kinds (`<prefix>-<operation>`) whose answers may carry complete screens and metadata: they get the 512 KiB response cap, every other kind 16 KiB. */
const largeResponsePrefixes = new Set(['cell', 'sgr', 'rgb', 'rendition', 'cursor', 'pixel', 'color', 'erase', 'editing', 'history', 'resize', 'saved', 'metadata', 'input', 'images',
    'sels', 'cole', 'csum', 'lrmm', 'crst', 'shi', 'vsc', 'lnk', 'tst', 'ost', 'xgtc', 'osn', 'ntf', 'dvq', 'csz', 'xcs', 'xss', 'xsv', 'kcl', 'ocx', 'cw', 'irm', 'kan', 'ovu']);
function largeResponsePrefix(kind) {
    return typeof kind === 'string' && kind.includes('-') && largeResponsePrefixes.has(kind.slice(0, kind.indexOf('-')));
}

function preservedOwnerCode(kind, code) {
    if (code === 'screenDetailsUnavailable') return true;
    const geometry = { 'resize-start': 'resizeGeometryUnavailable', 'input-start': 'inputGeometryUnavailable',
        'images-start': 'imageGeometryUnavailable', 'kan-start': 'imageGeometryUnavailable', 'saved-start': 'savedGeometryUnavailable' };
    if (geometry[kind] === code && code !== undefined) return true;
    return ['metadata-start', 'shi-start', 'vsc-start', 'lnk-start', 'tst-start', 'ost-start', 'osn-start', 'ntf-start', 'ocx-start', 'ovu-start'].includes(kind)
        && ['paneMetadataUnavailable', 'metadataGeometryUnavailable'].includes(code);
}

export function requestOwner(config, kind, extra = {}) {
    return new Promise((resolve, reject) => {
        const socket = createConnection(config.socket);
        socket.setEncoding('utf8');
        let buffer = '';
        /** Destroy this request's socket before resolving its result or rejecting the supplied error. */
        const finish = (error, result) => {
            socket.destroy();
            if (error) reject(error); else resolve(result);
        };
        socket.setTimeout(5000, () => finish(new Error('Ntilde owner did not respond.')));
        socket.on('error', reject);
        socket.on('connect', () => socket.write(JSON.stringify({
            ...extra, kind, token: config.token, runId: config.runId, workerPid: process.pid,
        }) + '\n'));
        /** Parse the first capped response line; malformed or refused replies never become ownership evidence. */
        socket.on('data', chunk => {
            buffer += chunk;
            const limit = largeResponsePrefix(kind) ? 512 * 1024 : 16384;
            if (Buffer.byteLength(buffer) > limit) return finish(new Error('Oversized owner response.'));
            if (!buffer.includes('\n')) return;
            try {
                const response = JSON.parse(buffer.slice(0, buffer.indexOf('\n')));
                if (!response.ok) {
                    const error = new Error(response.error ?? 'Ntilde ownership refused.');
                    if (preservedOwnerCode(kind, response.code)) error.code = response.code;
                    return finish(error);
                }
                finish(null, response);
            } catch (error) { finish(error); }
        });
        socket.on('end', () => { if (!buffer.includes('\n')) finish(new Error('Incomplete owner response.')); });
    });
}

function device(stat) {
    return { dev: String(stat.dev), rdev: String(stat.rdev), ino: String(stat.ino) };
}

export function bindOutput(out, tty) {
    if (out !== process.stdout || out.fd !== 1 || !isatty(0) || !isatty(1)) {
        throw new Error('Owned Ntilde worker requires real stdin and stdout PTYs.');
    }
    const input = fstatSync(0, { bigint: true });
    const output = fstatSync(1, { bigint: true });
    const claimed = statSync(tty, { bigint: true });
    if (![input, output, claimed].every(stat => stat.isCharacterDevice())
        || JSON.stringify(device(input)) !== JSON.stringify(device(output))
        || JSON.stringify(device(output)) !== JSON.stringify(device(claimed))) {
        throw new Error('Input, output and claimed Ntilde PTY devices differ.');
    }
    return {
        platform: 'darwin', selectedFd: 1, controllingTty: tty.slice('/dev/'.length), tabTty: tty,
        inputDevice: device(input), outputDevice: device(output),
    };
}

export function assertPrivateFile(path) {
    const stat = lstatSync(path);
    if (!stat.isFile() || stat.isSymbolicLink() || stat.uid !== process.getuid() || (stat.mode & 0o077) !== 0) {
        throw new Error('Worker configuration must be a private file owned by this user.');
    }
}

/**
 * Bind the worker's actual stdin/stdout device to a freshly acknowledged parent proof.
 * Refuse a foreign launch environment, executable, ancestry, UID or run marker before
 * returning the binding; private mode also requires the original kernel-mode token.
 * These process observations do not create an immutable PID or held PTY lease.
 */
export async function verifyNtildeOutput(config, out) {
    if (process.platform !== 'darwin') throw new Error('This owned adapter currently supports macOS.');
    if (process.env.NTILDE_APPDATA_ROOT !== config.appData || process.env.TERMINFO_RUN_ID !== config.runId
        || process.env.TERM_PROGRAM !== 'Ntilde'
        || process.argv[3] !== `--ntilde-terminfo-worker=${config.runId}`) throw new Error('Worker launch environment differs from this run.');
    const worker = processRecord(process.pid);
    const tty = `/dev/${worker.tty}`;
    const binding = bindOutput(out, tty);
    const receipt = await requestOwner(config, 'prove', { tty });
    const app = processRecord(receipt.ownerPid);
    const command = execFileSync('/bin/ps', ['-p', String(app.pid), '-o', 'command='], {
        encoding: 'utf8', timeout: 2000, maxBuffer: 16384,
    });
    const daemon = config.collectionScope?.persistence === 'KeepOnClose' ? readPersistentDaemon(worker.ppid, app.pid, config.appData) : undefined;
    assertProcessOwnership({ app: { ...app, executable: realpathSync(app.executable) }, worker,
        ownerPid: receipt.ownerPid, executable: config.executable, uid: process.getuid(),
        runId: config.runId, appCommand: command, tty, daemon, appData: config.appData });
    if (config.privateClipboard === true && (receipt.privateClipboard !== true || typeof receipt.originalTerminalState !== 'string')) throw new Error('Missing original private terminal state.');
    return { ...binding, ownerPid: app.pid, workerPid: worker.pid, launchRunId: config.runId,
        ...(config.privateClipboard === true ? { originalTerminalState: receipt.originalTerminalState } : {}) };
}

/** The facts a cleanup must re-verify before signalling: no pid is ever signalled on its number alone. */
export function persistentDaemonIdentity(daemon) {
    return { pid: daemon.record.pid, uid: daemon.record.uid, executable: daemon.record.executable };
}

/** Whether a command-plus-environment text is this run's daemon: the launched executable serving, with this root and this run in its environment. */
function isRunDaemon(text, { executable, appData, runId }) {
    return isDaemonCommand(text, executable) && environmentHas(text, 'NTILDE_APPDATA_ROOT', appData) && environmentHas(text, 'TERMINFO_RUN_ID', runId);
}

/**
 * Every process of this user that is this run's daemon, found by scanning command lines and environments. The descriptor is not consulted:
 * a daemon interrupted before it published one, or one whose record was never taken, is found the same way. A failed scan throws, so cleanup
 * is never reported verified without having looked.
 */
export function discoverPersistentDaemons(context, { run = execFileSync, uid = process.getuid() } = {}) {
    const listing = run('/bin/ps', ['-axww', '-E', '-o', 'pid=,uid=,command='], { encoding: 'utf8', timeout: 10000, maxBuffer: 64 * 1024 * 1024 });
    const found = [];
    for (const line of listing.split('\n')) {
        const match = /^ *(\d+) +(\d+) (.*)$/.exec(line);
        if (match && Number(match[2]) === uid && isRunDaemon(match[3].trimStart(), context)) found.push({ pid: Number(match[1]), uid, executable: context.executable });
    }
    return found;
}

function currentDaemon(recorded, context, timeout = 500) {
    const record = processRecord(recorded.pid, timeout);
    const text = execFileSync('/bin/ps', ['eww', '-p', String(recorded.pid), '-o', 'command='], { encoding: 'utf8', timeout, maxBuffer: 262144 }).trim();
    return record.uid === recorded.uid && record.executable === recorded.executable && recorded.executable === context.executable && isRunDaemon(text, context);
}

function verifyRecordedDaemon(recorded, context) {
    if (!workerExists(recorded.pid)) return false;
    try {
        if (!currentDaemon(recorded, context)) throw new Error('Multiplexer daemon identity changed; refusing to signal an unverified process.');
        return true;
    } catch (error) {
        if (!workerExists(recorded.pid)) return false;
        throw error;
    }
}

function daemonExits(pid, milliseconds) {
    const deadline = performance.now() + milliseconds;
    return new Promise(resolve => {
        const check = () => {
            if (!workerExists(pid)) return resolve(true);
            if (performance.now() >= deadline) return resolve(false);
            setTimeout(check, 50);
        };
        check();
    });
}

/** Signal the daemon with each escalation in turn, re-proving it is this run's before every one; the first that ends it settles. */
async function escalateDaemon(recorded, context, [signal, ...later]) {
    if (signal === undefined) throw new Error(`Owned multiplexer daemon ${recorded.pid} did not exit after SIGKILL.`);
    if (!verifyRecordedDaemon(recorded, context)) return;
    try { process.kill(recorded.pid, signal); }
    catch (error) {
        if (error.code === 'ESRCH') return;
        throw error;
    }
    if (await daemonExits(recorded.pid, 2000)) return;
    await escalateDaemon(recorded, context, later);
}

async function stopDaemon(recorded, context, stop) {
    if (!verifyRecordedDaemon(recorded, context)) return;
    stop(context.executable, ['mux', 'kill-server', '--force'], { env: { ...process.env, NTILDE_APPDATA_ROOT: context.appData },
        timeout: 15000, stdio: 'ignore', windowsHide: true });
    if (await daemonExits(recorded.pid, 5000)) return;
    await escalateDaemon(recorded, context, ['SIGTERM', 'SIGKILL']);
}

/**
 * Stop the multiplexer daemon(s) a persistent collection started. A daemon outlives the App and the worker by design, so the owned cleanup
 * asks each one to shut down through its own private root and then signals it only while it is still verifiably this run's. The recorded
 * identity and a scan of this user's processes name the candidates; a scan that still finds one afterwards fails the cleanup.
 */
export async function terminateRecordedDaemon(recorded, context, { stop = spawnSync, discover = discoverPersistentDaemons } = {}) {
    const candidates = new Map(discover(context).map(daemon => [daemon.pid, daemon]));
    if (recorded) candidates.set(recorded.pid, recorded);
    await Promise.all([...candidates.values()].map(daemon => stopDaemon(daemon, context, stop)));
    const survivors = discover(context);
    if (survivors.length > 0) throw new Error(`Owned multiplexer daemon ${survivors[0].pid} survived cleanup.`);
}
