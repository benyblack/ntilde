// Bounded static RGB/RGBA subset of the PNG specification; no animation or palette decoding.
// https://www.w3.org/TR/png-3/#5Chunk-layout
import { crc32, inflateSync } from 'node:zlib';

const signature = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);
export const limits = Object.freeze({ bytes: 3 * 1024 * 1024, pixels: 4_000_000, side: 8192, chunks: 1024 });
const invalid = () => new Error('Invalid or unsupported owned capture PNG.');

function chunkAt(bytes, offset) {
    if (offset + 12 > bytes.length) throw invalid();
    const length = bytes.readUInt32BE(offset), end = offset + 12 + length;
    if (end > bytes.length) throw invalid();
    const typeBytes = bytes.subarray(offset + 4, offset + 8);
    if (!typeBytes.every(value => value >= 65 && value <= 90 || value >= 97 && value <= 122)
        || (typeBytes[2] & 32)) throw invalid();
    if (crc32(bytes.subarray(offset + 4, end - 4)) !== bytes.readUInt32BE(end - 4)) throw invalid();
    return { type: typeBytes.toString('ascii'), data: bytes.subarray(offset + 8, end - 4), end };
}

function header(data) {
    if (data.length !== 13) throw invalid();
    const width = data.readUInt32BE(0), height = data.readUInt32BE(4), color = data[9];
    if (!width || !height || width > limits.side || height > limits.side || width * height > limits.pixels
        || data[8] !== 8 || ![2, 6].includes(color) || data[10] || data[11] || data[12]) throw invalid();
    return { width, height, channels: color === 2 ? 3 : 4 };
}

function acceptAncillary(state, item) {
    if (item.type[0] === item.type[0].toUpperCase()) throw invalid();
    if (state.data.length) state.idatEnded = true;
}

function acceptChunk(state, item, count, last) {
    if (['acTL', 'fcTL', 'fdAT', 'tRNS'].includes(item.type)) throw invalid();
    if (!state.imageHeader && item.type !== 'IHDR') throw invalid();
    switch (item.type) {
        case 'IHDR':
            if (state.imageHeader || count !== 1) throw invalid();
            state.imageHeader = header(item.data);
            break;
        case 'IDAT':
            if (state.idatEnded) throw invalid();
            state.data.push(item.data);
            break;
        case 'IEND':
            if (item.data.length || !state.data.length || !last) throw invalid();
            return { ...state.imageHeader, compressed: Buffer.concat(state.data) };
        default:
            acceptAncillary(state, item);
    }
}

function chunks(bytes) {
    let offset = 8, count = 0;
    const state = { imageHeader: null, idatEnded: false, data: [] };
    while (offset < bytes.length) {
        if (++count > limits.chunks) throw invalid();
        const item = chunkAt(bytes, offset);
        offset = item.end;
        const result = acceptChunk(state, item, count, offset === bytes.length);
        if (result) return result;
    }
    throw invalid();
}

function paeth(left, up, upperLeft) {
    const estimate = left + up - upperLeft;
    const a = Math.abs(estimate - left), b = Math.abs(estimate - up), c = Math.abs(estimate - upperLeft);
    if (a <= b && a <= c) return left;
    if (b <= c) return up;
    return upperLeft;
}

/** Type 1: each byte adds the same channel of the pixel to its left. */
function reconstructSub(raw, start, end, channels) {
    for (let at = start + channels; at < end; at++) raw[at] = (raw[at] + raw[at - channels]) & 255;
}

/** Type 2: each byte adds the byte above it; a first row has none. */
function reconstructUp(raw, start, end, back) {
    for (let at = start; at < end; at++) raw[at] = (raw[at] + raw[at - back]) & 255;
}

/** Type 3: each byte adds the floored mean of its left and upper neighbours. */
function reconstructAverage(raw, start, end, channels, back, hasPrevious) {
    const first = start + channels;
    for (let at = start; at < end; at++) {
        const left = at >= first ? raw[at - channels] : 0, up = hasPrevious ? raw[at - back] : 0;
        raw[at] = (raw[at] + ((left + up) >> 1)) & 255;
    }
}

/** Type 4: each byte adds whichever of left, up and upper-left is nearest their linear estimate. */
function reconstructPaeth(raw, start, end, channels, back, hasPrevious) {
    const first = start + channels;
    for (let at = start; at < end; at++) {
        const left = at >= first ? raw[at - channels] : 0, up = hasPrevious ? raw[at - back] : 0;
        const upperLeft = hasPrevious && at >= first ? raw[at - back - channels] : 0;
        raw[at] = (raw[at] + paeth(left, up, upperLeft)) & 255;
    }
}

/** One loop per filter type, each reading only the neighbours its predictor uses. A single loop that fetched all three
 * neighbours and dispatched on the type for every byte was a fifth of a receipt rederivation's time; every capture a
 * receipt admits is decoded here, and the rows are mostly type 0 or 2. The arithmetic is the PNG specification's. */
function reconstructRow(raw, start, stride, channels, hasPrevious) {
    const end = start + stride, back = stride + 1;
    switch (raw[start - 1]) {
        case 0: return;
        case 1: return reconstructSub(raw, start, end, channels);
        case 2: return hasPrevious ? reconstructUp(raw, start, end, back) : undefined;
        case 3: return reconstructAverage(raw, start, end, channels, back, hasPrevious);
        case 4: return reconstructPaeth(raw, start, end, channels, back, hasPrevious);
        default: throw invalid();
    }
}

function unfilter(raw, width, height, channels) {
    const stride = width * channels, pixels = Buffer.allocUnsafe(width * height * 4);
    for (let y = 0; y < height; y++) {
        const start = y * (stride + 1) + 1;
        reconstructRow(raw, start, stride, channels, y > 0);
        for (let x = 0; x < width; x++) {
            const source = start + x * channels, target = (y * width + x) * 4;
            pixels[target] = raw[source];
            pixels[target + 1] = raw[source + 1];
            pixels[target + 2] = raw[source + 2];
            pixels[target + 3] = channels === 4 ? raw[source + 3] : 255;
        }
    }
    return pixels;
}

/** Decode only bounded, non-interlaced 8-bit RGB/RGBA captures; no external image code executes. */
export function decodeCapturePng(bytes) {
    if (!Buffer.isBuffer(bytes) || bytes.length > limits.bytes || !bytes.subarray(0, 8).equals(signature)) throw invalid();
    const { width, height, channels, compressed } = chunks(bytes);
    const expected = height * (width * channels + 1);
    let inflated;
    try { inflated = inflateSync(compressed, { maxOutputLength: expected, info: true }); }
    catch { throw invalid(); }
    if (inflated.buffer.length !== expected || inflated.engine.bytesWritten !== compressed.length) throw invalid();
    return { width, height, pixels: unfilter(inflated.buffer, width, height, channels) };
}
