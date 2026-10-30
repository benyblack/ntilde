// Test-only preload: observe real resource boundaries without production hooks
// or timing-dependent sleeps. Existing smoke coverage also sends real OS signals.
import childProcess from 'node:child_process';
import fs from 'node:fs';
import { syncBuiltinESMExports } from 'node:module';
import { homedir } from 'node:os';
import { join } from 'node:path';

const log = process.env.NTILDE_TERMINFO_TEST_LIFECYCLE_LOG;
const selectedPhase = process.env.NTILDE_TERMINFO_TEST_LIFECYCLE_PHASE;
const prefix = join(homedir(), '.ntilde-qa', 'nt-');
const record = data => fs.appendFileSync(log, JSON.stringify(data) + '\n', { mode: 0o600 });
const handlers = () => ({ sigint: process.listenerCount('SIGINT'), sigterm: process.listenerCount('SIGTERM') });
let signaled = false;

function interruptAt(phase) {
    if (phase !== selectedPhase || signaled) return;
    signaled = true;
    record({ phase, when: 'before-signals', ...handlers() });
    process.emit('SIGINT');
    process.emit('SIGTERM');
    record({ phase, when: 'after-signals', ...handlers() });
}

const makeDirectory = fs.mkdtempSync;
fs.mkdtempSync = (...args) => {
    const path = makeDirectory(...args);
    if (path.startsWith(prefix)) {
        record({ scratch: path });
        interruptAt('preparation');
    }
    return path;
};

const spawn = childProcess.spawn;
childProcess.spawn = (...args) => {
    const child = spawn(...args);
    record({ appPid: child.pid });
    interruptAt('post-spawn');
    const kill = child.kill.bind(child);
    child.kill = signal => {
        interruptAt('teardown');
        return kill(signal);
    };
    return child;
};

const remove = fs.rmSync;
fs.rmSync = (path, ...args) => {
    if (typeof path === 'string' && path.startsWith(prefix)) interruptAt('fixture-removal');
    return remove(path, ...args);
};

process.once('beforeExit', () => record({ phase: 'finished', ...handlers() }));
syncBuiltinESMExports();
