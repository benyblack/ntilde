// The dated catalogue is data, never executable code. Frozen inputs remain
// reproducible when the live site, inventory or terminal changes.
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { join } from 'node:path';

export const modernPublication = 'docs/qa/terminfo/2026-11-15-modern-results';
export const requirementsSha256 = '50a9bf1d77cdd0226b5113393e405c3a9dd93348063c0be89754a63dc3da50ff';
export const mappingSha256 = 'a133dd7cbf4e4b580e423778f449ec4d501f21d3980a867258f2695c5a33c810';
export const digest = bytes => createHash('sha256').update(bytes).digest('hex');
export const requireModern = (condition, reason) => { if (!condition) throw new Error('Modern results: ' + reason); };
export const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
export const parseJson = path => JSON.parse(readFileSync(path));

/** Text pins use repository LF bytes on both Windows and Unix checkouts. */
export function pinnedJson(path, sha) {
    const bytes = readFileSync(path, 'utf8').replaceAll('\r\n', '\n');
    requireModern(digest(bytes) === sha, 'changed pinned input ' + path);
    return JSON.parse(bytes);
}

/** Only the four named bounded regular data members are admitted. */
export function snapshotFiles(path) {
    const python = process.env.PYTHON ?? (process.platform === 'win32' ? 'python' : 'python3');
    const script = `import zipfile,sys,json,base64,stat
with zipfile.ZipFile(sys.argv[1]) as z:
 names=z.namelist()
 assert sorted(names)==['LICENSE','baselines.json','features.json','modern-page-excerpt.md']
 for i in z.infolist():
  assert i.file_size<2000000 and stat.S_ISREG(i.external_attr>>16)
 print(json.dumps({n:base64.b64encode(z.read(n)).decode('ascii') for n in names}))
`;
    const run = spawnSync(python, ['-I', '-c', script, path], { encoding: 'utf8', windowsHide: true, maxBuffer: 4 << 20 });
    requireModern(run.status === 0, 'upstream snapshot read failed: ' + run.stderr);
    return Object.fromEntries(Object.entries(JSON.parse(run.stdout)).map(([name, value]) => [name, Buffer.from(value, 'base64')]));
}

/** Verify exact source bytes, membership, every criterion and the live-page list.
 * Git SHA-1 blob IDs are retained citation metadata; SHA-256 pins admit bytes. */
export function validateModernInputs(requirements, mapping, files) {
    requireModern(requirements.schemaVersion === 1 && requirements.baseline === 'modern' && mapping.schemaVersion === 1, 'input schema');
    for (const [name, bytes] of Object.entries(files)) requireModern(digest(bytes) === requirements.upstream.files[name], 'upstream member changed: ' + name);
    const features = JSON.parse(files['features.json']), baselines = JSON.parse(files['baselines.json']);
    const ids = Object.entries(features).filter(([, item]) => item?.baseline === 'modern').map(([id]) => id);
    requireModern(ids.length === 69 && same(requirements.rows.map(row => row.id), ids), 'expected the exact 69 catalogue IDs in order');
    requireModern(same(requirements.definition, baselines.modern), 'baseline definition changed');
    const page = files['modern-page-excerpt.md'].toString('utf8');
    const urls = [...page.matchAll(/^\| \[[^\]]+\]\(([^)]+)\)/gm)].map(match => match[1]);
    const byText = (a, b) => a.localeCompare(b, 'en');
    requireModern(same(urls.toSorted(byText), requirements.rows.map(row => row.page).toSorted(byText)), 'live-page membership differs');
    requireModern(mapping.rows.length === 69 && same(mapping.rows.map(row => row.id), ids), 'missing/duplicate/out-of-order mapping');
    for (const [index, row] of requirements.rows.entries()) {
        const feature = features[row.id], rule = mapping.rows[index];
        requireModern(row.name === feature.name && row.requirement === feature.probe && row.probeStatus === (feature.probeStatus ?? null), 'changed criterion: ' + row.id);
        requireModern(rule.criterionSha256 === digest(row.requirement), 'mapping uses another criterion: ' + row.id);
        requireModern(typeof rule.criterionPath === 'string' && rule.criterionPath.length > 0, 'missing observation path');
    }
    return { requirements, mapping };
}

export function loadModernInputs(base) {
    const dir = join(base, modernPublication);
    const requirements = pinnedJson(join(dir, 'requirements-v1.json'), requirementsSha256);
    const mapping = pinnedJson(join(dir, 'mapping-v1.json'), mappingSha256);
    const snapshot = join(dir, requirements.upstream.snapshot);
    requireModern(digest(readFileSync(snapshot)) === requirements.upstream.snapshotSha256, 'upstream archive changed');
    return validateModernInputs(requirements, mapping, snapshotFiles(snapshot));
}
