import { closeSync, constants, fstatSync, openSync, readSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
export const sttyTool = '/bin/stty';
export const ownedOutputPolicy = 'parent-verified -opost in proved private PTY for owned suites; previous mode restored before stock collection';
/** Require an explicit unambiguous disabled-output-processing fact from the system tool. */
export function validateOutputDescription(description) {
    if (typeof description !== 'string' || description.length > 4096) throw new Error('Invalid private PTY output description.');
    const tokens = new Set(description.split(/\s+/));
    if (!tokens.has('-opost') || tokens.has('opost')) throw new Error('Private PTY output translation is not disabled.');
}
/** Compare independently recorded kernel dimensions with the observed App grid. */
export function validateOutputGeometry(description, rows, cols) {
    validateOutputDescription(description);
    const match = /(?:^|[;\s])(\d+) rows;\s+(\d+) columns;/.exec(description);
    if (!match || Number(match[1]) !== rows || Number(match[2]) !== cols) throw new Error('Private PTY geometry does not match the observed App screen.');
}
/** Accept only a specific macOS PTY spelling; callers must separately prove its ownership. */
function ttyPath(tty) {
    if (typeof tty !== 'string' || !/^\/dev\/ttys\d+$/.test(tty)) throw new Error('Expected the proved private macOS worker PTY.');
}
function query(tty, args, run, timeoutMs) {
    ttyPath(tty);
    return run(sttyTool, ['-f', tty, ...args], { encoding: 'utf8', timeout: timeoutMs, maxBuffer: 4096 }).trim();
}
/** Independently read flags; an absent or contradictory fact never means untranslated output. */
export function readOwnedOutputMode(tty, timeoutMs = 1000, run = execFileSync) {
    const description = query(tty, ['-a'], run, timeoutMs);
    validateOutputDescription(description);
    return { tool: sttyTool, outputProcessing: false, description };
}
/** Caller must first prove the worker's exact PTY. Raw input alone still enables CRLF translation. */
export function prepareOwnedOutput(tty, run = execFileSync) {
    const saved = query(tty, ['-g'], run, 1000);
    if (!saved || saved.length > 4096 || /[\r\n]/.test(saved)) throw new Error('Invalid private PTY saved mode.');
    const restore = () => { query(tty, [saved], run, 1000); };
    try {
        query(tty, ['-opost'], run, 1000);
        readOwnedOutputMode(tty, 1000, run);
        return restore;
    } catch (error) {
        try { restore(); }
        catch (restoreError) { throw new AggregateError([error, restoreError], 'Private PTY output setup and restoration failed.'); }
        throw error;
    }
}

// New transport contract; historical receipts retain ownedOutputPolicy verbatim.
export const privateClipboardOutputPolicy = 'parent-verified -opost through owned suites and private stock close; exact original kernel mode restored before done';
/** Read a bounded canonical Darwin mode token from the already proved private PTY. */
export function readOwnedTerminalState(tty, timeoutMs = 1000, run = execFileSync) {
    const saved = query(tty, ['-g'], run, timeoutMs);
    if (!saved || saved.length > 4096 || !/^gfmt1:(?:[a-z][a-z0-9]*=[a-f0-9]+)(?::[a-z][a-z0-9]*=[a-f0-9]+)*$/.test(saved)) throw new Error('Invalid private PTY saved mode.');
    return saved;
}
/** Restore the original token, permit only the precise PENDIN settlement, then require exact kernel equality. */
export function restoreOwnedTerminalState(tty, original, timeoutMs = 1000, run = execFileSync) {
    if (typeof original !== 'string' || !original.length || original.length > 4096 || !/^gfmt1:(?:[a-z][a-z0-9]*=[a-f0-9]+)(?::[a-z][a-z0-9]*=[a-f0-9]+)*$/.test(original)) throw new Error('Invalid original private PTY mode.');
    query(tty, [original], run, timeoutMs);
    const observed = readOwnedTerminalState(tty, timeoutMs, run);
    if (observed !== original && pendingInputOnly(original, observed)) settlePendingInput(tty);
    if (readOwnedTerminalState(tty, timeoutMs, run) !== original) throw new Error('Private PTY original mode was not exactly restored.');
}
/** Attempt every independent restore even after falsy/original failures. Repeated calls never reapply modes. */
export function ownedTerminalRestorer(restores) {
    let called = false, failure;
    return () => {
        if (called) {
            if (failure) throw failure;
            return;
        }
        called = true; const errors = [];
        for (const restore of restores) { try { restore(); } catch (error) { errors.push(error); } }
        if (errors.length) { failure = new AggregateError(errors, 'Private PTY restoration failed.'); throw failure; }
    };
}

// Darwin canonical-mode transition sets PENDIN until the next kernel read. This
// is an explicit bounded input operation, never a normalization of final proof.
/** Recognize only a previously-clear Darwin PENDIN bit difference; do not normalize accepted state. */
function pendingInputOnly(original, observed) {
    if (process.platform !== 'darwin') return false;
    const a = /(?:^|:)lflag=([0-9a-f]+)(?=:|$)/.exec(original), b = /(?:^|:)lflag=([0-9a-f]+)(?=:|$)/.exec(observed);
    if (!a || !b || (BigInt('0x' + a[1]) & 0x20000000n) !== 0n
        || (BigInt('0x' + b[1]) ^ BigInt('0x' + a[1])) !== 0x20000000n) return false;
    return observed.replace(b[0], a[0]) === original;
}
/** Read at most one byte via a fresh same-device nonblocking descriptor; reject data and preserve read/close failures. */
function settlePendingInput(tty) {
    const fd = openSync(tty, constants.O_RDONLY | constants.O_NONBLOCK | constants.O_NOCTTY | constants.O_NOFOLLOW);
    let primary, failed = false;
    try {
        const actual = fstatSync(fd, { bigint: true }), input = fstatSync(0, { bigint: true });
        if (!actual.isCharacterDevice() || ['dev', 'rdev', 'ino'].some(key => actual[key] !== input[key])) throw new Error('Original private input device changed.');
        try {
            const count = readSync(fd, Buffer.alloc(1), 0, 1, null);
            if (count !== 0) throw new Error('Unexpected input during private PTY final restoration.');
        } catch (error) { if (error?.code !== 'EAGAIN' && error?.code !== 'EWOULDBLOCK') throw error; }
    } catch (error) { primary = error; failed = true; }
    try { closeSync(fd); }
    catch (error) {
        if (failed) throw new AggregateError([primary, error], 'Private PTY input settlement and close failed.');
        throw error;
    }
    if (failed) throw primary;
}
