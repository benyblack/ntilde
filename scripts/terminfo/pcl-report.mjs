// Bounded owned exchanges of the pointer-color qualification, via the shared query-probe framing
// (probe-suite-shared.mjs): one request earns a bounded sequence of complete OSC replies followed by the DA
// barrier that orders the exchange; a request with no reply earns the barrier alone, an explicitly absent reply
// list. The pinned probe's own frame pattern accepts a reply ended by BEL as well as ST, so the framing accepts
// both terminators and the pinned verdict never depends on the stricter effect-contract terminator. Every
// refusal names this suite.
import { probeSuiteFraming } from './probe-suite-shared.mjs';

const { parseReply: parsePclReply, parseFrame: parsePclFrame, validateExchange: validatePclExchange } =
    probeSuiteFraming({ label: 'pointer-color', replyLimit: 8 });

export { parsePclReply, parsePclFrame, validatePclExchange };
/** The most replies one exchange may retain. */
export const pclReplyLimit = 8;
