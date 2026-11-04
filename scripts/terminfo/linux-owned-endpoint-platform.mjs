// Internal filesystem/spawn seams only. The public entry point supplies fixed production dependencies.
import * as nodeFs from 'node:fs';
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { isDeepStrictEqual } from 'node:util';
import { fileURLToPath } from 'node:url';
import { linuxFileIdentity } from './linux-process-core.mjs';
import { endpointIdentity, linuxEndpointLimits } from './linux-owned-endpoint-core.mjs';

export const linuxEndpointHelperSha256 = '759c32cd4f8a95931b9ac7f1fbae7ad9ebcb60c5057b198368d754980cd202fb';
const sourcePath = fileURLToPath(new URL('./linux-endpoint-probe.py', import.meta.url));
const stable = stat => endpointIdentity(Object.fromEntries(['dev', 'ino', 'uid', 'mode'].map(key => [key, stat[key].toString()])));

export function createEndpointResources(fs = nodeFs) {
    const { constants } = fs;
    function hashFile(fd, kind, expected, before, budget) {
        const limit = kind === 'python' ? linuxEndpointLimits.interpreterBytes : linuxEndpointLimits.helperBytes;
        if (before.uid !== BigInt(kind === 'python' ? 0 : expected.uid) || (before.mode & 0o022n) !== 0n
            || before.size < 4n || before.size > BigInt(limit) || (kind === 'python' && (before.mode & 0o111n) === 0n)) throw new Error('Invalid pinned endpoint tool/source file.');
        const buffer = Buffer.alloc(65536), hash = createHash('sha256');
        let used = 0, magic = Buffer.alloc(0);
        while (true) {
            const count = budget.query(() => fs.readSync(fd, buffer, 0, buffer.length, null));
            if (count === 0) break;
            if (count > limit - used) throw new Error('Endpoint tool/source byte limit exceeded.');
            if (magic.length < 4) magic = Buffer.concat([magic, buffer.subarray(0, Math.min(4 - magic.length, count))]);
            used += count; hash.update(buffer.subarray(0, count));
        }
        const sha256 = hash.digest('hex'), pin = kind === 'python' ? expected.python.sha256 : linuxEndpointHelperSha256;
        if (used !== Number(before.size) || sha256 !== pin
            || (kind === 'python' && !magic.equals(Buffer.from([0x7f, 69, 76, 70])))) throw new Error('Endpoint executable/helper digest or ELF mismatch.');
        return sha256;
    }
    function open(kind, expected, budget) {
        let fd, observation = { kind };
        try {
            const directory = kind === 'directory';
            for (const flag of directory ? ['O_DIRECTORY', 'O_NOFOLLOW'] : ['O_NOFOLLOW']) {
                if (!Number.isInteger(constants[flag]) || constants[flag] === 0) throw new Error('Required endpoint open flag unavailable.');
            }
            let path = directory ? expected.appDataDirectory.path : sourcePath;
            if (kind === 'python') {
                path = budget.query(() => fs.realpathSync(expected.python.path));
                if (!/^\/usr\/bin\/python3(?:\.\d{1,2})?$/.test(path)) throw new Error('Unexpected canonical system Python path.');
            }
            if (directory && budget.query(() => fs.realpathSync(path)) !== path) throw new Error('Endpoint directory contains a symlink.');
            observation = { kind, path };
            budget.query(() => { fd = fs.openSync(path, constants.O_RDONLY | constants.O_NOFOLLOW | (directory ? constants.O_DIRECTORY : 0)); });
            const before = budget.query(() => fs.fstatSync(fd, { bigint: true }));
            let identity;
            if (directory) {
                identity = stable(before);
                if (!before.isDirectory() || !isDeepStrictEqual(identity, expected.appDataDirectory.device)) throw new Error('Held endpoint directory differs from creation proof.');
            } else {
                identity = Object.freeze(linuxFileIdentity(before, 'isFile'));
                observation = { ...observation, file: identity };
                const sha256 = hashFile(fd, kind, expected, before, budget);
                observation = { ...observation, sha256 };
            }
            const resource = Object.freeze({ kind, fd, identity, observation: Object.freeze({ ...observation, identity }) });
            checkOne(resource, expected, budget);
            return resource;
        } catch (error) {
            const errors = [error]; let closed = fd === undefined;
            if (fd !== undefined) {
                try { fs.closeSync(fd); closed = true; }
                catch (error_) { errors.push(error_); }
            }
            const failure = new AggregateError(errors, 'Endpoint resource admission failed.');
            Object.defineProperty(failure, 'linuxEndpointOpenResource', { value: Object.freeze({ ...observation, closeConfirmed: closed }) }); throw failure;
        }
    }
    function checkOne(resource, expected, budget) {
        const held = budget.query(() => fs.fstatSync(resource.fd, { bigint: true }));
        const named = budget.query(() => fs.lstatSync(resource.observation.path, { bigint: true }));
        const project = resource.kind === 'directory' ? stable : value => linuxFileIdentity(value, 'isFile');
        if (!isDeepStrictEqual(project(held), resource.identity) || !isDeepStrictEqual(project(named), resource.identity)) throw new Error('Held endpoint resource identity changed.');
        if (resource.kind === 'directory' && (!held.isDirectory() || !named.isDirectory()
            || !isDeepStrictEqual(resource.identity, expected.appDataDirectory.device))) throw new Error('Endpoint parent directory was replaced.');
    }
    return { open, check: (resources, expected, budget) => { for (const resource of resources) checkOne(resource, expected, budget); },
        close: resource => fs.closeSync(resource.fd) }; // Close once, never retry a possibly reused fd.
}

function helperInput(expected, milliseconds) {
    const value = JSON.stringify({ runId: expected.runId, uid: expected.uid, appPid: expected.app.pid,
        appDataPath: expected.appDataDirectory.path, directory: expected.appDataDirectory.device, remainingMs: milliseconds }) + '\n';
    if (Buffer.byteLength(value) > linuxEndpointLimits.bytes) throw new Error('Endpoint helper input exceeded its fixed bound.');
    return value;
}
export function createEndpointProbeRunner({ spawnChild = spawn, setTimer = setTimeout, clearTimer = clearTimeout } = {}) {
    return (resources, expected, budget) => {
        const workMs = budget.utilityMs(), input = helperInput(expected, workMs);
        const workDeadline = budget.now() + BigInt(workMs) * 1000000n;
        const [directory, interpreter, helper] = resources;
        if ([directory, interpreter, helper].some(item => !Number.isInteger(item?.fd) || item.fd < 0)) throw new Error('Invalid held endpoint descriptors.');
        return new Promise((resolve, reject) => {
            let child, workTimer, closeTimer, primary, exited = false, closed = false, finished = false, signalled = false;
            const stdout = [], stderr = []; let stdoutBytes = 0, stderrBytes = 0;
            const discard = () => {
                child.stdout.removeListener('data', out); child.stderr.removeListener('data', err);
                child.stdout.resume(); child.stderr.resume();
            };
            const finish = confirmed => {
                if (finished) return;
                finished = true; clearTimer(workTimer); clearTimer(closeTimer); discard();
                let result;
                if (!primary) {
                    try {
                        const text = new TextDecoder('utf-8', { fatal: true }).decode(Buffer.concat(stdout));
                        if (!text.endsWith('\n') || text.indexOf('\n') !== text.length - 1) throw new Error('Expected one complete endpoint helper frame.');
                        result = JSON.parse(text.slice(0, -1));
                    } catch (error) { primary = error; }
                }
                if (primary) {
                    const failure = new AggregateError([primary], 'Bounded private endpoint helper failed.');
                    Object.defineProperties(failure, { helperCloseConfirmed: { value: confirmed },
                        output: { value: Object.freeze({ stdoutBytes, stderrBytes, stdoutHex: Buffer.concat(stdout).toString('hex'), stderrHex: Buffer.concat(stderr).toString('hex') }) } });
                    reject(failure);
                } else resolve(result);
            };
            const stop = error => {
                if (finished) return;
                primary ??= error; discard();
                if (exited || closed || signalled || !child.pid) return;
                signalled = true;
                try { child.kill('SIGKILL'); } // Only our exact helper ChildProcess, never an App/peer PID.
                catch (error_) { primary = new AggregateError([primary, error_], 'Endpoint helper termination failed.'); }
            };
            const append = (bytes, chunks, used) => {
                if (finished || primary) return used;
                if (!Buffer.isBuffer(bytes) || bytes.length > linuxEndpointLimits.bytes - used) { stop(new Error('Endpoint helper output byte limit exceeded.')); return used; }
                chunks.push(Buffer.from(bytes)); return used + bytes.length;
            };
            const out = bytes => { stdoutBytes = append(bytes, stdout, stdoutBytes); };
            const err = bytes => { stderrBytes = append(bytes, stderr, stderrBytes); };
            try {
                child = spawnChild('/proc/self/fd/5', ['-I', '-S', '-B', '/proc/self/fd/4'], {
                    argv0: interpreter.observation.path, shell: false, detached: false,
                    env: { LC_ALL: 'C', LANG: 'C', PATH: '/usr/bin:/bin' }, stdio: ['pipe', 'pipe', 'pipe', directory.fd, helper.fd, interpreter.fd] });
            } catch (error) {
                const failure = new AggregateError([error], 'Endpoint helper spawn failed.');
                Object.defineProperty(failure, 'helperCloseConfirmed', { value: true }); reject(failure); return;
            }
            child.stdout.on('data', out); child.stderr.on('data', err);
            for (const stream of [child.stdin, child.stdout, child.stderr]) stream.once('error', stop);
            child.once('error', stop); child.once('exit', () => { exited = true; });
            child.once('close', (code, signal) => {
                closed = true;
                if (finished) return;
                if (code !== 0 || signal !== null || stderrBytes !== 0) primary ??= new Error('Endpoint helper exited unsuccessfully or emitted diagnostics.');
                if (budget.now() >= budget.deadlineNs) primary ??= new Error('Endpoint helper closed after the shared deadline.');
                finish(true);
            });
            const delay = deadline => Math.max(0, Number((deadline - budget.now()) / 1000000n));
            workTimer = setTimer(() => stop(new Error('Endpoint helper work deadline expired.')), delay(workDeadline));
            closeTimer = setTimer(() => {
                stop(new Error('Endpoint helper process/stdio close is unconfirmed.'));
                if (finished) return;
                child.stdin.destroy(); child.stdout.destroy(); child.stderr.destroy(); child.unref(); finish(false);
            }, delay(budget.deadlineNs));
            try { budget.check(); if (!primary) child.stdin.end(input); }
            catch (error) { stop(error); }
        });
    };
}

export const endpointResources = createEndpointResources();
export const runEndpointProbe = createEndpointProbeRunner();
