// Internal fixed-operation runner. No arbitrary command/argv/TTY API; only the public adapter constructs handles.
import { spawn } from 'node:child_process';
import { linuxOutputLimits, parseLinuxOutputMode } from './linux-owned-output-core.mjs';

function argumentsFor(kind, saved, hasPty) {
    if (typeof kind !== 'string') throw new Error('Invalid Linux stty operation selector.');
    if (kind === 'version' && saved === null) return ['--version'];
    if (!hasPty) throw new Error('Linux stty operation requires the held private PTY.');
    const fixed = { mode: '-g', size: 'size', description: '-a', disable: '-opost' };
    if (Object.hasOwn(fixed, kind) && saved === null) return ['--file=/proc/self/fd/3', fixed[kind]];
    if (kind === 'restore' && typeof saved === 'string' && parseLinuxOutputMode(saved + '\n') === saved)
        return ['--file=/proc/self/fd/3', saved];
    throw new Error('Unsupported Linux stty operation.');
}

/** Test injection is internal only; production uses the fixed spawn implementation below. */
export function createLinuxSttyRunner({ spawnChild = spawn, setTimer = setTimeout, clearTimer = clearTimeout } = {}) {
    return function run(heldTool, heldPty, kind, saved, budget) {
        const args = argumentsFor(kind, saved, !!heldPty);
        const workMs = budget.utilityMs();
        const workDeadline = budget.now() + BigInt(workMs) * 1000000n;
        if (!Number.isInteger(heldTool?.fd) || heldTool.fd < 0 || (heldPty && (!Number.isInteger(heldPty.fd) || heldPty.fd < 0)))
            throw new Error('Invalid internally held Linux stty descriptors.');
        return new Promise((resolve, reject) => {
            let child, workTimer, closeTimer, primary, finished = false, closed = false, exited = false, killSent = false;
            let exitCode = null, exitSignal = null;
            const stdout = [], stderr = [];
            let stdoutBytes = 0, stderrBytes = 0;
            const text = chunks => new TextDecoder('utf-8', { fatal: true }).decode(Buffer.concat(chunks));
            const discard = () => {
                child.stdout?.removeListener('data', out);
                child.stderr?.removeListener('data', err);
                child.stdout?.resume(); child.stderr?.resume();
            };
            const finish = (error, confirmed) => {
                if (finished) return;
                finished = true; clearTimer(workTimer); clearTimer(closeTimer); discard();
                if (error) {
                    const failure = new AggregateError([error], 'Bounded Linux stty operation failed.');
                    Object.defineProperties(failure, {
                        utilityCloseConfirmed: { value: confirmed },
                        operation: { value: kind },
                        output: { value: Object.freeze({ stdoutBytes, stderrBytes, exitCode, exitSignal,
                            stdoutHex: Buffer.concat(stdout).toString('hex'), stderrHex: Buffer.concat(stderr).toString('hex') }) },
                    });
                    reject(failure);
                } else {
                    try { resolve(text(stdout)); }
                    catch (decodeError) {
                        Object.defineProperty(decodeError, 'utilityCloseConfirmed', { value: true }); reject(decodeError);
                    }
                }
            };
            const stop = error => {
                if (finished) return;
                primary ??= error;
                discard();
                if (closed || exited || killSent || !child?.pid) return;
                killSent = true;
                // Only this direct ChildProcess handle. Never process.kill or an App/worker PID.
                try { child.kill('SIGKILL'); }
                catch (killError) { primary = new AggregateError([primary, killError], 'Private utility termination failed.'); }
            };
            const append = (chunk, target, used) => {
                if (finished || primary) return used;
                if (!Buffer.isBuffer(chunk) || chunk.length > linuxOutputLimits.outputBytes - used) {
                    stop(new Error('Linux stty output exceeded its fixed byte budget.')); return used;
                }
                target.push(Buffer.from(chunk)); return used + chunk.length;
            };
            const out = chunk => { stdoutBytes = append(chunk, stdout, stdoutBytes); };
            const err = chunk => { stderrBytes = append(chunk, stderr, stderrBytes); };
            try {
                child = spawnChild('/proc/self/fd/4', args, {
                    argv0: 'stty', shell: false, detached: false,
                    env: { LC_ALL: 'C', LANG: 'C', PATH: '/usr/bin:/bin' },
                    stdio: ['ignore', 'pipe', 'pipe', heldPty ? heldPty.fd : 'ignore', heldTool.fd],
                });
            } catch (error) {
                // spawn threw before it returned an owned child handle; no deferred command is queued.
                const failure = new AggregateError([error], 'Linux stty spawn failed before returning a child.');
                Object.defineProperty(failure, 'utilityCloseConfirmed', { value: true }); reject(failure); return;
            }
            child.stdout.on('data', out); child.stderr.on('data', err);
            child.once('error', error => stop(error));
            child.stdout.once('error', error => stop(error)); child.stderr.once('error', error => stop(error));
            child.once('exit', () => { exited = true; });
            child.once('close', (code, signal) => {
                closed = true; exitCode = code; exitSignal = signal;
                if (finished) return;
                if (code !== 0 || signal !== null) primary ??= new Error('Linux stty did not exit successfully.');
                if (stderrBytes !== 0) primary ??= new Error('Linux stty emitted unexpected diagnostics.');
                // A successful exit after the deadline is still a failed operation.
                if (budget.now() >= budget.deadlineNs) primary ??= new Error('Linux stty closed after the shared deadline.');
                finish(primary, true);
            });
            const delay = end => Math.max(0, Number((end - budget.now()) / 1000000n));
            workTimer = setTimer(() => stop(new Error('Linux stty operation deadline expired.')), delay(workDeadline));
            closeTimer = setTimer(() => {
                if (finished) return;
                stop(new Error('Linux stty process/stdio closure is unconfirmed.'));
                // Destroy only our pipe ends; do not reinterpret a later close event as success.
                child.stdout.destroy(); child.stderr.destroy();
                child.unref(); // Closure remains explicitly unknown/fatal, never completed evidence.
                finish(primary, false);
            }, delay(budget.deadlineNs));
            try { budget.check(); } catch (error) { stop(error); }
        });
    };
}

export const runLinuxStty = createLinuxSttyRunner();
