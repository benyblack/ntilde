#!/usr/bin/env node
import { execFile, spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { once } from 'node:events';
import { createReadStream, mkdirSync, readFileSync, realpathSync, writeFileSync } from 'node:fs';
import { basename, dirname, join, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import { acquireDesktopLease, desktopLockAuditPath, desktopLockWaitMs } from './terminfo/desktop-lock.mjs';

const exec = promisify(execFile);
const here = dirname(fileURLToPath(import.meta.url));
const repo = dirname(here);
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
    const git = spawn('git', ['archive', '--format=tar', revision], { cwd: repo, stdio: ['ignore', 'pipe', 'inherit'] });
    const [actual, expected, [exit]] = await Promise.all([hash(archive), hashStream(git.stdout), once(git, 'close')]);
    need(exit === 0 && actual === expected, 'App source archive differs from the exact Git archive of ' + revision);
    return actual;
}

const docker = async (args, timeout = 15000) => (await exec('docker', args, { timeout, maxBuffer: 4 * 1024 * 1024 })).stdout.trim();
const printJson = (path, value) => writeFileSync(path, JSON.stringify(value, null, 2) + '\n', { flag: 'wx' });

async function runContainer(name, output, image, sources, sidecar, lease, appSourceRevision) {
    let id, primary;
    const state = { image, name, runId: lease.leaseId };
    try {
        // Record attempted launch before any resource can outlive this process.
        lease.noteLaunchAttempt();
        id = await docker(['create', '--name', name, '--label', 'ntilde.qa.run=' + lease.leaseId,
            '--init', '--user', '1000:1000', '--network', 'none', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges',
            '--mount', 'type=bind,source=' + sources + ',target=/sources,readonly',
            '--mount', 'type=bind,source=' + sidecar + ',target=/inputs/sidecar.tar,readonly',
            image, '/usr/bin/python3', '-I', '-B', '/sources/collect.py', containerOutput, lease.leaseId, appSourceRevision]);
        need(/^[a-f0-9]{64}$/.test(id), 'invalid owned Docker ID');
        state.id = id;
        const [info] = JSON.parse(await docker(['inspect', id]));
        need(info.Id === id && info.Config.Labels['ntilde.qa.run'] === lease.leaseId && info.Image === image
            && info.HostConfig.NetworkMode === 'none' && info.Config.User === '1000:1000'
            && info.Mounts.length === 2 && info.Mounts.every(mount => !mount.RW), 'wrong container isolation');
        state.isolation = { network: info.HostConfig.NetworkMode, user: info.Config.User,
            mounts: info.Mounts.map(({ Destination, RW }) => ({ destination: Destination, writable: RW })) };
        state.startedAt = new Date().toISOString();
        await docker(['start', '-a', id], 115000);
        const [after] = JSON.parse(await docker(['inspect', id]));
        state.status = after.State.Status;
        state.exitCode = after.State.ExitCode;
        need(state.status === 'exited' && state.exitCode === 0, 'owned collector did not exit cleanly');
    } catch (error) {
        primary = error;
    }
    try {
        if (id) {
            await docker(['stop', '--timeout', '4', id]);
            try {
                await docker(['cp', id + ':' + containerOutput + '/.', output]);
            } catch (error) {
                primary ??= error;
            }
            await docker(['rm', id]);
        }
        const remaining = await docker(['ps', '-aq', '--filter', 'label=ntilde.qa.run=' + lease.leaseId]);
        need(!remaining, 'owned collector container remains');
        state.removedAt = new Date().toISOString();
        lease.attestCleanup('owned-container-removed');
    } catch (error) {
        throw primary ? new AggregateError([primary, error], 'collection and cleanup failed') : error;
    } finally {
        printJson(join(output, 'container.json'), state);
    }
    if (primary) throw primary;
}

function captureAudit(lease, output) {
    const path = desktopLockAuditPath(lease.lockPath);
    const events = [path + '.1', path].flatMap(file => {
        try {
            return readFileSync(file, 'utf8').trim().split('\n')
                .filter(line => line.includes(lease.leaseId)).map(line => JSON.parse(line));
        } catch (error) {
            if (error.code !== 'ENOENT') throw error;
            return [];
        }
    }).filter(event => event.lease === lease.leaseId);
    printJson(join(output, 'desktop-lease.json'), events);
}

async function snapshotSources(sources, revision) {
    const shared = [fileURLToPath(import.meta.url), join(here, 'terminfo', 'desktop-lock.mjs')];
    const specific = sourceNames.map(name => join(here, 'terminfo', 'rich-identity', name));
    return Object.fromEntries(await Promise.all([...shared, ...specific].map(async path => {
        const gitPath = relative(repo, path).split(sep).join('/');
        const { stdout: blob } = await exec('git', ['show', revision + ':' + gitPath], { cwd: repo, encoding: 'buffer' });
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
    const sidecar = join(stage, 'sidecar.tar');
    const image = await docker(['image', 'inspect', '--format', '{{.Id}}', imageName]);
    need(/^sha256:[a-f0-9]{64}$/.test(image), 'invalid Docker image ID');
    const { stdout } = await exec('git', ['rev-parse', 'HEAD'], { cwd: repo });
    const appSourceArchiveSha256 = await appSourceHash(join(stage, 'app-source.tar'), appSourceRevision);
    const sidecarSha256 = await hash(sidecar);
    const lease = await acquireDesktopLease({ entrypoint, worktree: repo, waitMs: desktopLockWaitMs() });
    const output = join(stage, lease.leaseId);
    try {
        mkdirSync(output, { mode: 0o700 });
        console.log(output);
        const sources = join(output, 'sources');
        mkdirSync(sources, { mode: 0o755 });
        const scriptsRevision = stdout.trim();
        need(/^[a-f0-9]{40}$/.test(scriptsRevision), 'invalid collector commit');
        const files = await snapshotSources(sources, scriptsRevision);
        printJson(join(output, 'identity.json'), { appSourceRevision, appSourceArchiveSha256, sidecarSha256, scriptsRevision, files });
        await runContainer('ntilde-identity-' + lease.leaseId, output, image, sources, sidecar, lease, appSourceRevision);
        const after = await Promise.all(Object.entries(files).map(async ([name, sha]) => await hash(join(sources, name)) === sha));
        need(after.every(Boolean) && await hash(sidecar) === sidecarSha256, 'measured inputs changed');
    } catch (error) {
        if (lease.launchAttempted && !lease.cleanupVerified) lease.retain('Owned Docker container removal could not be proved.');
        throw error;
    } finally {
        if (!lease.launchAttempted || lease.cleanupVerified) {
            lease.release();
            captureAudit(lease, output);
        }
    }
}

try {
    await main();
} catch (error) {
    console.error(error);
    process.exitCode = 1;
}
