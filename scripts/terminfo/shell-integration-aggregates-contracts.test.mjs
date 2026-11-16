import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
    aggregateCaseIds, aggregateEvidenceSets, aggregateEvidenceTag, aggregatePinnedNotes, aggregatesSuiteId, aggregatesVersion,
    declaredCapabilityVariants, osc133Carrier, osc633AggregateEffectContractId, osc633Carrier, semanticPromptsEffectContractId,
} from './shell-integration-aggregates-contracts.mjs';

test('each aggregate declares a complete evidence set over one admitted carrier, with every required case named', () => {
    for (const [caseId, set] of Object.entries(aggregateEvidenceSets)) {
        assert.ok(aggregateCaseIds.includes(caseId), caseId);
        assert.ok(set.carrier.suiteId.startsWith('ntilde-owned-'), caseId);
        assert.match(set.carrier.publication, /^docs\/qa\/terminfo\/[a-z0-9-]+\/$/);
        assert.ok(set.required.length > 0, caseId);
        const requiredIds = new Set(set.required.map(entry => entry.caseId));
        for (const entry of set.required) {
            assert.match(entry.caseId, /^extensions\./, caseId);
            assert.equal(entry.requiredOutcome, 'supported');
        }
        // A pending entry is required evidence whose publication does not exist yet: pending ⊆ required.
        for (const entry of set.pending) {
            assert.match(entry.owner, /^#\d+$/, caseId);
            assert.ok(requiredIds.has(entry.caseId), `${caseId}: ${entry.caseId} is pending without being required`);
        }
        for (const entry of set.excluded) assert.ok(entry.reason.length > 20, caseId);
        assert.ok(['supported', 'inconclusive'].includes(set.outcome), caseId);
    }
});

test('the semantic-prompts aggregate requires the four OSC 133 lifecycle publications and excludes the separately owned P audit', () => {
    const set = aggregateEvidenceSets['extensions.semantic-prompts'];
    assert.deepEqual(set.required.map(entry => entry.caseId), ['extensions.osc133-a', 'extensions.osc133-b', 'extensions.osc133-c', 'extensions.osc133-d']);
    assert.equal(set.carrier, osc133Carrier);
    assert.equal(set.effectContractId, semanticPromptsEffectContractId);
    assert.equal(set.outcome, 'supported');
    assert.deepEqual(set.pending, []);
    assert.match(set.excluded[0].reason, /\(#985\)/);
});

test('the OSC 633 aggregate additionally requires the E/P records, stays inconclusive while they are pending and names their owner', () => {
    const set = aggregateEvidenceSets['extensions.osc-633-vscode'];
    assert.deepEqual(set.required.map(entry => entry.caseId),
        ['extensions.osc633-a', 'extensions.osc633-b', 'extensions.osc633-c', 'extensions.osc633-d', 'extensions.osc633-e', 'extensions.osc633-p']);
    assert.equal(set.carrier, osc633Carrier);
    assert.equal(set.effectContractId, osc633AggregateEffectContractId);
    assert.equal(set.outcome, 'inconclusive');
    assert.deepEqual(set.pending.map(entry => [entry.caseId, entry.owner]), [['extensions.osc633-e', '#983'], ['extensions.osc633-p', '#983']]);
    assert.match(set.authoritativeContract, /^https:\/\/code\.visualstudio\.com\//);
});

test('the pinned aggregates have no fixture bytes and keep their probe notes verbatim', () => {
    const expected = {
        'extensions.semantic-prompts': {
            termless: 'Declared semantic-prompt capability does not establish shell prompt integration',
            terminal: 'No shell prompt integration metadata or behavior readback for OSC 133',
        },
        'extensions.osc-633-vscode': {
            termless: 'Declared semantic-prompt capability does not establish VS Code OSC 633 shell integration',
            terminal: 'No VS Code shell integration metadata or behavior readback for OSC 633',
        },
    };
    assert.deepEqual(aggregatePinnedNotes, expected);
    assert.deepEqual(declaredCapabilityVariants, [false, true]);
    assert.equal(aggregateEvidenceTag, 'admitted-stored-cells-and-pane-metadata-lifecycle-publications');
    assert.equal(aggregatesVersion, 1);
    assert.deepEqual(aggregateCaseIds, ['extensions.osc-633-vscode', 'extensions.semantic-prompts']);
    assert.match(aggregatesSuiteId, /^ntilde-owned-.*-projection-v1$/);
    assert.notEqual(semanticPromptsEffectContractId, osc633AggregateEffectContractId);
    // The projection names no adapter of its own: it reuses the carriers' admitted evidence.
    assert.equal(osc133Carrier.effectContractId, 'ntilde-osc133-shell-lifecycle-effect-v1');
    assert.equal(osc633Carrier.effectContractId, 'ntilde-osc633-shell-lifecycle-effect-v1');
});
