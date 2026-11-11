import { execFileSync } from 'node:child_process';
import { existsSync, realpathSync } from 'node:fs';
/** Resolve Git from explicit installation paths; never dispatch it through PATH. */
export function commonCoreGit() {
    const candidates =
        process.platform === 'win32'
            ? [
                  'C:/Program Files/Git/cmd/git.exe',
                  'C:/Program Files/Git/bin/git.exe',
                  'C:/Program Files (x86)/Git/cmd/git.exe',
              ]
            : ['/usr/bin/git'];
    const path = candidates.find((candidate) => existsSync(candidate));
    if (!path) throw new Error('Git is unavailable at the supported explicit installation paths.');
    return realpathSync(path);
}

/** Refuse dirty source before launching any application and recheck it before accepting completion. */
export function assertCleanCommonCoreSource(directory, expectedRevision) {
    const git = commonCoreGit();
    const revision = execFileSync(git, ['rev-parse', 'HEAD'], {
        cwd: directory,
        encoding: 'utf8',
        timeout: 5000,
    }).trim();
    const dirt = execFileSync(git, ['status', '--porcelain', '--untracked-files=all'], {
        cwd: directory,
        encoding: 'utf8',
        timeout: 5000,
    }).trim();
    if (dirt) throw new Error('Upstream worktree has uncommitted source inputs: ' + dirt);
    if (expectedRevision && revision !== expectedRevision)
        throw new Error('Upstream revision changed during collection.');
    return revision;
}
