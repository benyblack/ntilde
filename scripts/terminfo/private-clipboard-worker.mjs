import { clipboardControlPromise, PRIVATE_CLIPBOARD_PROBES } from './private-clipboard-controller.mjs';
import { privateClipboardNonce } from './private-clipboard-request.mjs';

export const ownedPrivateClipboardPolicy = 'private-app-storage-parent-transactions-v1';
export const ownedPrivateClipboardSuiteId = 'ntilde-owned-private-clipboard-v1';
const profile = 'ntilde-private-app-v1';

function exact(value, keys) {
    if (!value || typeof value !== 'object' || Array.isArray(value)
        || Object.keys(value).length !== keys.length || !keys.every(key => Object.hasOwn(value, key)))
        throw new Error('Invalid private clipboard parent acknowledgment.');
}

/** Worker proxy only. The independently authenticated parent owns the App RPCs,
 * source/process/PTY/pane checks, bounded controller and retained journal.
 */
export function createPrivateClipboardWorker(owner, runId) {
    if (typeof owner !== 'function' || !/^[a-f\d]{32}$/.test(runId ?? '')) throw new Error('Missing private clipboard owner/run.');
    let busy = false, failed = false, finished = false, completed = 0, binding;
    const refuse = () => { if (failed || finished) throw new Error('Private clipboard proxy cannot resume.'); };
    const call = async frame => {
        refuse();
        try { return await owner(structuredClone(frame)); }
        catch (error) { failed = true; throw error; }
    };
    const acknowledge = async frame => {
        const result = await call(frame);
        if (result !== null) throw new Error('Unexpected private clipboard control acknowledgment.');
    };
    const transaction = async (probeId, work, emit = () => {}) => {
        if (busy || typeof work !== 'function' || typeof emit !== 'function' || probeId !== PRIVATE_CLIPBOARD_PROBES[completed]) {
            failed = true; throw new Error('Unexpected private clipboard callback.');
        }
        refuse(); busy = true;
        const lease = { active: true, pending: null, controls: [] };
        let result, pendingWrite, attempted = false;
        const errors = [];
        const invoke = action => {
            if (!lease.active || lease.pending) {
                failed = true;
                return clipboardControlPromise(Promise.reject(new Error('Private clipboard proxy is inactive or busy.')), () => {});
            }
            const control = { consumed: false, promise: null };
            lease.pending = control; lease.controls.push(control);
            control.promise = Promise.resolve().then(() => { refuse(); return action(); }).then(value => {
                if (!lease.active) throw new Error('Private clipboard control outlived callback.');
                refuse(); return value;
            }).catch(error => { failed = true; throw error; }).finally(() => { lease.pending = null; });
            void control.promise.catch(() => {});
            return clipboardControlPromise(control.promise, () => { control.consumed = true; });
        };
        const fixture = Object.freeze({
            beforeOsc52Write: value => invoke(async () => {
                if (!privateClipboardNonce(value)) throw new Error('Invalid private clipboard write nonce.');
                await acknowledge({ kind: 'arm', value }); pendingWrite = value;
            }),
            readText: () => invoke(async () => {
                if (!pendingWrite) throw new Error('Private clipboard read was not armed.');
                const value = await call({ kind: 'read' });
                if (value !== pendingWrite) throw new Error('Invalid private clipboard readback.');
                pendingWrite = undefined; return value;
            }),
            writeText: value => invoke(async () => {
                if (!privateClipboardNonce(value)) throw new Error('Invalid private clipboard seed nonce.');
                await acknowledge({ kind: 'seed', value });
            }),
        });
        try {
            attempted = true;
            const opened = await call({ kind: 'start', probeId }); exact(opened, ['probeId']);
            if (opened.probeId !== probeId) throw new Error('Wrong private clipboard callback acknowledgment.');
            result = await work(fixture);
            if (lease.pending || lease.controls.some(control => !control.consumed))
                throw new Error('Private clipboard callback did not consume its controls.');
            refuse();
            const restored = await call({ kind: 'complete' }); exact(restored, ['completedProbes']);
            if (JSON.stringify(restored.completedProbes) !== JSON.stringify(PRIVATE_CLIPBOARD_PROBES.slice(0, completed + 1)))
                throw new Error('Missing private clipboard restoration acknowledgment.');
            completed++;
            emit({ profile, probeId, restored: true });
        } catch (error) { errors.push(error); failed = true; }
        finally {
            lease.active = false;
            if (lease.pending) await lease.pending.promise.catch(() => {});
            if (errors.length && attempted) {
                try { await owner({ kind: 'abort' }); }
                catch (error) { errors.push(error); }
            }
            busy = false;
        }
        if (errors.length > 1) throw new AggregateError(errors, 'Private clipboard callback and parent abort failed.');
        if (errors.length) throw errors[0];
        return result; // Original callback/grader object, only after independent restoration.
    };
    const finish = async () => {
        refuse();
        if (busy || completed !== PRIVATE_CLIPBOARD_PROBES.length) { failed = true; throw new Error('Private clipboard proxy is incomplete.'); }
        busy = true;
        try {
            const result = await call({ kind: 'finish' });
            exact(result, ['runId', 'suiteId', 'file', 'sha256', 'counts', 'complete']);
            if (result.runId !== runId
                || result.suiteId !== ownedPrivateClipboardSuiteId || result.file !== 'private-clipboard-observations.json'
                || !/^[a-f\d]{64}$/.test(result.sha256 ?? '') || result.complete !== true
                || JSON.stringify(result.counts) !== JSON.stringify({ completedProbes: 3, acceptedWrites: 2 }))
                throw new Error('Invalid private clipboard completion binding.');
            binding = structuredClone(result); finished = true;
            return structuredClone(binding);
        } catch (error) { failed = true; throw error; }
        finally { busy = false; }
    };
    return Object.freeze({ profile, withClipboardFixture: transaction, finish,
        get summary() { return JSON.stringify({ policy: ownedPrivateClipboardPolicy, profile,
            boundary: 'App-private storage only; no native interchange or clipboard-read permission',
            complete: finished && !failed, completedProbes: completed, binding: binding ?? null }); } });
}
