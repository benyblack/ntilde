// One implementation of the pinned Core publication check shared by the erase and tab-stops publications:
// the measurement manifest, owned archive, production-source manifest and retained attempts are pinned by SHA-256,
// and every row is re-derived from the archived owned run (never from an editable label).
import { readFileSync, writeFileSync, mkdtempSync, rmSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { extractOwnedArchive, loadMapping, verifyOwnedRun, reportText, root } from './terminfo-core-results.mjs';
import { assertTextCursorCurrentSource, sourceBytes } from './terminfo-core-text-cursor-results.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');

/** `derive(directory, mapping, identity, measurement)` returns `{ report, mapping }`; `readable(report, mapping)` renders report.md. */
export function createCorePublication({ publication, manifestSha256, label, derive, readable }) {
    const check = (ok, message) => { if (!ok) throw new Error(`Core ${label} publication: ${message}`); };
    function loadMeasurement(base = root) {
        const dir = join(base, publication), bytes = readFileSync(join(dir, 'measurement.json'));
        check(hash(bytes) === manifestSha256, 'measurement manifest changed');
        const m = JSON.parse(bytes);
        check(m.schemaVersion === 1 && /^[a-f\d]{64}$/.test(m.ownedArchiveSha256)
            && /^[a-f\d]{64}$/.test(m.appSourceManifestSha256), 'invalid manifest');
        check(hash(readFileSync(join(dir, 'owned-run.zip'))) === m.ownedArchiveSha256, 'archive changed');
        check(hash(readFileSync(join(dir, 'app-source-manifest.json'))) === m.appSourceManifestSha256, 'source manifest changed');
        for (const attempt of m.attempts) if (attempt.archive)
            check(hash(readFileSync(join(dir, attempt.archive))) === attempt.sha256, 'attempt archive changed');
        return m;
    }
    function buildPublication(base = root) {
        const meta = loadMeasurement(base), mapping = loadMapping(base), temp = mkdtempSync(join(tmpdir(), `ntilde-core-${label}-results-`));
        try {
            extractOwnedArchive(base, { archives: { 'owned-run.zip': meta.ownedArchiveSha256 } }, temp, join(base, publication, 'owned-run.zip'));
            const identity = verifyOwnedRun(temp, { ...mapping, measurement: meta.measurement });
            const result = derive(temp, mapping, identity, meta.measurement);
            result.report.measurement.ownedArchiveSha256 = meta.ownedArchiveSha256;
            result.report.measurement.appSourceManifestSha256 = meta.appSourceManifestSha256;
            result.report.attempts = meta.attempts;
            return result;
        } finally { rmSync(temp, { recursive: true, force: true }); }
    }
    function verifyOutput(actual, expected, file) {
        check(actual.replaceAll('\r\n', '\n') === expected, 'stale report: ' + file);
    }
    function assertCurrentSource(base = root) {
        const meta = loadMeasurement(base), manifest = JSON.parse(readFileSync(join(base, publication, 'app-source-manifest.json')));
        // The earlier publication pins exactly this complete production input set.
        assertTextCursorCurrentSource(base);
        for (const [file, digest] of Object.entries(manifest))
            check(hash(sourceBytes(file, readFileSync(join(base, file)))) === digest, 'production source changed: ' + file);
        return meta.measurement.appRevision;
    }
    function main(args = process.argv) {
        const mode = args[2];
        check(args.length === 3 && ['--check', '--write', '--assert-current-source'].includes(mode), 'use --check, --write or --assert-current-source');
        if (mode === '--assert-current-source') { console.log('Measured production sources match: ' + assertCurrentSource()); return; }
        const { report, mapping } = buildPublication();
        for (const [file, content] of Object.entries({ 'report.json': reportText(report), 'report.md': readable(report, mapping) })) {
            const path = join(root, publication, file);
            if (mode === '--write') writeFileSync(path, content);
            else verifyOutput(readFileSync(path, 'utf8'), content, file);
        }
        console.log(`Core results: ${report.totals.pass} pass / ${report.totals.fail} fail / ${report.totals.inconclusive} inconclusive; 80 exact rows.`);
    }
    /** Run `main` when the calling module is the process entry point. */
    function runIfEntry(metaUrl) {
        if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(metaUrl)) {
            try { main(); } catch (error) { console.error(error.stack); process.exitCode = 1; }
        }
    }
    return { publication, manifestSha256, loadMeasurement, buildPublication, verifyOutput, assertCurrentSource, main, runIfEntry };
}
