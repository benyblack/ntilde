import { parseQueryFrame, querySequence, querySpec } from './color-query.mjs';
// DA is an ordering barrier only. Cell/cursor assertions come from readScreen.
export function terminalFence(input, output, timeoutMs = 1000) {
    const wasRaw = input.isRaw, wasPaused = input.isPaused();
    let pending, fatal;
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
    return {
        write(sequence) { return request(sequence); },
        query(expected, prefix = '') {
            const spec = querySpec(expected);
            return request(prefix + querySequence(spec), spec);
        },
        dispose() {
            pending?.finish(new Error('Cell barrier disposed.'));
            input.removeListener('data', receive); input.removeListener('error', fail);
            input.setRawMode(Boolean(wasRaw));
            if (wasPaused) input.pause();
        },
    };
}
