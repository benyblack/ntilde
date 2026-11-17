#!/usr/bin/env node
// Assemble the persistent-tab Kitty animation publication from a finished owned run (#1025).
// Usage: node scripts/terminfo/kitty-animation-persistent-assemble.mjs <artifacts/terminfo/owned-RUN> <docs/qa/terminfo/PUBLICATION> <pinned-bundle.mjs.gz>
// The raw run is archived byte-for-byte (sorted, deflate 9); the summary and report are projected from the extracted archive, so what is
// published is exactly what the publication test re-derives.
import { createHash } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, relative, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { measuredAppRevision, measuredScriptsRevision, persistentAnimationQualification, pinnedBundleArchive } from './kitty-animation-persistent-qualification.mjs';

const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const [runArgument, publicationArgument, bundleArgument] = process.argv.slice(2);
if (!runArgument || !publicationArgument || !bundleArgument) throw new Error('Usage: kitty-animation-persistent-assemble.mjs <owned run> <publication directory> <pinned bundle .gz>');
const run = resolve(runArgument), publication = resolve(publicationArgument);

/** Every file of the run, relative and sorted, so the archive bytes depend only on the run. */
function filesOf(directory) {
    return readdirSync(directory, { withFileTypes: true }).flatMap(entry => (entry.isDirectory()
        ? filesOf(join(directory, entry.name)) : [join(directory, entry.name)])).sort();
}
mkdirSync(publication, { recursive: true });
const archive = join(publication, 'owned-run.zip');
const script = String.raw`import zipfile,sys,os
root=sys.argv[1]; files=sys.argv[3:]
with zipfile.ZipFile(sys.argv[2],'w',zipfile.ZIP_DEFLATED,compresslevel=9) as z:
 for f in files:
  info=zipfile.ZipInfo(os.path.relpath(f,root),(2026,1,1,0,0,0)); info.compress_type=zipfile.ZIP_DEFLATED; info.external_attr=0o644<<16
  z.writestr(info,open(f,'rb').read(),compresslevel=9)
`;
const zipped = spawnSync(process.env.PYTHON ?? 'python3', ['-c', script, run, archive, ...filesOf(run)], { encoding: 'utf8' });
if (zipped.status !== 0) throw new Error('archiving failed: ' + zipped.stderr);
const bundle = readFileSync(resolve(bundleArgument));
writeFileSync(join(publication, pinnedBundleArchive), bundle);
const manifest = { archives: { 'owned-run.zip': sha(readFileSync(archive)), [pinnedBundleArchive]: sha(bundle) } };
writeFileSync(join(publication, 'manifest.json'), JSON.stringify(manifest) + '\n');

const root = mkdtempSync(join(tmpdir(), 'nt-kitty-animation-persistent-'));
try {
    extractOwnedArchive(publication, manifest, root, archive);
    const projected = await persistentAnimationQualification(root, { appRevision: measuredAppRevision, scriptsRevision: measuredScriptsRevision }, bundle);
    writeFileSync(join(publication, 'summary.json'), JSON.stringify(projected.summary, null, 2) + '\n');
    writeFileSync(join(publication, 'report.json'), JSON.stringify(projected.report, null, 2) + '\n');
    console.log(JSON.stringify({ publication: relative(process.cwd(), publication), runId: projected.summary.ownedRunId, outcome: projected.report.cases[0].conclusion.outcome,
        counts: projected.summary.carrier.counts, bytes: statSync(archive).size }, null, 2));
} finally { rmSync(root, { recursive: true, force: true }); }
