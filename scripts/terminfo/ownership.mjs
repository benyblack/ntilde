import { execFileSync } from 'node:child_process';
import { fstatSync, lstatSync, realpathSync, statSync } from 'node:fs';
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

export function assertProcessOwnership({ app, worker, ownerPid, executable, uid, runId, appCommand, tty }) {
    if (!/^[a-f0-9]{32}$/.test(runId)) throw new Error('Invalid launch run ID.');
    if (app.pid !== ownerPid || worker.ppid !== app.pid) throw new Error('Worker is not a direct child of the owned app.');
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
    'sels', 'cole', 'csum', 'lrmm', 'crst', 'shi', 'lnk', 'tst', 'ost', 'cw']);
function largeResponsePrefix(kind) {
    return typeof kind === 'string' && kind.includes('-') && largeResponsePrefixes.has(kind.slice(0, kind.indexOf('-')));
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
                    if (response.code === 'screenDetailsUnavailable'
                        || (kind === 'resize-start' && response.code === 'resizeGeometryUnavailable')
                        || (kind === 'input-start' && response.code === 'inputGeometryUnavailable')
                        || (kind === 'images-start' && response.code === 'imageGeometryUnavailable')
                        || (kind === 'saved-start' && response.code === 'savedGeometryUnavailable')
                        || (kind === 'metadata-start' && ['paneMetadataUnavailable', 'metadataGeometryUnavailable'].includes(response.code))
                        || (kind === 'shi-start' && ['paneMetadataUnavailable', 'metadataGeometryUnavailable'].includes(response.code))
                        || (kind === 'lnk-start' && ['paneMetadataUnavailable', 'metadataGeometryUnavailable'].includes(response.code))
                        || (kind === 'tst-start' && ['paneMetadataUnavailable', 'metadataGeometryUnavailable'].includes(response.code))) error.code = response.code;
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
    assertProcessOwnership({ app: { ...app, executable: realpathSync(app.executable) }, worker,
        ownerPid: receipt.ownerPid, executable: config.executable, uid: process.getuid(),
        runId: config.runId, appCommand: command, tty });
    if (config.privateClipboard === true && (receipt.privateClipboard !== true || typeof receipt.originalTerminalState !== 'string')) throw new Error('Missing original private terminal state.');
    return { ...binding, ownerPid: app.pid, workerPid: worker.pid, launchRunId: config.runId,
        ...(config.privateClipboard === true ? { originalTerminalState: receipt.originalTerminalState } : {}) };
}
