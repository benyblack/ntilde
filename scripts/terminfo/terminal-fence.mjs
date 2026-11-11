import { parseQueryFrame, querySequence, querySpec } from './color-query.mjs';
import { cursorReportQuery, parseCursorReportFrame } from './cursor-report.mjs';
import { checksumReportExpectation, parseChecksumReportFrame } from './checksum-report.mjs';
// DA is an ordering barrier only. Cell/cursor assertions come from readScreen.
export function terminalFence(input, output, timeoutMs = 1000) {
    const wasRaw = input.isRaw, wasPaused = input.isPaused();
    let pending, fatal, disposed = false, detached = false;
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
                let result;
                if (pending.expected === cursorReportQuery) result = parseCursorReportFrame(pending.bytes);
                else if (pending.expected === checksumReportExpectation) result = parseChecksumReportFrame(pending.bytes);
                else result = parseQueryFrame(pending.bytes, pending.expected);
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
        if (disposed || detached) return Promise.reject(new Error('Cell barrier is not active (disposed or detached).'));
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
        if (!fatal || pending || disposed || detached || typeof sequence !== 'string' || !sequence.length || sequence.length > 4096) {
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
        /** One cursor position report: the raw CPR (or its explicit absence) and the DA ordering barrier. */
        cursorReport(prefix = '') {
            return request(prefix + cursorReportQuery, cursorReportQuery);
        },
        /** One DECRQCRA exchange: the exact request bytes, then the raw DCS reply (or its explicit absence) and the DA ordering barrier. */
        checksumReport(sequence) {
            if (typeof sequence !== 'string' || !sequence.length || sequence.length > 256) return Promise.reject(new Error('Invalid rectangle checksum request.'));
            return request(sequence, checksumReportExpectation);
        },
        /** One bounded DCS exchange (a DECRQSS request, for example): the exact request bytes, then the raw DCS reply (or its explicit absence) and the DA ordering barrier. Same framing and limits as `checksumReport`. */
        dcsReport(sequence) {
            if (typeof sequence !== 'string' || !sequence.length || sequence.length > 64) return Promise.reject(new Error('Invalid DCS report request.'));
            return request(sequence, checksumReportExpectation);
        },
        /** One OSC color query exchange with the exact request bytes the caller chose (BEL or ST spelling): the raw reply (or its explicit absence) and the DA ordering barrier. */
        colorExchange(sequence, expected) {
            if (typeof sequence !== 'string' || !sequence.length || sequence.length > 64) return Promise.reject(new Error('Invalid owned color query request.'));
            try { return request(sequence, querySpec(expected)); } catch (error) { return Promise.reject(error); }
        },
        query(expected, prefix = '') {
            const spec = querySpec(expected);
            return request(prefix + querySequence(spec), spec);
        },
        detachInput() {
            if (disposed || detached) throw new Error('Expected an active, not detached fence.');
            if (fatal) throw new Error('Expected a healthy, not failed fence.');
            if (pending) throw new Error('Expected an idle fence without pending reads.');
            detached = true;
            input.removeListener('data', receive); input.removeListener('error', fail);
            // The next exclusive reader inherits raw input and controlled -opost.
            // Only outer dispose restores the modes captured by this original fence.
        },
        retireInput() {
            if (disposed || fatal || pending) throw new Error('Expected a healthy idle fence before final reader retirement.');
            disposed = true;
            input.removeListener('data', receive); input.removeListener('error', fail);
            // Stock owns the only reader. Keep raw/-opost until its final private close.
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
