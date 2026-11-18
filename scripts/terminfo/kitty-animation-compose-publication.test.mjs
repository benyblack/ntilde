import { after, before, describe, test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { cpSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { directComposeScope, persistentComposeScope } from './collection-scope.mjs';
import { replayPinnedAnimationProbe } from './kitty-animation-qualification.mjs';
import { composeControls, composeEffectContract, composeQualification, composeQualificationSuiteId, composeReceiptSuiteId, directComposeEffectContract, directComposeQualificationSuiteId,
    directComposeReceiptSuiteId, directMeasuredAppRevision, directMeasuredScriptsRevision, measuredAppRevision, measuredScriptsRevision, pinnedBundleArchive, verifyComposeRun } from './kitty-animation-compose-qualification.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const replaceBlend = 'kitty.animation.compose-replace-blend', refused = 'kitty.animation.compose-refused';
const publication = name => fileURLToPath(new URL(`../../docs/qa/terminfo/${name}/`, import.meta.url));

/** The two publications of the same suite: a live persistent tab (policy 4) and an ordinary tab. */
const variants = [
    { label: 'persistent tab', kind: 'persistent', dir: publication('2026-11-18-kitty-animation-compose'), scope: persistentComposeScope, receiptSuiteId: composeReceiptSuiteId, qualificationSuiteId: composeQualificationSuiteId,
        contract: composeEffectContract, app: measuredAppRevision, scripts: measuredScriptsRevision, attempts: 2, daemon: true,
        limits: ['image policy 4', 'No stock collector ran', 'an ordinary (non-persistent) tab on the desktop', 'rectangle offsets and sizes smaller than the image', 'a second client', 'remote persistent sessions'] },
    { label: 'ordinary tab', kind: 'direct', dir: publication('2026-11-18-kitty-animation-compose-direct'), scope: directComposeScope, receiptSuiteId: directComposeReceiptSuiteId, qualificationSuiteId: directComposeQualificationSuiteId,
        contract: directComposeEffectContract, app: directMeasuredAppRevision, scripts: directMeasuredScriptsRevision, attempts: 2, daemon: false,
        limits: ['ordinary (non-persistent, SessionPersistence Off) tab', 'through the host decoder', 'No stock collector ran', 'a persistent tab in this publication', 'rectangle offsets and sizes smaller than the image', 'a second client', 'remote sessions'] },
];

for (const variant of variants) describe(`compose publication: ${variant.label}`, () => {
    const read = name => readFileSync(join(variant.dir, name));
    let root;
    before(() => {
        const manifest = JSON.parse(read('manifest.json'));
        assert.deepEqual(Object.keys(manifest.archives).sort(), ['owned-run.zip', pinnedBundleArchive]);
        assert.equal(hash(read('owned-run.zip')), manifest.archives['owned-run.zip']);
        root = mkdtempSync(join(tmpdir(), 'nt-kitty-animation-compose-'));
        extractOwnedArchive(variant.dir, manifest, root, join(variant.dir, 'owned-run.zip'));
    });
    after(() => { if (root) rmSync(root, { recursive: true, force: true }); });

    const retained = () => JSON.parse(readFileSync(join(root, 'animation-observations.json')));
    /** Extract a private mutable copy of the run for one refusal check. */
    function withCopy(action) {
        const copy = mkdtempSync(join(tmpdir(), 'nt-kitty-animation-compose-copy-'));
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
        const verified = verifyComposeRun(root, variant.kind);
        assert.equal(verified.receipt.suiteId, variant.receiptSuiteId);
        assert.deepEqual(verified.receipt.collectionScope, variant.scope);
        assert.equal(verified.receipt.fixtureRemoved, true);
        assert.match(verified.context.sourceSnapshotPolicy, /^readonly-owned-source-graph-v\d+$/);
        const projected = await composeQualification(root, { appRevision: variant.app, scriptsRevision: variant.scripts }, read(pinnedBundleArchive), variant.kind);
        assert.deepEqual(projected.summary, JSON.parse(read('summary.json')));
        assert.deepEqual(projected.report, JSON.parse(read('report.json')));
        assert.equal(projected.report.suiteId, variant.qualificationSuiteId);
    });

    test('the pinned callbacks are replayed from the retained pinned bundle and are inconclusive on every declaration', async () => {
        const archive = read(pinnedBundleArchive);
        assert.equal(hash(archive), JSON.parse(read('manifest.json')).archives[pinnedBundleArchive]);
        const replay = (await replayPinnedAnimationProbe(archive))['extensions.kitty-graphics.animation'];
        assert.equal(replay.termless.observation.outcome, 'inconclusive');
        assert.equal(replay.terminal.observation.outcome, 'inconclusive');
    });

    test('the composition effect result and the pinned divergence stay separate, and no stock or Core claim is made', () => {
        const report = JSON.parse(read('report.json')), summary = JSON.parse(read('summary.json')), [item] = report.cases;
        assert.equal(item.effectContract.id, variant.contract.id);
        assert.equal(item.contractResult.outcome, 'supported');
        assert.equal(item.pinned.observation.outcome, 'inconclusive');
        assert.deepEqual(item.divergence, { kind: 'pinned-inconclusive-by-design', pinnedOutcome: 'inconclusive', contractOutcome: 'supported' });
        assert.equal(item.stock, undefined); assert.equal(summary.stockCollector, undefined);
        assert.ok(item.effectContract.validity.length >= 16 && item.effectContract.validity.every(value => value.held));
        assert.deepEqual(summary.carrier.caseResults.map(value => [value.caseId, value.result]), [[replaceBlend, 'passed'], [refused, 'passed']]);
        assert.deepEqual(summary.carrier.counts, { passed: 10 });
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
        assert.ok(composeControls({ proof, frames, report: retained() }, variant.kind).every(value => value.held));
    });

    test('what the composed panes show: a replacement, a blend of a half-transparent frame onto the root, and refusals that change nothing', () => {
        const report = retained(), stage = id => report.observations.find(value => value.featureId === id);
        for (const [id, frame] of [[replaceBlend + '.replace', 'overBackground'], [replaceBlend + '.blend', 'overRoot'], [refused + '.refuse', 'root'], [refused + '.intact', 'second']]) {
            const value = stage(id);
            assert.equal(value.result, 'passed'); assert.equal(value.classification.frameId, frame); assert.equal(value.timing.clock, 'static'); assert.ok(value.reply.passed);
        }
        assert.deepEqual(stage(refused + '.refuse').reply.statuses.map(status => status.split(':')[0]), ['ENOENT', 'EINVAL', 'EINVAL']);
        assert.ok(report.observations.every(value => value.reply.passed === true));
    });

    test('only the one requested case is qualified; the claim stays bounded to its kind of tab and whole-frame compositions', () => {
        const report = JSON.parse(read('report.json'));
        assert.equal(report.cases.length, 1);
        for (const limit of variant.limits) assert.ok(report.scope.includes(limit), limit);
    });

    test('a downgraded, stripped, relaunched or foreign compose run is refused', () => {
        const refuses = (mutation, message) => withCopy(context => {
            mutation(context);
            assert.throws(() => verifyComposeRun(context.copy, variant.kind), message);
        });
        refuses(context => rebind(context, 'ownership-proof.json', proof => { proof.proofs[0].relaunches = []; }), /relaunched it/);
        // The captured App process must be the one the launch proof names: a foreign pid, re-bound to the receipt, is refused.
        refuses(context => rebind(context, 'ownership-proof.json', proof => { proof.proofs[0].app.pid += 1; }), /other than the launched one|not bound to the launched App/);
        // The launch proof's own identities are what every observation is held to: a foreign App executable, or a worker whose parent is another process, is refused in either kind of tab.
        refuses(context => rebind(context, 'ownership-proof.json', proof => { proof.proofs[0].app.executable = '/foreign/Ntilde'; }), /launched executable|not bound to the launched App|not a child of the recorded daemon|launched executable serving/);
        refuses(context => rebind(context, 'ownership-proof.json', proof => { proof.proofs[0].worker.ppid = 1; }), /not bound to the launched App|not a child of the recorded daemon/);
        refuses(context => rebind(context, 'context.json', value => { value.collectionScope = { ...value.collectionScope, suiteVersion: 2 }; }), /declare the persistent .*scope/);
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
        // A refusal that answered another code is a different observation: re-bound to the receipt, the report no longer re-derives from its journals.
        refuses(context => {
            const report = context.json('animation-observations.json');
            report.observations.find(item => item.featureId === refused + '.refuse').reply.statuses[0] = 'ENOTSUP:Frame not found';
            context.write('animation-observations.json', report);
            const receipt = context.json('persistent-animation-receipt.json');
            receipt.animationEvidence.sha256 = hash(readFileSync(join(context.copy, 'animation-observations.json')));
            context.write('persistent-animation-receipt.json', receipt);
        }, /re-derive/);
    });

    test('every development attempt is retained with its real outcome, none is the published run, and each retained file matches its recorded digest', () => {
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
