import { execFileSync } from 'node:child_process';
import { fstatSync, lstatSync, realpathSync, statSync } from 'node:fs';
import { createConnection } from 'node:net';
import { isatty } from 'node:tty';

export function processRecord(pid) {
    if (!Number.isSafeInteger(pid) || pid < 2) throw new Error('Invalid process ID.');
    const text = execFileSync('/bin/ps', ['-p', String(pid), '-o', 'pid=,ppid=,uid=,tty=,comm='], {
        encoding: 'utf8', timeout: 2000, maxBuffer: 8192,
    }).trim();
    return parseProcessRecord(text, pid);
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

export function requestOwner(config, kind, extra = {}) {
    return new Promise((resolve, reject) => {
        const socket = createConnection(config.socket);
        let buffer = '';
        const finish = (error, result) => {
            socket.destroy();
            if (error) reject(error); else resolve(result);
        };
        socket.setTimeout(5000, () => finish(new Error('Ntilde owner did not respond.')));
        socket.on('error', reject);
        socket.on('connect', () => socket.write(JSON.stringify({
            ...extra, kind, token: config.token, runId: config.runId, workerPid: process.pid,
        }) + '\n'));
        socket.on('data', chunk => {
            buffer += chunk;
            if (buffer.length > 16384) return finish(new Error('Oversized owner response.'));
            if (!buffer.includes('\n')) return;
            try {
                const response = JSON.parse(buffer.slice(0, buffer.indexOf('\n')));
                if (!response.ok) return finish(new Error(response.error ?? 'Ntilde ownership refused.'));
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

export async function verifyNtildeOutput(config, out) {
    if (process.platform !== 'darwin') throw new Error('This owned adapter currently supports macOS.');
    if (process.env.NTILDE_APPDATA_ROOT !== config.appData || process.env.TERMINFO_RUN_ID !== config.runId
        || process.env.TERM_PROGRAM !== 'Ntilde') throw new Error('Worker launch environment differs from this run.');
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
    return { ...binding, ownerPid: app.pid, workerPid: worker.pid, launchRunId: config.runId };
}
