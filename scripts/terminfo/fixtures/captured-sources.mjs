// Read immutable published source bytes for historical receipt fixtures. Never execute archive code.
import { readFileSync } from 'node:fs';
import { inflateRawSync } from 'node:zlib';
import { createHash } from 'node:crypto';
const cache = new Map();
export function historicalArchive(name) {
    if (!cache.has(name)) {
        const publication = JSON.parse(readFileSync(new URL('../../../docs/qa/terminfo/' + name + '.json', import.meta.url)));
        const archive = readFileSync(new URL('../../../docs/qa/terminfo/' + publication.archive.file, import.meta.url));
        if (createHash('sha256').update(archive).digest('hex') !== publication.archive.sha256) throw new Error('Frozen source archive changed.');
        const values = new Map();
        for (let offset = 0; archive.readUInt32LE(offset) === 0x04034b50;) {
            const method = archive.readUInt16LE(offset + 8), size = archive.readUInt32LE(offset + 18);
            const start = offset + 30, length = archive.readUInt16LE(offset + 26), extra = archive.readUInt16LE(offset + 28);
            const path = archive.subarray(start, start + length).toString(), data = archive.subarray(start + length + extra, start + length + extra + size);
            let bytes;
            if (method === 8) bytes = inflateRawSync(data);
            else if (method === 0) bytes = data;
            else throw new Error('Unsupported frozen archive method.');
            if (!bytes || createHash('sha256').update(bytes).digest('hex') !== publication.archive.fileSha256[path]) throw new Error('Frozen source member changed.');
            values.set(path, bytes); offset = start + length + extra + size;
        }
        cache.set(name, values);
    }
    return new Map([...cache.get(name)].map(([file, bytes]) => [file, Buffer.from(bytes)]));
}
export function historicalSource(file, captured = false) {
    const name = captured ? '2026-11-04-metadata-snapshot' : '2026-11-03-metadata';
    if (!cache.has(name)) historicalArchive(name);
    const value = cache.get(name).get('sources/' + file);
    if (!value) throw new Error('Missing frozen source: ' + file);
    return Buffer.from(value);
}
