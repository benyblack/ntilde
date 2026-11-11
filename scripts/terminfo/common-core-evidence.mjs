import { readFileSync, realpathSync } from 'node:fs';
import { resolve, relative, isAbsolute, sep } from 'node:path';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';

export const pythonCommand = process.platform === 'win32' ? 'python' : 'python3';
export const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
/** Resolve retained evidence only within its publication, including through symlinks. */
export function verifiedEvidenceFile(directory, entry) {
    if (!entry || typeof entry.path !== 'string' || isAbsolute(entry.path) || !/^[a-zA-Z0-9._/-]+$/.test(entry.path) ||
        !/^[a-f0-9]{64}$/.test(entry.sha256)) throw new Error('Invalid evidence path or digest.');
    const base = realpathSync(directory), path = realpathSync(resolve(base, entry.path));
    const inside = relative(base, path);
    if (inside === '..' || inside.startsWith('..' + sep) || isAbsolute(inside)) throw new Error('Evidence path escapes its publication.');
    const bytes = readFileSync(path);
    if (sha256(bytes) !== entry.sha256) throw new Error(`Evidence digest differs: ${entry.path}`);
    return { path, bytes };
}
/** Validate archived bytes rather than accepting screenshot names as proof of retained pixels. */
export function verifyCommonCoreArchive(path, rawBytes) {
    const index = JSON.parse(execFileSync(pythonCommand, ['-c', `import zipfile,hashlib,json,sys
with zipfile.ZipFile(sys.argv[1]) as z:
    names=z.namelist()
    if len(names)!=len(set(names)): raise ValueError('duplicate archive members')
    if any(n.startswith('/') or '..' in n.split('/') or chr(92) in n for n in names): raise ValueError('unsafe archive member')
    if sum(i.file_size for i in z.infolist())>256*1024*1024: raise ValueError('archive expands beyond 256 MiB')
    print(json.dumps({n:hashlib.sha256(z.read(n)).hexdigest() for n in names if not n.endswith('/')}))`, path], { timeout: 30000, maxBuffer: 4 * 1024 * 1024, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }));
    if (index['raw.json'] !== sha256(rawBytes)) throw new Error('Archive contains a different raw run.');
    const raw = JSON.parse(rawBytes);
    for (const ref of raw.screenshotRefs ?? []) {
        if (!/^sha256:[a-f0-9]{64}$/.test(ref) || index[`artifacts/${ref.slice(7)}.png`] !== ref.slice(7)) {
            throw new Error(`Screenshot bytes missing or altered: ${ref}`);
        }
    }
    return index;
}

/** Read the immutable raw record from its verified archive without extracting files. */
export function readCommonCoreRaw(path) {
    return execFileSync(pythonCommand, ['-c', `import zipfile,sys
with zipfile.ZipFile(sys.argv[1]) as z:
    if sum(i.filename=='raw.json' for i in z.infolist())!=1: raise ValueError('raw record is missing or duplicated')
    if z.getinfo('raw.json').file_size>64*1024*1024: raise ValueError('raw record exceeds 64 MiB')
    sys.stdout.buffer.write(z.read('raw.json'))`, path], { timeout: 30000, maxBuffer: 64 * 1024 * 1024, stdio: ['ignore', 'pipe', 'pipe'] });
}
