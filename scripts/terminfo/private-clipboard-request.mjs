import { createConnection } from 'node:net';

const uuid = value => typeof value === 'string' && /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/.test(value)
    && value !== '00000000-0000-0000-0000-000000000000';
export const privateClipboardNonce = value => typeof value === 'string'
    && /^terminfo-osc52-[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/.test(value);

/** Validate only the bounded App-private API, never an arbitrary acting method. */
export function validatePrivateClipboardRequest(value) {
    const allowed = new Set(['paneId', 'runId', 'operation', 'fixtureId', 'value']);
    if (!value || Object.keys(value).some(key => !allowed.has(key)) || !uuid(value.paneId)
        || typeof value.runId !== 'string' || !/^[a-f\d]{32}$/.test(value.runId)) throw new Error('Invalid private clipboard request.');
    if (value.operation === 'begin') {
        if (value.fixtureId !== undefined || value.value !== undefined) throw new Error('Invalid private clipboard begin.');
    } else {
        if (!uuid(value.fixtureId)) throw new Error('Invalid private clipboard fixture identifier.');
        if (['armWrite', 'seed'].includes(value.operation)) {
            if (!privateClipboardNonce(value.value)) throw new Error('Invalid private clipboard nonce.');
        } else if (!['read', 'restore', 'close'].includes(value.operation) || value.value !== undefined) {
            throw new Error('Invalid private clipboard operation.');
        }
    }
}

/** Reject unknown/private-host data before retention; this is not ownership attestation. */
export function validatePrivateClipboardResult(value, request) {
    const required = ['fixtureId', 'state', 'valueSha256', 'acceptedWrites', 'restored', 'complete'];
    if (!value || required.some(key => !Object.hasOwn(value, key))
        || Object.keys(value).some(key => ![...required, 'value'].includes(key))
        || !uuid(value.fixtureId) || (request.fixtureId && value.fixtureId !== request.fixtureId)
        || !['active', 'aborted', 'closed'].includes(value.state)
        || typeof value.valueSha256 !== 'string' || !/^[a-f\d]{64}$/.test(value.valueSha256)
        || !Number.isSafeInteger(value.acceptedWrites) || value.acceptedWrites < 0 || value.acceptedWrites > 2
        || typeof value.restored !== 'boolean' || typeof value.complete !== 'boolean') throw new Error('Invalid private clipboard result.');
    if (request.operation === 'read') {
        if (value.value !== 'terminfo-private-seed-' + request.runId && !privateClipboardNonce(value.value))
            throw new Error('Private clipboard returned an unknown value.');
    } else if (value.value !== undefined && value.value !== null) throw new Error('Unexpected private clipboard value.');
    return structuredClone(value);
}

/** One request, one EOF-terminated response. Timeout/disconnect is indeterminate; never retry. */
export function privateClipboardRequest(endpoint, request, timeoutMs) {
    validatePrivateClipboardRequest(request);
    if (typeof endpoint !== 'string' || endpoint.length < 1 || endpoint.length > 1024
        || !Number.isSafeInteger(timeoutMs) || timeoutMs < 1 || timeoutMs > 4000) throw new Error('Invalid private clipboard transport.');
    const params = structuredClone(request);
    return new Promise((resolve, reject) => {
        const socket = createConnection({ path: endpoint, allowHalfOpen: true });
        const chunks = [];
        let received = 0, settled = false;
        const finish = (error, result) => {
            if (settled) return;
            settled = true; clearTimeout(timer); socket.destroy();
            if (error) reject(error); else resolve(result);
        };
        const timer = setTimeout(() => finish(new Error('Private clipboard response timed out; do not retry.')), timeoutMs);
        socket.on('error', () => finish(new Error('Private clipboard transport failed; do not retry.')));
        // Half-close the request side: AgentHost answers the line, sees EOF and closes its response side.
        socket.on('connect', () => socket.end(JSON.stringify({ v: 1, id: 1, method: 'clipboardFixture', params }) + '\n'));
        socket.on('data', chunk => {
            received += chunk.length;
            if (received > 4096) return finish(new Error('Oversized private clipboard response.'));
            chunks.push(chunk);
        });
        socket.on('end', () => {
            try {
                const raw = new TextDecoder('utf-8', { fatal: true }).decode(Buffer.concat(chunks));
                if (!raw.endsWith('\n') || raw.slice(0, -1).includes('\n')) throw new Error('Incomplete or duplicate private clipboard frame.');
                const frame = JSON.parse(raw);
                if (frame.v !== 1 || frame.id !== 1 || (frame.error !== undefined && frame.error !== null) || !frame.result
                    || Object.keys(frame).some(key => !['v', 'id', 'result', 'error'].includes(key))) throw new Error('Refused or uncorrelated private clipboard result.');
                finish(null, validatePrivateClipboardResult(frame.result, params));
            } catch { finish(new Error('Invalid private clipboard response; do not retry.')); }
        });
        socket.on('close', () => { if (!settled) finish(new Error('Incomplete private clipboard response; do not retry.')); });
    });
}
