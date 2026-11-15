// Bounded owned exchanges of the xterm SGR-stack qualification. A push or pop is silent, so one exchange earns no reply, then the DA barrier that orders it; anything that does arrive before the barrier is retained
// byte for byte as evidence (a stack operation that answers is a mismatch, never an error). The framing is the xterm color-stack suite's, reused unchanged: the same bounded replies and DA barrier.
// The reply limit, one bounded reply, the accumulated bytes of one exchange (any bounded replies then the DA barrier; null while incomplete) and a retained exchange (raw replies or their explicit absence, and the barrier).
export {
    xcsReplyLimit as xssReplyLimit,
    parseXcsReply as parseXssReply,
    parseXcsFrame as parseXssFrame,
    validateXcsExchange as validateXssExchange,
} from './xcs-report.mjs';
