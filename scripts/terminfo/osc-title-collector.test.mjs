import test from 'node:test';
import assert from 'node:assert/strict';
import { oscTitleCases } from './osc-title-contracts.mjs';
import { collectOscTitle, oscTitlePrerequisiteReason, oscTitleTermlessContext } from './osc-title-collector.mjs';
import { oscTitleCaseIds } from './osc-title-contracts.mjs';
import { adaptOscTitleCollector, oscTitleSeams } from './osc-title-grading-collector.mjs';

test('adapter refuses changed or duplicated pinned definitions', () => {
    const source = oscTitleSeams.join('\n') + '\nexport const ntildeTitleStackAdapter = Object.freeze({});\n';
    assert.ok(adaptOscTitleCollector(source).startsWith(source));
    for (const seam of oscTitleSeams) {
        assert.throws(() => adaptOscTitleCollector(source.replace(seam, 'changed')), /seam changed/);
        assert.throws(() => adaptOscTitleCollector(source + seam), /seam changed/);
    }
});

test('pinned contexts reject altered bytes and wrong read order', () => {
    for (const [id, spec] of Object.entries(oscTitleCases)) {
        const ctx = oscTitleTermlessContext(id, 'before', 'after');
        if (spec.code !== 2) assert.equal(ctx.getTitle(), 'before');
        assert.throws(() => ctx.feed(spec.fixture + 'x'), /feed/);
        ctx.feed(spec.fixture);
        assert.equal(ctx.getTitle(), 'after');
        assert.throws(() => ctx.getTitle(), /order/);
        assert.throws(() => ctx.feed(spec.fixture), /feed/);
    }
    assert.throws(() => oscTitleTermlessContext('other', 'before', 'after'), /facts/);
});

test('only typed initial unavailability can skip collection', async () => {
    const args = { config: { runId: 'a'.repeat(32) }, fence: { write() { throw new Error('unexpected write'); } },
        gradingModule: { ntildeOscTitleAdapter: { probeIds: oscTitleCaseIds } } };
    for (const code of ['screenDetailsUnavailable', 'paneMetadataUnavailable', 'metadataGeometryUnavailable']) {
        const owner = async () => { throw Object.assign(new Error('unavailable'), { code }); };
        assert.deepEqual(await collectOscTitle({ ...args, owner }), { status: 'unavailable', reason: code });
        const partial = async () => { throw Object.assign(new Error('failure after partial responses'), { code }); };
        await assert.rejects(collectOscTitle({ ...args, owner: partial }), /after partial/);
    }
    await assert.rejects(collectOscTitle({ ...args, owner: async () => { throw new Error('lost identity'); } }), /lost identity/);
});

test('a restored prerequisite proceeds and a typed unavailable prerequisite is propagated', () => {
    assert.equal(oscTitlePrerequisiteReason(true, false, null), null);
    assert.equal(oscTitlePrerequisiteReason(false, true, 'paneMetadataUnavailable'), 'paneMetadataUnavailable');
    assert.throws(() => oscTitlePrerequisiteReason(false, false, null), /must restore/);
    assert.throws(() => oscTitlePrerequisiteReason(false, true, 'unexpected'), /refusal/);
});
