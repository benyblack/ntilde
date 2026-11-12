import assert from 'node:assert/strict';
import { test } from 'node:test';
import { xtgettcapContracts, xtgettcapFixture, xtgettcapRequest, oracleReplies, replayXtgettcapPinned, xtgettcapRefusal,
    xtgettcapMaxNames, xtgettcapPlanSeed, xtgettcapReset, xtgettcapReseed, xtgettcapSeed, pinned, gradeXtgettcapCase,
    xtgettcapObservation } from './xtgettcap-contracts.mjs';
import { syntheticXtgettcapScreens } from './fixtures/xtgettcap-evidence.mjs';
import { planFeeds } from './graded-plan.mjs';

const contract = xtgettcapContracts[0];
const st = '\x1b\\';

test('the pinned fixture is the exact DCS + q 544e ST request read from the bundle', () => {
    assert.equal(pinned.query, '\x1bP+q544e\x1b\\');
    assert.equal(pinned.expected, 'complete XTGETTCAP status 1 for TN with even-length hex value and ST');
    assert.equal(xtgettcapRequest('544e'), pinned.query);
    assert.deepEqual(contract.pinnedFeeds, [pinned.query]);
    assert.equal(contract.pinnedRead, contract.roles.indexOf('pinned'));
});

test('the pinned replay mirrors deviceReplyResult for this spec', () => {
    const refused = replayXtgettcapPinned(xtgettcapRefusal);
    assert.equal(refused.pass, false);
    assert.deepEqual(refused.observation, { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'query',
        note: 'Requested setting or name refused; other settings or names unmeasured' });
    assert.equal(refused.assertions, undefined);

    const value = replayXtgettcapPinned('\x1bP1+r544e=323536' + st);
    assert.equal(value.pass, true);
    assert.deepEqual(value.observation, { outcome: 'supported', evidence: 'query' });
    assert.deepEqual(value.assertions, [{ kind: 'positive', expected: pinned.expected, observed: '\x1bP1+r544e=323536' + st }]);

    // Case-insensitive value hex and an odd-digit value that cannot be the valid frame.
    assert.equal(replayXtgettcapPinned('\x1bP1+r544e=32Ab' + st).pass, true);
    const odd = replayXtgettcapPinned('\x1bP1+r544e=32353' + st);
    assert.equal(odd.pass, false);
    assert.equal(odd.observation.reason, 'invalid-reply', 'an unframable 1+r body is the malformed case');

    // A reply that only follows the primary DA is not a reply to this request.
    const afterDa = replayXtgettcapPinned('\x1b[?62;4;22c' + xtgettcapRefusal);
    assert.equal(afterDa.observation.reason, 'no-response');

    assert.deepEqual(replayXtgettcapPinned('').observation, { outcome: 'inconclusive', reason: 'no-response', evidence: 'query' });
    assert.deepEqual(replayXtgettcapPinned().observation, { outcome: 'inconclusive', reason: 'no-response', evidence: 'query' }, 'an absent capture is the empty default');
});

test('the reply oracle is written from the documented table, not the parser', () => {
    assert.deepEqual(oracleReplies('436f'), ['\x1bP1+r436f=323536' + st]);
    assert.deepEqual(oracleReplies('636f6c6f7273'), ['\x1bP1+r636f6c6f7273=323536' + st]);
    assert.deepEqual(oracleReplies('524742'), ['\x1bP1+r524742' + st], 'RGB is a flag: no = value');
    assert.deepEqual(oracleReplies('5463'), ['\x1bP1+r5463' + st]);
    assert.deepEqual(oracleReplies('536d756c78'), ['\x1bP1+r536d756c78=1B5B343A25703125646D' + st], 'a string value is uppercase hex of its bytes');
    assert.deepEqual(oracleReplies('436f;544e;5463;436f'), ['\x1bP1+r436f=323536' + st, xtgettcapRefusal, '\x1bP1+r5463' + st, '\x1bP1+r436f=323536' + st],
        'one reply per name, in order, per-name outcomes');
    assert.deepEqual(oracleReplies('544e;436f'), [xtgettcapRefusal, '\x1bP1+r436f=323536' + st], 'the Q12 witness list');
    assert.deepEqual(oracleReplies('436F'), ['\x1bP1+r436F=323536' + st], 'the name is echoed as spelled');
    assert.deepEqual(oracleReplies('636f'), [xtgettcapRefusal], 'names are case-sensitive');
    assert.deepEqual(oracleReplies('6b63757531'), [xtgettcapRefusal], 'kcuu1 is inherited from xterm-256color');
    assert.deepEqual(oracleReplies('6e6f742d612d6361706162696c697479'), [xtgettcapRefusal], 'a controlled unknown');
});

test('the reply oracle refuses an invalid list as a whole, with exactly one refusal', () => {
    for (const list of ['', '5', '436', 'zz', '436g', ' 436f', '436f;', ';436f', '436f;;5463', '436f;5', '1b5b', '4320', '43c3',
        Array.from({ length: xtgettcapMaxNames + 1 }, () => '5463').join(';'), '41'.repeat(33)]) {
        assert.deepEqual(oracleReplies(list), [xtgettcapRefusal], JSON.stringify(list));
    }
    assert.deepEqual(oracleReplies('5'.repeat(64)), [xtgettcapRefusal], 'a 64-hex-digit name decodes to 32 U characters and is the longest accepted (then refused as unknown)');
    assert.deepEqual(oracleReplies('41'.repeat(32) + '41'), [xtgettcapRefusal], 'a 33-character name is refused whole');
    // Ms is deliberately outside the contract: the oracle never advertises it.
    assert.equal(xtgettcapFixture.advertisedNames.includes('Ms'), false);
});

test('the plan sends bounded requests in a fixed order with the pinned request first', () => {
    const feeds = planFeeds(contract);
    assert.equal(feeds[0], xtgettcapReseed, 'the plan begins with the static attributed reseed');
    assert.equal(feeds[1], pinned.query, 'the first exchange is the exact pinned request');
    assert.equal(feeds.includes('\x1bP+q436f;544e;5463;436f' + st), true);
    assert.equal(feeds.includes('\x1bP+q' + st), true, 'the empty list is its own malformed request');
    for (const feed of feeds.slice(1)) {
        assert.ok(feed.length <= 64, `the fence exchange bound: ${feed.length}`);
        assert.ok(feed.startsWith('\x1bP+q') && feed.endsWith(st));
    }
    // Every malformed request is followed by a recovery request that must still be answered exactly.
    const order = contract.roles;
    for (const malformed of ['malformed-odd', 'malformed-hex', 'malformed-empty', 'malformed-mixed']) {
        const next = order[order.indexOf(malformed) + 1];
        assert.match(next, /^recovery-/, 'a malformed request is immediately followed by a verified recovery');
    }
    assert.equal(order.indexOf('recovery-pinned') > order.indexOf('malformed-empty'), true);
});

test('the measured seed binds the nonce at home over an attributed screen with margins', () => {
    const seed = xtgettcapSeed('NTILDE_XG_x');
    assert.ok(seed.includes('\x1b[2;6r'), 'margins 2..6');
    assert.ok(seed.endsWith('\x1b[1;1HNTILDE_XG_x'), 'the nonce is the last write, at home');
    const planSeed = xtgettcapPlanSeed('a'.repeat(32));
    assert.ok(planSeed.startsWith(xtgettcapReset));
    assert.ok(planSeed.endsWith('NTILDE_XG_' + 'a'.repeat(32)));
    assert.throws(() => xtgettcapPlanSeed('short'), /Invalid XTGETTCAP fixture run identity/);
});

test('the grade refuses to conclude from an unmeasured seed', () => {
    const good = syntheticXtgettcapScreens();
    const observation = xtgettcapObservation(contract, good.map(read => read.screen), good.map(read => read.replies), 'NTILDE_XG_' + 'a'.repeat(32));
    assert.equal(gradeXtgettcapCase(contract, observation).outcome, 'supported');
    const broken = structuredClone(observation);
    broken.initial.cursor = [2, 0];
    const graded = gradeXtgettcapCase(contract, broken);
    assert.equal(graded.outcome, 'inconclusive');
    assert.match(graded.note, /attributed seed .* was not measured/);
});
