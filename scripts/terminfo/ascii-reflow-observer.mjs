import { OwnedResizeObserver } from './resize-capture.mjs';
import { asciiReflowNoncePrefix, makeReflowCases } from './ascii-reflow-contracts.mjs';

/**
 * Only a trusted parent selects reflow resize targets; workers can advance the fixed plan, never supply dimensions.
 * The paired-read, parent-selected-action and restoration protocol is the resize observer's, unchanged: ASCII reflow differs only in
 * its cases, its request-kind and nonce prefixes and its error label, so it names exactly those and carries no copy of the protocol.
 */
export class OwnedReflowObserver extends OwnedResizeObserver {
    static label = 'ASCII reflow';
    static kindPrefix = 'reflow';
    static noncePrefix = asciiReflowNoncePrefix;
    static makeCases = makeReflowCases;
}
