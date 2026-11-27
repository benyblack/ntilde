import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { isDeepStrictEqual as same } from 'node:util';
import { gunzipSync } from 'node:zlib';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { leaseReleasedAt } from './modern-child-environment-results.mjs';
import { digest, pinnedJson, snapshotFiles } from './modern-inputs.mjs';
import { assessEnvironment, controls, controlVariable, families, requireVendorEnvironment as requireEvidence, scopeName } from './rich-vendor-environment.mjs';
import { verifyChildEnvironmentRun } from './rich-vendor-environment-owned.mjs';

const root = fileURLToPath(new URL('../../docs/qa/terminfo/', import.meta.url));
const directory = join(root, '2026-11-27-rich-vendor-environment');
const manifestSha256 = 'a5d61116014c890483df75a272608c171bf824fa04b2d62722aca61bd8b4e9e5';
const bundlePath = '2026-11-16-shell-integration-aggregates/terminfo-dev-4.0.0.bundle.mjs.gz';
const bundleSha256 = 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6';
const requirementsSha256 = '483c7e141c43ac483e8698afe608473680a4ac77c1e4921ea8416ddef2d0caa0';
const stockSha256 = 'd4418484f935ac1a534eaf0b0368d2e5bb174974ae92f2c9c9a4ff2f8905a82b';
const manual = 'Manual verification required — no automated probe available.';
const scope = 'macOS direct-local App profile executing Node in a native PTY before shell startup';

export function pinnedCriteria() {
    const rich = pinnedJson(join(root, '2026-11-26-rich-tui', 'requirements-v1.json'), requirementsSha256);
    const stock = pinnedJson(join(root, '2026-11-09-stock-baseline.json'), stockSha256);
    const catalogue = join(root, rich.catalogue.path);
    const bundle = gunzipSync(readFileSync(join(root, bundlePath)));
    requireEvidence(digest(bundle) === bundleSha256 && stock.source.pinnedBundleSha256 === bundleSha256
        && digest(readFileSync(catalogue)) === rich.catalogue.sha256, 'changed pinned Rich TUI sources');
    const features = JSON.parse(snapshotFiles(catalogue)['features.json']);
    return families.map(({ id }) => {
        const [row, ...duplicates] = rich.rows.filter(item => item.id === id);
        const feature = features[id];
        requireEvidence(row && duplicates.length === 0 && row.name === feature.name && row.page.endsWith('/' + feature.slug)
            && feature.baseline === 'rich' && feature.probeStatus === 'manual' && feature.probe === manual
            && !bundle.includes(feature.slug) && !bundle.includes(id)
            && !Object.hasOwn(stock.outcomes, id) && !Object.hasOwn(stock.ungradedDiagnostics ?? {}, id),
        'changed criterion, pinned registration or stock coverage for ' + id);
        return { id, sequence: feature.sequence, description: feature.body, criterion: feature.probe,
            catalogueProbeStatus: feature.probeStatus, pinnedVerdict: 'unprobed', stock: null };
    });
}

export function verifyVendorEnvironmentRun(scratch) {
    const { child, context, ownership, launch, pane, receipt } = verifyChildEnvironmentRun(scratch);
    const proof = ownership.proofs[0];
    const host = launch.host;
    requireEvidence(host?.product === 'macOS' && /^\d+\.\d+(\.\d+)?$/.test(host.version)
        && /^[A-Z\d]+$/.test(host.build) && ['arm64', 'x64'].includes(host.arch), 'missing macOS launch metadata');
    return { child, measurement: {
        runId: context.runId, control: launch.control, host, startedAt: context.startedAt, appVersion: context.appVersion,
        appVersionSource: context.appVersionSource, executableSha256: context.executableSha256, appBinarySha256: launch.appBinaries,
        nodeVersion: context.nodeVersion, nodeSha256: launch.nodeSha256, sourcePolicy: context.sourceSnapshotPolicy,
        collectorSnapshotSha256: context.sourceSnapshotManifestSha256, receiptSchema: receipt.schemaVersion,
        childOutputSha256: receipt.sha256['child-environment.json'], appData: launch.appData, pane, app: proof.app, child: proof.worker,
        terminalRestoredAt: proof.privateClipboard.restoredAt, cleanedUpAt: ownership.cleanedUpAt, fixtureRemoved: receipt.fixtureRemoved,
        sourceSnapshotRemovedAt: ownership.sourceSnapshot.removedAt, leaseReleasedAt: leaseReleasedAt(scratch, context, ownership),
    } };
}

export function withRetainedRun(entry, callback) {
    const scratch = mkdtempSync(join(tmpdir(), 'ntilde-rich-vendor-environment-'));
    try {
        requireEvidence(controls.includes(entry.control) && entry.archive === entry.control + '-run.zip', 'unexpected archive name');
        extractOwnedArchive(directory, { archives: { 'owned-run.zip': entry.sha256 } }, scratch, join(directory, entry.archive));
        return callback(scratch);
    } finally {
        rmSync(scratch, { recursive: true, force: true });
    }
}

function readRun(entry) {
    const { child, measurement } = withRetainedRun(entry, verifyVendorEnvironmentRun);
    requireEvidence(measurement.runId === entry.runId && measurement.control === entry.control, 'run identity differs from manifest');
    return { child, measurement: { ...measurement, archive: entry.archive, archiveSha256: entry.sha256 } };
}

export function buildPublication() {
    const manifest = pinnedJson(join(directory, 'manifest.json'), manifestSha256);
    requireEvidence(manifest.schemaVersion === 1 && [manifest.appRevision, manifest.scriptsRevision].every(value => /^[a-f\d]{40}$/.test(value))
        && same(manifest.runs.map(run => run.control), controls), 'unexpected pair in the measurement manifest');
    const [clean, inherited] = manifest.runs.map(readRun);
    const a = clean.measurement, b = inherited.measurement;
    const common = ['host', 'appVersion', 'executableSha256', 'appBinarySha256', 'nodeVersion', 'nodeSha256',
        'sourcePolicy', 'collectorSnapshotSha256', 'receiptSchema'];
    requireEvidence(a.runId !== b.runId && a.appData !== b.appData && a.pane.paneId !== b.pane.paneId
        && a.app.executable === b.app.executable && a.child.executable === b.child.executable
        && Date.parse(a.leaseReleasedAt) <= Date.parse(b.startedAt)
        && common.every(field => same(a[field], b[field])), 'expected two isolated, serialized runs of the same App and collector');
    const result = assessEnvironment(clean.child, inherited.child);
    const criteria = new Map(pinnedCriteria().map(item => [item.id, item]));
    return {
        schemaVersion: 1, scope, manifestSha256, appRevision: manifest.appRevision, scriptsRevision: manifest.scriptsRevision,
        revisionSource: 'recorded build and collector checkouts with retained executable and source hashes',
        pinned: { package: 'terminfo.dev@4.0.0', bundlePath, bundleSha256, requirementsSha256, stockSha256 },
        runs: [a, b], controls: result.controls,
        rows: result.rows.map(row => ({ ...row, frozen: criteria.get(row.id) })),
    };
}

const code = value => '\x60' + value + '\x60';
function sampleCell({ state, value }) {
    if (state !== 'present') return state;
    return '<code>' + value.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;')
        .replaceAll('|', '&#124;').replaceAll('\n', '&#10;') + '</code>';
}
function variableLine([name, { clean, inherited }]) {
    return '| ' + code(name) + ' | ' + [sampleCell(clean.input), sampleCell(clean.child) + ' (' + clean.origin + ')',
        sampleCell(inherited.input), sampleCell(inherited.child) + ' (' + inherited.origin + ')'].join(' | ') + ' |';
}

function readableReport({ appRevision, scriptsRevision, pinned, runs, controls: observedControls, rows }) {
    const [clean] = runs;
    const { product, version, build, arch } = clean.host;
    return [
        '# Rich TUI vendor child environment assessment', '',
        'The frozen catalogue designates all four cases manual. The pinned ' + code(pinned.package)
            + ' bundle has no runnable registration for these IDs. The stock baseline has no grade for any of them. '
            + 'Each decision below applies to Ntilde’s own vendor-variable provision on the measured macOS direct-local path.', '',
        '| Frozen case | Documented producer | Applicability to Ntilde | Pinned | Stock |',
        '| --- | --- | --- | --- | --- |',
        ...rows.map(row => '| ' + code(row.id) + ' | ' + row.producer + ' | ' + row.applicability + ' | '
            + row.frozen.pinnedVerdict + ' | ungraded |'), '',
        'The seeded values show ordinary environment inheritance. These strings carry no vendor session, pane, process, executable or socket proof. This assessment contributes no stock, inventory, Core, Modern or comparison points.', '',
        '## Observed values', '',
        'The owner removed the allowlisted vendor names before the clean launch and seeded every one with a distinct synthetic value before the inherited launch. '
            + 'A disposable local profile launched Node directly with shell integration and session persistence disabled. '
            + 'The worker sampled only the allowlist. The raw format preserves empty strings separately from absent keys.', '',
        '| Variable | Clean App input | Clean PTY child | Seeded App input | Seeded PTY child |',
        '| --- | --- | --- | --- | --- |',
        ...rows.flatMap(row => Object.entries(row.variables).map(variableLine)), ...Object.entries(observedControls).map(variableLine), '',
        'The App’s native PTY setup explicitly writes ' + code('TERM_PROGRAM=Ntilde')
            + '. The independent neutral control was inherited in both launches. [Ntilde PTY setup](https://github.com/benyblack/ntilde/blob/'
            + appRevision + '/src/Ntilde.App/native/src/lib.rs#L986-L994).', '',
        '## Evidence and reproduction', '',
        'The App checkout was ' + code(appRevision) + ' (version ' + code(clean.appVersion)
            + '). The collector checkout was ' + code(scriptsRevision) + ' with Node ' + clean.nodeVersion
            + ' on ' + product + ' ' + version + ' (' + build + ') ' + arch + '.', '',
        ...runs.map(run => '- [' + run.control + ' launch](' + run.archive + ') ' + code(run.runId) + ' bound App PID ' + run.app.pid
            + ', its direct child PID ' + run.child.pid + ', and pane ' + code(run.pane.paneId)
            + '. The kernel terminal attributes were restored and the desktop lease released at ' + run.leaseReleasedAt + '.'), '',
        'The two archives retain the App executable, assembly, managed PTY and native PTY library hashes, the collector’s source snapshot, '
            + 'the launch settings, the child and pane proof, the raw environment result, and the filtered lock log. '
            + 'The executable and source hashes identify the measured inputs. The App checkout is build provenance. '
            + 'The observation covers local PTY inheritance on this build and host. [Contract and primary sources](../rich-vendor-environment.md) · '
            + '[Measurement manifest](manifest.json) · [Exact criteria, identities and observations](report.json).', '',
        'Re-derive both reports offline with Node 24 and Python 3.', '',
        '~~~sh',
        'node scripts/terminfo/rich-vendor-environment-results.mjs --check',
        '~~~', '',
        'To repeat the measurement, build the recorded App checkout and run both controls from the recorded collector checkout on macOS. Each launch uses the shared desktop lease.', '',
        '~~~sh',
        'scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar',
        ...controls.map(control => controlVariable + '=' + control + ' node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version '
            + clean.appVersion + ' --scope ' + scopeName),
        '~~~', '',
    ].join('\n');
}

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
    const [flag, ...rest] = process.argv.slice(2);
    requireEvidence(['--check', '--write'].includes(flag) && rest.length === 0, 'expected --check or --write');
    const report = buildPublication();
    const outputs = { 'report.json': JSON.stringify(report, null, 2) + '\n', 'report.md': readableReport(report) };
    for (const [name, content] of Object.entries(outputs)) {
        const path = join(directory, name);
        if (flag === '--check') requireEvidence(readFileSync(path, 'utf8').replaceAll('\r\n', '\n') === content, 'published ' + name + ' differs');
        else writeFileSync(path, content);
    }
}
