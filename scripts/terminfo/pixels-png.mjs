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

function chunks(bytes) {
    let offset = 8, count = 0, imageHeader, idatEnded = false;
    const data = [];
    while (offset < bytes.length) {
        if (++count > limits.chunks) throw invalid();
        const item = chunkAt(bytes, offset);
        offset = item.end;
        if (['acTL', 'fcTL', 'fdAT'].includes(item.type)) throw invalid();
        if (item.type === 'IHDR') {
            if (imageHeader || count !== 1) throw invalid();
            imageHeader = header(item.data);
        } else {
            if (!imageHeader) throw invalid();
            if (item.type === 'IDAT') {
                if (idatEnded) throw invalid();
                data.push(item.data);
            } else if (item.type === 'IEND') {
                if (item.data.length || !data.length || offset !== bytes.length) throw invalid();
                return { ...imageHeader, compressed: Buffer.concat(data) };
            } else {
                if (item.type[0] === item.type[0].toUpperCase()) throw invalid();
                if (data.length) idatEnded = true;
            }
        }
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

function predictor(filter, left, up, upperLeft) {
    switch (filter) {
        case 0: return 0;
        case 1: return left;
        case 2: return up;
        case 3: return Math.floor((left + up) / 2);
        case 4: return paeth(left, up, upperLeft);
        default: throw invalid();
    }
}

function unfilter(raw, width, height, channels) {
    const stride = width * channels, pixels = Buffer.allocUnsafe(width * height * 4);
    for (let y = 0; y < height; y++) {
        const start = y * (stride + 1) + 1, filter = raw[start - 1];
        if (filter > 4) throw invalid();
        for (let x = 0; x < stride; x++) {
            const left = x >= channels ? raw[start + x - channels] : 0;
            const up = y ? raw[start + x - stride - 1] : 0;
            const upperLeft = y && x >= channels ? raw[start + x - stride - 1 - channels] : 0;
            raw[start + x] = (raw[start + x] + predictor(filter, left, up, upperLeft)) & 255;
        }
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
