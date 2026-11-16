// Bounded owned exchanges of the kitty color-control qualification. One request list earns a bounded sequence of complete OSC 21 (or legacy OSC color) replies followed by the DA barrier that orders the
// exchange; a request with no reply earns the barrier alone, an explicitly absent reply list. The framing is the device-query suite's, reused unchanged: bounded string replies ended by ST, then a DA barrier.
// The reply limit, one bounded reply, the accumulated bytes of one exchange (any bounded replies then the DA barrier; null while incomplete) and a retained exchange (raw replies or their explicit absence, and the barrier).
export {
    devqReplyLimit as kclReplyLimit,
    parseDevqReply as parseKclReply,
    parseDevqFrame as parseKclFrame,
    validateDevqExchange as validateKclExchange,
} from './devq-report.mjs';
