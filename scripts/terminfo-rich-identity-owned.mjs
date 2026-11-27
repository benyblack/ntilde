#!/usr/bin/env node
import { execFile, spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { once } from 'node:events';
import { chmodSync, constants, copyFileSync, createReadStream, mkdirSync, readFileSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { basename, dirname, join, relative, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { promisify } from 'node:util';
import { acquireDesktopLease, desktopLockAuditPath, desktopLockWaitMs } from './terminfo/desktop-lock.mjs';

const exec = promisify(execFile);
const here = dirname(fileURLToPath(import.meta.url));
const repo = dirname(here);
const gitExecutable = '/usr/bin/git';
const entrypoint = 'terminfo-rich-identity-owned.mjs';
const containerOutput = '/home/node/identity-evidence';
const sourceNames = ['collect.py', 'pty_child.py', 'vte_reference.py', 'plan.json', 'Dockerfile'];

function need(condition, message) {
    if (!condition) throw new Error(message);
}

async function hashStream(stream) {
    const sha = createHash('sha256');
    for await (const chunk of stream) sha.update(chunk);
    return sha.digest('hex');
}

const hash = path => hashStream(createReadStream(path));

async function appSourceHash(archive, revision) {
    const git = spawn(gitExecutable, ['archive', '--format=tar', revision], { cwd: repo, stdio: ['ignore', 'pipe', 'inherit'] });
    const [actual, expected, [exit]] = await Promise.all([hash(archive), hashStream(git.stdout), once(git, 'close')]);
    need(exit === 0 && actual === expected, 'App source archive differs from the exact Git archive of ' + revision);
    return actual;
}

const docker = async (args, timeout = 15000) => (await exec('docker', args, { timeout, maxBuffer: 4 * 1024 * 1024 })).stdout.trim();
const printJson = (path, value) => writeFileSync(path, JSON.stringify(value, null, 2) + '\n', { flag: 'wx' });

function bindMount(source, target) {
    const field = 'source=' + source;
    const encoded = /[,"\r\n]/.test(field) ? '"' + field.replaceAll('"', '""') + '"' : field;
    return ['--mount', 'type=bind,' + encoded + ',target=' + target + ',readonly'];
}

const matchingName = name => 'name=^/' + name + '$';
const validId = id => /^[a-f0-9]{64}$/.test(id);
const inspect = async (invoke, id) => JSON.parse(await invoke(['inspect', id]))[0];

async function removeContainer(invoke, state, output) {
    const { id, name, runId } = state;
    const nameFilter = matchingName(name);
    const discovered = id || await invoke(['ps', '-aq', '--no-trunc', '--filter', nameFilter]);
    if (discovered) {
        need(validId(discovered), 'ambiguous owned container ID during cleanup');
        const info = await inspect(invoke, discovered);
        need(info.Id === discovered && info.Name === '/' + name
            && info.Config.Labels['ntilde.qa.run'] === runId, 'refusing cleanup of a foreign container');
        state.id = discovered;
        await invoke(['stop', '--timeout', '4', discovered]);
        // A create whose response was lost may never have started.
        let copyFailure;
        if (state.startedAt) {
            try { await invoke(['cp', discovered + ':' + containerOutput + '/.', output]); }
            catch (error) { copyFailure = error; }
        }
        await invoke(['rm', discovered]);
        return copyFailure;
    }
    return undefined;
}

export async function runContainer({ output, image, sources, sidecar, lease, appSourceRevision, audit }, invoke = docker) {
    let primary;
    const name = 'ntilde-identity-' + lease.leaseId;
    const state = { image, name, runId: lease.leaseId };
    try {
        // Record attempted launch before any resource can outlive this process.
        lease.noteLaunchAttempt();
        audit('launch-attempt');
        const id = await invoke(['create', '--name', name, '--label', 'ntilde.qa.run=' + lease.leaseId,
            '--init', '--user', '1000:1000', '--network', 'none', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges',
            ...bindMount(sources, '/sources'), ...bindMount(sidecar, '/inputs/sidecar.tar'),
            image, '/usr/bin/python3', '-I', '-B', '/sources/collect.py', containerOutput, lease.leaseId, appSourceRevision]);
        need(validId(id), 'invalid owned Docker ID');
        state.id = id;
        const info = await inspect(invoke, id);
        need(info.Id === id && info.Name === '/' + name && info.Config.Labels['ntilde.qa.run'] === lease.leaseId && info.Image === image
            && info.HostConfig.NetworkMode === 'none' && info.Config.User === '1000:1000'
            && info.Mounts.length === 2 && info.Mounts.every(mount => !mount.RW), 'wrong container isolation');
        state.isolation = { network: info.HostConfig.NetworkMode, user: info.Config.User,
            mounts: info.Mounts.map(({ Destination, RW }) => ({ destination: Destination, writable: RW })) };
        state.startedAt = new Date().toISOString();
        await invoke(['start', '-a', id], 115000);
        const after = await inspect(invoke, id);
        state.status = after.State.Status;
        state.exitCode = after.State.ExitCode;
        need(state.status === 'exited' && state.exitCode === 0, 'owned collector did not exit cleanly');
    } catch (error) {
        primary = error;
    }
    try {
        const copyFailure = await removeContainer(invoke, state, output);
        primary ??= copyFailure;
        const remaining = await Promise.all([
            invoke(['ps', '-aq', '--filter', 'label=ntilde.qa.run=' + lease.leaseId]),
            invoke(['ps', '-aq', '--filter', matchingName(name)]),
        ]);
        need(remaining.every(value => value === ''), 'owned collector container remains');
        state.removedAt = new Date().toISOString();
        lease.attestCleanup('owned-container-removed');
        audit('cleanup-verified');
    } catch (error) {
        throw primary ? new AggregateError([primary, error], 'collection and cleanup failed') : error;
    } finally {
        printJson(join(output, 'container.json'), state);
    }
    if (primary) throw primary;
}

function leaseAudit(lease) {
    const path = desktopLockAuditPath(lease.lockPath);
    const events = new Map();
    const capture = expected => {
        // Read the current file first. If it rotates during the read, the old file
        // still has the record. Re-read current to cover a simultaneous append.
        for (const file of [path, path + '.1', path]) {
            let lines;
            try { lines = readFileSync(file, 'utf8').split('\n'); }
            catch (error) {
                if (error.code !== 'ENOENT') throw error;
                continue;
            }
            for (const line of lines.filter(line => line.includes('"lease":"' + lease.leaseId + '"'))) {
                const entry = JSON.parse(line);
                if (entry.lease === lease.leaseId) events.set(line, entry);
            }
        }
        need([...events.values()].filter(entry => entry.event === expected).length === 1, 'incomplete desktop lease audit: ' + expected);
    };
    return { capture, rows: () => [...events.values()] };
}

export async function withLaunchLease(lease, output, collect) {
    const audit = leaseAudit(lease);
    let primary;
    let ready = false;
    try {
        mkdirSync(output, { mode: 0o700 });
        ready = true;
        audit.capture('acquired');
        await collect(audit.capture);
    } catch (error) {
        primary = error;
    }
    try {
        if (lease.launchAttempted && !lease.cleanupVerified) {
            lease.retain('Owned Docker container removal could not be proved.');
            audit.capture('retained');
        } else {
            lease.release();
            if (ready) audit.capture('released');
        }
        if (ready) printJson(join(output, 'desktop-lease.json'), audit.rows());
    } catch (error) {
        throw primary ? new AggregateError([primary, error], 'collection and lease settlement failed') : error;
    }
    if (primary) throw primary;
}

async function snapshotSources(sources, revision) {
    const shared = [fileURLToPath(import.meta.url), join(here, 'terminfo', 'desktop-lock.mjs')];
    const specific = sourceNames.map(name => join(here, 'terminfo', 'rich-identity', name));
    return Object.fromEntries(await Promise.all([...shared, ...specific].map(async path => {
        const gitPath = relative(repo, path).split(sep).join('/');
        const { stdout: blob } = await exec(gitExecutable, ['show', revision + ':' + gitPath], { cwd: repo, encoding: 'buffer' });
        const name = basename(path);
        need(readFileSync(path).equals(blob), 'collector source differs from ' + revision + ': ' + gitPath);
        writeFileSync(join(sources, name), blob, { mode: 0o444, flag: 'wx' });
        return [name, createHash('sha256').update(blob).digest('hex')];
    })));
}

async function main() {
    const [stageDirectory, imageName, appSourceRevision, ...extra] = process.argv.slice(2);
    need(stageDirectory && imageName && /^[a-f0-9]{40}$/.test(appSourceRevision ?? '') && extra.length === 0,
        'usage: node scripts/terminfo-rich-identity-owned.mjs PRIVATE-STAGE IMAGE APP-SOURCE-COMMIT');
    const stage = realpathSync(stageDirectory);
    const image = await docker(['image', 'inspect', '--format', '{{.Id}}', imageName]);
    need(/^sha256:[a-f0-9]{64}$/.test(image), 'invalid Docker image ID');
    const { stdout } = await exec(gitExecutable, ['rev-parse', 'HEAD'], { cwd: repo });
    const scriptsRevision = stdout.trim();
    need(/^[a-f0-9]{40}$/.test(scriptsRevision), 'invalid collector commit');
    const appSourceArchiveSha256 = await appSourceHash(join(stage, 'app-source.tar'), appSourceRevision);
    const lease = await acquireDesktopLease({ entrypoint, worktree: repo, waitMs: desktopLockWaitMs() });
    const output = join(stage, lease.leaseId);
    await withLaunchLease(lease, output, async audit => {
        console.log(output);
        const sources = join(output, 'sources');
        mkdirSync(sources, { mode: 0o755 });
        const files = await snapshotSources(sources, scriptsRevision);
        const sidecar = join(output, 'private-sidecar.tar');
        try {
            copyFileSync(join(stage, 'sidecar.tar'), sidecar, constants.COPYFILE_EXCL);
            chmodSync(sidecar, 0o444);
            const sidecarSha256 = await hash(sidecar);
            printJson(join(output, 'identity.json'), { appSourceRevision, appSourceArchiveSha256, sidecarSha256, scriptsRevision, files });
            await runContainer({ output, image, sources, sidecar, lease, appSourceRevision, audit });
            const after = await Promise.all(Object.entries(files).map(async ([name, sha]) => await hash(join(sources, name)) === sha));
            need(after.every(Boolean) && await hash(sidecar) === sidecarSha256, 'measured inputs changed');
        } finally {
            if (!lease.launchAttempted || lease.cleanupVerified) rmSync(sidecar, { force: true });
        }
    });
}

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
    try {
        await main();
    } catch (error) {
        console.error(error);
        process.exitCode = 1;
    }
}
