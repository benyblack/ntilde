// Immutable independent 16x16 solid-color frames for the Kitty animation suite. These are protocol inputs,
// not decoded or rendered runtime evidence; each is generated from its color and pinned by SHA-256.
import { createHash } from 'node:crypto';

export const animationImageSize = Object.freeze({ width: 16, height: 16 });
export const animationBackground = Object.freeze([16, 16, 16, 255]);
const frames = Object.freeze({
    root: Object.freeze({ color: Object.freeze([255, 0, 0, 255]), sha256: '' }),
    second: Object.freeze({ color: Object.freeze([0, 255, 0, 255]), sha256: '' }),
    third: Object.freeze({ color: Object.freeze([0, 0, 255, 255]), sha256: '' }),
});
export const animationFrameIds = Object.freeze(Object.keys(frames));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const solid = color => Buffer.concat(Array.from({ length: animationImageSize.width * animationImageSize.height }, () => Buffer.from(color)));
const pins = Object.freeze({
    root: hash(solid(frames.root.color)),
    second: hash(solid(frames.second.color)),
    third: hash(solid(frames.third.color)),
});

/** Fresh raw RGBA bytes (f=32, s=16, v=16) with their pin; callers cannot mutate a later input. */
export function animationFrameInput(frameId) {
    if (!Object.hasOwn(frames, frameId)) throw new Error('Unknown animation frame input.');
    const bytes = solid(frames[frameId].color);
    if (hash(bytes) !== pins[frameId]) throw new Error('Animation frame input pin mismatch.');
    return { frameId, ...animationImageSize, color: [...frames[frameId].color], bytes, sha256: pins[frameId] };
}
export const animationFrameColor = frameId => {
    if (!Object.hasOwn(frames, frameId)) throw new Error('Unknown animation frame.');
    return [...frames[frameId].color];
};
export const animationInputPins = pins;
