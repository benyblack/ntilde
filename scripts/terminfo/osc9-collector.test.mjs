import test from 'node:test';
import assert from 'node:assert/strict';
import { collectGradedPlans, planFeeds, planReads } from './graded-plan.mjs';
import { collectOsc9, exposeOsc9Facts, osc9PrerequisiteReason, replayOsc9Pinned } from './osc9-collector.mjs';
import { osc9CaseIds } from './osc9-contracts.mjs';
import { adaptOsc9Collector, osc9Seams } from './osc9-grading-collector.mjs';

const stubModule = overrides => ({ ntildeOsc9Adapter: { id: 'stub', probeIds: osc9CaseIds }, ntildeOsc9HasTermless: id => id === 'extensions.notifications',
    ntildeRunOsc9Termless: (id, ctx) => ({ probeId: id, pass: false, response: JSON.stringify({ declared: ctx.capabilities.extensions.has('osc9') }), note: 'n', observation: { outcome: 'inconclusive' } }),
    ntildeRunOsc9Terminal: async id => ({ probeId: id, pass: false, note: 't', observation: { outcome: 'inconclusive' } }), ...overrides });

test('the adapter refuses changed, duplicated or missing pinned definitions', () => {
    const source = osc9Seams.join('\n') + '\n';
    assert.ok(adaptOsc9Collector(source).startsWith(source));
    for (const seam of osc9Seams) {
        assert.throws(() => adaptOsc9Collector(source.replace(seam, 'changed')), /seam changed/);
        assert.throws(() => adaptOsc9Collector(source + seam), /seam changed/);
    }
});

test('pinned replay serves only the declared capability and refuses any other access or a changed termless presence', async () => {
    const replay = await replayOsc9Pinned(stubModule());
    assert.equal(replay[0].termless, null);
    assert.equal(JSON.parse(replay[1].termless.response).declared, true);
    await assert.rejects(replayOsc9Pinned(stubModule({ ntildeRunOsc9Terminal: async (id, ctx) => ctx.write('x') })), /Unexpected pinned context access/);
    await assert.rejects(replayOsc9Pinned(stubModule({ ntildeOsc9HasTermless: () => true })), /termless presence changed/);
    await assert.rejects(replayOsc9Pinned(stubModule({ ntildeRunOsc9Terminal: async () => ({ probeId: 'other' }) })), /Incomplete pinned/);
});

test('only typed initial unavailability can skip collection', async () => {
    const args = { config: { runId: 'a'.repeat(32) }, fence: { write() { throw new Error('unexpected write'); } }, gradingModule: stubModule() };
    for (const code of ['screenDetailsUnavailable', 'paneMetadataUnavailable', 'metadataGeometryUnavailable']) {
        const owner = async () => { throw Object.assign(new Error('unavailable'), { code }); };
        assert.deepEqual(await collectOsc9({ ...args, owner }), { status: 'unavailable', reason: code });
        await assert.rejects(collectOsc9({ ...args, owner: async () => { throw Object.assign(new Error('failure after partial responses'), { code }); } }), /after partial/);
    }
    await assert.rejects(collectOsc9({ ...args, owner: async () => { throw new Error('lost identity'); } }), /lost identity/);
    await assert.rejects(collectOsc9({ ...args, owner: async () => ({}), gradingModule: stubModule({ ntildeOsc9Adapter: { id: 'x', probeIds: [] } }) }), /Changed OSC 9 adapter/);
});

test('a restored prerequisite proceeds and a typed unavailable prerequisite is propagated', () => {
    assert.equal(osc9PrerequisiteReason(true, false, null), null);
    assert.equal(osc9PrerequisiteReason(false, true, 'paneMetadataUnavailable'), 'paneMetadataUnavailable');
    assert.throws(() => osc9PrerequisiteReason(false, false, null), /must restore/);
    assert.throws(() => osc9PrerequisiteReason(false, true, 'unexpected'), /refusal/);
});

test('the shared driver waits, performs parent actions through the owner and journals their answers', async () => {
    const events = [], contract = { caseId: 'c', plan: [{ feed: 'a' }, { action: 'defocus' }, { read: 'before', kind: 'setup' }, { wait: 5 }, { feed: 'b' }, { read: 'after', kind: 'effect' }] };
    const owner = async (kind, barrier) => { events.push([kind, barrier ?? null]); return kind === 'x-read' ? { screen: { cursorRow: 1, cursorCol: 2 }, facts: { v: 1 } } : kind === 'x-defocus' ? { action: 'defocus' } : { screen: { cursorRow: 0, cursorCol: 0 } }; };
    const fence = { write: async bytes => `barrier-${bytes}` };
    const [result] = await collectGradedPlans({ contracts: [contract], prefix: 'x', reset: 'r', bind: 'bind', finish: 'fin', nonce: 'n', fence, owner, planFeeds, planReads });
    assert.deepEqual(events.map(item => item[0]), ['x-start', 'x-bind', 'x-defocus', 'x-read', 'x-read', 'x-finish']);
    assert.equal(events[2][1], 'barrier-a');
    assert.deepEqual(result.actions, [{ action: 'defocus', response: { action: 'defocus' } }]);
    assert.equal(result.reads.length, 2); assert.deepEqual(result.feeds, ['a', 'b']);
});

test('observation facts expose stored progress, tab text, toast and the focus and opt-in conditions', () => {
    const presentation = { progressOwner: 'p', progressKind: 'Normal', progressPercent: 4, tabText: 't', toastVisible: true, toastTitle: 'a', toastMessage: 'b',
        windowActive: false, windowVisible: true, paneActive: true, focusMode: false, notificationsEnabled: true, progressEnabled: true };
    assert.deepEqual(exposeOsc9Facts({ metadata: { presentation } }), { progress: { owner: 'p', kind: 'Normal', percent: 4 }, tabText: 't', toast: { visible: true, title: 'a', message: 'b' },
        focus: { windowActive: false, windowVisible: true, paneActive: true, focusMode: false, notificationsEnabled: true, progressEnabled: true } });
});
