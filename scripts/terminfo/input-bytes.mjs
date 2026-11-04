import { randomInt } from 'node:crypto';
// Exclusive raw child-stdin capture for a declared generated event. The caller
// separately proves the App/worker/PTY relation and validates the action result.
const maximumInput = 512, maximumBarrier = 128;
function freshNonces(used) {
    for (let attempt = 0; attempt < 8; attempt++) {
        const first = randomInt(32768, 65536);
        let second = randomInt(32768, 65535);
        if (second >= first) second++;
        const pair = [first, second];
        if (!used.has(pair.join(','))) return pair;
    }
    throw new Error('Input ordering challenge generation exhausted.');
}
function unframedInputLength(bytes, expected, minimumOffset) {
    let length = bytes.length;
    if (!expected) return length;
    for (let prefix = 1; prefix < expected.length; prefix++) {
        if (bytes.length - prefix >= minimumOffset && bytes.subarray(-prefix).equals(expected.subarray(0, prefix))) length = bytes.length - prefix;
    }
    return length;
}
function challenge(nextNonces, used) {
    const nonces = nextNonces(used);
    if (!Array.isArray(nonces) || nonces.length !== 2
        || !nonces.every(n => Number.isSafeInteger(n) && n >= 32768 && n <= 65535)
        || nonces[0] === nonces[1] || used.has(nonces.join(','))) throw new Error('Invalid or reused input ordering challenge.');
    used.add(nonces.join(','));
    return { nonces, query: nonces.map(n => `\x1b[?${n}$p`).join(''),
        expected: Buffer.from(nonces.map(n => `\x1b[?${n};0$y`).join('')) };
}

export function rawInputCapture(input, output, timeoutMs = 4500, nextNonces = freshNonces) {
    if (!Number.isSafeInteger(timeoutMs) || timeoutMs < 1 || timeoutMs > 10000
        || input.listenerCount('data') !== 0 || input.readableEncoding != null) {
        throw new Error('Raw input capture requires exclusive, undecoded input and a bounded deadline.');
    }
    const wasRaw = input.isRaw, wasPaused = input.isPaused(), used = new Set();
    let pending, lastAttempt, fatal, disposed = false, captures = 0;
    const fail = error => {
        fatal ??= error;
        if (lastAttempt) lastAttempt.error = String(fatal.message ?? fatal).slice(0, 1024);
        pending?.finish(fatal);
    };
    const receive = chunk => {
        if (Buffer.isBuffer(chunk) && chunk.length === 0) return;
        if (!pending) return fail(new Error('Unexpected input outside the generated-event capture.'));
        if (!Buffer.isBuffer(chunk) || pending.bytes.length + chunk.length > maximumInput + maximumBarrier) {
            return fail(new Error('Invalid or oversized raw input capture.'));
        }
        // Copy before the emitter or a retained backing buffer can mutate bytes.
        pending.bytes = Buffer.concat([pending.bytes, chunk]);
        const offset = pending.expected ? pending.bytes.indexOf(pending.expected, pending.minimumFenceOffset) : -1;
        if (offset < 0) {
            if (unframedInputLength(pending.bytes, pending.expected, pending.minimumFenceOffset) > maximumInput) {
                fail(new Error('Generated input exceeds the capture budget.'));
            }
            return;
        }
        if (offset > maximumInput || pending.bytes.length !== offset + pending.expected.length) {
            return fail(new Error('Invalid raw-input ordering reply or trailing input.'));
        }
        pending.finish(null, { hex: pending.bytes.subarray(0, offset).toString('hex'), byteLength: offset,
            barrier: pending.expected.toString(), nonces: [...pending.nonces] });
    };
    input.setRawMode(true);
    input.on('data', receive); input.on('error', fail); input.resume();
    return {
        get failed() { return Boolean(fatal); },
        get lastAttempt() {
            if (!lastAttempt) return null;
            return { hex: lastAttempt.bytes.toString('hex'), nonces: lastAttempt.nonces ?? null,
                fenceRequested: Boolean(lastAttempt.expected), completed: lastAttempt.completed, error: lastAttempt.error ?? null };
        },
        capture(action) {
            if (fatal) return Promise.reject(fatal);
            if (disposed || typeof action !== 'function') return Promise.reject(new Error('Expected an active capture and a declared action.'));
            if (pending) return Promise.reject(new Error('Concurrent generated-event captures are forbidden.'));
            if (++captures > 128) return Promise.reject(new Error('Generated-event capture budget exhausted.'));
            return new Promise((resolve, reject) => {
                const controller = new AbortController();
                const timer = setTimeout(() => fail(new Error('Generated input capture timed out.')), timeoutMs);
                const attempt = { bytes: Buffer.alloc(0), minimumFenceOffset: 0, completed: false, finish(error, result) {
                    clearTimeout(timer); pending = undefined;
                    if (error) { attempt.error = String(error.message ?? error).slice(0, 1024); controller.abort(error); reject(error); }
                    else { attempt.completed = true; resolve(result); }
                } };
                pending = lastAttempt = attempt;
                // Choose unpredictable, clamped-parameter-safe mode numbers only
                // after the action RPC returns. These high private modes are not
                // implemented; two exact DECRPM replies are an ordering fence,
                // not a capability grade. Local input/replies share a FIFO writer.
                Promise.resolve().then(() => action(controller.signal)).then(() => {
                    if (pending !== attempt || fatal || disposed) return;
                    Object.assign(attempt, challenge(nextNonces, used), { minimumFenceOffset: attempt.bytes.length });
                    output.write(attempt.query, error => { if (error) fail(error); });
                }).catch(fail);
            });
        },
        dispose() {
            if (disposed) return;
            disposed = true;
            pending?.finish(new Error('Raw input capture disposed.'));
            input.removeListener('data', receive); input.removeListener('error', fail);
            input.setRawMode(Boolean(wasRaw));
            if (wasPaused) input.pause();
        },
    };
}
