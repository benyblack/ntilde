import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { caseIds, deriveRun } from './kitty-aggregate-owned.mjs';
import { replayPinnedAggregates } from './kitty-aggregate-pinned.mjs';
import { assertRetainedSourceClosure, capturedKittyAggregateSourceFiles, capturedKittyAggregateSourcePolicy } from './owned-sources.mjs';
import { digest, focusedMeasurement, pinnedBundle, readJson, readManifest, withOwnedArchive, writeUnicodeReport } from './unicode-publication-inputs.mjs';

const qa = fileURLToPath(new URL('../../docs/qa/terminfo/', import.meta.url));
export const publication = join(qa, '2026-11-28-rich-kitty-aggregates');
const manifestHash = '6f579301aa8687cf0daa6c2022e7951e6d2b33459f86ded6cce874619c4561cf';
export const bundleArchive = join(qa, '2026-11-16-static-images/terminfo-dev-4.0.0.bundle.mjs.gz');

function verifySources(output, context) {
    const retained = join(output, 'sources'), source = readJson(output, 'source-snapshot.json');
    const fields = Object.entries(capturedKittyAggregateSourceFiles);
    assert.equal(context.sourceSnapshotPolicy, capturedKittyAggregateSourcePolicy);
    assert.equal(source.schemaVersion, 1);
    assert.equal(source.policy, context.sourceSnapshotPolicy);
    assert.deepEqual(readJson(retained, 'owned-source-inventory.json'), capturedKittyAggregateSourceFiles);
    assert.equal(Object.keys(source.hashes).length, fields.length);
    assert.equal(readdirSync(retained).length, fields.length);
    for (const [key, file] of fields) {
        assert.equal(source.hashes[key], context[key], file + ' manifest binding');
        assert.equal(digest(readFileSync(join(retained, file))), context[key], file + ' retained bytes');
    }
    assertRetainedSourceClosure(retained, capturedKittyAggregateSourceFiles);
}

export function withAggregateArchive(callback) {
    return withOwnedArchive(publication, readManifest(publication, manifestHash, assert.ok), callback);
}

function rederiveMeasurement(output, manifest) {
    const { context, frames, report, receipt } = deriveRun(output);
    assert.deepEqual(report, readJson(output, 'aggregate-report.json'));
    assert.deepEqual(receipt, readJson(output, 'aggregate-receipt.json'));
    verifySources(output, context);
    const { screen, paneId, ownership } = frames[0];
    const measurement = focusedMeasurement(output, manifest, context, receipt, screen, {
        paneId, tty: ownership.tty, appPid: ownership.app.pid, workerPid: ownership.worker.pid,
    });
    assert.ok(Date.parse(measurement.releasedAt) >= Date.parse(readJson(output, 'ownership-proof.json').sourceSnapshot.removedAt));
    return { report, measurement };
}

const historical = [
    ['2026-11-16-kitty-animation', '2026-11-16-kitty-animation-qualification.md', 'Ordinary-tab timed playback, loop, stop and PNG frame blending'],
    ['2026-11-17-kitty-animation-persistent', '2026-11-17-kitty-animation-persistent-qualification.md', 'Live persistent-tab timed playback, loop, stop and PNG frame blending'],
    ['2026-11-17-kitty-animation-restore', '2026-11-17-kitty-animation-restore-qualification.md', 'Persistent-tab App reattach after SIGTERM, including playback restoration'],
    ['2026-11-18-kitty-animation-compose-direct', '2026-11-18-kitty-animation-compose-direct-qualification.md', 'Ordinary-tab whole-frame replacement, blending and rejection'],
    ['2026-11-18-kitty-animation-compose', '2026-11-18-kitty-animation-compose-qualification.md', 'Persistent-tab whole-frame replacement, blending and rejection'],
    ['2026-11-19-kitty-placeholders-direct', '2026-11-19-kitty-placeholders-qualification.md', 'Ordinary-tab two-cell tiles, erasure and mark inheritance'],
    ['2026-11-19-kitty-placeholders', '2026-11-19-kitty-placeholders-qualification.md', 'Persistent-tab two-cell tiles, erasure and mark inheritance'],
    ['2026-11-19-kitty-placeholder-grid-direct', '2026-11-19-kitty-placeholder-grid-qualification.md', 'Ordinary-tab 4 × 2 grid, nonzero row inheritance and tile movement with text'],
    ['2026-11-19-kitty-placeholder-grid', '2026-11-19-kitty-placeholder-grid-qualification.md', 'Persistent-tab 4 × 2 grid, nonzero row inheritance and tile movement with text'],
];

function retainedCapabilities() {
    const keyboard = readJson(join(qa, '2026-11-26-rich-tui'), 'kitty-report.json');
    const graphics = readJson(join(qa, '2026-11-26-rich-kitty-graphics'), 'report.json');
    return {
        routedInput: { measurement: keyboard.measurement, rows: keyboard.rows },
        transmitAndDisplay: { measurement: graphics.measurement, cases: graphics.cases },
        rendering: historical.map(([directory, publication, scope]) => {
            const { appRevision, scriptsRevision, ownedRunId } = readJson(join(qa, directory), 'summary.json');
            const { cases: [item], runId } = readJson(join(qa, directory), 'report.json');
            assert.equal(ownedRunId, runId);
            return { id: item.caseId, scope, publication, platform: 'macOS', appRevision, scriptsRevision, runId,
                pinned: item.pinned, effect: item.conclusion.outcome };
        }),
    };
}

export async function buildPublication() {
    const { report, measurement } = withAggregateArchive(rederiveMeasurement);
    const replay = await replayPinnedAggregates(readFileSync(bundleArchive), report.observations);
    const evidence = readJson(join(qa, '2026-11-26-rich-tui'), 'evidence-map.json');
    const contracts = [
        ['Keyboard enhancement bit 1 negotiation and flag restoration', report.effects.keyboard.bitOneActivated],
        ['Acceptance of the direct RGB query', report.effects.graphics.accepted],
    ];
    const cases = caseIds.map((id, index) => {
        const stock = evidence.rows.find(row => row.id === id)?.stock;
        assert.equal(stock, 'supported');
        const [scope, supported] = contracts[index];
        const verdict = supported ? 'supported' : (index === 0 ? 'unsupported' : 'inconclusive');
        assert.ok(Object.values(replay[id]).every(path => path.observation.outcome === verdict));
        return { id, stock, pinned: replay[id], owned: { scope, verdict } };
    });
    return { schemaVersion: 1, suite: report.suite, pinnedBundleSha256: pinnedBundle, cases,
        observations: report.observations, effects: report.effects, measurement, retainedCapabilities: retainedCapabilities() };
}

const code = value => '\x60' + value + '\x60';
const caseRow = row => '| ' + [code(row.id), row.stock, row.pinned.terminal.observation.outcome,
    row.pinned.termless.observation.outcome, row.owned.scope + ' (' + row.owned.verdict + ')'].join(' | ') + ' |';

export function renderReport(report) {
    const { measurement: m, effects: { keyboard, graphics }, retainedCapabilities: prior } = report;
    const routed = prior.routedInput.measurement, images = prior.transmitAndDisplay.measurement;
    const imageControl = report.observations.find(row => row.stage === 'query');
    return [
        '# Rich TUI Kitty keyboard negotiation and graphics query', '',
        '| Exact terminfo.dev 4.0.0 case | Historical stock | Terminal replay | Termless replay | Owned scope |',
        '| --- | --- | --- | --- | --- |',
        ...report.cases.map(caseRow), '',
        'One disposable direct-local App pane with session persistence off carried both fixtures on its prepared alternate screen. The archive retains screen and placement state, two App offscreen PNGs per stage, exact actions and child PTY bytes, and two fresh DECRPM challenges per write. The pinned primary DA sentinel was ' + code('CSI c') + ' and replied ' + code('CSI ?62;4;22c') + ' after the tested reply. Both pinned callbacks consume the matching exchange, with the sentinel included only where the pinned path uses it.', '',
        'Keyboard detection returned ' + code('CSI ?' + keyboard.before + 'u') + '. The exact ' + code('CSI >1u') + ' push returned no bytes before its fence. The next ' + code('CSI ?u') + ' returned ' + code('CSI ?' + keyboard.active + 'u') + '. The exact ' + code('CSI <u') + ' pop was silent and a separate post-pop query returned ' + code('CSI ?' + keyboard.after + 'u') + ', the original flags.', '',
        'The graphics request was ' + code('APC Gi=31,s=1,v=1,a=q,t=d,f=24;AAAA ST') + '. It produced the complete ' + code('APC Gi=31;OK ST') + ' reply (hex ' + code(imageControl.replyHex) + '). Placement by ID 31 returned ' + code('ENOENT:Image not found') + ' before and after the query. The query left ' + graphics.placementCount + ' placements and identical pane PNGs. An explicit red upload under ID 31 then acknowledged successfully, placement painted the red square, and hard deletion restored the blank bound frame and the same missing-image reply.', '',
        '## Separately measured capabilities', '',
        'The five routed-input effects were supported for the tested keys and layout on ' + routed.platform + ', App ' + code(routed.appRevision) + ', collector ' + code(routed.scriptsRevision) + '. The child received the expected disambiguation, press/repeat/release, alternate-key, all-key and associated-text reports. [Five-flag XTEST assessment](../2026-11-26-rich-tui/kitty-report.md)', '',
        'Transmit and display were supported by both pinned replies and their separately observed effects on ' + images.platform + ', App ' + code(images.appRevision) + ', collector ' + code(images.scriptsRevision) + '. The upload left the pane unchanged, and the separate display fixture painted a 1 × 1 RGB source into the requested cell envelope. [Transmit and display assessment](../2026-11-26-rich-kitty-graphics/report.md)', '',
        'The retained animation and Unicode-placeholder publications grade each listed effect supported. Both pinned callbacks in those publications return inconclusive. Each row names its measured App and collector revisions and its rendering scope.', '',
        '| Owned render observation | Platform | App revision | Collector revision | Evidence |',
        '| --- | --- | --- | --- | --- |',
        ...prior.rendering.map(row => '| ' + [row.scope, row.platform, code(row.appRevision), code(row.scriptsRevision),
            '[Publication](../' + row.publication + ')'].join(' | ') + ' |'),
        '', '## Reproduction', '',
        'Run ' + code(m.runId) + ' used ' + m.platform + ', Node ' + code(m.nodeVersion) + ', a ' + m.geometry.rows + ' × ' + m.geometry.cols + ' grid, App ' + code(m.appRevision) + ' and collector ' + code(m.scriptsRevision) + '. The initial screen and PTY mode were restored, the fixture and captured-source directory were removed, and the desktop lease was released after verification. The App build revision and the retained executable and assembly hashes are recorded separately. [Owned archive](owned-run.zip) · [Machine-readable report](report.json)', '',
        'Run the offline verification from the repository root with Node 24 and Python 3.', '',
        '~~~sh',
        'node scripts/terminfo/kitty-aggregate-publication.mjs --check',
        '~~~', '',
        'For a fresh run, publish the recorded App revision to a private sidecar and run from the recorded collector revision. The launcher acquires the shared desktop lock.', '',
        '~~~sh',
        'scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar',
        'node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version ' + m.version + ' --scope kitty-aggregate',
        '~~~', '',
    ].join('\n');
}

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
    assert.equal(process.argv.length, 3);
    assert.ok(['--write', '--check'].includes(process.argv[2]));
    const report = await buildPublication();
    writeUnicodeReport(publication, report, renderReport(report), process.argv[2] === '--check', assert.ok);
}
