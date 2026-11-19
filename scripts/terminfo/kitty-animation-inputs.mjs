// Immutable independent 16x16 solid-color frames for the Kitty animation suite. These are protocol inputs,
// not decoded or rendered runtime evidence; each is generated from its color and pinned by SHA-256.
import { createHash } from 'node:crypto';
import { deflateSync } from 'node:zlib';

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

// The Unicode placeholder suite (#989) shows one image through placeholder cells: 16x16 raw RGBA, the left half red and the right half blue, so the tile each cell shows can be told by its color.
// Like every input here it is generated from its colors and pinned by SHA-256, independently of the App.
export const placeholderColors = Object.freeze({ red: Object.freeze([255, 0, 0, 255]), blue: Object.freeze([0, 0, 255, 255]) });
const halvesBytes = () => Buffer.concat(Array.from({ length: animationImageSize.width * animationImageSize.height },
    (_, index) => Buffer.from(index % animationImageSize.width < animationImageSize.width / 2 ? placeholderColors.red : placeholderColors.blue)));
export const placeholderInputPin = hash(halvesBytes());
/** Fresh raw RGBA bytes (f=32, s=16, v=16) of the red/blue image with their pin. */
export function placeholderHalvesInput() {
    const bytes = halvesBytes();
    if (hash(bytes) !== placeholderInputPin) throw new Error('Placeholder image input pin mismatch.');
    return { inputId: 'halves', ...animationImageSize, bytes, sha256: placeholderInputPin };
}
/** What the two cells of the placeholder row must show: the color of the tile each one is expected to paint, or 'absent' for nothing. */
export const placeholderAppearances = Object.freeze({
    absent: Object.freeze(['absent', 'absent']),
    redBlue: Object.freeze(['red', 'blue']),
    blueRed: Object.freeze(['blue', 'red']),
    absentRed: Object.freeze(['absent', 'red']),
});

// Half-transparent PNG frames (f=100). Each is encoded here from its color, independently of the App and of Skia, and pinned by SHA-256.
const pngFrames = Object.freeze({
    overRoot: Object.freeze({ color: Object.freeze([0, 255, 0, 128]) }),
    overEdit: Object.freeze({ color: Object.freeze([255, 0, 0, 128]) }),
});
export const animationPngIds = Object.freeze(Object.keys(pngFrames));
const crcTable = Array.from({ length: 256 }, (_, n) => {
    let c = n;
    for (let k = 0; k < 8; k++) {
        c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    }
    return c >>> 0;
});
const crc32 = bytes => {
    let c = 0xffffffff;
    for (const byte of bytes) {
        c = crcTable[(c ^ byte) & 0xff] ^ (c >>> 8);
    }
    return (c ^ 0xffffffff) >>> 0;
};
const chunk = (type, data) => {
    const body = Buffer.concat([Buffer.from(type, 'ascii'), data]), out = Buffer.alloc(body.length + 8);
    out.writeUInt32BE(data.length, 0); body.copy(out, 4); out.writeUInt32BE(crc32(body), body.length + 4);
    return out;
};
/** A deterministic 16x16 RGBA PNG: signature, IHDR (8-bit, color type 6), one deflated IDAT of unfiltered rows, IEND. */
function encodePng(color) {
    const { width, height } = animationImageSize, header = Buffer.alloc(13);
    header.writeUInt32BE(width, 0); header.writeUInt32BE(height, 4); header[8] = 8; header[9] = 6;
    const row = Buffer.concat([Buffer.from([0]), ...Array.from({ length: width }, () => Buffer.from(color))]);
    const rows = Buffer.concat(Array.from({ length: height }, () => row));
    return Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), chunk('IHDR', header), chunk('IDAT', deflateSync(rows, { level: 9 })), chunk('IEND', Buffer.alloc(0))]);
}
const pngPins = Object.freeze({ overRoot: hash(encodePng(pngFrames.overRoot.color)), overEdit: hash(encodePng(pngFrames.overEdit.color)) });
export const animationPngPins = pngPins;

/** Fresh PNG bytes (f=100) with their pin; callers cannot mutate a later input. */
export function animationPngInput(pngId) {
    if (!Object.hasOwn(pngFrames, pngId)) throw new Error('Unknown animation PNG input.');
    const bytes = encodePng(pngFrames[pngId].color);
    if (hash(bytes) !== pngPins[pngId]) throw new Error('Animation PNG input pin mismatch.');
    return { pngId, ...animationImageSize, color: [...pngFrames[pngId].color], bytes, sha256: pngPins[pngId] };
}

/**
 * Straight-alpha source-over of one pixel onto an opaque one, by the protocol's own formula and independent of the App's integer arithmetic:
 * out = src * a + dst * (1 - a), rounded to nearest. Composed frames are expected to land within a rounding step of this.
 */
export const composeOver = (source, destination) => destination.slice(0, 3).map((value, index) => Math.round(source[index] * source[3] / 255 + value * (1 - source[3] / 255))).concat(255);
export const composedTolerance = 2;
const root = frames.root.color;
// What each composed frame must look like: the half-transparent green PNG built over the red root, then the half-transparent red PNG over the blue-edited root.
const composed = Object.freeze({
    overRoot: Object.freeze(composeOver(pngFrames.overRoot.color, root)),
    overEdit: Object.freeze(composeOver(pngFrames.overEdit.color, frames.third.color)),
    // A half-transparent frame that replaced an opaque one (a=c,C=1) keeps its alpha, so it shows over the terminal background, not over the frame it replaced.
    overBackground: Object.freeze(composeOver(pngFrames.overRoot.color, animationBackground)),
});
export const animationComposedIds = Object.freeze(Object.keys(composed));
export const animationAppearanceIds = Object.freeze([...animationFrameIds, ...animationComposedIds]);
/** The expected color of any appearance id, plus how far a rendered pixel may sit from it (solid frames are exact). */
export const animationAppearanceColor = appearance => {
    if (Object.hasOwn(composed, appearance)) return { color: [...composed[appearance]], tolerance: composedTolerance };
    return { color: animationFrameColor(appearance), tolerance: 0 };
};
