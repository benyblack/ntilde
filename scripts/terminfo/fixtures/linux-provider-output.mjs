/** Test-fixture output only. Refuse before retaining an oversized chunk and latch the refusal. */
export class LinuxProviderOutput {
    #chunks = []; #used = 0; #failed = false;
    append(bytes) {
        if (this.#failed) throw new Error('Linux provider fixture output already refused.');
        if (!Buffer.isBuffer(bytes) || bytes.length > 4096 - this.#used) {
            this.#failed = true;
            throw new Error('Oversized Linux provider fixture output.');
        }
        this.#chunks.push(Buffer.from(bytes)); this.#used += bytes.length;
    }
    text() { return Buffer.concat(this.#chunks, this.#used).toString('utf8'); }
}


/** Observe drained close, retaining actual exit facts plus explicit validation errors. Caller bounds the wait. */
export function linuxProviderOutputClosed(child, validate) {
    return new Promise(resolve => {
        child.once('error', error => resolve({ code: null, signal: null, error }));
        child.once('close', (code, signal) => {
            try { validate(); resolve({ code, signal }); }
            catch (error) { resolve({ code, signal, error }); }
        });
    });
}


/** Every retained byte must belong to an already validated newline-terminated frame. */
export function assertLinuxProviderOutputComplete(output, processed) {
    if (!Number.isSafeInteger(processed) || processed < 0 || processed !== output.text().length)
        throw new Error('Provider fixture left unterminated stdout.');
}
