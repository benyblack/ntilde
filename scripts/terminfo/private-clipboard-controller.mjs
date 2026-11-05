import { createHash } from 'node:crypto';
import { privateClipboardNonce, validatePrivateClipboardRequest, validatePrivateClipboardResult } from './private-clipboard-request.mjs';

export const PRIVATE_CLIPBOARD_PROBES = Object.freeze([
    'extensions.osc52-clipboard', 'extensions.osc52-write', 'extensions.osc52-read',
]);
const hash = value => createHash('sha256').update(value, 'utf8').digest('hex');

// Consumer assimilation is the required observation here. This deliberately
// instruments a native Promise, delegates to its implementation and keeps native
// species. The exact then override is reviewed and covered by await, ignored-
// rejection and chaining controls; a plain thenable is not used.
class ClipboardControlPromise extends Promise {
    #consume;
    constructor(promise, consume) {
        super((resolve, reject) => promise.then(resolve, reject));
        this.#consume = consume;
    }
    static get [Symbol.species]() { return Promise; }
    then(onFulfilled, onRejected) { // NOSONAR - S7739: intentional Promise consumption instrumentation; see class remarks.
        this.#consume();
        return super.then(onFulfilled, onRejected);
    }
}

function clipboardControlPromise(promise, consume) {
    const result = new ClipboardControlPromise(promise, consume);
    // Handling an ignored rejection internally is not caller consumption.
    void Promise.prototype.then.call(result, undefined, () => {});
    return result;
}

/** Prototype only: caller-supplied binding verification must independently prove process/PTY/source identity.
 * Its stable digest is an equality witness, never proof supplied by this helper or by App results.
 */
export class PrivateClipboardController {
    #runId; #paneId; #rpc; #verify; #budget; #binding; #fixtureId; #busy = false; #fatal = false;
    #finished = false; #attemptedBegin = false; #restoreFailed = false; #completed = []; #events = []; #count = 0;
    #seed; #current; #restored = false; #phase = []; #probe; #pendingNonce;

    constructor({ runId, paneId, rpc, verifyBinding, budget }) {
        validatePrivateClipboardRequest({ runId, paneId, operation: 'begin' });
        if ([rpc, verifyBinding, budget].some(value => typeof value !== 'function')) throw new Error('Missing private clipboard boundary.');
        this.#runId = runId; this.#paneId = paneId; this.#rpc = rpc; this.#verify = verifyBinding; this.#budget = budget;
        this.#seed = 'terminfo-private-seed-' + runId; this.#current = this.#seed;
    }

    /** Defensive evidence copies contain source-known fixture data only, never arbitrary response values. */
    evidence() {
        return structuredClone({ profile: 'ntilde-private-app-prototype', boundary: 'App-private storage only; no native interchange or child-delivery attestation',
            runId: this.#runId, paneId: this.#paneId, fixtureId: this.#fixtureId ?? null, fatal: this.#fatal,
            complete: this.#finished && !this.#fatal, completedProbes: this.#completed, events: this.#events });
    }

    #assertOpen() {
        if (this.#fatal || this.#finished) throw new Error('Private clipboard lifecycle cannot resume.');
    }

    async #checkBinding() {
        const witness = await this.#verify(this.#budget());
        if (typeof witness !== 'string' || !/^[a-f\d]{64}$/.test(witness)) throw new Error('Missing private clipboard binding witness.');
        this.#binding ??= witness;
        if (witness !== this.#binding) throw new Error('Private clipboard binding changed.');
    }

    /** Retain actual validated App facts even when the post-operation binding check fails. No replay. */
    async #call(operation, value, readExpectation) {
        if (this.#events.length >= 64) throw new Error('Private clipboard operation budget exhausted.');
        const request = { paneId: this.#paneId, runId: this.#runId, operation,
            ...(operation === 'begin' ? {} : { fixtureId: this.#fixtureId }), ...(value === undefined ? {} : { value }) };
        validatePrivateClipboardRequest(request);
        const event = { index: this.#events.length, probeId: this.#probe ?? null, request, rpcStarted: false, postVerified: false };
        this.#events.push(event);
        try {
            await this.#checkBinding();
            const timeout = this.#budget();
            if (!Number.isSafeInteger(timeout) || timeout < 1 || timeout > 4000) throw new Error('Invalid private clipboard deadline.');
            event.rpcStarted = true;
            const result = validatePrivateClipboardResult(await this.#rpc(structuredClone(request), timeout), request);
            if (operation === 'read') {
                this.#require(result, readExpectation);
                if (result.value !== readExpectation.value) throw new Error('Missing private clipboard readback.');
            }
            event.result = result;
            if (operation === 'begin') this.#fixtureId = result.fixtureId;
            await this.#checkBinding();
            event.postVerified = true;
            return result;
        } catch (error) {
            event.failed = true; this.#fatal = true;
            throw error;
        }
    }

    #require(result, { value = this.#current, count = this.#count, restored = this.#restored, closed = false } = {}) {
        if (result.fixtureId !== this.#fixtureId || result.state !== (closed ? 'closed' : 'active')
            || result.complete !== closed || result.acceptedWrites !== count || result.restored !== restored
            || result.valueSha256 !== hash(value) || (Object.hasOwn(result, 'value') && result.value !== null && result.value !== value)) {
            this.#fatal = true;
            throw new Error('Private clipboard state/hash/count mismatch.');
        }
    }

    async #readExpected(value = this.#current, count = this.#count, restored = this.#restored) {
        const result = await this.#call('read', undefined, { value, count, restored });
        return result.value;
    }

    /** Lazy begin occurs at the first Q11 transaction, never at owner startup before RIS cases. */
    async #begin() {
        if (this.#attemptedBegin) throw new Error('Private clipboard cannot begin twice.');
        this.#attemptedBegin = true;
        this.#require(await this.#call('begin'));
        await this.#restore();
    }

    /** Restore cancels an arm and independently reads the fixed seed; never closes or revives a fixture. */
    async #restore() {
        if (!this.#fixtureId) return;
        if (this.#restoreFailed) throw new Error('Private clipboard restoration failed; do not retry.');
        try {
            const result = await this.#call('restore');
            // A write may have landed before a failed read; only retain a monotonic bounded actual count.
            const maximum = this.#count + (this.#pendingNonce ? 1 : 0);
            if (result.acceptedWrites < this.#count || result.acceptedWrites > maximum) throw new Error('Invalid private clipboard restore count.');
            this.#count = result.acceptedWrites;
            this.#require(result, { value: this.#seed, restored: true });
            this.#current = this.#seed; this.#restored = true; this.#pendingNonce = undefined;
            await this.#readExpected();
        } catch (error) { this.#restoreFailed = true; throw error; }
    }

    #step(name) {
        if (this.#phase.shift() !== name) throw new Error('Private clipboard callback operation order changed.');
    }

    async #beforeWrite(nonce) {
        this.#assertOpen(); this.#step('arm');
        if (!privateClipboardNonce(nonce)) throw new Error('Invalid private clipboard nonce.');
        this.#require(await this.#call('armWrite', nonce));
        this.#pendingNonce = nonce;
    }

    async #readText() {
        this.#assertOpen(); this.#step('readWrite');
        if (!this.#pendingNonce) throw new Error('Private clipboard write was not armed.');
        const value = this.#pendingNonce;
        await this.#readExpected(value, this.#count + 1, false);
        this.#count++; this.#current = value; this.#restored = false; this.#pendingNonce = undefined;
        return value;
    }

    async #writeText(value) {
        this.#assertOpen(); this.#step('seed');
        if (!privateClipboardNonce(value)) throw new Error('Invalid private clipboard nonce.');
        this.#require(await this.#call('seed', value), { value, restored: false });
        this.#current = value; this.#restored = false;
        await this.#readExpected();
    }

    async #runWork(probeId, work) {
        let result;
        this.#phase = probeId === PRIVATE_CLIPBOARD_PROBES[0] ? ['arm', 'readWrite', 'seed'] : ['arm', 'readWrite'];
        if (probeId === PRIVATE_CLIPBOARD_PROBES[2]) this.#phase = ['seed'];
        const lease = { active: true, pending: null, controls: [] };
        const invoke = action => {
            if (!lease.active || lease.pending) {
                this.#fatal = true;
                return clipboardControlPromise(Promise.reject(new Error('Private clipboard callback is inactive or busy.')), () => {});
            }
            const control = { consumed: false, promise: null };
            lease.pending = control;
            lease.controls.push(control);
            control.promise = Promise.resolve().then(() => {
                if (!lease.active) throw new Error('Private clipboard callback ended before its operation.');
                return action();
            }).then(value => {
                if (!lease.active) throw new Error('Private clipboard operation was not awaited.');
                this.#assertOpen();
                return value;
            }).catch(error => {
                this.#fatal = true;
                throw error;
            }).finally(() => {
                lease.pending = null;
            });
            // Track consumption independently of settlement; ignored controls can settle
            // while the callback awaits unrelated work. Keep their errors handled here.
            control.promise.catch(() => {});
            return clipboardControlPromise(control.promise, () => { control.consumed = true; });
        };
        const fixture = Object.freeze({ beforeOsc52Write: value => invoke(() => this.#beforeWrite(value)),
            readText: () => invoke(() => this.#readText()), writeText: value => invoke(() => this.#writeText(value)) });
        try {
            result = await work(fixture);
            if (lease.pending || this.#phase.length || lease.controls.some(control => !control.consumed))
                throw new Error('Private clipboard callback omitted or did not await its controls.');
        } finally {
            lease.active = false;
            if (lease.pending) await lease.pending.promise.catch(() => {});
        }
        this.#assertOpen();
        return result;
    }

    /** Exact three stock callback spellings in their pinned tail order; no reentrancy or automatic retry. */
    async transaction(probeId, work) {
        if (this.#busy || typeof work !== 'function' || probeId !== PRIVATE_CLIPBOARD_PROBES[this.#completed.length]) {
            this.#fatal = true; throw new Error('Invalid or overlapping private clipboard transaction.');
        }
        this.#assertOpen(); this.#busy = true; this.#probe = probeId;
        let result; const errors = [];
        try {
            if (!this.#attemptedBegin) await this.#begin();
            await this.#readExpected(this.#seed, this.#count, true);
            result = await this.#runWork(probeId, work);
        } catch (error) { errors.push(error); this.#fatal = true; }
        finally {
            try { await this.#restore(); }
            catch (error) { errors.push(error); this.#fatal = true; }
            this.#busy = false; this.#probe = undefined;
        }
        // Rejection reasons may be falsy; presence, not truthiness, decides failure.
        if (errors.length > 1) throw new AggregateError(errors, 'Private clipboard callback and restoration failed.');
        if (errors.length) throw errors[0];
        this.#completed.push(probeId);
        return result; // Preserve the stock grader, including empty-read denial and negative observations.
    }

    /** Only this explicit final close may yield completion; caller must separately finish process/PTY cleanup. */
    async finish() {
        this.#assertOpen();
        if (this.#busy || this.#completed.length !== 3 || this.#count !== 2 || !this.#restored) {
            this.#fatal = true; throw new Error('Private clipboard probes are not complete.');
        }
        this.#busy = true;
        try {
            await this.#readExpected(this.#seed, 2, true);
            this.#require(await this.#call('close'), { value: this.#seed, count: 2, restored: true, closed: true });
            this.#finished = true;
            return this.evidence();
        } catch (error) { this.#fatal = true; throw error; }
        finally { this.#busy = false; }
    }
}
