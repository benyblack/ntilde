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


/** Observe close, not exit: only close follows final stdout/stderr delivery. Caller bounds the shutdown wait. */
export function linuxProviderOutputClosed(child, validate) {
    return new Promise((resolve, reject) => {
        child.once('error', reject);
        child.once('close', (code, signal) => {
            try { validate(); resolve({ code, signal }); } catch (error) { reject(error); }
        });
    });
}
