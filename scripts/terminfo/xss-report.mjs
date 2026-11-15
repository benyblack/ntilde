// Bounded owned exchanges of the xterm SGR-stack qualification. A push or pop is silent, so one exchange earns no reply, then the DA barrier that orders it; anything that does arrive before the barrier is retained
// byte for byte as evidence (a stack operation that answers is a mismatch, never an error). The framing is the xterm color-stack suite's, reused unchanged: the same bounded replies and DA barrier.
import { xcsReplyLimit, parseXcsReply, parseXcsFrame, validateXcsExchange } from './xcs-report.mjs';

/** The most replies one exchange may retain. */
export const xssReplyLimit = xcsReplyLimit;
/** One bounded reply: the framing and its refusals are the color-stack suite's. */
export const parseXssReply = parseXcsReply;
/** The accumulated bytes of one exchange: any bounded replies followed by the DA barrier (null while incomplete). */
export const parseXssFrame = parseXcsFrame;
/** A retained exchange: its raw replies (or explicit absence) and DA barrier. */
export const validateXssExchange = validateXcsExchange;
