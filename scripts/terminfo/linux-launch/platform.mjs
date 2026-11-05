// Internal fixed-descriptor resources/transport. No public reader, spawn, path or argv override.
import * as nodeFs from 'node:fs';
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { isDeepStrictEqual } from 'node:util';
import { fileURLToPath } from 'node:url';
import { linuxFileIdentity } from '../linux-process-core.mjs';
import { launchLimits } from './core.mjs';
export const helperSha256 = 'f94daf00da6365397bcaa249b54a40a59867d6bca0adab03baaa28b7094bb20e';
const sourcePath = fileURLToPath(new URL('./helper.py', import.meta.url));
const stable = stat => Object.fromEntries(['dev', 'ino', 'uid', 'mode'].map(name => [name, stat[name].toString()]));

/** Open only fixed pinned resources and retain partial-open/close evidence; tests may supply a filesystem seam. */
export function createLaunchResources(fs = nodeFs, architecture = process.arch) {
    const { constants: flags } = fs;
    const regular = stat => linuxFileIdentity(stat, 'isFile');
    /** Read a held bounded file with explicit offsets and mutation checks inside one phase budget. */
    function hash(resource, maximum, phase) {
        const stat = phase.query(() => fs.fstatSync(resource.fd, { bigint: true }));
        if (stat.size < 1n || stat.size > BigInt(maximum)) throw new Error('Launch file exceeds its declared byte bound.');
        const buffer = Buffer.alloc(65536), value = createHash('sha256'); let offset = 0;
        while (BigInt(offset) < stat.size) {
            const length = phase.query(() => fs.readSync(resource.fd, buffer, 0, Math.min(buffer.length, Number(stat.size) - offset), offset));
            if (length < 1) throw new Error('Pinned launch file ended early.');
            value.update(buffer.subarray(0, length)); offset += length;
        }
        if (!isDeepStrictEqual(regular(phase.query(() => fs.fstatSync(resource.fd, { bigint: true }))), resource.file)) throw new Error('Pinned launch file changed while hashing.');
        return value.digest('hex');
    }
    /** Admit one exact owner/mode/hash and close its descriptor even if the post-open deadline expires. */
    function admitFile(path, kind, uid, expected, maximum, phase, executable = false) {
        let resource;
        try {
            phase.query(() => { const fd = fs.openSync(path, flags.O_RDONLY | flags.O_NOFOLLOW); resource = { kind, path, fd, closed: false }; });
            const stat = phase.query(() => fs.fstatSync(resource.fd, { bigint: true })); resource.file = regular(stat);
            if (stat.uid !== BigInt(uid) || stat.nlink !== 1n || (stat.mode & 0o6022n) !== 0n
                || executable && (stat.mode & 0o111n) === 0n) throw new Error('Pinned launch file ownership/mode is invalid.');
            resource.sha256 = hash(resource, maximum, phase);
            if (resource.sha256 !== expected) throw new Error('Pinned launch file digest mismatch.');
            return resource;
        } catch (error) { closePartial(resource, error); }
    }
    /** Preserve the primary admission error and attempt only its known descriptor closure once. */
    function closePartial(resource, primary) {
        const errors = [primary];
        if (resource && !resource.closed) {
            resource.closed = true;
            try { fs.closeSync(resource.fd); resource.closeConfirmed = true; } catch (error) { errors.push(error); }
        }
        const failure = new AggregateError(errors, 'Launch resource admission failed.');
        Object.defineProperty(failure, 'linuxLaunchResource', { value: observation(resource) }); throw failure;
    }
    /** Hold the exact private directory inode and reject symlink/canonical-path substitution. */
    function admitDirectory(path, kind, expected, phase) {
        let resource;
        try {
            if (phase.query(() => fs.realpathSync(path)) !== path) throw new Error('Launch directory contains a symlink.');
            phase.query(() => { const fd = fs.openSync(path, flags.O_RDONLY | flags.O_DIRECTORY | flags.O_NOFOLLOW); resource = { kind, path, fd, closed: false }; });
            const stat = phase.query(() => fs.fstatSync(resource.fd, { bigint: true })); resource.file = stable(stat);
            if (!stat.isDirectory() || !isDeepStrictEqual(resource.file, expected)) throw new Error('Held launch directory differs from creation proof.');
            return resource;
        } catch (error) { closePartial(resource, error); }
    }
    /** Check the fixed executable format/architecture; file hashes alone are not semantic App attestation. */
    function elf(resource, phase, app = false) {
        const header = Buffer.alloc(app ? 20 : 4);
        const count = phase.query(() => fs.readSync(resource.fd, header, 0, header.length, 0));
        if (count !== header.length || !header.subarray(0, 4).equals(Buffer.from([127, 69, 76, 70]))) throw new Error('Pinned launch executable is not ELF.');
        if (app && (header[4] !== 2 || header[5] !== 1 || ![2, 3].includes(header.readUInt16LE(16))
            || header.readUInt16LE(18) !== ({ x64: 62, arm64: 183 })[architecture])) throw new Error('Unsupported App ELF architecture/type.');
    }
    /** Create only fresh private home/log entries and hold the verified null-input descriptor. */
    function createPrivate(input, resources, phase) {
        const home = input.fixture.scratch.path + '/home';
        phase.query(() => fs.mkdirSync(home, { mode: 0o700 }));
        const homeStat = phase.query(() => fs.lstatSync(home, { bigint: true }));
        resources.push(admitDirectory(home, 'home', stable(homeStat), phase));
        const logPath = input.fixture.scratch.path + '/app-launch.log';
        let log;
        phase.query(() => { const fd = fs.openSync(logPath, flags.O_WRONLY | flags.O_CREAT | flags.O_EXCL | flags.O_NOFOLLOW, 0o600);
            log = { kind: 'log', path: logPath, fd, closed: false }; resources.push(log); });
        const stat = phase.query(() => fs.fstatSync(log.fd, { bigint: true })); log.file = regular(stat);
        if (stat.uid !== BigInt(input.fixture.uid) || stat.mode !== 0o100600n || stat.size !== 0n || stat.nlink !== 1n) throw new Error('Expected a fresh private launch log.');
        let nullDevice;
        phase.query(() => { const fd = fs.openSync('/dev/null', flags.O_RDONLY | flags.O_NOFOLLOW | flags.O_NOCTTY);
            nullDevice = { kind: 'null', path: '/dev/null', fd, closed: false }; resources.push(nullDevice); });
        const device = phase.query(() => fs.fstatSync(nullDevice.fd, { bigint: true }));
        if (!device.isCharacterDevice() || device.rdev !== 259n || device.uid !== 0n) throw new Error('Expected the Linux null device.');
        nullDevice.file = stable(device);
    }
    /** Recheck original held/named identities, treating settings bytes as initial admission input only. */
    function check(resources, phase, initial = true) {
        for (const resource of resources) {
            if (resource.closed || resource.kind === 'log' || resource.kind === 'null' || !initial && resource.kind === 'settings') continue;
            const held = phase.query(() => fs.fstatSync(resource.fd, { bigint: true }));
            const named = phase.query(() => fs.lstatSync(resource.path, { bigint: true }));
            const project = ['appDirectory', 'scratch', 'appData', 'home'].includes(resource.kind) ? stable : regular;
            if (!isDeepStrictEqual(project(held), resource.file) || !isDeepStrictEqual(project(named), resource.file)) throw new Error('Original launch resource identity changed.');
        }
    }
    /** Complete the fixed source/tool/artifact preparation before any helper or App child is spawned. */
    function open(input, phase) {
        for (const name of ['O_DIRECTORY', 'O_NOFOLLOW', 'O_NOCTTY']) {
            if (!Number.isInteger(flags[name]) || flags[name] === 0) throw new Error('Required launch open flag is unavailable.');
        }
        const resources = [];
        const add = value => { resources.push(value); return value; };
        try {
            const appPath = input.artifact.directory.path + '/Ntilde';
            add(admitDirectory(input.artifact.directory.path, 'appDirectory', input.artifact.directory.device, phase));
            add(admitDirectory(input.fixture.scratch.path, 'scratch', input.fixture.scratch.device, phase));
            add(admitDirectory(input.fixture.appDataDirectory.path, 'appData', input.fixture.appDataDirectory.device, phase));
            elf(add(admitFile(appPath, 'app', input.fixture.uid, input.artifact.sha256.app, launchLimits.appBytes, phase, true)), phase, true);
            add(admitFile(input.artifact.directory.path + '/Ntilde.dll', 'managed', input.fixture.uid, input.artifact.sha256.managed, launchLimits.managedBytes, phase));
            add(admitFile(input.artifact.directory.path + '/librusty_pty.so', 'pty', input.fixture.uid, input.artifact.sha256.pty, launchLimits.ptyBytes, phase));
            add(admitFile(input.fixture.appDataDirectory.path + '/settings.json', 'settings', input.fixture.uid, input.fixture.initialSettingsSha256, 65536, phase));
            const pythonPath = phase.query(() => fs.realpathSync(input.python.path));
            if (!/^\/usr\/bin\/python3(?:\.\d{1,2})?$/.test(pythonPath)) throw new Error('Unexpected canonical system Python path.');
            elf(add(admitFile(pythonPath, 'python', 0, input.python.sha256, launchLimits.pythonBytes, phase, true)), phase);
            add(admitFile(sourcePath, 'source', input.fixture.uid, helperSha256, launchLimits.helperBytes, phase));
            createPrivate(input, resources, phase); check(resources, phase, true); return resources;
        } catch (error_) {
            const errors = [error_];
            for (const item of resources.toReversed()) { try { close(item); } catch (error) { errors.push(error); } }
            const failure = new AggregateError(errors, 'Launch resource preparation failed.');
            Object.defineProperty(failure, 'linuxLaunchResources', { value: resources.map(observation) }); throw failure;
        }
    }
    /** Never retry a possibly reused descriptor after an unconfirmed close. */
    function close(resource) {
        if (resource.closed) return;
        resource.closed = true; fs.closeSync(resource.fd); resource.closeConfirmed = true;
    }
    return { open, check, close };
}
/** Expose resource identity and actual closure facts without exporting descriptor selectors. */
function observation(resource) {
    if (!resource) return null;
    const { kind, path, file, sha256, closeConfirmed = false } = resource;
    return { kind, path, file, ...(sha256 ? { sha256 } : {}), closeConfirmed };
}
/** Construct the sole fixed descriptor/path configuration, without caller argv or inherited environment. */
function config(input, resources) {
    const by = Object.fromEntries(resources.map(value => [value.kind, value]));
    return { op: 'init', runId: input.fixture.runId, uid: input.fixture.uid, appPath: by.app.path,
        appDirectory: by.appDirectory.path, scratch: by.scratch.path, appData: by.appData.path, home: by.home.path,
        locale: input.fixture.locale, appVersion: input.fixture.appVersion,
        pins: { ...input.artifact.sha256, settings: input.fixture.initialSettingsSha256 },
        files: Object.fromEntries(['app', 'managed', 'pty', 'settings'].map(key => [key, by[key].file])),
        directories: { app: by.appDirectory.file, scratch: by.scratch.file, appData: by.appData.file, home: by.home.file } };
}

/** Build a single-flight bounded helper channel; only internal tests can replace spawn/resources. */
export function createLaunchChannelFactory({ spawnChild = spawn, setTimer = setTimeout, clearTimer = clearTimeout,
    resources: policy = createLaunchResources() } = {}) {
    /** Hold one fixed helper and its pipes/resources until bounded terminal settlement. */
    return async function open(input, initial) {
        const resources = policy.open(input, initial), by = Object.fromEntries(resources.map(value => [value.kind, value]));
        let child, pending, failure, sequence = 0, commands = 0, stdoutBytes = 0, stderr = Buffer.alloc(0), buffer = Buffer.alloc(0),
            lastObservation, exitCode, signalCode, closed = false, inputEnded = false, execReleaseRequested = false, stdoutOverflow = false, stderrOverflow = false;
        const helperArgv = [by.python.path, '-I', '-S', '-B', '/proc/self/fd/4', '--run-id=' + input.fixture.runId];
        let resolveClose;
        const close = new Promise(resolve => { resolveClose = resolve; });
        const snapshot = () => ({ helperPid: child?.pid ?? null, exitCode: exitCode ?? null, signalCode: signalCode ?? null,
            closeConfirmed: closed, stdinEnded: inputEnded, failed: !!failure, stdoutBytes, stderrBytes: stderr.length,
            execReleaseRequested, stdoutOverflow, stderrOverflow, observation: lastObservation ?? null, resources: resources.map(observation) });
        /** Close the coordinator writer once so source-owned EOF cleanup can run. */
        function stopInput() { if (!inputEnded && child?.stdin) { inputEnded = true; child.stdin.end(); } }
        /** Latch transport failure and reject the outstanding request without retrying target actions. */
        function fail(error) {
            failure ??= error; stopInput();
            if (pending) { const item = pending; pending = null; clearTimer(item.timer); item.reject(error); }
        }
        /** Correlate a fixed helper response; an autonomous abort remains a failure with retained actual facts. */
        function frame(value) {
            if (!value || Object.getPrototypeOf(value) !== Object.prototype || Object.keys(value).some(key => !['seq', 'ok', 'observation', 'clockNs'].includes(key)) || typeof value.ok !== 'boolean' || !value.observation) throw new Error('Malformed launch helper response.');
            lastObservation = value.observation;
            if (value.seq === null && !value.ok) { const error = new Error('Launch helper entered autonomous abort.'); error.launchObservation = lastObservation; fail(error); return; }
            if (!pending || value.seq !== pending.sequence) throw new Error('Uncorrelated launch helper response.');
            const item = pending; pending = null; clearTimer(item.timer);
            if (!value.ok) { const error = new Error('Launch helper refused operation.'); error.launchObservation = lastObservation; item.reject(error); }
            else item.resolve(value);
        }
        /** Apply byte limits before append and parse only bounded helper frames, never App output. */
        function receive(chunk) {
            if (stdoutOverflow) return;
            if (chunk.length > launchLimits.stdoutBytes - stdoutBytes || chunk.length > launchLimits.frameBytes - buffer.length) {
                stdoutOverflow = true; fail(new Error('Bounded launch helper stdout exceeded.')); return;
            }
            stdoutBytes += chunk.length; buffer = Buffer.concat([buffer, chunk]);
            try {
                while (buffer.includes(10)) {
                    const end = buffer.indexOf(10), line = buffer.subarray(0, end); buffer = buffer.subarray(end + 1);
                    frame(JSON.parse(line.toString('utf8')));
                }
            } catch (error) { fail(error); }
        }
        /** Send one bounded sequenced operation before its deadline; unknown delivery must not be retried. */
        function request(command, phase) {
            if (failure) return Promise.reject(failure);
            if (pending || closed || inputEnded || ++commands > launchLimits.commands) { const error = new Error('Launch channel overlap/limit/closure.'); fail(error); return Promise.reject(error); }
            const current = ++sequence, raw = Buffer.from(JSON.stringify({ seq: current, ...command }) + '\n');
            if (raw.length > launchLimits.frameBytes) { const error = new Error('Launch request exceeds frame bound.'); fail(error); return Promise.reject(error); }
            return new Promise((resolve, reject) => {
                let milliseconds;
                try { milliseconds = phase.remainingMs(); } catch (error) { fail(error); reject(error); return; }
                const timer = setTimer(() => fail(new Error('Launch helper operation timed out.')), milliseconds);
                pending = { sequence: current, resolve, reject, timer };
                if (command.op === 'release') execReleaseRequested = true;
                try { child.stdin.write(raw, error => { if (error) fail(error); }); }
                catch (error) { fail(error); }
            });
        }
        /** Bound helper/stdio closure, preserve all close failures and signal only the exact spawned helper. */
        async function finish(phase) {
            stopInput(); let timer; const errors = [];
            try {
                const milliseconds = phase.remainingMs();
                await Promise.race([close, new Promise((_, reject) => { timer = setTimer(() => reject(new Error('Launch helper close is unconfirmed.')), milliseconds); })]);
                if (buffer.length !== 0 || exitCode !== 0 || signalCode !== null) throw new Error('Launch helper closure failed.');
            } catch (error) {
                failure ??= error; errors.push(error);
                if (!closed && child) {
                    try { child.kill('SIGKILL'); } // Only the exact spawned helper, never a target PID.
                    catch (killError) { errors.push(killError); }
                }
            } finally {
                clearTimer(timer);
                for (const resource of resources) { try { policy.close(resource); } catch (error) { errors.push(error); } }
            }
            if (errors.length) throw new AggregateError(errors, 'Launch helper/parent resources did not close.');
            return lastObservation;
        }
        try {
            const stdio = ['pipe', 'pipe', 'pipe', by.log.fd, by.source.fd, by.python.fd, by.app.fd,
                by.appDirectory.fd, by.null.fd, by.settings.fd, by.scratch.fd, by.managed.fd, by.pty.fd, by.appData.fd, by.home.fd];
            child = spawnChild('/proc/self/fd/5', helperArgv.slice(1), { argv0: by.python.path,
                env: { PATH: '/usr/bin:/bin', LC_ALL: 'C.UTF-8' }, stdio });
            child.stdout.on('data', receive);
            child.stderr.on('data', chunk => {
                if (stderrOverflow) return;
                if (chunk.length > launchLimits.stderrBytes - stderr.length) { stderrOverflow = true; fail(new Error('Bounded launch helper stderr exceeded.')); return; }
                stderr = Buffer.concat([stderr, chunk]);
            });
            child.stdin.on('error', fail); child.on('error', fail);
            child.on('exit', (code, sig) => { exitCode = code; signalCode = sig; });
            child.on('close', () => { closed = true; resolveClose(); if (pending) fail(new Error('Launch helper closed before response.')); });
            await request(config(input, resources), initial);
            initial.check(); policy.check(resources, initial, true);
            return { request, finish, check: (phase, initial_) => policy.check(resources, phase, initial_), snapshot,
                resources: () => ({ helperPid: child.pid, helperArgv, python: by.python, app: by.app,
                    appArgv: [by.app.path, '--ntilde-terminfo-owner=' + input.fixture.runId, '--ntilde-terminfo-private-clipboard=' + input.fixture.runId] }) };
        } catch (error_) {
            const errors = [error_];
            try { if (child) await finish(initial); else for (const item of resources) policy.close(item); } catch (error) { errors.push(error); }
            const error = new AggregateError(errors, 'Pinned launch helper did not initialize.');
            Object.defineProperty(error, 'linuxLaunchChannel', { value: snapshot() }); throw error;
        }
    };
}
export const openLaunchChannel = createLaunchChannelFactory();
