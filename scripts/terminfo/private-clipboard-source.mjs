import { createHash } from 'node:crypto';

export const PRIVATE_CLIPBOARD_UPSTREAM_SHA256 = 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6';
export const PRIVATE_CLIPBOARD_CALLBACK_SHA256 = 'd83822d3948d34479e10215e11bee87cb9a8d58024e60a268c55c98e6419dd3f';
const anchor = '      const nonce = `terminfo-osc52-${randomUUID()}`;\n      ctx.write(`\\x1B]52;c;${btoa(nonce)}\\x07`);';

/** Exact pinned stock source only. Call before other adapter transforms, retaining both digests. */
export function preparePrivateClipboardSource(source) {
    if (typeof source !== 'string') throw new Error('Private clipboard source must be exact UTF-8 text.');
    const upstreamSha256 = createHash('sha256').update(source).digest('hex');
    if (upstreamSha256 !== PRIVATE_CLIPBOARD_UPSTREAM_SHA256) throw new Error('Private clipboard upstream source pin mismatch.');
    const start = source.indexOf('function liveClipboardNotTested() {');
    const end = source.indexOf('var sixelDa1Reply, foregroundQuery');
    if (start < 0 || end <= start) throw new Error('Missing private clipboard callback.');
    const callback = source.slice(start, end);
    const derived = source.slice(0, start) + preparePrivateClipboardCallback(callback) + source.slice(end);
    return { source: derived, upstreamSha256, derivedSha256: createHash('sha256').update(derived).digest('hex'),
        boundary: 'private-App storage; awaited preparation before original OSC52 write; stock packets and grader unchanged' };
}

/** Exact stock callback slice for deterministic tests and independently retained source evidence. */
export function preparePrivateClipboardCallback(source) {
    if (typeof source !== 'string') throw new Error('Private clipboard callback must be exact UTF-8 text.');
    if (createHash('sha256').update(source).digest('hex') !== PRIVATE_CLIPBOARD_CALLBACK_SHA256)
        throw new Error('Private clipboard callback pin mismatch.');
    if (source.split(anchor).length !== 2) throw new Error('Private clipboard write seam is not unique.');
    const insertion = '      if (typeof fixture.beforeOsc52Write !== "function") throw new Error("Missing private clipboard preparation");\n'
        + '      await fixture.beforeOsc52Write(nonce);\n';
    const originalWrite = '      ctx.write(`\\x1B]52;c;${btoa(nonce)}\\x07`);';
    return source.replace(anchor, anchor.replace(originalWrite, insertion + originalWrite));
}
