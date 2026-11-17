// Bounded owned exchanges of the rxvt special-color qualification. One request list earns a bounded sequence of complete OSC 5 (or legacy color) replies followed by the DA barrier that orders the
// exchange; a request with no reply earns the barrier alone, an explicitly absent reply list. The framing is the device-query suite's, reused unchanged: bounded string replies ended by ST, then a DA barrier.
export {
    devqReplyLimit as osc5ReplyLimit,
    parseDevqReply as parseOsc5Reply,
    parseDevqFrame as parseOsc5Frame,
    validateDevqExchange as validateOsc5Exchange,
} from './devq-report.mjs';
