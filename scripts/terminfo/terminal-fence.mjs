import { parseQueryFrame, querySequence, querySpec } from './color-query.mjs';
// DA is an ordering barrier only. Cell/cursor assertions come from readScreen.
export function terminalFence(input, output, timeoutMs = 1000) {
    const wasRaw = input.isRaw, wasPaused = input.isPaused();
    let pending, fatal, disposed = false;
    const fail = error => {
        fatal = error;
        pending?.finish(error);
    };
    const receive = chunk => {
        if (!pending) return fail(new Error('Unexpected input during cell collection.'));
        const limit = pending.expected ? 512 : 128;
        if (pending.bytes.length + chunk.length > limit) return fail(new Error('Invalid or oversized barrier response.'));
        pending.bytes += chunk.toString('latin1');
        if (pending.expected) {
            try {
                const result = parseQueryFrame(pending.bytes, pending.expected);
                if (result) pending.finish(null, result);
            } catch (error) { fail(error); }
            return;
        }
        if (pending.bytes.length > 128 || !pending.bytes.startsWith('\x1b')) return fail(new Error('Invalid cell barrier response.'));
        if (/^\x1b\[\?[\d;]+c$/.test(pending.bytes)) pending.finish(null, pending.bytes);
    };
    input.setRawMode(true);
    input.on('data', receive); input.on('error', fail); input.resume();
    const request = (sequence, expected = null) => {
        if (fatal) return Promise.reject(fatal);
        if (pending) return Promise.reject(new Error('Concurrent cell barriers are forbidden.'));
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => fail(new Error('Cell barrier timed out.')), timeoutMs);
            pending = { bytes: '', expected, finish(error, reply) {
                clearTimeout(timer); pending = undefined;
                if (error) reject(error); else resolve(reply);
            } };
            output.write(sequence + '\x1b[c', error => { if (error) fail(error); });
        });
    };
    // Best-effort writes after a fatal input error never request/accept a barrier and
    // never revive the fence. Only disposable-fixture teardown uses this path.
    const writeCleanup = sequence => {
        if (!fatal || pending || disposed || typeof sequence !== 'string' || !sequence.length || sequence.length > 4096) {
            return Promise.reject(new Error('Cleanup requires a failed, active fence and bounded bytes.'));
        }
        return new Promise((resolve, reject) => {
            let settled = false;
            const finish = error => {
                if (settled) return;
                settled = true; clearTimeout(timer);
                if (error) reject(error); else resolve();
            };
            const timer = setTimeout(() => finish(new Error('Cleanup write timed out.')), timeoutMs);
            try { output.write(sequence, finish); }
            catch (error) { finish(error); }
        });
    };
    return {
        get failed() { return Boolean(fatal); },
        writeCleanup,
        write(sequence) { return request(sequence); },
        query(expected, prefix = '') {
            const spec = querySpec(expected);
            return request(prefix + querySequence(spec), spec);
        },
        dispose() {
            disposed = true;
            pending?.finish(new Error('Cell barrier disposed.'));
            input.removeListener('data', receive); input.removeListener('error', fail);
            input.setRawMode(Boolean(wasRaw));
            if (wasPaused) input.pause();
        },
    };
}
