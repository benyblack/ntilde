import { PrivateClipboardBridge } from './private-clipboard-bridge.mjs';
import { PRIVATE_CLIPBOARD_PROBES } from './private-clipboard-controller.mjs';
import { privateClipboardNonce, privateClipboardRequest } from './private-clipboard-request.mjs';
import { observerEndpoint, snapshotHash } from './agent-observer.mjs';
import { createProcessBudget } from './runtime.mjs';
import { buildPrivateClipboardReport, checkPrivateClipboard, privateClipboardBinding, privateClipboardCallbackResult,
    privateClipboardData, privateClipboardEvidenceData, privateClipboardFailureFacts, validatePrivateClipboardOwnership } from './private-clipboard.mjs';
import { queryScreen } from './queries.mjs';
const check = checkPrivateClipboard;

/** Parent-side coordinator. The caller authenticates every owner frame BEFORE
 * dispatch and supplies fresh process/source/PTY attestation plus a dedicated
 * read-only App observer. This class never gains authority from child claims.
 */
export class OwnedPrivateClipboardObserver {
    #binding; #bridge; #attest; #capture; #activeBudget; #clock; #frames = []; #callbacks = [];
    #busy = false; #baseline; #failure; #failed = false; #finished = false; #report;

    /** Bind only explicit parent callbacks and fixed process facts; construction itself performs no App or clipboard operation. */
    constructor({ binding, attest, capture, appData, rpc = privateClipboardRequest, endpoint = observerEndpoint,
        clock = () => performance.now() }) {
        this.#binding = privateClipboardBinding(binding);
        check([attest, capture, rpc, endpoint, clock].every(value => typeof value === 'function'));
        this.#attest = attest; this.#capture = capture; this.#clock = clock;
        const budget = () => {
            check(typeof this.#activeBudget === 'function');
            return this.#activeBudget();
        };
        this.#bridge = new PrivateClipboardBridge({ runId: binding.runId, paneId: binding.paneId, budget, clock,
            verifyBinding: remaining => this.#verify(remaining),
            rpc: (request, remaining) => rpc(endpoint(appData, binding.ownerPid), request, remaining) });
    }

    /** Read one bracketed App snapshot inside the frozen event budget; require baseline equality before authority. */
    async #verify(milliseconds) {
        const budget = createProcessBudget(milliseconds, this.#clock);
        const event = this.#bridge.evidence().events.at(-1);
        check(event && event.index < 22);
        const beforeOwnership = await this.#attest(budget);
        const screen = queryScreen(await this.#capture(budget));
        const afterOwnership = await this.#attest(budget);
        budget();
        const before = validatePrivateClipboardOwnership(beforeOwnership, this.#binding, screen);
        const after = validatePrivateClipboardOwnership(afterOwnership, this.#binding, screen);
        const sample = { beforeOwnership: before, screen, sha256: snapshotHash(screen), afterOwnership: after };
        let frame = this.#frames[event.index];
        if (!frame) {
            check(event.index === this.#frames.length && !Object.hasOwn(event, 'result'));
            frame = { index: event.index, before: sample }; this.#frames.push(frame);
        } else {
            check(!frame.after && Object.hasOwn(event, 'result')); frame.after = sample;
        }
        this.#baseline ??= screen;
        check(snapshotHash(screen) === snapshotHash(this.#baseline));
        // This is an equality witness over parent-supplied static facts only.
        // Capture hashes and timestamps deliberately do not enter it.
        return snapshotHash(this.#binding);
    }

    /** Admit only the fixed callback/control shapes and source-known nonce grammar before retaining any fields. */
    #validateFrame(input) {
        const frame = privateClipboardData(input), fields = { start: ['kind', 'probeId'], arm: ['kind', 'value'],
            seed: ['kind', 'value'], read: ['kind'], complete: ['kind', 'callbackResult'], finish: ['kind'], abort: ['kind'] }[frame?.kind];
        check(fields?.length && Object.keys(frame).length === fields.length && fields.every(key => Object.hasOwn(frame, key)));
        if (frame.kind === 'start') check(frame.probeId === PRIVATE_CLIPBOARD_PROBES[this.#callbacks.length]);
        if (frame.kind === 'arm' || frame.kind === 'seed') check(privateClipboardNonce(frame.value));
        return frame;
    }

    /** Poison permanently and preserve the first reason even when it is undefined or falsy. */
    #fail(error) {
        if (!this.#failed) this.#failure = error;
        this.#failed = true;
    }

    /** Reject overlap without replacing the active event budget; parent retention records abort failures. */
    async #rejectOverlap() {
        const error = new Error('Overlapping owned private clipboard owner frame.');
        this.#fail(error);
        try { await this.#bridge.abort(error); } catch {}
        throw error;
    }
    /** Attempt bounded restoration and suppress only the identical source-known requested-abort reason. */
    async #requestedAbort() {
        const requested = new Error('Private clipboard callback requested abort.');
        this.#fail(requested);
        try { await this.#bridge.abort(requested); }
        catch (error) { if (error !== requested) throw error; }
        return null; // Only the exact original requested-abort reason can be suppressed.
    }
    /** Route one admitted frame serially; only final close can build a complete report. */
    async #dispatchFrame(frame) {
        if (frame.kind === 'abort') return this.#requestedAbort();
        let callback;
        if (frame.kind === 'complete') {
            const probeId = PRIVATE_CLIPBOARD_PROBES[this.#callbacks.length];
            callback = { probeId, result: privateClipboardCallbackResult(frame.callbackResult, probeId, this.#bridge.evidence().events) };
        }
        const result = await this.#bridge.handle(frame.kind === 'complete' ? { kind: 'complete' } : frame);
        if (callback) this.#callbacks.push(callback); // Independent restoration has already completed.
        if (frame.kind === 'finish') {
            this.#report = buildPrivateClipboardReport({ binding: this.#binding, controller: result,
                frames: this.#frames, callbackResults: this.#callbacks });
            this.#finished = true;
        }
        return result;
    }
    /** Serialize authenticated owner frames; malformed data or any operation/cleanup failure permanently refuses completion. */
    async handle(input, budget) {
        if (this.#busy) return this.#rejectOverlap();
        check(!this.#finished && !this.#failed && typeof budget === 'function');
        this.#busy = true; this.#activeBudget = budget;
        try { return await this.#dispatchFrame(this.#validateFrame(input)); }
        catch (error) {
            this.#fail(error);
            try { await this.#bridge.abort(error); }
            catch (error_) { if (error_ !== error) throw new AggregateError([error, error_], 'Private clipboard operation and cleanup failed.'); }
            throw error;
        } finally { this.#busy = false; }
    }

    /** Authenticated outer-dispatch failure is immediately fatal. The parent still
     * awaits this bounded cleanup before retiring its disposable App and worker.
     */
    async abort(error, budget) {
        this.#fail(error);
        if (budget && !this.#busy) this.#activeBudget = budget;
        return this.#bridge.abort(error);
    }
    /** Completion requires the final close and no prior control, overlap, capture or restoration failure. */
    get complete() { return this.#finished && !this.#failed; }
    /** Copy every retained partial/complete frame under the fixed per-screen and shared byte budgets, without exposing mutable internals. */
    get journal() {
        return privateClipboardEvidenceData({ binding: this.#binding, controller: this.#bridge.evidence(),
            frames: this.#frames, callbackResults: this.#callbacks, failed: this.#failed, complete: this.complete,
            ...(this.#failed ? { failure: privateClipboardFailureFacts(this.#failure) } : {}) });
    }
    /** Return a bounded independent report copy only after successful final close; partial or poisoned lifecycles always throw. */
    get report() { check(this.complete); return privateClipboardEvidenceData(this.#report); }
}
