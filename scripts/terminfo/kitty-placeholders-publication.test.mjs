import { after, before, describe, test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { cpSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { directPlaceholdersScope, persistentPlaceholdersScope } from './collection-scope.mjs';
import { replayPinnedPlaceholdersProbe } from './kitty-animation-qualification.mjs';
import { directPlaceholdersEffectContract, directPlaceholdersQualificationSuiteId, directPlaceholdersReceiptSuiteId, directMeasuredAppRevision, directMeasuredScriptsRevision, measuredAppRevision, measuredScriptsRevision,
    pinnedBundleArchive, placeholdersControls, placeholdersEffectContract, placeholdersQualification, placeholdersQualificationSuiteId, placeholdersReceiptSuiteId, verifyPlaceholdersRun } from './kitty-placeholders-qualification.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const [tiles, mapping, unbound] = ['tiles', 'cell-mapping', 'unbound'].map(name => 'kitty.placeholders.' + name);
const publication = name => fileURLToPath(new URL(`../../docs/qa/terminfo/${name}/`, import.meta.url));

/** The two publications of the same suite: a live persistent tab (image policy 5) and an ordinary tab. */
const variants = [
    { label: 'persistent tab', kind: 'persistent', dir: publication('2026-11-19-kitty-placeholders'), scope: persistentPlaceholdersScope, receiptSuiteId: placeholdersReceiptSuiteId, qualificationSuiteId: placeholdersQualificationSuiteId,
        contract: placeholdersEffectContract, app: measuredAppRevision, scripts: measuredScriptsRevision, attempts: 0, daemon: true,
        limits: ['image policy 5', 'No stock collector ran', 'an ordinary (non-persistent) tab in this publication', 'a grid whose aspect differs', 'ids above 24 bits', 'relative placements', 'a second client', 'remote persistent sessions', 'a restart or reattach'] },
    { label: 'ordinary tab', kind: 'direct', dir: publication('2026-11-19-kitty-placeholders-direct'), scope: directPlaceholdersScope, receiptSuiteId: directPlaceholdersReceiptSuiteId, qualificationSuiteId: directPlaceholdersQualificationSuiteId,
        contract: directPlaceholdersEffectContract, app: directMeasuredAppRevision, scripts: directMeasuredScriptsRevision, attempts: 1, daemon: false,
        limits: ['ordinary (non-persistent, SessionPersistence Off) tab', 'No stock collector ran', 'a persistent tab in this publication', 'a grid whose aspect differs', 'ids above 24 bits', 'relative placements', 'scrolling', 'a second client', 'remote sessions'] },
];

for (const variant of variants) describe(`placeholders publication: ${variant.label}`, () => {
    const read = name => readFileSync(join(variant.dir, name));
    let root;
    before(() => {
        const manifest = JSON.parse(read('manifest.json'));
        assert.deepEqual(Object.keys(manifest.archives).sort(), ['owned-run.zip', pinnedBundleArchive]);
        assert.equal(hash(read('owned-run.zip')), manifest.archives['owned-run.zip']);
        root = mkdtempSync(join(tmpdir(), 'nt-kitty-placeholders-'));
        extractOwnedArchive(variant.dir, manifest, root, join(variant.dir, 'owned-run.zip'));
    });
    after(() => { if (root) rmSync(root, { recursive: true, force: true }); });

    const retained = () => JSON.parse(readFileSync(join(root, 'animation-observations.json')));
    /** Extract a private mutable copy of the run for one refusal check. */
    function withCopy(action) {
        const copy = mkdtempSync(join(tmpdir(), 'nt-kitty-placeholders-copy-'));
        try {
            cpSync(root, copy, { recursive: true });
            const json = name => JSON.parse(readFileSync(join(copy, name)));
            const write = (name, value) => writeFileSync(join(copy, name), typeof value === 'string' ? value : JSON.stringify(value, null, 2) + '\n');
            return action({ copy, json, write });
        } finally { rmSync(copy, { recursive: true, force: true }); }
    }
    /** Mutate a retained file, then re-bind the receipt to the new bytes so the refusal under test is the semantic one, not the hash. */
    const rebind = ({ copy, json, write }, name, mutate) => {
        const value = json(name); mutate(value); write(name, value);
        const receipt = json('persistent-animation-receipt.json');
        receipt.ownershipProofSha256 = hash(readFileSync(join(copy, 'ownership-proof.json')));
        receipt.contextSha256 = hash(readFileSync(join(copy, 'context.json')));
        write('persistent-animation-receipt.json', receipt);
    };

    test('the owned run independently re-derives its receipt, lineage, report and the publication projection', async () => {
        const verified = verifyPlaceholdersRun(root, variant.kind);
        assert.equal(verified.receipt.suiteId, variant.receiptSuiteId);
        assert.deepEqual(verified.receipt.collectionScope, variant.scope);
        assert.equal(verified.receipt.fixtureRemoved, true);
        assert.match(verified.context.sourceSnapshotPolicy, /^readonly-owned-source-graph-v\d+$/);
        const projected = await placeholdersQualification(root, { appRevision: variant.app, scriptsRevision: variant.scripts }, read(pinnedBundleArchive), variant.kind);
        assert.deepEqual(projected.summary, JSON.parse(read('summary.json')));
        assert.deepEqual(projected.report, JSON.parse(read('report.json')));
        assert.equal(projected.report.suiteId, variant.qualificationSuiteId);
    });

    test('the pinned callbacks are replayed from the retained pinned bundle and are inconclusive on every declaration', async () => {
        const archive = read(pinnedBundleArchive);
        assert.equal(hash(archive), JSON.parse(read('manifest.json')).archives[pinnedBundleArchive]);
        const replay = (await replayPinnedPlaceholdersProbe(archive))['extensions.kitty-graphics.unicode-placeholders'];
        assert.equal(replay.termless.observation.outcome, 'inconclusive');
        assert.equal(replay.terminal.observation.outcome, 'inconclusive');
    });

    test('the placeholder effect result and the pinned divergence stay separate, and no stock or Core claim is made', () => {
        const report = JSON.parse(read('report.json')), summary = JSON.parse(read('summary.json')), [item] = report.cases;
        assert.equal(item.effectContract.id, variant.contract.id);
        assert.equal(item.contractResult.outcome, 'supported');
        assert.equal(item.pinned.observation.outcome, 'inconclusive');
        assert.deepEqual(item.divergence, { kind: 'pinned-inconclusive-by-design', pinnedOutcome: 'inconclusive', contractOutcome: 'supported' });
        assert.equal(item.stock, undefined); assert.equal(summary.stockCollector, undefined);
        assert.ok(item.effectContract.validity.length >= 17 && item.effectContract.validity.every(value => value.held));
        assert.deepEqual(summary.carrier.caseResults.map(value => [value.caseId, value.result]), [[tiles, 'passed'], [mapping, 'passed'], [unbound, 'passed']]);
        assert.deepEqual(summary.carrier.counts, { passed: 19 });
    });

    test('one App process and one worker observed every stage, with the lineage the kind of tab has', () => {
        const proof = JSON.parse(readFileSync(join(root, 'ownership-proof.json'))), summary = JSON.parse(read('summary.json')), frames = retained().snapshots;
        assert.equal(new Set(frames.map(frame => frame.ownership.app.pid)).size, 1);
        assert.equal(proof.proofs[0].relaunches, undefined);
        assert.ok(frames.every(frame => frame.ownership.worker.pid === proof.proofs[0].worker.pid));
        if (variant.daemon) assert.ok(frames.every(frame => frame.ownership.daemon.pid === proof.persistentDaemon.identity.pid));
        else {
            assert.equal(proof.persistentDaemon, undefined);
            assert.ok(frames.every(frame => frame.ownership.daemon === undefined && frame.ownership.worker.ppid === frame.ownership.app.pid), 'the worker is the App\'s own child');
        }
        assert.equal(summary.pane.stable, true);
        assert.ok(placeholdersControls({ proof, frames, report: retained() }, variant.kind).every(value => value.held));
    });

    test('what the placeholder cells showed: the tiles in order, the tile the marks choose, the inheritance, and nothing for cells that name no image, placement or tile', () => {
        const report = retained(), stage = id => report.observations.find(value => value.featureId === id);
        const expected = [[tiles + '.build', ['absent', 'absent']], [tiles + '.show', ['red', 'blue']], [tiles + '.erase', ['absent', 'absent']],
            [mapping + '.swapped', ['blue', 'red']], [mapping + '.single', ['absent', 'red']], [mapping + '.inherit', ['red', 'blue']],
            [unbound + '.wrong-image', ['absent', 'absent']], [unbound + '.stale-placement', ['absent', 'absent']], [unbound + '.outside-grid', ['absent', 'absent']],
            [unbound + '.valid', ['red', 'blue']], [unbound + '.deleted', ['absent', 'absent']]];
        for (const [id, tilesShown] of expected) {
            const value = stage(id);
            assert.equal(value.result, 'passed', id); assert.deepEqual(value.classification.tiles, tilesShown, id); assert.equal(value.timing.clock, 'static'); assert.ok(value.reply.passed);
        }
        assert.deepEqual(stage(tiles + '.build').reply.statuses, ['OK', 'OK']);
        assert.ok(report.observations.every(value => value.reply.passed === true));
        // A painted tile is the band of its own color, as wide as its cell, with the undistorted height of the fit of a square image into the whole two-cell grid (its shorter side).
        for (const id of [tiles + '.show', unbound + '.valid']) {
            const { cells, envelope } = stage(id).measurement;
            assert.ok(cells.every(cell => cell.bounds.width === cell.rect.width && cell.bounds.height === Math.min(envelope.width, envelope.height)), id);
        }
    });

    test('only the one requested case is qualified; the claim stays bounded to its kind of tab and to one image, one grid and one cell font', () => {
        const report = JSON.parse(read('report.json'));
        assert.equal(report.cases.length, 1);
        for (const limit of variant.limits) assert.ok(report.scope.includes(limit), limit);
    });

    test('a downgraded, stripped, relaunched or foreign placeholder run is refused', () => {
        const refuses = (mutation, message) => withCopy(context => {
            mutation(context);
            assert.throws(() => verifyPlaceholdersRun(context.copy, variant.kind), message);
        });
        refuses(context => rebind(context, 'ownership-proof.json', proof => { proof.proofs[0].relaunches = []; }), /relaunched it/);
        // The captured App process must be the one the launch proof names: a foreign pid, re-bound to the receipt, is refused.
        refuses(context => rebind(context, 'ownership-proof.json', proof => { proof.proofs[0].app.pid += 1; }), /other than the launched one|not bound to the launched App/);
        // The launch proof's own identities are what every observation is held to: a foreign App executable, or a worker whose parent is another process, is refused in either kind of tab.
        refuses(context => rebind(context, 'ownership-proof.json', proof => { proof.proofs[0].app.executable = '/foreign/Ntilde'; }), /launched executable|not bound to the launched App|not a child of the recorded daemon|launched executable serving/);
        refuses(context => rebind(context, 'ownership-proof.json', proof => { proof.proofs[0].worker.ppid = 1; }), /not bound to the launched App|not a child of the recorded daemon/);
        refuses(context => rebind(context, 'context.json', value => { value.collectionScope = { ...value.collectionScope, suiteVersion: 4 }; }), /declare the persistent .*scope/);
        refuses(context => {
            rebind(context, 'initial-settings.json', settings => { settings.SessionPersistence = variant.daemon ? 'Off' : 'KeepOnClose'; });
            rebind(context, 'context.json', value => { value.initialSettingsSha256 = hash(readFileSync(join(context.copy, 'initial-settings.json'))); });
        }, /not configured for (a persistent|an ordinary) tab/);
        // A daemon in an ordinary-tab proof, or none in a persistent one, is the other kind of run.
        if (variant.daemon) refuses(context => rebind(context, 'ownership-proof.json', proof => { delete proof.persistentDaemon; }), /no multiplexer daemon/);
        else refuses(context => rebind(context, 'ownership-proof.json', proof => { proof.persistentDaemon = { identity: { pid: 1 } }; }), /multiplexer daemon in its proof/);
        refuses(({ copy }) => rmSync(join(copy, 'source-snapshot.json')), /source-snapshot/);
        refuses(({ copy }) => writeFileSync(join(copy, 'sources', 'worker.mjs'), '// changed\n'), /retained source|differs/);
        refuses(({ json, write }) => { const report = json('animation-observations.json'); report.observations[0].result = 'failed'; write('animation-observations.json', report); }, /receipt does not bind/);
        // A different reply is a different observation: re-bound to the receipt, the report no longer re-derives from its journals.
        refuses(context => {
            const report = context.json('animation-observations.json');
            report.observations.find(item => item.featureId === tiles + '.build').reply.statuses[1] = 'ENOTSUP:Graphics action not supported';
            context.write('animation-observations.json', report);
            const receipt = context.json('persistent-animation-receipt.json');
            receipt.animationEvidence.sha256 = hash(readFileSync(join(context.copy, 'animation-observations.json')));
            context.write('persistent-animation-receipt.json', receipt);
        }, /re-derive/);
        // A grade that was changed to a pass, or a tile that was repainted in the retained report, does not re-derive either.
        refuses(context => {
            const report = context.json('animation-observations.json');
            report.observations.find(item => item.featureId === mapping + '.swapped').classification.tiles = ['red', 'blue'];
            context.write('animation-observations.json', report);
            const receipt = context.json('persistent-animation-receipt.json');
            receipt.animationEvidence.sha256 = hash(readFileSync(join(context.copy, 'animation-observations.json')));
            context.write('persistent-animation-receipt.json', receipt);
        }, /re-derive/);
    });

    // The persistent-tab publication needed no earlier attempt, so it has none to check (and no skipped test: the inventory refuses a rederivation with a skip).
    if (variant.attempts > 0) test('every development attempt is retained with its real outcome, none is the published run, and each retained file matches its recorded digest', () => {
        const attempts = JSON.parse(read('superseded-attempts/attempts.json')), published = JSON.parse(read('summary.json')).ownedRunId;
        assert.equal(attempts.schemaVersion, 1);
        assert.equal(attempts.attempts.length, variant.attempts);
        assert.equal(new Set(attempts.attempts.map(item => item.runId)).size, attempts.attempts.length, 'every retained attempt is a distinct run');
        for (const item of attempts.attempts) {
            assert.notEqual(item.runId, published);
            assert.ok(item.reason.length > 20 && item.scripts.length > 0 && item.files.length >= 2, item.attempt);
            for (const file of item.files) {
                // Retained text is LF in git; a Windows checkout may add CR, so hash and size the LF form.
                const bytes = Buffer.from(read(join('superseded-attempts', file.file)).toString('latin1').replaceAll('\r\n', '\n'), 'latin1');
                assert.equal(hash(bytes), file.sha256, file.file);
                assert.equal(bytes.length, file.bytes, file.file);
            }
        }
    });
});
