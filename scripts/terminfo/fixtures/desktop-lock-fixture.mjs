// Test seam for launcher copies: point a COPIED launcher at a private lock path so deterministic tests never touch the
// shared desktop lock. Production has no such seam (no environment value or argument can move the lock); only this
// source rewrite of a temporary copy can, and it fails loudly if the launcher no longer builds its broker as expected.
import { copyFileSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';

const brokerCall = 'createDesktopLeaseBroker({ entrypoint';

export function privateLockLauncherSource(source, lockPath) {
    const patched = source.replace(brokerCall, 'createDesktopLeaseBroker({ lockPath: ' + JSON.stringify(lockPath) + ', entrypoint');
    if (patched === source) throw new Error('The launcher no longer builds its desktop lease broker where the fixture expects.');
    return patched;
}

/** Copy the launcher and its pinned lock helper into `root`, with the launcher bound to `lockPath`. */
export function installPrivateLockLauncher(repo, root, lockPath) {
    mkdirSync(join(root, 'scripts/terminfo'), { recursive: true });
    mkdirSync(dirname(lockPath), { recursive: true });
    writeFileSync(join(root, 'scripts/terminfo-owned.mjs'), privateLockLauncherSource(readFileSync(join(repo, 'scripts/terminfo-owned.mjs'), 'utf8'), lockPath));
    copyFileSync(join(repo, 'scripts/terminfo/desktop-lock.mjs'), join(root, 'scripts/terminfo/desktop-lock.mjs'));
}
