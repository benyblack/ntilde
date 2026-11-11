// Frozen real-App publication. Synthetic fixtures do not replace this measurement; every digest below is checked against the
// committed evidence. The publication is split into reviewable files; the test reassembles the exact journal order and re-derives
// the report from the retained journal, run log and the 43 original PNGs.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { buildColorResetGradingReport } from './color-reset-grading.mjs';
import { colorResetAdapterId } from './color-reset-grading-collector.mjs';
import { colorResetSuiteId, colorResetContracts, colorResetCaseIds } from './color-reset-contracts.mjs';
import { colorResetCounts } from './color-reset-receipt.mjs';
import { benchmarkSuites } from './qualification-inventory.mjs';

const dirUrl = new URL('../../docs/qa/terminfo/2026-11-11-runtime-color-reset/', import.meta.url);
const output = fileURLToPath(dirUrl);
const read = name => JSON.parse(readFileSync(new URL(name, dirUrl)));
const load = () => {
    const summary = read('summary.json'), run = read('run-log.json'), common = read('journal-common.json'), header = common.reportHeader;
    const snapshots = [common.frames.start, common.frames.bind];
    for (const contract of colorResetContracts) {
        for (let index = 0; index < contract.roles.length; index++) {
            const journal = read(`journal-${contract.caseId}-read${index}.json`);
            assert.equal(journal.caseId, contract.caseId);
            assert.equal(journal.read, index);
            snapshots.push(journal.frame);
        }
    }
    snapshots.push(common.frames.finish);
    const cases = colorResetCaseIds.map(id => read(`case-${id}.json`));
    return { summary, run, report: { ...header, cases, snapshots } };
};
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const cases = () => Object.fromEntries(load().report.cases.map(item => [item.caseId.slice('extensions.'.length), item]));
const reply = (code, rgb) => `\x1b]${code};rgb:${rgb}\x1b\\`;
const experiment = (item, id) => item.observed[id];
const replies = (item, id) => experiment(item, id).stages.map(stage => stage.replies.map(entry => entry.reply));

test('the published evidence re-derives exactly from its retained journal, run log and original renders', () => {
    const { summary, run, report } = load();
    const derived = buildColorResetGradingReport(report.snapshots, run, summary.context.runId, report.adapter, { contractsVersion: report.contractsVersion, output });
    assert.ok(JSON.stringify(report) === JSON.stringify(derived), 'the published report differs from its re-derivation');
    assert.equal(report.suiteId, colorResetSuiteId);
    assert.equal(report.adapter.id, colorResetAdapterId);
    assert.equal(report.adapter.derivedFromAdapterId, 'ntilde-owned-left-right-margins-grading-v1');
    assert.equal(report.snapshots.length, 3 + colorResetContracts.reduce((sum, contract) => sum + contract.roles.length, 0));
    assert.equal(report.snapshots.length, 47);
    assert.deepEqual(readdirSync(new URL('crst-pixels/', dirUrl)).sort(), report.snapshots.map(frame => frame.pixel.file.split('/')[1]).sort());
});

test('all four pinned cases: the pinned termless code is supported, the independent effect contract agrees, the pinned terminal path is inconclusive by design', () => {
    const { report } = load();
    assert.deepEqual(report.cases.map(item => item.caseId), colorResetCaseIds);
    for (const item of report.cases) {
        assert.equal(item.pinned.observation.outcome, 'supported', item.caseId);
        assert.equal(item.pinned.observation.evidence, 'behavior');
        assert.equal(item.contractResult.outcome, 'supported');
        assert.equal(item.conclusion.outcome, 'supported');
        assert.equal(item.conclusion.pinnedTermless, 'supported');
        assert.equal(item.conclusion.pinnedTerminal, 'inconclusive-by-design');
        assert.deepEqual([item.pinnedTerminal.outcome, item.pinnedTerminal.evidence, item.pinnedTerminal.byDesign], ['inconclusive', 'none', true]);
        assert.equal(item.divergence, undefined, 'the pinned and effect verdicts agree');
        assert.equal(item.contractResult.experiments.length, item.caseId.endsWith('palette') ? 4 : 3);
        for (const experiment of item.contractResult.experiments) {
            assert.equal(experiment.outcome, 'supported', `${item.caseId} ${experiment.id}`);
            assert.ok(experiment.controls.every(entry => entry.passed) && experiment.assertions.every(entry => entry.passed));
        }
    }
    assert.deepEqual(colorResetCounts(report), { supported: 4 });
});

test('the pinned fixture was replayed byte-exactly: the BEL spellings, the query three times, the set and the reset once each', () => {
    const { report } = load();
    const pinned = {
        'osc104-reset-palette': ['\x1b]4;0;?\x07', '\x1b]4;0;rgb:aa/bb/cc\x07', '\x1b]104;0\x07'], 'osc110-reset-fg': ['\x1b]10;?\x07', '\x1b]10;rgb:aa/bb/cc\x07', '\x1b]110\x07'],
        'osc111-reset-bg': ['\x1b]11;?\x07', '\x1b]11;rgb:aa/bb/cc\x07', '\x1b]111\x07'], 'osc112-reset-cursor': ['\x1b]12;?\x07', '\x1b]12;rgb:aa/bb/cc\x07', '\x1b]112\x07'],
    };
    for (const [id, [query, set, reset]] of Object.entries(pinned)) {
        const item = cases()[id];
        assert.deepEqual(item.pinnedFeeds, [query, set, query, reset, query]);
        const at = item.feeds.findIndex((bytes, index) => bytes === query && item.feeds[index + 1] === set);
        assert.deepEqual(item.feeds.slice(at, at + 5), item.pinnedFeeds, 'nothing else was written between the pinned bytes');
    }
});

test('baseline, override and reset replies of the pinned fixture: the reset returns the measured configured-theme baseline in every case', () => {
    const items = cases();
    assert.deepEqual(replies(items['osc104-reset-palette'], 'pinned'), [[reply('4;0', '0000/0000/0000')], [reply('4;0', 'aaaa/bbbb/cccc')], [reply('4;0', '0000/0000/0000')]].map(list => list.map(text => text.replace(/\x1b/g, String.raw`\e`))));
    for (const [id, code, baseline] of [['osc110-reset-fg', 10, 'd3d3/d3d3/d3d3'], ['osc111-reset-bg', 11, '0000/0000/0000'], ['osc112-reset-cursor', 12, 'ffff/ffff/ffff']]) {
        assert.deepEqual(replies(items[id], 'pinned').flat(), [reply(code, baseline), reply(code, 'aaaa/bbbb/cccc'), reply(code, baseline)].map(text => text.replace(/\x1b/g, String.raw`\e`)), id);
    }
    assert.deepEqual(items['osc104-reset-palette'].observed.pinned.stages.map(stage => stage.binding.paletteOverrideIndices), [[], [0], []]);
    assert.deepEqual(items['osc110-reset-fg'].observed.pinned.stages.map(stage => stage.binding.foregroundOverride), [false, true, false]);
    assert.deepEqual(items['osc111-reset-bg'].observed.pinned.stages.map(stage => stage.binding.backgroundOverride), [false, true, false]);
    assert.deepEqual(items['osc112-reset-cursor'].observed.pinned.stages.map(stage => [stage.theme.cursorOverride, stage.theme.cursor]), [[false, '#ffffff'], [true, '#aabbcc'], [false, '#ffffff']]);
});

test('individual and whole-palette resets: unrelated slots stay overridden, and every one of the 256 slots returns', () => {
    const item = cases()['osc104-reset-palette'];
    const slots = experiment(item, 'individual-slots').stages.map(stage => stage.binding.paletteOverrideIndices);
    assert.deepEqual(slots, [[], [1, 9, 16, 200, 255], [9, 16, 200, 255], [16, 255], []]);
    const whole = experiment(item, 'whole-palette').stages.map(stage => stage.binding.paletteOverrideIndices.length);
    assert.deepEqual(whole, [0, 256, 0], 'a partial whole-palette reset could not pass');
    const apart = experiment(item, 'isolation').stages;
    assert.deepEqual(apart.map(stage => stage.binding.paletteOverrideIndices), [[], [5, 6], [6], []]);
    assert.ok(apart.slice(1).every(stage => stage.binding.foregroundOverride && stage.binding.backgroundOverride && stage.theme.cursorOverride), 'palette resets left the dynamic colors overridden');
    const render = item.contractResult.experiments.find(entry => entry.id === 'whole-palette');
    assert.ok(render.assertions.some(entry => /complete theme, derived values included, equals the baseline/.test(entry.description) && entry.passed));
});

test('the other overrides survive a dynamic-color reset, and SGR 39/49 neither stands in for the reset nor is changed by it', () => {
    const items = cases();
    const flags = (id, name) => experiment(items[id], 'isolation').stages.map(stage => [stage.binding.foregroundOverride, stage.binding.backgroundOverride, stage.theme.cursorOverride]);
    assert.deepEqual(flags('osc110-reset-fg'), [[false, false, false], [true, true, true], [false, true, true]]);
    assert.deepEqual(flags('osc111-reset-bg'), [[false, false, false], [true, true, true], [true, false, true]]);
    assert.deepEqual(flags('osc112-reset-cursor'), [[false, false, false], [true, true, true], [true, true, false]]);
    for (const id of ['osc110-reset-fg', 'osc111-reset-bg', 'osc112-reset-cursor']) {
        assert.deepEqual(experiment(items[id], 'isolation').stages.map(stage => stage.binding.paletteOverrideIndices), [[], [5], [5]], `${id}: the unrelated palette slot stays overridden`);
    }
    for (const id of ['osc110-reset-fg', 'osc111-reset-bg']) {
        const overrides = experiment(items[id], 'sgr-default').stages.map(stage => [stage.binding.foregroundOverride, stage.binding.backgroundOverride]);
        const at = id === 'osc110-reset-fg' ? 0 : 1;
        assert.deepEqual(overrides.map(entry => entry[at]), [false, true, true, false], 'SGR 39/49 left the runtime override in place; the OSC reset cleared it');
    }
});

test('the cursor shape and visibility are unchanged by the cursor-color reset, and the hidden cursor stays hidden', () => {
    const item = cases()['osc112-reset-cursor'];
    for (const id of ['pinned', 'isolation']) {
        assert.ok(experiment(item, id).stages.every(stage => stage.cursor.visible === true && stage.cursor.style === 0 && stage.cursor.row === 8 && stage.cursor.col === 8), id);
    }
    assert.ok(experiment(item, 'hidden-beam').stages.every(stage => stage.cursor.visible === false && stage.cursor.style === 1));
    const hidden = experiment(item, 'hidden-beam').stages;
    assert.equal(new Set(hidden.map(stage => stage.pixelSha256)).size, 1, 'a hidden cursor changes no pixel');
    assert.deepEqual(hidden.map(stage => stage.theme.cursorOverride), [false, true, false]);
});

test('the reset renders are byte-identical to their baseline renders where no override remains', () => {
    for (const item of Object.values(cases())) {
        // Only experiments that end with no runtime override left (the isolation experiments keep two, the SGR ones also write text).
        for (const [id, record] of Object.entries(item.observed)) {
            if (!['pinned', 'individual-slots', 'whole-palette'].includes(id)) continue;
            const stages = record.stages;
            assert.equal(stages.at(-1).pixelSha256, stages[0].pixelSha256, `${item.caseId} ${id}`);
            assert.notEqual(stages[1].pixelSha256, stages[0].pixelSha256, `${item.caseId} ${id}: the override changed the render`);
        }
    }
});

test('the receipt binds the run, adapters and counts, and every predecessor and finding reconfirm in the same run', async () => {
    const { summary, run, report } = load();
    const { receipt } = summary;
    assert.equal(receipt.schemaVersion, 42);
    assert.equal(receipt.colorResetEvidence.runId, summary.context.runId);
    assert.deepEqual(receipt.colorResetEvidence.counts, colorResetCounts(report));
    assert.equal(receipt.colorResetEvidence.adapterBundleSha256, summary.context.colorResetBundleSha256);
    assert.equal(receipt.colorResetEvidence.sha256, hash(JSON.stringify(report, null, 2) + '\n'));
    assert.equal(receipt.colorResetEvidence.runSha256, hash(JSON.stringify(run) + '\n'));
    assert.equal(summary.retained.colorResetJournalSha256, hash(report.snapshots.map(frame => JSON.stringify(frame) + '\n').join('')));
    assert.equal(summary.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v35');
    assert.equal(receipt.fixtureRemoved, true);
    const predecessors = benchmarkSuites.slice(0, benchmarkSuites.findIndex(item => item.suite === 'color-reset'));
    const expected = [];
    for (const suite of predecessors) expected.push(...(await import(new URL(suite.module, import.meta.url)))[suite.exports[2]]);
    const unique = new Set(Object.values(summary.sameRun).flatMap(suite => suite.cases.map(item => item.caseId)));
    assert.deepEqual([...unique].sort(), [...new Set(expected)].sort());
    assert.equal(Object.keys(summary.sameRun).length, predecessors.length);
    for (const [name, suite] of Object.entries(summary.sameRun)) {
        if (suite.cases.some(item => item.effectContract)) assert.ok(suite.cases.every(item => item.effectContract === 'supported'), name);
        else assert.ok(suite.cases.every(item => item.outcome === 'supported'), name);
    }
    for (const id of colorResetCaseIds) assert.ok(!unique.has(id));
    assert.deepEqual(summary.queryCounts, { matched: 33, mismatched: 7 }, 'the query suite reconfirms with the same frozen mismatches');
    assert.equal(summary.truecolorFinding.cases.length, 10);
    assert.equal(summary.underlineResolved.cases.length, 3);
});

test('the stock run keeps all four cases inconclusive (the pinned terminal path never decides) and its counts are the measured ones', () => {
    const { summary } = load();
    assert.deepEqual(summary.stockCollector.counts, { inconclusive: 167, supported: 87, unsupported: 1 });
    for (const id of colorResetCaseIds) assert.deepEqual([summary.stockCollector.probes[id].outcome, summary.stockCollector.probes[id].evidence], ['inconclusive', 'none'], id);
});

test('revisions, attempts, redactions and file sizes', () => {
    const { summary } = load();
    assert.match(summary.appRevision, /^[0-9a-f]{40}$/);
    assert.match(summary.scriptsRevision, /^[0-9a-f]{40}$/);
    assert.ok(summary.attempts.length >= 1);
    assert.equal(summary.attempts.at(-1).runId, summary.context.runId);
    assert.ok(summary.attempts.slice(0, -1).every(attempt => attempt.receiptCompleted !== undefined && attempt.superseded === true && attempt.reason.length > 20), 'superseded attempts keep their reasons');
    assert.ok(!summary.context.executable.startsWith('/'), 'no private absolute path is published');
    for (const name of readdirSync(output)) if (name.endsWith('.json')) assert.ok(readFileSync(new URL(name, dirUrl), 'utf8').split('\n').length <= 1500, name);
});
