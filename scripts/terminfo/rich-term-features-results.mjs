import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { gunzipSync } from 'node:zlib';
import { readEnvironmentPair } from './child-environment-archives.mjs';
import { digest, pinnedJson, snapshotFiles } from './modern-inputs.mjs';
import { pinnedBundle, writePublicationFiles } from './unicode-publication-inputs.mjs';
import { assessPair, names, parentValues, requireEvidence } from './rich-term-features.mjs';
import { verifyChildEnvironmentRun } from './rich-term-features-owned.mjs';

const qa = fileURLToPath(new URL('../../docs/qa/terminfo/', import.meta.url));
export const directory = join(qa, '2026-11-27-rich-term-features');
const manifestSha256 = '0e5d7ae2f495b5da7c1ab5099cc847c05393d4dbe830faf8da7354b61a39a9b7';
const id = 'device.term-features';
const bundlePath = '2026-11-16-shell-integration-aggregates/terminfo-dev-4.0.0.bundle.mjs.gz';

// Verbatim body of the pinned callback. Check its entire registration before running it.
const callback = () => {
      const advertised = typeof process !== "undefined" && !!process.env.TERM_FEATURES;
      return Promise.resolve({
        pass: false,
        note: advertised ? "TERM_FEATURES advertised; source unverified" : "TERM_FEATURES not advertised",
        observation: { outcome: "inconclusive", reason: "insufficient-evidence", evidence: "legacy" }
      });
    };

function pinnedCriterion() {
    const rich = pinnedJson(join(qa, '2026-11-26-rich-tui/requirements-v1.json'), '483c7e141c43ac483e8698afe608473680a4ac77c1e4921ea8416ddef2d0caa0');
    const stock = pinnedJson(join(qa, '2026-11-09-stock-baseline.json'), 'd4418484f935ac1a534eaf0b0368d2e5bb174974ae92f2c9c9a4ff2f8905a82b');
    const bundle = gunzipSync(readFileSync(join(qa, bundlePath)));
    assert.equal(digest(bundle), pinnedBundle);
    assert.equal(stock.source.pinnedBundleSha256, pinnedBundle);
    const registration = 'probe("' + id + '", null, ' + callback.toString().replaceAll('\r\n', '\n') + ', "legacy")';
    assert.ok(bundle.includes(registration));
    const catalogue = join(qa, rich.catalogue.path);
    assert.equal(digest(readFileSync(catalogue)), rich.catalogue.sha256);
    const row = JSON.parse(snapshotFiles(catalogue)['features.json'])[id];
    assert.deepEqual(rich.rows.filter(item => item.id === id).map(item => item.name), [row.name]);
    return { package: 'terminfo.dev@4.0.0', bundlePath, bundleSha256: pinnedBundle, criterion: row.probe,
        termless: null, termInput: 'collector process.env.TERM_FEATURES', stock: stock.outcomes[id] };
}

function replay(value) {
    const saved = process.env.TERM_FEATURES;
    try {
        if (value === null) delete process.env.TERM_FEATURES;
        else process.env.TERM_FEATURES = value;
        return callback();
    } finally {
        if (saved === undefined) delete process.env.TERM_FEATURES;
        else process.env.TERM_FEATURES = saved;
    }
}

export async function buildPublication() {
    const { manifest, clean, inherited } = readEnvironmentPair(directory, manifestSha256, verifyChildEnvironmentRun);
    const assessment = assessPair(clean.child, inherited.child);
    const pinned = pinnedCriterion();
    const runs = await Promise.all([clean, inherited].map(async ({ child, measurement }) => {
        const inputs = parentValues(measurement.control);
        return { measurement, inputs, observation: child, replay: await replay(inputs.TERM_FEATURES.value) };
    }));
    return { schemaVersion: 1, appRevision: manifest.appRevision, scriptsRevision: manifest.scriptsRevision,
        manifestSha256, assessment, pinned, runs };
}

const code = value => '\x60' + value + '\x60';
const display = ({ state, value }) => state === 'absent' ? 'absent' : code(value);

export function readableReport({ appRevision, scriptsRevision, runs, assessment, pinned }) {
    const [a, b] = runs;
    const m = a.measurement;
    return [
        '# Direct-local TERM_FEATURES assessment', '',
        'Ntilde supplied no feature string on the clean launch and preserved the inherited sentinel on the second launch. '
            + 'The same PTY child that sampled its environment sent the capability request before any shell startup.', '',
        '| Variable | Clean App input | Clean child | Seeded App input | Seeded child |',
        '| --- | --- | --- | --- | --- |',
        ...names.map(name => '| ' + [code(name), display(a.inputs[name]), display(a.observation.environment[name]),
            display(b.inputs[name]), display(b.observation.environment[name])].join(' | ') + ' |'), '',
        'Each child sent BEL-terminated ' + code('OSC 1337 ; RequestCapabilities') + ' after a DA preflight. '
            + 'Each received one ST-terminated ' + code('OSC 1337 ; Capabilities=' + assessment.replyCodes.join(''))
            + ' and a matching DA barrier. Both profiles disabled clipboard writes. The exact bytes and both pane identities are in the [rederived report](report.json).', '',
        'A live settings change can withdraw or restore ' + code('Cw') + ' after the process inherits its environment. '
            + 'The direct-local declaration stays deferred. [Per-code evidence and settings policy](../iterm-feature-reporting.md#direct-local-declaration-assessment)', '',
        'The pinned ' + code(assessment.id) + ' callback read its own process environment. With the key absent it reported “' + a.replay.note
            + '”. With the sentinel it reported “' + b.replay.note + '”. Both returned ' + code(a.replay.observation.outcome)
            + ' with ' + code(a.replay.observation.reason) + '. The registration has no termless path. Historical stock was ' + code(pinned.stock)
            + '. This assessment adds zero inventory, Core, Modern or comparison points.', '',
        '## Reproduction', '',
        'App ' + m.appVersion + ' was built from ' + code(appRevision) + '. The collector ran from ' + code(scriptsRevision)
            + ' on ' + m.host.product + ' ' + m.host.version + ' ' + m.host.arch + ', Node ' + m.nodeVersion
            + '. The [manifest](manifest.json) pins the archives. Each contains the collector sources, executable and assembly hashes, '
            + 'private settings, child ancestry, kernel restoration proof and desktop lease release.', '',
        ...runs.map(({ measurement: run }) => '- [' + run.control + ' archive](' + run.archive + ') ' + code(run.runId)
            + ', App PID ' + run.app.pid + ', child PID ' + run.child.pid + ', pane ' + code(run.pane.paneId) + '.'), '',
        'Rebuild the publication offline with Node 24 and Python 3.', '',
        '~~~sh', 'node scripts/terminfo/rich-term-features-results.mjs --check', '~~~', '',
        'For a new macOS arm64 pair, publish the App from the recorded build revision to a private sidecar directory, then run the recorded collector revision.', '',
        '~~~sh', 'scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar',
        ...runs.map(({ measurement: run }) => 'NTILDE_TERMINFO_TERM_FEATURES_CONTROL=' + run.control
            + ' node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version ' + m.appVersion + ' --scope rich-term-features'), '~~~', '',
    ].join('\n');
}

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
    const [flag, ...rest] = process.argv.slice(2);
    requireEvidence(['--check', '--write'].includes(flag) && rest.length === 0, 'expected --check or --write');
    const report = await buildPublication();
    writePublicationFiles(directory, { 'report.json': JSON.stringify(report, null, 2) + '\n', 'report.md': readableReport(report) },
        flag === '--check', requireEvidence);
}
