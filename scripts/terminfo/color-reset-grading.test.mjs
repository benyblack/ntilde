import assert from 'node:assert/strict';
import { test } from 'node:test';
import { pinnedColorResetProbe } from './fixtures/color-reset-pinned.mjs';
import { colorResetContracts } from './color-reset-contracts.mjs';
import { colorResetRecordingContext } from './color-reset-collector.mjs';
import { buildColorResetGradingReport, replayColorResetPinned } from './color-reset-grading.mjs';
import { syntheticColorResetEvidence, cloneColorResetEvidence } from './fixtures/color-reset-evidence.mjs';

/** The verbatim pinned colorResetProbe and its helpers, evaluated from the pinned seams: the replay's oracle. */
const colorResetProbe = await pinnedColorResetProbe();
const pinnedArgs = [[4, 104, 0], [10, 110], [11, 111], [12, 112]];
const reply = (code, value) => ({ raw: `\x1b]${code};rgb:${value}\x1b\\` });
const codes = ['4;0', '10', '11', '12'];
const sets = [
    ['c0c0/c0c0/c0c0', 'aaaa/bbbb/cccc', 'c0c0/c0c0/c0c0'], ['c0c0/c0c0/c0c0', 'aaaa/bbbb/cccc', '1111/2222/3333'], ['c0c0/c0c0/c0c0', 'c0c0/c0c0/c0c0', 'c0c0/c0c0/c0c0'],
    ['0a0a/0b0b/0c0c', 'AAAA/BBBB/CCCC', '0a0a/0b0b/0c0c'], ['0a/0b/0c', 'aa/bb/cc', '0a/0b/0c'], ['1/2/3', 'aaaa/bbbb/cccc', '1/2/3'], ['c0c0/c0c0/c0c0', 'aaaa/bbbb/cccc', 'c0c0/c0c0/c0c1'],
];

test('the replay is the pinned colorResetProbe result byte for byte, over supported, unsupported, unestablished and odd replies', () => {
    for (const [index, contract] of colorResetContracts.entries()) {
        const batches = [...sets.map(set => set.map(value => reply(codes[index], value))), [null, null, null], [{ raw: `\x1b]${codes[index]};junk` }, null, null],
            [reply(codes[index], 'c0c0/c0c0/c0c0'), null, reply(codes[index], 'c0c0/c0c0/c0c0')], [reply(codes[index], 'c0c0/c0c0/c0c0'), reply(codes[index], 'aaaa/bbbb/cccc'), null],
            [{ raw: `noise\x1b]${codes[index]};rgb:c0c0/c0c0/c0c0\x07tail` }, { raw: `\x1b]${codes[index]};rgb:aaaa/bbbb/cccc\x07` }, { raw: `\x1b]${codes[index]};rgb:c0c0/c0c0/c0c0\x07` }]];
        for (const replies of batches) {
            const context = colorResetRecordingContext(contract, replies);
            assert.deepEqual(replayColorResetPinned(contract, replies), colorResetProbe(...pinnedArgs[index])(context), JSON.stringify(replies).slice(0, 80));
        }
    }
});

test('an original already equal to the pinned color makes the pinned code feed its alternate: the contract refuses to grade it', () => {
    const contract = colorResetContracts[1];
    const context = colorResetRecordingContext(contract, [reply('10', 'aaaa/bbbb/cccc'), null, null]);
    assert.throws(() => colorResetProbe(10, 110)(context), /feed 1 differs from the contract/);
});

test('the recording context refuses any pinned feed that is not the contracted bytes in the contracted kind', () => {
    const contract = colorResetContracts[1];
    const context = () => colorResetRecordingContext(contract, [null, null, null]);
    assert.throws(() => context().feed(contract.pinnedFeeds[0]), /feed 0 differs from the contract/, 'a query must be a capture');
    assert.throws(() => context().feedCapture('\x1b]10;?\x1b\\'), /feed 0 differs/, 'the ST spelling is not the pinned one');
    const started = context();
    started.feedCapture(contract.pinnedFeeds[0]);
    assert.throws(() => started.feed(contract.pinnedFeeds[0]), /feed 1 differs/);
    assert.throws(() => started.getCell(), /must not read cells/);
    assert.throws(() => started.getCursor(), /must not read the cursor/);
    assert.throws(() => started.getText(), /must not read text/);
    assert.equal(started.fedCount, 1);
});

const report = evidence => buildColorResetGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter, { captureOf: evidence.captureOf });

test('the report re-derives every case from the journal: pinned replay, independent effect and the by-design terminal path, side by side', () => {
    const evidence = syntheticColorResetEvidence();
    const value = report(evidence);
    assert.equal(value.suiteId, 'ntilde-owned-runtime-color-reset-graded-v1');
    assert.equal(value.snapshots.length, 47);
    assert.deepEqual(value.cases.map(item => item.conclusion.outcome), ['supported', 'supported', 'supported', 'supported']);
    for (const item of value.cases) {
        assert.equal(item.conclusion.pinnedTermless, 'supported');
        assert.equal(item.conclusion.pinnedTerminal, 'inconclusive-by-design');
        assert.deepEqual(item.pinnedTerminal, { outcome: 'inconclusive', evidence: 'none', byDesign: true, note: item.terminalFixture.note });
        assert.equal(item.pinned.observation.evidence, 'behavior');
        assert.equal(item.divergence, undefined);
        assert.equal(item.reads.length, item.contractResult.experiments.length ? item.reads.length : 0);
        assert.deepEqual(Object.keys(item.observed), item.contractResult.experiments.map(experiment => experiment.id));
    }
    assert.deepEqual(report(syntheticColorResetEvidence()), value, 'deterministic');
});

test('a reset the render did not follow is recorded as a divergence: the pinned replies pass, the stricter contract does not', () => {
    const value = report(syntheticColorResetEvidence('a'.repeat(32), 'f'.repeat(64), { renderStale: true }));
    for (const item of value.cases) {
        assert.equal(item.pinned.observation.outcome, 'supported');
        assert.equal(item.conclusion.outcome, 'unsupported');
        assert.deepEqual(item.divergence, { pinnedOutcome: 'supported', contractOutcome: 'unsupported', kind: 'contract-stricter-than-pinned' });
    }
});

test('a wrong-theme restoration is unsupported by the pinned replay and the contract alike, with no divergence', () => {
    const value = report(syntheticColorResetEvidence('a'.repeat(32), 'f'.repeat(64), { wrongTheme: true }));
    for (const item of value.cases) {
        assert.equal(item.pinned.observation.outcome, 'unsupported');
        assert.equal(item.conclusion.outcome, 'unsupported');
        assert.equal(item.divergence, undefined);
    }
});

test('missing replies are inconclusive in the pinned replay and the contract, never unsupported', () => {
    const value = report(syntheticColorResetEvidence('a'.repeat(32), 'f'.repeat(64), { noReplies: true }));
    for (const item of value.cases) {
        assert.equal(item.pinned.observation.outcome, 'inconclusive');
        assert.equal(item.pinned.observation.reason, 'no-response');
        assert.equal(item.conclusion.outcome, 'inconclusive');
    }
});

const refuses = (mutate, pattern, behavior = {}) => {
    const evidence = cloneColorResetEvidence(syntheticColorResetEvidence('a'.repeat(32), 'f'.repeat(64), behavior));
    mutate(evidence);
    assert.throws(() => report(evidence), pattern);
};
const rehash = (evidence, index) => { /* frames carry their own digests; a changed screen must be refused through them */ void evidence; void index; };

for (const [label, mutate, pattern] of [
    ['a missing frame', e => { e.frames.pop(); }, /Incomplete Runtime-color reset snapshot sequence/],
    ['a reordered frame', e => { [e.frames[5], e.frames[6]] = [e.frames[6], e.frames[5]]; }, /Changed Runtime-color reset snapshot evidence/],
    ['a changed screen digest', e => { e.frames[7].screen.lines[1] = 'tampered'; }, /Changed Runtime-color reset snapshot evidence/],
    ['a changed bracketing screen', e => { e.frames[7].afterSha256 = '0'.repeat(64); }, /bracketing state changed/],
    ['a run bound to another run id', e => { e.runId = 'b'.repeat(32); }, /not bound to this run/],
    ['a changed adapter identity', e => { e.adapter.id = 'other'; }, /Invalid Runtime-color reset adapter identity/],
    ['a changed derivation parent', e => { e.adapter.derivedFromAdapterId = 'other'; }, /adapter identity/],
    ['an adapter without a digest', e => { e.adapter.bundleSha256 = 'x'; }, /adapter identity/],
    ['missing cases', e => { e.run.cases.pop(); }, /does not cover the contracted cases/],
    ['reordered probe ids', e => { e.run.probeIds.reverse(); }, /does not cover the contracted cases/],
    ['a changed capture binding', e => { e.frames[9].pixel.file = 'crst-pixels/frame-001.png'; }, /Invalid Runtime-color reset capture binding/],
    ['a changed capture phase', e => { e.frames[9].pixel.presentationTimeMs = 530; }, /Invalid Runtime-color reset capture binding/],
    ['a changed render context', e => { e.frames[9].pixel.renderDetails.font.sizeDip = 15; }, /Changed Runtime-color reset render context/],
    ['a changed theme identity', e => { for (const frame of e.frames.slice(4)) frame.screen.colorBinding.themeName = 'Other'; }, /theme identity|Changed/],
    ['a run that never restored the runtime colors', e => { e.frames.at(-1).pixel.renderDetails.theme.foregroundOverride = true; }, /not restored|Changed/],
    ['a run that started dirty', e => { for (const frame of [e.frames[0], e.frames.at(-1)]) frame.pixel.renderDetails.theme.cursorOverride = true; }, /not restored/],
    ['a reply the worker did not record', e => { e.run.cases[1].reads[3].queries[0].reply = null; }, /replies differ from their read frame/],
    ['a reply moved to another barrier', e => { e.run.cases[1].reads[3].queries[0].barrier = '\x1b[?1c'; }, /reply differs from its exchange/],
    ['a changed reply spec', e => { e.run.cases[2].reads[0].queries[0].spec = { code: 10 }; }, /reply differs from its exchange|replies differ/],
    ['a malformed reply', e => { e.frames[2].queries[0].reply.raw = e.frames[2].queries[0].reply.raw.replace('rgb:', 'rgb;'); e.run.cases[0].reads[0].queries[0].reply.raw = e.frames[2].queries[0].reply.raw; }, /Malformed color reply/],
    ['a reply with changed channels', e => { e.frames[2].queries[0].reply.channels16 = [1, 2, 3]; e.run.cases[0].reads[0].queries[0].reply.channels16 = [1, 2, 3]; }, /Invalid owned color capture or query binding/],
    ['a changed feed list', e => { e.run.cases[3].feeds[2] = '\x1b]12;?\x1b\\'; }, /feeds do not match the pinned contract/],
    ['a changed barrier list', e => { e.run.cases[3].barriers[4] = 'not-a-barrier'; }, /Invalid extensions.osc112-reset-cursor graded capture record/],
    ['a wrong read kind', e => { e.run.cases[0].reads[2].kind = 'cells'; }, /Wrong extensions.osc104-reset-palette read kind/],
    ['a cursor the frame does not show', e => { e.run.cases[0].reads[1].cursor = [0, 0]; }, /cursor differs from its read frame/],
    ['a pinned result the replies do not give', e => { e.run.cases[1].pinned.observation = { outcome: 'unsupported', evidence: 'behavior' }; }, /does not match its observed evidence/],
    ['a missing pinned result', e => { delete e.run.cases[2].pinned; }, /Missing pinned extensions.osc111-reset-bg grading result/],
    ['a read frame of another case', e => { e.frames[20].caseId = 'extensions.osc104-reset-palette'; }, /belongs to another case|Invalid|plan/],
]) test('the report refuses ' + label, () => refuses(mutate, pattern));

test('the report needs its retained captures', () => {
    const evidence = syntheticColorResetEvidence();
    assert.throws(() => buildColorResetGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter), /needs its retained captures/);
    void rehash;
});
