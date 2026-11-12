import test from 'node:test';
import assert from 'node:assert/strict';
import { oscTitleCases, oscTitleExperiments } from './osc-title-contracts.mjs';

test('independent pinned fixtures and controls have fresh, distinct baselines', () => {
    const experiments = oscTitleExperiments('0123456789abcdef0123456789abcdef');
    assert.equal(experiments.length, 24);
    assert.equal(new Set(experiments.map(item => item.baseline.title)).size, 24);
    assert.equal(new Set(experiments.map(item => item.baseline.icon)).size, 24);
    for (const [id, definition] of Object.entries(oscTitleCases)) {
        const owned = experiments.filter(item => item.owner === id);
        assert.equal(owned[0].plan[3].feed, definition.fixture);
        assert.equal(owned[0].role, 'pinned');
        assert.ok(owned.slice(1).every(item => item.role === 'control'));
        for (const item of owned) {
            assert.notEqual(item.baseline.title, item.baseline.icon);
            if (definition.code === 1) assert.equal(item.expected.title, item.baseline.title);
            if (definition.code === 2) assert.equal(item.expected.icon, item.baseline.icon);
        }
    }
    assert.throws(() => oscTitleExperiments('stale'), /identity/);
});
