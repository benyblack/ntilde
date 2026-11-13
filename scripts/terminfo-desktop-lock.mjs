#!/usr/bin/env node
// Offline desktop-lock diagnostics and explicit conservative recovery. It never acquires the lock, launches an App,
// or touches another session's collection, so it is safe to run while another session owns the desktop.
import { canonicalDesktopLockPath, inspectDesktopLock, recoverDesktopLease } from './terminfo/desktop-lock.mjs';

const usage = `Usage:
  node scripts/terminfo-desktop-lock.mjs status
  node scripts/terminfo-desktop-lock.mjs recover --lease <32-hex lease id> [--confirm-desktop-idle]

status   Print bounded JSON for ${canonicalDesktopLockPath()}; read-only.
recover  Move a dead holder's lock aside (evidence preserved, never deleted) only when ALL hold: the lock is a well-formed lease
         with exactly that id, its recorded process is gone (absent, or its PID was reused), no process carrying its run
         markers survives, and for a lease that attempted a launch you pass --confirm-desktop-idle after checking that no
         owned App window remains. Legacy, partial, malformed and live-holder locks are never recovered here.`;

const [command, ...rest] = process.argv.slice(2);
try {
    if (command === 'status' && rest.length === 0) console.log(JSON.stringify(inspectDesktopLock(), null, 2));
    else if (command === 'recover') {
        const known = new Set(['--lease', '--confirm-desktop-idle']);
        const leaseIndex = rest.indexOf('--lease');
        const stray = rest.filter((arg, index) => !known.has(arg) && index !== leaseIndex + 1);
        if (leaseIndex < 0 || stray.length || rest.filter(arg => arg === '--lease').length !== 1) throw new Error(usage);
        console.log(JSON.stringify(recoverDesktopLease({ leaseId: rest[leaseIndex + 1], confirmDesktopIdle: rest.includes('--confirm-desktop-idle') }), null, 2));
    } else if (['help', '--help', '-h'].includes(command) && rest.length === 0) console.log(usage);
    else throw new Error(usage);
} catch (error) {
    console.error('[terminfo-desktop-lock] ' + error.message);
    process.exitCode = 1;
}
