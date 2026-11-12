import test from 'node:test';
import assert from 'node:assert/strict';
import { oscTitleCases } from './osc-title-contracts.mjs';
import { oscTitleTermlessContext } from './osc-title-collector.mjs';
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
