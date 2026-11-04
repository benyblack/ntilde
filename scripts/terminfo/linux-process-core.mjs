// Internal provider/test seam. Production callers use linux-process-provider.mjs, never an alternate proc root.
const pidValue = value => Number.isSafeInteger(value) && value >= 1 && value <= 2147483647;
const decimal = value => typeof value === 'string' && /^(?:0|[1-9]\d*)$/.test(value);
const integer = (text, max = 2147483647n) => {
    if (!decimal(text) || BigInt(text) > max) throw new Error('Invalid Linux identity integer.');
    return Number(text);
};

/** comm is not a whitespace-delimited field and can contain parentheses/newlines. Keep start ticks lossless. */
export function parseLinuxStat(text, expectedPid) {
    if (typeof text !== 'string' || text.length > 8192 || text.includes('\0')) throw new Error('Invalid Linux stat.');
    const open = text.indexOf(' ('), close = text.lastIndexOf(') ');
    if (open < 1 || close <= open) throw new Error('Invalid Linux stat comm.');
    const pid = integer(text.slice(0, open));
    if (pid !== expectedPid) throw new Error('Linux stat PID changed.');
    const fields = text.slice(close + 2).trimEnd().split(' ');
    if (fields.length < 50 || fields.length > 64 || !/^[RSDTtIW]$/.test(fields[0])
        || fields.slice(1).some(value => !/^-?(?:0|[1-9]\d*)$/.test(value))) throw new Error('Invalid or dead Linux stat state/fields.');
    const start = fields[19];
    if (!decimal(start) || BigInt(start) > 18446744073709551615n) throw new Error('Invalid Linux start identity.');
    const tty = BigInt(fields[4]);
    if (tty < -2147483648n || tty > 2147483647n) throw new Error('Invalid Linux controlling device.');
    return { pid, ppid: integer(fields[1]), processGroupId: integer(fields[2]), sessionId: integer(fields[3]),
        startTimeTicks: start, controllingDevice: BigInt.asUintN(32, tty).toString(), state: fields[0] };
}

/** Real/effective/saved/filesystem UID must agree; require exactly one authoritative identity line. */
export function parseLinuxStatus(text, process) {
    if (typeof text !== 'string' || text.length > 32768 || text.includes('\0')) throw new Error('Invalid Linux status.');
    const field = name => {
        const rows = text.split('\n').filter(line => line.startsWith(name + ':'));
        if (rows.length !== 1) throw new Error('Missing or duplicate Linux status field.');
        return rows[0].slice(name.length + 1).trim().split(/\s+/);
    };
    for (const [name, value] of [['Pid', process.pid], ['Tgid', process.pid], ['PPid', process.ppid]]) {
        const values = field(name);
        if (values.length !== 1 || integer(values[0]) !== value) throw new Error('Linux status identity mismatch.');
    }
    const uids = field('Uid').map(value => integer(value, 4294967294n));
    if (uids.length !== 4 || !uids.every(value => value === uids[0])) throw new Error('Linux process credential mismatch.');
    return uids;
}

/** cmdline can be changed by the process. It is an exact comparison fact, not provenance. */
export function parseLinuxArgv(bytes) {
    if (!Buffer.isBuffer(bytes) || bytes.length < 2 || bytes.length > 32768 || bytes.at(-1) !== 0) throw new Error('Invalid Linux cmdline.');
    const text = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(bytes);
    const argv = text.slice(0, -1).split('\0');
    if (argv.length > 128 || !argv[0] || argv.some(value => value.length > 8192)) throw new Error('Invalid Linux argv.');
    return argv;
}

function copyIdentity(stat, kind, fields) {
    if (!stat || typeof stat[kind] !== 'function' || !stat[kind]()) throw new Error('Linux file type mismatch.');
    const result = {};
    for (const key of fields) {
        const value = stat[key];
        if (typeof value !== 'bigint') throw new Error('Missing lossless Linux file identity.');
        if (['dev', 'ino', 'rdev'].includes(key)) {
            // Node/libuv may expose unsigned 64-bit identities as signed int64 BigInts.
            if (value < -9223372036854775808n || value > 18446744073709551615n) throw new Error('Out-of-range Linux unsigned file identity.');
            result[key] = BigInt.asUintN(64, value).toString();
        } else {
            if (value < 0n) throw new Error('Invalid negative Linux file metadata.');
            result[key] = value.toString();
        }
    }
    return result;
}

export function linuxFileIdentity(stat, kind) {
    return copyIdentity(stat, kind, ['dev', 'ino', 'rdev', 'uid', 'mode', 'size', 'mtimeNs', 'ctimeNs']);
}

/** PTY traffic updates timestamps; keep device identity/owner/mode while executable checks retain timestamps. */
export function linuxDeviceIdentity(stat) {
    return copyIdentity(stat, 'isCharacterDevice', ['dev', 'ino', 'rdev', 'uid', 'mode']);
}

/** Kernel new_encode_dev format differs from userspace dev_t; do not compare raw integers. */
export function linuxControllingDevice(rdev) {
    if (typeof rdev !== 'bigint' || rdev < 0n || rdev > 18446744073709551615n) throw new Error('Invalid Linux dev_t.');
    const major = ((rdev >> 8n) & 0xfffn) | ((rdev >> 32n) & 0xfffff000n);
    const minor = (rdev & 0xffn) | ((rdev >> 12n) & 0xffffff00n);
    if (major > 4095n || minor > 1048575n) throw new Error('Linux device is outside kernel encoded range.');
    return ((minor & 255n) | (major << 8n) | ((minor & ~255n) << 12n)).toString();
}

function same(a, b) { return JSON.stringify(a) === JSON.stringify(b); }
function pathValue(path) {
    return typeof path === 'string' && path.startsWith('/') && path.length < 4096
        && !/[\x00-\x1f\x7f]/.test(path) && !path.endsWith(' (deleted)')
        && path.split('/').every((part, index) => index === 0 || (part && part !== '.' && part !== '..'));
}

function snapshot(reader, handle, pid, pty, query) {
    const read = (name, limit) => query(() => reader.read(handle, name, limit, query));
    const process = parseLinuxStat(new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(read('stat', 8192)), pid);
    const uids = parseLinuxStatus(new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(read('status', 32768)), process);
    const directory = linuxFileIdentity(query(() => reader.directoryStat(handle)), 'isDirectory');
    if (directory.uid !== String(uids[0])) throw new Error('Linux proc directory owner mismatch.');
    const argv = parseLinuxArgv(read('cmdline', 32768));
    const executable = query(() => reader.link(handle, 'exe'));
    if (!pathValue(executable) || query(() => reader.canonical(executable)) !== executable) throw new Error('Invalid Linux executable path.');
    const executableFile = linuxFileIdentity(query(() => reader.linkStat(handle, 'exe')), 'isFile');
    const namedExecutable = linuxFileIdentity(query(() => reader.pathStat(executable)), 'isFile');
    if (!same(executableFile, namedExecutable)) throw new Error('Linux executable path no longer identifies the mapped file.');
    const { state, ...stable } = process;
    const result = { platform: 'linux', ...stable, uids, argv, executable, executableFile, procDirectory: directory };
    if (pty) result.pty = ptySnapshot(reader, handle, process, uids[0], query);
    return result;
}

function ptySnapshot(reader, handle, process, uid, query) {
    const descriptors = [0, 1].map(fd => {
        const path = query(() => reader.link(handle, 'fd/' + fd));
        if (typeof path !== 'string' || !/^\/dev\/pts\/(?:0|[1-9]\d{0,6})$/.test(path) || Number(path.slice(9)) > 1048575) throw new Error('Linux descriptor is not a private PTY path.');
        const descriptor = linuxDeviceIdentity(query(() => reader.linkStat(handle, 'fd/' + fd)));
        const named = linuxDeviceIdentity(query(() => reader.pathStat(path)));
        if (descriptor.uid !== String(uid) || !same(descriptor, named)
            || linuxControllingDevice(BigInt(descriptor.rdev)) !== process.controllingDevice || process.controllingDevice === '0') {
            throw new Error('Linux PTY descriptor/path/controlling-device mismatch.');
        }
        return { fd, path, device: descriptor };
    });
    if (descriptors[0].path !== descriptors[1].path || !same(descriptors[0].device, descriptors[1].device)) throw new Error('Linux stdin and stdout are different PTYs.');
    return { selectedFd: 1, descriptors };
}

/** Internal deterministic seam. Readers are test objects, never CLI-controlled filesystem roots. */
export function readLinuxRecords(reader, requests, timeoutMs) {
    if (!Number.isSafeInteger(timeoutMs) || timeoutMs < 1 || timeoutMs > 4000 || !Array.isArray(requests)
        || requests.length < 1 || requests.length > 2 || requests.some(value => !pidValue(value.pid) || typeof value.pty !== 'boolean')) {
        throw new Error('Invalid Linux provider query.');
    }
    const deadline = reader.now() + BigInt(timeoutMs) * 1000000n;
    let operations = 0;
    const query = action => {
        if (++operations > 160 || reader.now() >= deadline) throw new Error('Linux provider query budget exhausted.');
        const value = action();
        if (reader.now() >= deadline) throw new Error('Linux provider query deadline exceeded.');
        return value;
    };
    const opened = [];
    try {
        for (const request of requests) query(() => {
            const handle = reader.open(request.pid, query);
            opened.push({ ...request, handle });
        });
        const before = opened.map(value => snapshot(reader, value.handle, value.pid, value.pty, query));
        for (let i = opened.length - 1; i >= 0; i--) {
            const value = opened[i];
            if (!same(before[i], snapshot(reader, value.handle, value.pid, value.pty, query))) throw new Error('Linux process identity changed during query.');
        }
        return before;
    } finally {
        let closeError;
        for (const value of opened.toReversed()) {
            try { reader.close(value.handle); } catch (error) { closeError ??= error; }
        }
        if (closeError) throw closeError;
    }
}

/** Exact expected launch values must come from the caller's own spawn, not these mutable observations. */
export function assertLinuxOwnedChild(parent, child, expected) {
    if (!expected || !pidValue(expected.parentPid) || !pidValue(expected.childPid) || !Number.isSafeInteger(expected.uid)
        || !pathValue(expected.executable) || !Array.isArray(expected.argv) || expected.argv.length < 1
        || !/^[a-f\d]{32}$/.test(expected.runId ?? '')) throw new Error('Invalid Linux child expectation.');
    if (parent.pid !== expected.parentPid || child.pid !== expected.childPid || child.ppid !== parent.pid
        || parent.uids.some(uid => uid !== expected.uid) || child.uids.some(uid => uid !== expected.uid)
        || child.executable !== expected.executable || !same(child.argv, expected.argv)
        || child.argv.filter(value => value === '--ntilde-terminfo-worker=' + expected.runId).length !== 1) {
        throw new Error('Linux child launch facts do not match the caller-owned spawn.');
    }
    return { parent, child, qualification: 'provider-foundation-only' };
}
