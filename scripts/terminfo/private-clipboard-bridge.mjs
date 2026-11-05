import { PrivateClipboardController, PRIVATE_CLIPBOARD_PROBES } from './private-clipboard-controller.mjs';
import { privateClipboardNonce } from './private-clipboard-request.mjs';

function deferred() {
    let resolve, reject;
    const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
    void promise.catch(() => {});
    return { promise, resolve, reject };
}

function validateFrame(frame) {
    if (typeof frame !== 'object' || Array.isArray(frame) || typeof frame?.kind !== 'string')
        throw new Error('Invalid clipboard bridge frame or kind.');
    const fields = { start: ['kind', 'probeId'], arm: ['kind', 'value'], read: ['kind'],
        seed: ['kind', 'value'], complete: ['kind'], finish: ['kind'] }[frame.kind];
    if (Object.keys(frame).length !== fields?.length || !fields.every(key => Object.hasOwn(frame, key)))
        throw new Error('Unexpected clipboard bridge fields.');
    if (frame.kind === 'start' && !PRIVATE_CLIPBOARD_PROBES.includes(frame.probeId))
        throw new Error('Unknown clipboard bridge probe.');
    if (['arm', 'seed'].includes(frame.kind) && !privateClipboardNonce(frame.value))
        throw new Error('Invalid clipboard bridge nonce.');
}

/** Parent-side seam only. Callers authenticate every frame and independently prove
 * the App/process/PTY/source binding. This module grants no such authority.
 * The central collector is deliberately not wired to this seam yet.
 */
export class PrivateClipboardBridge {
    #controller; #fixture; #work; #transaction; #busy = false; #failure; #abortPromise; #finished = false;

    constructor(options) { this.#controller = new PrivateClipboardController(options); }

    evidence() {
        return { ...this.#controller.evidence(), complete: this.#finished && !this.#failure, bridgeFailed: Boolean(this.#failure),
            bridgeFinished: this.#finished, qualification: 'unwired-parent-bridge-only' };
    }

    async #start(probeId) {
        if (this.#transaction) throw new Error('Clipboard bridge callback already started.');
        const ready = deferred();
        this.#work = deferred();
        this.#transaction = this.#controller.transaction(probeId, fixture => {
            this.#fixture = fixture;
            ready.resolve();
            return this.#work.promise;
        });
        // Begin can fail before the callback exposes its fixture. Settle that
        // start request with the original error; never wait for a callback then.
        void this.#transaction.catch(error => ready.reject(error));
        await ready.promise;
        return { probeId };
    }

    async #complete() {
        if (!this.#transaction || !this.#fixture) throw new Error('Clipboard bridge callback is not active.');
        this.#work.resolve();
        await this.#transaction; // Includes independent restore and seed readback.
        this.#transaction = undefined; this.#work = undefined; this.#fixture = undefined;
        return { completedProbes: this.#controller.evidence().completedProbes };
    }

    async #dispatch(frame) {
        if (frame.kind === 'start') return this.#start(frame.probeId);
        if (frame.kind === 'complete') return this.#complete();
        if (frame.kind === 'finish') {
            if (this.#transaction) throw new Error('Clipboard bridge callback still active.');
            await this.#controller.finish();
            if (this.#failure) throw this.#failure;
            this.#finished = true;
            return this.evidence();
        }
        if (!this.#fixture) throw new Error('Clipboard bridge callback is not active.');
        if (frame.kind === 'arm') { await this.#fixture.beforeOsc52Write(frame.value); return null; }
        if (frame.kind === 'seed') { await this.#fixture.writeText(frame.value); return null; }
        if (frame.kind === 'read') return this.#fixture.readText();
        throw new Error('Unknown clipboard bridge kind.');
    }

    /** Strictly serialized authenticated frames. A refusal poisons the bridge even
     * when a downstream collector catches it and attempts to continue.
     */
    async handle(frame) {
        if (this.#finished) throw new Error('Clipboard bridge is closed.');
        if (this.#failure) throw this.#failure;
        if (this.#busy) {
            const error = new Error('Overlapping clipboard bridge operation.');
            await this.abort(error);
            throw error;
        }
        this.#busy = true;
        try {
            validateFrame(frame);
            const result = await this.#dispatch(structuredClone(frame));
            if (this.#failure) throw this.#failure;
            return result;
        } catch (error) {
            try { await this.abort(error); }
            catch (error_) {
                if (error_ === error) throw error;
                throw new AggregateError([error, error_], 'Clipboard bridge operation and cleanup failed.');
            }
            throw error;
        } finally { this.#busy = false; }
    }

    /** Reject the current callback, allowing the controller to attempt its bounded
     * restoration. No finish/close claim is manufactured and no retry is allowed.
     */
    abort(error = new Error('Clipboard bridge aborted.')) {
        if (this.#finished) return Promise.resolve();
        // Rejection/cancellation reasons are arbitrary JS values. Normalize the
        // failure marker while retaining the exact supplied value as its cause.
        this.#failure ??= error instanceof Error ? error : new Error('Clipboard bridge aborted.', { cause: error });
        this.#abortPromise ??= this.#settleAbort();
        return this.#abortPromise;
    }

    async #settleAbort() {
        this.#work?.reject(this.#failure);
        if (this.#transaction) await this.#transaction;
    }
}
