import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { blinkHash, checkBlink } from './modern-blink.mjs';

const census = fileURLToPath(new URL('./modern-blink-window.jxa', import.meta.url));

function ownedWindow(pid, budget) {
    const windows = JSON.parse(execFileSync('/usr/bin/osascript', ['-l', 'JavaScript', census, String(pid)],
        { encoding: 'utf8', timeout: budget(), maxBuffer: 4096 }));
    checkBlink(windows.length === 1 && windows[0].length === 6 && windows[0].every(Number.isSafeInteger)
        && windows[0][1] === pid && windows[0][4] > 0 && windows[0][5] > 0, 'expected one on-screen owned native window');
    return windows[0];
}

export function captureBlinkWindow(output, file, pid, budget) {
    const before = ownedWindow(pid, budget), path = join(output, file);
    const startMs = performance.now();
    execFileSync('/usr/sbin/screencapture', ['-l', String(before[0]), '-o', '-x', '-t', 'png', path], { timeout: budget() });
    const endMs = performance.now();
    const after = ownedWindow(pid, budget);
    return { file, before, after, startMs, endMs, sha256: blinkHash(readFileSync(path)) };
}
