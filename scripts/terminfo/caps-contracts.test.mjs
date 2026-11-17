import assert from 'node:assert/strict';
import { test } from 'node:test';
import { capsContracts, capsPlanSeed, capsPrefix, capsFinish, capsFixture, capsObservation, gradeCapsCase } from './caps-contracts.mjs';

test('the contract carries the pinned case with its exact pinned feed and the shared seed', () => {
    assert.deepEqual(capsContracts.map(item => item.caseId), ['extensions.osc1337-capabilities']);
    const [contract] = capsContracts;
    assert.equal(contract.pinnedFeeds.length, 1);
    assert.equal(contract.pinnedFeeds[0], '\x1b]1337;RequestCapabilities\x07');
    assert.equal(contract.pinnedPath, 'termless');
    assert.ok(contract.pinnedCriterion.includes('OSC 1337 ; RequestCapabilities BEL'));
    assert.ok(contract.plan[0].feed.includes('NTILDE') === false, 'the plan feeds are static; the nonce is appended by the seed');
    assert.equal(typeof capsPlanSeed('a'.repeat(32)), 'string');
    assert.throws(() => capsPlanSeed('short'), /run identity/);
    assert.ok(capsFinish('a'.repeat(32)).includes('\x1b]0;CAP-base-aaaaaaaa'));
    assert.equal(capsPrefix, 'caps');
});

test('every protocol group demands the complete declared reply and every policy group demands silence', () => {
    const [contract] = capsContracts;
    const byRole = Object.fromEntries(contract.groups.map(group => [group.role, group]));
    assert.deepEqual(byRole.pinned.demand, [capsFixture.expectedReply]);
    assert.deepEqual(byRole['st-terminated'].demand, [capsFixture.expectedReply]);
    assert.deepEqual(byRole.repeat.demand, [capsFixture.expectedReply, capsFixture.expectedReply]);
    assert.deepEqual(byRole['silent-forms'].demand, []);
    assert.deepEqual(byRole['silent-assignment'].demand, []);
    assert.deepEqual(byRole['silent-suffix'].demand, []);
    assert.deepEqual(byRole['silent-case'].demand, []);
    assert.deepEqual(byRole.recover.demand, [capsFixture.expectedReply]);
    assert.deepEqual(byRole.pinned.kind, 'protocol');
    for (const role of ['silent-forms', 'silent-assignment', 'silent-suffix', 'silent-case']) assert.deepEqual(byRole[role].kind, 'policy');
    // Each silent group's request carries its payloads exactly once, BEL-terminated, within the fence's 64-byte exchange bound.
    const grouped = [['silent-forms', capsFixture.silentPayloads.slice(0, 2)], ['silent-assignment', capsFixture.silentPayloads.slice(2, 3)],
        ['silent-suffix', capsFixture.silentPayloads.slice(3, 4)], ['silent-case', capsFixture.silentPayloads.slice(4)]];
    for (const [role, payloads] of grouped) {
        for (const payload of payloads) assert.ok(byRole[role].request.includes(`\x1b]${payload}\x07`), payload);
        assert.ok(byRole[role].request.length <= 64, `${role} stays within the exchange bound`);
    }
});

test('an observation built from the fixture grades supported, and tampered reads refuse', () => {
    const [contract] = capsContracts;
    assert.throws(() => capsObservation(contract, [], [], 'n'), /read captures/);
    assert.throws(() => gradeCapsCase(contract, null), /read captures/);
    assert.throws(() => gradeCapsCase(contract, { initial: null }), /read captures/);
});
