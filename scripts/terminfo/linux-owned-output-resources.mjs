// Internal filesystem seam. Public callers cannot supply a filesystem, path policy or reader.
import * as nodeFs from 'node:fs';
import { createHash } from 'node:crypto';
import { isDeepStrictEqual } from 'node:util';
import { linuxFileIdentity, linuxDeviceIdentity } from './linux-process-core.mjs';
import { linuxOutputLimits } from './linux-owned-output-core.mjs';

export function createLinuxOutputResources(filesystem = nodeFs) {
    const { constants, openSync, closeSync, fstatSync, lstatSync, realpathSync, readSync, statfsSync } = filesystem;
    function closeFailedOpen(fd, error, observation) {
        let closeError;
        if (fd !== undefined) { try { closeSync(fd); } catch (failure) { closeError = failure; } }
        const failure = new AggregateError(closeError ? [error, closeError] : [error], 'Linux output resource admission failed.');
        Object.defineProperty(failure, 'linuxOutputOpenResource', { value: Object.freeze({ ...observation, closeConfirmed: fd === undefined || !closeError }) });
        throw failure;
    }
    function openTool(expected, budget) {
        let fd, observation = { kind: 'system-stty', path: expected.path };
        try {
            if (!Number.isInteger(constants.O_NOFOLLOW) || constants.O_NOFOLLOW === 0) throw new Error('Required Linux executable no-follow flag unavailable.');
            const canonical = budget.query(() => realpathSync(expected.path));
            if (!['/usr/bin/stty', '/bin/stty', '/usr/bin/gnustty'].includes(canonical)) throw new Error('Unexpected canonical system stty path.');
            budget.query(() => { fd = openSync(canonical, constants.O_RDONLY | constants.O_NOFOLLOW); });
            const before = budget.query(() => fstatSync(fd, { bigint: true }));
            const identity = Object.freeze(linuxFileIdentity(before, 'isFile'));
            if (before.uid !== 0n || (before.mode & 0o022n) !== 0n || (before.mode & 0o111n) === 0n
                || before.size < 4n || before.size > BigInt(linuxOutputLimits.elfBytes)) throw new Error('Untrusted or oversized system GNU stty file.');
            observation = { ...observation, path: canonical, file: identity };
            const buffer = Buffer.alloc(65536), hash = createHash('sha256');
            let used = 0, magic = Buffer.alloc(0);
            while (true) {
                const count = budget.query(() => readSync(fd, buffer, 0, buffer.length, null));
                if (count === 0) break;
                if (count > linuxOutputLimits.elfBytes - used) throw new Error('System stty ELF exceeds fixed byte budget.');
                if (magic.length < 4) magic = Buffer.concat([magic, buffer.subarray(0, Math.min(4 - magic.length, count))]);
                used += count; hash.update(buffer.subarray(0, count));
            }
            if (used !== Number(before.size) || !magic.equals(Buffer.from([0x7f, 0x45, 0x4c, 0x46]))) throw new Error('Expected a complete system stty ELF, not a script.');
            const sha256 = hash.digest('hex');
            if (sha256 !== expected.sha256) throw new Error('Pinned system stty SHA-256 mismatch.');
            const after = linuxFileIdentity(budget.query(() => fstatSync(fd, { bigint: true })), 'isFile');
            const named = linuxFileIdentity(budget.query(() => lstatSync(canonical, { bigint: true })), 'isFile');
            if (!isDeepStrictEqual(identity, after) || !isDeepStrictEqual(identity, named)) throw new Error('System stty identity changed during admission.');
            return Object.freeze({ fd, identity, observation: Object.freeze({ ...observation, sha256 }) });
        } catch (error) { closeFailedOpen(fd, error, observation); }
    }
    function openPty(expected, budget) {
        let fd, observation = { kind: 'held-private-pty', path: expected.path, device: expected.device };
        try {
            for (const flag of ['O_NOCTTY', 'O_NOFOLLOW', 'O_NONBLOCK']) {
                if (!Number.isInteger(constants[flag]) || constants[flag] === 0) throw new Error('Required Linux PTY opening flag unavailable.');
            }
            const parent = budget.query(() => lstatSync('/dev/pts', { bigint: true }));
            if (!parent.isDirectory() || parent.uid !== 0n || (parent.mode & 2n) !== 0n
                || budget.query(() => realpathSync('/dev/pts')) !== '/dev/pts'
                || budget.query(() => statfsSync('/dev/pts', { bigint: true })).type !== 0x1cd1n) throw new Error('Expected the fixed trusted devpts directory.');
            const named = linuxDeviceIdentity(budget.query(() => lstatSync(expected.path, { bigint: true })));
            if (!isDeepStrictEqual(named, expected.device)) throw new Error('Proved Linux PTY path changed before opening.');
            budget.query(() => { fd = openSync(expected.path, constants.O_RDWR | constants.O_NOCTTY | constants.O_NOFOLLOW | constants.O_NONBLOCK); });
            const identity = Object.freeze(linuxDeviceIdentity(budget.query(() => fstatSync(fd, { bigint: true }))));
            const after = linuxDeviceIdentity(budget.query(() => lstatSync(expected.path, { bigint: true })));
            if (!isDeepStrictEqual(identity, expected.device) || !isDeepStrictEqual(identity, after)) throw new Error('Held Linux PTY is not the proved device.');
            return Object.freeze({ fd, identity, observation: Object.freeze(observation) });
        } catch (error) { closeFailedOpen(fd, error, observation); }
    }
    return {
        openTool, openPty,
        checkTool: held => {
            if (!isDeepStrictEqual(linuxFileIdentity(fstatSync(held.fd, { bigint: true }), 'isFile'), held.identity)) throw new Error('Held stty file identity changed.');
        },
        checkPty: held => {
            // TTY I/O changes timestamps. The existing provider helper deliberately excludes them.
            if (!isDeepStrictEqual(linuxDeviceIdentity(fstatSync(held.fd, { bigint: true })), held.identity)) throw new Error('Held PTY identity changed.');
        },
        close: held => closeSync(held.fd), // Never retry an ambiguous numeric-FD close.
    };
}

export const linuxOutputResources = createLinuxOutputResources();
