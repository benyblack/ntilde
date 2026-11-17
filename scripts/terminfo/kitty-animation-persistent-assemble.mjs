#!/usr/bin/env node
// Assemble the persistent-tab Kitty animation publication from a finished owned run (#1025).
// Usage: node scripts/terminfo/kitty-animation-persistent-assemble.mjs <artifacts/terminfo/owned-RUN> <docs/qa/terminfo/PUBLICATION> <pinned-bundle.mjs.gz> <app-revision> <scripts-revision>
// The raw run is archived byte-for-byte (sorted, deflate 9); the summary and report are projected from the extracted archive, so what is
// published is exactly what the publication test re-derives. Everything is built and verified in a staging directory and only then copied to the
// destination, which must not already hold a publication; the scripts revision must be the commit whose measured files the run's digests name.
import { createHash } from 'node:crypto';
import { cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, relative, resolve } from 'node:path';
import { execFileSync, spawnSync } from 'node:child_process';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { retainedInventory } from './metadata-receipt.mjs';
import { ownedSourcePath } from './owned-sources.mjs';
import { measuredAppRevision, measuredScriptsRevision, persistentAnimationQualification, pinnedBundleArchive } from './kitty-animation-persistent-qualification.mjs';

// Absolute executables (override with NTILDE_TERMINFO_GIT_EXECUTABLE / PYTHON): no PATH lookup decides what runs.
const gitExecutable = process.env.NTILDE_TERMINFO_GIT_EXECUTABLE ?? '/usr/bin/git';
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const [runArgument, publicationArgument, bundleArgument, appRevision, scriptsRevision] = process.argv.slice(2);
if (!runArgument || !publicationArgument || !bundleArgument || !/^[a-f\d]{40}$/.test(appRevision ?? '') || !/^[a-f\d]{40}$/.test(scriptsRevision ?? '')) {
    throw new Error('Usage: kitty-animation-persistent-assemble.mjs <owned run> <publication directory> <pinned bundle .gz> <40-hex app revision> <40-hex scripts revision>');
}
if (appRevision !== measuredAppRevision || scriptsRevision !== measuredScriptsRevision) throw new Error('The revisions differ from the measured constants of kitty-animation-persistent-qualification.mjs; update them first.');
const run = resolve(runArgument), publication = resolve(publicationArgument);
if (existsSync(join(publication, 'manifest.json'))) throw new Error('The destination already holds a publication; refusing to overwrite it.');

/** Every file of the run, relative and sorted, so the archive bytes depend only on the run. */
function filesOf(directory) {
    return readdirSync(directory, { withFileTypes: true }).flatMap(entry => (entry.isDirectory()
        ? filesOf(join(directory, entry.name)) : [join(directory, entry.name)])).sort((a, b) => a.localeCompare(b));
}

/** The measured scripts revision names exactly the files the run measured: each inventory file at that commit has the digest the run recorded. */
function checkScriptsRevision(directory, revision) {
    const context = JSON.parse(readFileSync(join(directory, 'context.json')));
    for (const [key, file] of Object.entries(retainedInventory(directory, context))) {
        const path = relative('', ownedSourcePath('', file)).replaceAll('\\', '/');
        // --filters yields the working-tree form (CRLF where .gitattributes says so), which is what the launcher captured.
        const bytes = execFileSync(gitExecutable, ['cat-file', '--filters', `${revision}:${path}`], { maxBuffer: 64 * 1024 * 1024 });
        if (sha(bytes) !== context[key]) throw new Error(`${path} at ${revision} is not the file this run measured`);
    }
}

const stage = mkdtempSync(join(tmpdir(), 'nt-kitty-animation-persistent-stage-')), root = mkdtempSync(join(tmpdir(), 'nt-kitty-animation-persistent-'));
try {
    checkScriptsRevision(run, scriptsRevision);
    const archive = join(stage, 'owned-run.zip');
    const script = `import zipfile,sys,os
root=sys.argv[1]; files=sys.argv[3:]
with zipfile.ZipFile(sys.argv[2],'w',zipfile.ZIP_DEFLATED,compresslevel=9) as z:
 for f in files:
  info=zipfile.ZipInfo(os.path.relpath(f,root),(2026,1,1,0,0,0)); info.compress_type=zipfile.ZIP_DEFLATED; info.external_attr=0o644<<16
  z.writestr(info,open(f,'rb').read(),compresslevel=9)
`;
    const zipped = spawnSync(process.env.PYTHON ?? '/usr/bin/python3', ['-c', script, run, archive, ...filesOf(run)], { encoding: 'utf8' });
    if (zipped.status !== 0) throw new Error('archiving failed: ' + zipped.stderr);
    const bundle = readFileSync(resolve(bundleArgument));
    writeFileSync(join(stage, pinnedBundleArchive), bundle);
    const manifest = { archives: { 'owned-run.zip': sha(readFileSync(archive)), [pinnedBundleArchive]: sha(bundle) } };
    writeFileSync(join(stage, 'manifest.json'), JSON.stringify(manifest) + '\n');
    extractOwnedArchive(stage, manifest, root, archive);
    const projected = await persistentAnimationQualification(root, { appRevision, scriptsRevision }, bundle);
    writeFileSync(join(stage, 'summary.json'), JSON.stringify(projected.summary, null, 2) + '\n');
    writeFileSync(join(stage, 'report.json'), JSON.stringify(projected.report, null, 2) + '\n');
    mkdirSync(publication, { recursive: true });
    cpSync(stage, publication, { recursive: true });
    console.log(JSON.stringify({ publication: relative(process.cwd(), publication), runId: projected.summary.ownedRunId, outcome: projected.report.cases[0].conclusion.outcome,
        counts: projected.summary.carrier.counts, bytes: statSync(archive).size }, null, 2));
} finally {
    rmSync(stage, { recursive: true, force: true });
    rmSync(root, { recursive: true, force: true });
}
