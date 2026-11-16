// Bounded owned exchanges of the xterm saved-mode qualification. A mode save or restore is silent, so one exchange earns no reply, then the DA barrier that orders it; anything that does arrive before the barrier is retained
// byte for byte as evidence (a save or restore that answers is a mismatch, never an error). The framing is the xterm color-stack suite's, reused unchanged: the same bounded replies and DA barrier.
// The reply limit, one bounded reply, the accumulated bytes of one exchange (any bounded replies then the DA barrier; null while incomplete) and a retained exchange (raw replies or their explicit absence, and the barrier).
export {
    xcsReplyLimit as xsvReplyLimit,
    parseXcsReply as parseXsvReply,
    parseXcsFrame as parseXsvFrame,
    validateXcsExchange as validateXsvExchange,
} from './xcs-report.mjs';
