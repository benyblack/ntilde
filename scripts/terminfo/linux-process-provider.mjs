import { constants, openSync, closeSync, readSync, fstatSync, readlinkSync, statSync, lstatSync, realpathSync, statfsSync } from 'node:fs';
import { readLinuxRecords, assertLinuxOwnedChild } from './linux-process-core.mjs';

/** Fixed procfs only. No environment variable, CLI flag or public alternate-root/test-reader option. */
function reader() {
    if (process.platform !== 'linux') throw new Error('Linux process provider requires Linux.');
    const names = new Set(['stat', 'status', 'cmdline', 'exe', 'fd/0', 'fd/1']);
    const entry = (handle, name) => {
        if (!names.has(name)) throw new Error('Unsupported Linux proc entry.');
        return '/proc/self/fd/' + handle + '/' + name;
    };
    return {
        now: () => process.hrtime.bigint(),
        open(pid, query) {
            if (query(() => statfsSync('/proc', { bigint: true })).type !== 0x9fa0n) throw new Error('Expected Linux procfs.');
            return openSync('/proc/' + pid, constants.O_RDONLY | constants.O_DIRECTORY | constants.O_NOFOLLOW);
        },
        close: handle => closeSync(handle),
        directoryStat: handle => fstatSync(handle, { bigint: true }),
        link: (handle, name) => readlinkSync(entry(handle, name), { encoding: 'utf8' }),
        linkStat: (handle, name) => statSync(entry(handle, name), { bigint: true }),
        pathStat: path => lstatSync(path, { bigint: true }),
        canonical: path => realpathSync(path),
        read(handle, name, limit, query) {
            let fd;
            try {
                query(() => { fd = openSync(entry(handle, name), constants.O_RDONLY); });
                const buffer = Buffer.alloc(limit + 1);
                let used = 0;
                while (used < buffer.length) {
                    const count = query(() => readSync(fd, buffer, used, buffer.length - used, null));
                    if (count === 0) break;
                    used += count;
                }
                if (used > limit) throw new Error('Oversized Linux proc entry.');
                return buffer.subarray(0, used);
            } finally { if (fd !== undefined) closeSync(fd); }
        },
    };
}

function timeout(options) {
    if (!options || typeof options !== 'object' || Array.isArray(options) || Object.keys(options).some(key => key !== 'timeoutMs')) throw new Error('Unsupported Linux provider options.');
    return options.timeoutMs ?? 2000;
}

/** Point-in-time process facts; no signal, enumeration, launch, attestation or terminal qualification. */
export function readLinuxProcessFacts(pid, options = {}) {
    const source = reader();
    return readLinuxRecords(source, [{ pid, pty: false }], timeout(options))[0];
}

/** Same fixed-root query with stdin/stdout private PTY path/device/controlling-terminal checks. */
export function readLinuxPtyFacts(pid, options = {}) {
    const source = reader();
    return readLinuxRecords(source, [{ pid, pty: true }], timeout(options))[0];
}

/** Keep both proc directory handles live while rechecking parent and explicitly spawned child. */
export function readLinuxOwnedChildFacts(expected, options = {}) {
    const source = reader();
    const [parent, child] = readLinuxRecords(source, [{ pid: expected?.parentPid, pty: false },
        { pid: expected?.childPid, pty: true }], timeout(options));
    return assertLinuxOwnedChild(parent, child, expected);
}
