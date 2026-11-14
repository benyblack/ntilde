import test from 'node:test';
import assert from 'node:assert/strict';
import { collectNotify, exposeNotifyFacts, notifyPrerequisiteReason, replayNotifyPinned, replayOsc777Pinned } from './notify-collector.mjs';
import { notifyCaseIds, osc777CaseId, osc777PinnedWrites, osc99CaseId } from './notify-contracts.mjs';
import { adaptNotifyCollector, notifySeams } from './notify-grading-collector.mjs';

/** The unchanged pinned OSC 777 terminal path, reproduced as a stub: two writes, a cursor query, unmeasuredCellResult. */
const pinned777 = async (id, ctx) => {
    ctx.write(osc777PinnedWrites[0]); ctx.write(osc777PinnedWrites[1]);
    const position = await ctx.queryCursorPosition();
    return { probeId: id, pass: false, response: `${position.row};${position.col}`, note: 'Cursor response does not measure OSC 777 notification delivery',
        observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'query' } };
};
const stubModule = overrides => ({ ntildeNotifyAdapter: { id: 'stub', probeIds: notifyCaseIds }, ntildeNotifyHasTermless: () => false, ntildeRunNotifyTerminal: pinned777, ...overrides });
const experiments = [{ caseId: `${osc777CaseId}/pinned-fixture`, reads: [{ kind: 'before', cursor: [0, 0] }, { kind: 'after', cursor: [0, 0] }] }];

test('the adapter refuses changed, duplicated or missing pinned definitions', () => {
    const source = notifySeams.join('\n') + '\n';
    assert.ok(adaptNotifyCollector(source).startsWith(source));
    for (const seam of notifySeams) {
        assert.throws(() => adaptNotifyCollector(source.replace(seam, 'changed')), /seam changed/);
        assert.throws(() => adaptNotifyCollector(source + seam), /seam changed/);
    }
});

test('the pinned OSC 777 replay serves the journaled cursor one-based and requires exactly the pinned writes', async () => {
    const replay = await replayOsc777Pinned(stubModule(), [0, 0]);
    assert.deepEqual(replay.writes, [...osc777PinnedWrites]); assert.deepEqual(replay.cursor, [1, 1]);
    assert.equal(replay.terminal.response, '1;1'); assert.equal(replay.terminal.observation.outcome, 'inconclusive');
    await assert.rejects(replayOsc777Pinned(stubModule({ ntildeRunNotifyTerminal: async (id, ctx) => { ctx.write('x'); return { probeId: id }; } }), [0, 0]), /replay changed/);
    await assert.rejects(replayOsc777Pinned(stubModule({ ntildeRunNotifyTerminal: async (id, ctx) => ctx.getCell(0, 0) }), [0, 0]), /Unexpected pinned context access/);
    await assert.rejects(replayOsc777Pinned(stubModule({ ntildeRunNotifyTerminal: async () => ({ probeId: 'other' }) }), [0, 0]), /replay changed/);
    await assert.rejects(replayOsc777Pinned(stubModule(), [0]), /Invalid OSC 777 pinned cursor/);
});

test('both pinned records: OSC 777 replayed, OSC 99 identified only; a changed termless presence or a missing fixture is refused', async () => {
    const [first, second] = await replayNotifyPinned(stubModule(), experiments);
    assert.equal(first.caseId, osc777CaseId); assert.equal(second.caseId, osc99CaseId);
    assert.deepEqual([second.termless, second.terminal, second.replay], [null, null, 'stock-collector-and-exact-query-witness']);
    await assert.rejects(replayNotifyPinned(stubModule({ ntildeNotifyHasTermless: () => true }), experiments), /termless presence changed/);
    await assert.rejects(replayNotifyPinned(stubModule(), []), /pinned-fixture experiment is missing/);
});

test('only typed initial unavailability can skip collection', async () => {
    const args = { config: { runId: 'a'.repeat(32) }, fence: { write() { throw new Error('unexpected write'); } }, gradingModule: stubModule() };
    for (const code of ['screenDetailsUnavailable', 'paneMetadataUnavailable', 'metadataGeometryUnavailable']) {
        const owner = async () => { throw Object.assign(new Error('unavailable'), { code }); };
        assert.deepEqual(await collectNotify({ ...args, owner }), { status: 'unavailable', reason: code });
        await assert.rejects(collectNotify({ ...args, owner: async () => { throw Object.assign(new Error('failure after partial responses'), { code }); } }), /after partial/);
    }
    await assert.rejects(collectNotify({ ...args, owner: async () => { throw new Error('lost identity'); } }), /lost identity/);
    await assert.rejects(collectNotify({ ...args, owner: async () => ({}), gradingModule: stubModule({ ntildeNotifyAdapter: { id: 'x', probeIds: [] } }) }), /Changed notification adapter/);
});

test('a restored prerequisite proceeds and a typed unavailable prerequisite is propagated', () => {
    assert.equal(notifyPrerequisiteReason(true, false, null), null);
    assert.equal(notifyPrerequisiteReason(false, true, 'paneMetadataUnavailable'), 'paneMetadataUnavailable');
    assert.throws(() => notifyPrerequisiteReason(false, false, null), /must restore/);
    assert.throws(() => notifyPrerequisiteReason(false, true, 'unexpected'), /refusal/);
});

test('observation facts expose the toast and the focus and opt-in conditions', () => {
    const presentation = { progressOwner: 'p', progressKind: 'Normal', progressPercent: 4, tabText: 't', toastVisible: true, toastTitle: 'a', toastMessage: 'b',
        windowActive: false, windowVisible: true, paneActive: true, focusMode: false, notificationsEnabled: true, progressEnabled: true };
    const facts = exposeNotifyFacts({ metadata: { presentation } });
    assert.deepEqual(facts.toast, { visible: true, title: 'a', message: 'b' });
    assert.deepEqual(facts.focus, { windowActive: false, windowVisible: true, paneActive: true, focusMode: false, notificationsEnabled: true, progressEnabled: true });
});
