import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { isDeepStrictEqual as same } from 'node:util';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { caseIds, contractsVersion, requireWidth } from './unicode-width-contracts.mjs';
import { verifyRun } from './unicode-width-owned.mjs';

export const publication = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-25-unicode-width/', import.meta.url));
export const pinnedBundle = 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6';
const manifestSha256 = '765817e9a1054a12b6c2a68a64c8e087963e011e7204aa44218eb094d9186e81';
const json = (directory, file) => JSON.parse(readFileSync(join(directory, file), 'utf8'));
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const serialize = value => JSON.stringify(value, null, 2) + '\n';

export function readMeasurement(directory = publication) {
    const manifestBytes = readFileSync(join(directory, 'manifest.json'));
    requireWidth(digest(manifestBytes) === manifestSha256, 'publication manifest digest differs');
    const manifest = JSON.parse(manifestBytes);
    requireWidth(manifest.schemaVersion === 1 && manifest.pinnedBundleSha256 === pinnedBundle, 'publication manifest identity differs');
    return manifest;
}

export function withMeasurement(callback, directory = publication) {
    const manifest = readMeasurement(directory), scratch = mkdtempSync(join(tmpdir(), 'ntilde-uw-'));
    try {
        extractOwnedArchive(directory, manifest, scratch, join(directory, 'owned-run.zip'));
        return callback(scratch, manifest);
    } finally {
        rmSync(scratch, { recursive: true, force: true });
    }
}

function leaseEvents(directory, runId) {
    const records = readFileSync(join(directory, 'desktop-lease.jsonl'), 'utf8').trimEnd().split('\n').map(line => JSON.parse(line));
    requireWidth(same(records.map(record => record.event), ['acquired', 'launch-attempt', 'cleanup-verified', 'released'])
        && records.every((record, index) => record.lease === runId && record.pid === records[0].pid
            && Number.isFinite(Date.parse(record.at)) && (!index || Date.parse(record.at) >= Date.parse(records[index - 1].at)))
        && records[0].entrypoint === 'terminfo-owned.mjs' && records[2].by === 'marker-scan', 'desktop lease release did not verify');
    return records.at(-1).at;
}

export function buildPublication(directory = publication) {
    return withMeasurement((scratch, manifest) => {
        const report = verifyRun(scratch), context = json(scratch, 'context.json');
        const receipt = json(scratch, 'unicode-width-receipt.json');
        const m = manifest.measurement;
        requireWidth(m.runId === report.runId && m.runId === context.runId && m.sourcePolicy === context.sourceSnapshotPolicy
            && m.receiptSchema === receipt.schemaVersion && context.appVersion.endsWith('+' + m.appRevision)
            && same(report.rows.map(row => row.id), caseIds), 'wrong run, receipt, executable stamp or case order');
        const frame = JSON.parse(readFileSync(join(scratch, 'unicode-width-frames.jsonl'), 'utf8').split('\n', 1)[0]);
        return { schemaVersion: 1, baseline: 'unicode', contractsVersion, pinnedBundleSha256: pinnedBundle, ...report,
            measurement: { ...m, appVersion: context.appVersion, nodeVersion: context.nodeVersion,
                geometry: { rows: frame.screen.rows, columns: frame.screen.cols }, startedAt: context.startedAt,
                executableSha256: context.executableSha256, assemblySha256: context.assemblySha256,
                sourceSnapshotManifestSha256: context.sourceSnapshotManifestSha256, releasedAt: leaseEvents(scratch, m.runId),
                archiveSha256: manifest.archives['owned-run.zip'] } };
    }, directory);
}

const links = ['text/combining-characters', 'text/emoji-wide-chars', 'text/emoji-regional-indicator-flags', 'text/variation-selector-16-emoji',
    'text/emoji-zwj-sequences'];
const code = value => '\x60' + value + '\x60';

export function readable(report) {
    const { measurement: m } = report;
    return [
        '# Ntilde Unicode width qualification', '',
        'Five cases from the eight-case [Unicode baseline](https://terminfo.dev/baseline/unicode) measured in a disposable, ordinary local terminal on macOS.', '',
        '| Case | Sample | Width from pinned PTY replay | Stored-cell effect |', '| --- | --- | ---: | --- |',
        ...report.rows.map((row, index) => `| [${row.id}](https://terminfo.dev/${links[index]}) | ${code(row.sample)} | ${row.sampleWidth ?? 'unavailable'} (${row.terminal}) | ${row.effect} |`),
        '', 'The stored-cell contract combines the full UTF-8 specimen in the App’s row text, the physical wide and continuation flags, the cursor, and an untouched guard row. The following X occupies the second terminal column for the combining case and the third for the flag, VS16, and ZWJ cases. The single emoji fixture has no X.',
        '', 'The pinned 4.0.0 terminal callback grades ASCII calibration and cursor advance. The replay uses the raw cursor reports from the pinned width helper’s procedure, with DA fences added for ordering. The parser callback is checked separately against the retained cell projections in the real-bundle replay test. The report covers stored text and cursor movement in these fixtures. Glyph appearance and font fallback require different observations.',
        '', 'The historical stock collector already supported these five IDs. This standalone assessment adds owned App evidence for them. The existing qualification inventory tracks stock-inconclusive and ungraded IDs.',
        '', '## Reproduce', '',
        `Run ${code('node scripts/terminfo/unicode-width-publication.mjs --check')} to rederive the report from [the immutable run archive](owned-run.zip). Run ${code('NTILDE_TERMINFO_REAL_BUNDLE_REQUIRED=1 node --test scripts/terminfo/unicode-width-grading-collector.test.mjs')} to compare both probe paths with the original 4.0.0 bundle.`,
        '', `Run ${code(m.runId)} started ${m.startedAt}. Grid ${m.geometry.rows} × ${m.geometry.columns}. Sidecar ${m.appVersion} on ${m.platform}. Measured collector revision ${code(m.scriptsRevision)}. Fixture cleanup and desktop lease release were verified.`,
        '', `Build the sidecar from the recorded App revision with ${code('scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar')}.`,
        '', `Collect with ${code('node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version ' + m.appVersion + ' --scope unicode-width')} using the recorded collector revision and the shared desktop lease.`, '',
    ].join('\n');
}

export function publish(check) {
    const report = buildPublication();
    for (const [name, text] of [['report.json', serialize(report)], ['report.md', readable(report)]]) {
        const path = join(publication, name);
        if (check) requireWidth(readFileSync(path, 'utf8').replaceAll('\r\n', '\n') === text, 'published ' + name + ' differs');
        else writeFileSync(path, text);
    }
}

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
    const args = process.argv.slice(2);
    requireWidth(args.length === 1 && ['--check', '--write'].includes(args[0]), 'expected --check or --write');
    publish(args[0] === '--check');
}
