// Internal filesystem/transport seams. Public callers cannot inject readers or helpers.
import * as nodeFs from 'node:fs';
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { isDeepStrictEqual } from 'node:util';
import { fileURLToPath } from 'node:url';
import { linuxFileIdentity } from '../linux-process-core.mjs';
import { lifecycleLimits } from './core.mjs';

export const helperSha256 = 'beb8320080a7961133087899f9318a11254492c560b5bde5e1213c83702ce804';
const helperPath = fileURLToPath(new URL('./pidfd-helper.py', import.meta.url));

function validateFile(kind, stat, expected, limit) {
    if (stat.uid !== BigInt(kind === 'python' ? 0 : expected.uid) || (stat.mode & 0o022n) !== 0n
        || stat.size < 4n || stat.size > BigInt(limit)) throw new Error('Invalid lifecycle source/interpreter ownership or size.');
    if (kind === 'python' && (stat.mode & 0o111n) === 0n) throw new Error('Lifecycle interpreter is not executable.');
}

export function createResources(fs = nodeFs) {
    function digest(kind, fd, stat, python, limit, budget) {
        const hash = createHash('sha256'), buffer = Buffer.alloc(65536);
        let used = 0, magic = Buffer.alloc(0);
        while (true) {
            const count = budget.query(() => fs.readSync(fd, buffer, 0, buffer.length, null));
            if (!count) break;
            if (count > limit - used) throw new Error('Lifecycle source/interpreter byte cap exceeded.');
            if (magic.length < 4) magic = Buffer.concat([magic, buffer.subarray(0, Math.min(4 - magic.length, count))]);
            used += count; hash.update(buffer.subarray(0, count));
        }
        const sha256 = hash.digest('hex');
        if (used !== Number(stat.size) || sha256 !== (kind === 'python' ? python.sha256 : helperSha256)) throw new Error('Lifecycle source/interpreter digest mismatch.');
        if (kind === 'python' && !magic.equals(Buffer.from([127, 69, 76, 70]))) throw new Error('Lifecycle interpreter ELF mismatch.');
        return sha256;
    }
    function open(kind, expected, python, budget) {
        const { constants } = fs;
        if (!Number.isInteger(constants.O_NOFOLLOW) || !constants.O_NOFOLLOW) throw new Error('Required lifecycle no-follow flag unavailable.');
        const path = kind === 'python' ? budget.query(() => fs.realpathSync(python.path)) : helperPath;
        if (kind === 'python' && !/^\/usr\/bin\/python3(?:\.\d{1,2})?$/.test(path)) throw new Error('Unexpected canonical system Python.');
        let fd, observation = { kind, path };
        try {
            budget.query(() => { fd = fs.openSync(path, constants.O_RDONLY | constants.O_NOFOLLOW); });
            const stat = budget.query(() => fs.fstatSync(fd, { bigint: true }));
            const identity = Object.freeze(linuxFileIdentity(stat, 'isFile'));
            const limit = kind === 'python' ? lifecycleLimits.interpreterBytes : lifecycleLimits.helperBytes;
            validateFile(kind, stat, expected, limit);
            observation = { ...observation, identity };
            const sha256 = digest(kind, fd, stat, python, limit, budget);
            const resource = Object.freeze({ fd, identity, observation: Object.freeze({ ...observation, sha256 }) });
            check(resource, budget); return resource;
        } catch (error) {
            const errors = [error]; let closeConfirmed = fd === undefined;
            if (fd !== undefined) { try { fs.closeSync(fd); closeConfirmed = true; } catch (closeError) { errors.push(closeError); } }
            const failure = new AggregateError(errors, 'Lifecycle retained resource admission failed.');
            Object.defineProperty(failure, 'linuxLifecycleResource', { value: Object.freeze({ ...observation, closeConfirmed }) }); throw failure;
        }
    }
    function check(resource, budget) {
        const held = budget.query(() => fs.fstatSync(resource.fd, { bigint: true }));
        const named = budget.query(() => fs.lstatSync(resource.observation.path, { bigint: true }));
        if (!isDeepStrictEqual(linuxFileIdentity(held, 'isFile'), resource.identity)
            || !isDeepStrictEqual(linuxFileIdentity(named, 'isFile'), resource.identity)) throw new Error('Lifecycle source/interpreter identity changed.');
    }
    return { open, check, close: resource => fs.closeSync(resource.fd) };
}

export function createChannelFactory({ spawnChild = spawn, setTimer = setTimeout, clearTimer = clearTimeout,
    resources = createResources() } = {}) {
    return async (expected, python, lifetimeMs, budget) => {
        let interpreter, helper, child, sequence = 0, pending, fatal, closed = false, exited = false;
        let stdoutBytes = 0, stderrBytes = 0, buffered = Buffer.alloc(0), helperCloseConfirmed = false;
        let interpreterClosed = false, sourceClosed = false, interpreterCloseAttempted = false, sourceCloseAttempted = false,
            shutdown = false, closeStatus, settlementRequested = false;
        const stderr = [], accepted = [];
        const observation = () => Object.freeze({ interpreter: interpreter?.observation, source: helper?.observation,
            interpreterCloseConfirmed: interpreterClosed, sourceCloseConfirmed: sourceClosed,
            helperCloseConfirmed, stdoutBytes, stderrBytes, stderrHex: Buffer.concat(stderr).toString('hex'),
            frames: Object.freeze([...accepted]) });
        const closeResources = () => {
            const failures = [];
            for (const [resource, kind] of [[helper, 'helper'], [interpreter, 'python']]) {
                if (!resource || (kind === 'helper' ? sourceCloseAttempted : interpreterCloseAttempted)) continue;
                if (kind === 'helper') sourceCloseAttempted = true; else interpreterCloseAttempted = true;
                try { resources.close(resource); if (kind === 'helper') sourceClosed = true; else interpreterClosed = true; }
                catch (error) { failures.push(error); }
            }
            return failures;
        };
        function tagged(error) {
            Object.defineProperty(error, 'linuxLifecycleHelper', { value: observation(), configurable: true }); return error;
        }
        const stop = error => {
            fatal ??= error;
            if (pending) { const item = pending; pending = undefined; clearTimer(item.timer); item.reject(tagged(fatal)); }
            if (child && !exited && !closed && child.pid && !shutdown) {
                shutdown = true;
                try { child.kill('SIGKILL'); } // Exact helper ChildProcess only. Original targets use pidfds inside Python.
                catch (killError) { fatal = new AggregateError([fatal, killError], 'Lifecycle helper termination failed.'); }
            }
        };
        const deliver = raw => {
            const frame = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(raw));
            if (!pending || frame.seq !== pending.sequence || frame.kind !== pending.op) throw new Error('Unsolicited/mismatched lifecycle response.');
            if (accepted.length >= lifecycleLimits.commands) throw new Error('Lifecycle response count exceeded.');
            const item = pending; pending = undefined; clearTimer(item.timer);
            accepted.push(Object.freeze({ sequence: item.sequence, op: item.op })); item.resolve(frame);
        };
        const stdout = chunk => {
            if (fatal) return;
            if (!Buffer.isBuffer(chunk) || chunk.length > lifecycleLimits.stdoutBytes - stdoutBytes) { stop(new Error('Lifecycle stdout cap exceeded.')); return; }
            stdoutBytes += chunk.length;
            buffered = Buffer.concat([buffered, chunk]);
            while (true) {
                const end = buffered.indexOf(10);
                if (end < 0) {
                    if (buffered.length >= lifecycleLimits.frameBytes) stop(new Error('Lifecycle response frame cap exceeded.'));
                    return;
                }
                if (end + 1 > lifecycleLimits.frameBytes) { stop(new Error('Lifecycle response frame cap exceeded.')); return; }
                try {
                    const frame = buffered.subarray(0, end);
                    buffered = buffered.subarray(end + 1);
                    deliver(frame);
                } catch (error) { stop(error); return; }
            }
        };
        const errorOutput = chunk => {
            if (!Buffer.isBuffer(chunk) || chunk.length > lifecycleLimits.stderrBytes - stderrBytes) { stop(new Error('Lifecycle stderr cap exceeded.')); return; }
            stderr.push(Buffer.from(chunk)); stderrBytes += chunk.length;
            stop(new Error('Lifecycle helper emitted diagnostics.'));
        };
        let closePromise;
        try {
            interpreter = resources.open('python', expected, python, budget);
            helper = resources.open('helper', expected, python, budget);
            resources.check(interpreter, budget); resources.check(helper, budget);
            child = spawnChild('/proc/self/fd/5', ['-I', '-S', '-B', '/proc/self/fd/4'], {
                argv0: interpreter.observation.path, shell: false, detached: false,
                env: { LC_ALL: 'C', LANG: 'C', PATH: '/usr/bin:/bin' },
                stdio: ['pipe', 'pipe', 'pipe', 'ignore', helper.fd, interpreter.fd] });
            child.stdout.on('data', stdout); child.stderr.on('data', errorOutput);
            for (const stream of [child.stdin, child.stdout, child.stderr]) stream.once('error', stop);
            child.once('error', stop); child.once('exit', () => { exited = true; });
            closePromise = new Promise(resolve => child.once('close', (code, signal) => {
                closed = true; helperCloseConfirmed = true; closeStatus = { code, signal };
                if (pending) stop(new Error('Lifecycle helper closed during a request.'));
                const errors = closeResources();
                if (errors.length) fatal = new AggregateError([fatal, ...errors].filter(Boolean), 'Lifecycle retained file close failed.');
                resolve();
            }));
        } catch (error) { throw tagged(new AggregateError([error, ...closeResources()], 'Lifecycle resource/spawn failed.')); }
        const finish = async (phase, interrupt = false) => {
            // Failed normal close must not manufacture an admitted EOF and signal live targets.
            // Only the explicit internal interruption control intentionally exercises EOF cleanup.
            if (!closed && interrupt) child.stdin.end();
            else if (!closed && !settlementRequested) stop(new Error('Lifecycle helper was not explicitly settled.'));
            let timer;
            try {
                await Promise.race([closePromise, new Promise((_, reject) => {
                    const delay = Math.max(0, Number((phase.deadlineNs - phase.now()) / 1000000n));
                    timer = setTimer(() => {
                        stop(new Error('Lifecycle helper/stdio close unconfirmed.'));
                        child.stdin.destroy(); child.stdout.destroy(); child.stderr.destroy(); child.unref();
                        reject(fatal);
                    }, delay);
                })]);
            } catch (error) { throw tagged(new AggregateError([error, ...closeResources()], 'Lifecycle close failed.')); }
            finally { clearTimer(timer); }
            const errors = closeResources();
            if (fatal || closeStatus?.code !== 0 || closeStatus?.signal !== null || stderrBytes || buffered.length || errors.length) {
                throw tagged(new AggregateError([fatal, ...errors].filter(Boolean), 'Lifecycle helper/resource settlement failed.'));
            }
        };
        const request = (value, phase) => {
            if (fatal) return Promise.reject(tagged(fatal));
            if (pending || closed || ++sequence > lifecycleLimits.commands) return Promise.reject(tagged(new Error('Lifecycle channel unavailable/overlapping/exhausted.')));
            try { phase.check(); resources.check(interpreter, phase); resources.check(helper, phase); }
            catch (error) { stop(error); return Promise.reject(tagged(error)); }
            const frame = JSON.stringify({ ...value, seq: sequence }) + '\n';
            if (Buffer.byteLength(frame) > lifecycleLimits.frameBytes) return Promise.reject(tagged(new Error('Lifecycle request frame cap exceeded.')));
            return new Promise((resolve, reject) => {
                const delay = Math.max(0, Number((phase.deadlineNs - phase.now()) / 1000000n) - lifecycleLimits.closeReserveMs);
                pending = { sequence, op: value.op === 'open' ? 'opened' : value.op, resolve, reject,
                    timer: setTimer(() => stop(new Error('Lifecycle request deadline expired.')), delay) };
                try {
                    phase.check();
                    if (['close', 'abort'].includes(value.op)) settlementRequested = true;
                    child.stdin.write(frame);
                } catch (error) { stop(error); }
            });
        };
        const channel = Object.freeze({ request, finish, interrupt: phase => finish(phase, true), resources: observation });
        try { await request({ op: 'open', appPid: expected.app.pid, workerPid: expected.worker.pid, lifetimeMs }, budget); }
        catch (error) { try { await finish(budget); } catch (closeError) { throw tagged(new AggregateError([error, closeError], 'Lifecycle acquisition and close failed.')); } throw error; }
        return channel;
    };
}

export const openLifecycleChannel = createChannelFactory();
