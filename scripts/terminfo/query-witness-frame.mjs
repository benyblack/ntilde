import { queryWitnessPlan } from './query-witness-cases.mjs';
const esc = '\x1b';
const captureKeys = ['hex', 'byteLength', 'barrier', 'nonces'];

/** Validate an already completed exclusive raw capture, never a decoded text approximation.
 * Ownership, global nonce freshness, paired state and cleanup are separate integration gates. */
export function inspectQueryWitness(context, id, capture) {
    const spec = queryWitnessPlan(context).find(value => value.id === id);
    if (!spec) throw new Error('Unknown exact-query witness.');
    if (!capture || Object.keys(capture).length !== captureKeys.length
        || Object.keys(capture).some(k => !captureKeys.includes(k))
        || !captureKeys.every(k => Object.hasOwn(capture, k))
        || typeof capture.hex !== 'string' || !/^(?:[0-9a-f]{2}){0,512}$/.test(capture.hex)
        || !Number.isSafeInteger(capture.byteLength) || capture.byteLength !== capture.hex.length / 2
        || !Array.isArray(capture.nonces) || capture.nonces.length !== 2
        || ![0, 1].every(i => Object.hasOwn(capture.nonces, i) && Number.isSafeInteger(capture.nonces[i])
            && capture.nonces[i] >= 32768 && capture.nonces[i] <= 65535)
        || capture.nonces[0] === capture.nonces[1]
        || capture.barrier !== capture.nonces.map(n => `${esc}[?${n};0$y`).join('')) {
        throw new Error('Invalid bounded raw-query capture or ordering-fence fact.');
    }
    const expectedHex = Buffer.from(spec.expectedReply, 'ascii').toString('hex');
    const contractMatched = capture.hex === expectedHex;
    let observedEvidence = 'unexpected-bytes';
    if (contractMatched) observedEvidence = spec.expectedEvidence;
    else if (capture.byteLength === 0) observedEvidence = 'silence';
    return Object.freeze({ id: spec.id, featureId: spec.featureId, expectedEvidence: spec.expectedEvidence, observedEvidence,
        contractMatched, expectedHex, receivedHex: capture.hex,
        byteLength: capture.byteLength, orderingNonces: Object.freeze([...capture.nonces]),
    });
}
