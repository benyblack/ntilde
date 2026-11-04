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
        || !captureKeys.every(k => Object.hasOwn(capture, k))) {
        throw new Error('Invalid bounded raw-query capture or ordering-fence fact.');
    }
    const { hex, byteLength, barrier, nonces: sourceNonces } = capture;
    if (!Array.isArray(sourceNonces) || sourceNonces.length !== 2
        || ![0, 1].every(i => Object.hasOwn(sourceNonces, i))) {
        throw new Error('Invalid bounded raw-query capture or ordering-fence fact.');
    }
    const nonces = Object.freeze([sourceNonces[0], sourceNonces[1]]);
    if (typeof hex !== 'string' || !/^(?:[0-9a-f]{2}){0,512}$/.test(hex)
        || !Number.isSafeInteger(byteLength) || byteLength !== hex.length / 2
        || !nonces.every(n => Number.isSafeInteger(n) && n >= 32768 && n <= 65535)
        || nonces[0] === nonces[1]
        || barrier !== nonces.map(n => `${esc}[?${n};0$y`).join('')) {
        throw new Error('Invalid bounded raw-query capture or ordering-fence fact.');
    }
    const expectedHex = Buffer.from(spec.expectedReply, 'ascii').toString('hex');
    const contractMatched = hex === expectedHex;
    let observedEvidence = 'unexpected-bytes';
    if (contractMatched) observedEvidence = spec.expectedEvidence;
    else if (byteLength === 0) observedEvidence = 'silence';
    return Object.freeze({ id: spec.id, featureId: spec.featureId, expectedEvidence: spec.expectedEvidence, observedEvidence,
        contractMatched, expectedHex, receivedHex: hex,
        byteLength, orderingNonces: nonces,
    });
}
