import { spawnSync } from 'node:child_process';
import { existsSync, lstatSync, mkdirSync, mkdtempSync, realpathSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, isAbsolute, join } from 'node:path';

export function resolveNpmCli(nodeExecutable = process.execPath, configuredPath = process.env.NTILDE_TERMINFO_NPM_CLI) {
    const path = configuredPath ?? join(dirname(nodeExecutable), '..', 'lib', 'node_modules', 'npm', 'bin', 'npm-cli.js');
    if (!isAbsolute(path)) throw new Error('NTILDE_TERMINFO_NPM_CLI must be an absolute npm-cli.js path.');
    if (!existsSync(path)) throw new Error('Prepare the collector cache manually or set NTILDE_TERMINFO_NPM_CLI; see docs/qa/QA_TERMINFO.md.');
    return realpathSync(path);
}

export function checkoutRevision(repo, gitExecutable = process.env.NTILDE_TERMINFO_GIT_EXECUTABLE
    ?? (process.platform === 'win32' ? null : '/usr/bin/git')) {
    if (gitExecutable === null) return null;
    if (!isAbsolute(gitExecutable)) throw new Error('NTILDE_TERMINFO_GIT_EXECUTABLE must be an absolute executable path.');
    const result = spawnSync(gitExecutable, ['-C', repo, 'rev-parse', 'HEAD'], {
        encoding: 'utf8', timeout: 2000, maxBuffer: 1024,
    });
    const revision = result.stdout?.trim() ?? '';
    return result.status === 0 && /^(?:[a-f\d]{40}|[a-f\d]{64})$/.test(revision) ? revision : null;
}

export function createPrivateScratch(root = join(homedir(), '.ntilde-qa')) {
    mkdirSync(root, { recursive: true, mode: 0o700 });
    const parent = lstatSync(root);
    if (!parent.isDirectory() || parent.isSymbolicLink() || parent.uid !== process.getuid()
        || (parent.mode & 0o077) !== 0) {
        throw new Error('The QA fixture parent must be a private directory owned by this user.');
    }
    // Darwin sockaddr_un allows 103 path bytes plus its terminating NUL.
    // Reject before making the fixture so a long home path leaves no scratch data.
    if (Buffer.byteLength(join(root, 'nt-123456', 'owner.sock')) > 103) {
        throw new Error('The private QA directory path is too long for a macOS owner socket.');
    }
    return mkdtempSync(join(root, 'nt-'));
}
