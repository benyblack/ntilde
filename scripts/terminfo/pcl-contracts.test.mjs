import assert from 'node:assert/strict';
import { test } from 'node:test';
import { pclContracts, pclPlanSeed, pclPrefix, pclFinish, pclFixture, pclObservation, gradePclCase, pclCaseIds, pclPinned, pclPinnedScripts, replayPclTermless, replayPclTerminal,
    pclEffectContractId } from './pcl-contracts.mjs';
import { syntheticPclGrade, syntheticPclScreens, syntheticPclExchanges } from './fixtures/pcl-evidence.mjs';

const nonce = 'NTILDE_PCL_' + 'a'.repeat(32);
const gradeWith = (caseId, mutate) => syntheticPclGrade(caseId, undefined, mutate);

test('each contract carries its pinned case with the exact pinned query, the terminal writes and the shared seed', () => {
    assert.deepEqual(pclContracts.map(item => item.caseId), pclCaseIds);
    const [fg, bg] = pclContracts;
    assert.deepEqual([fg.pinnedFeeds, bg.pinnedFeeds], [['\x1b]13;?\x07'], ['\x1b]14;?\x07']]);
    assert.deepEqual(pclPinnedScripts['extensions.osc113-reset-pointer-fg'].terminal, [['write', '\x1b[1;1H\x1b[2K'], ['write', '\x1b]113\x07'], ['queryCursorPosition']]);
    assert.deepEqual(pclPinnedScripts['extensions.osc114-reset-pointer-bg'].terminal, [['write', '\x1b[1;1H\x1b[2K'], ['write', '\x1b]114\x07'], ['queryCursorPosition']]);
    for (const contract of pclContracts) {
        assert.equal(contract.pinnedPath, 'termless');
        assert.ok(contract.pinnedCriterion.includes('neither path can be unsupported'));
        assert.equal(contract.effectContractId, pclEffectContractId);
        assert.equal(contract.roles[0], 'initial');
        assert.equal(contract.roles.length, contract.groups.length + 1);
        assert.equal(contract.groups.find(group => group.role === 'pinned').request, pclPinned[contract.caseId].query);
    }
    assert.equal(typeof pclPlanSeed('a'.repeat(32)), 'string');
    assert.throws(() => pclPlanSeed('short'), /run identity/);
    assert.ok(pclFinish('a'.repeat(32)).includes('\x1b]0;PCL-base-aaaaaaaa'));
    assert.equal(pclPrefix, 'pcl');
});

test('every pointer request demands silence, the colors demand the seeded and chained literals, and every request fits the exchange bound', () => {
    for (const contract of pclContracts) {
        const { setCode, resetCode } = pclPinned[contract.caseId];
        const byRole = Object.fromEntries(contract.groups.map(group => [group.role, group]));
        for (const role of ['pinned', 'query-st', 'query-after-set', 'chain']) {
            assert.deepEqual(byRole[role].demand, [], role);
            assert.equal(byRole[role].kind, 'protocol');
        }
        // Every mutating spelling carries its own five-color read: silence for the pointer sequence itself, the seeded colors right after it.
        for (const role of ['set', 'set-hex', 'reset', 'reset-st', 'reset-argument']) {
            assert.deepEqual(byRole[role].demand, pclFixture.seededReplies, role);
            assert.ok(byRole[role].request.endsWith(pclFixture.colorRead), role);
            assert.equal(byRole[role].kind, 'protocol');
        }
        assert.deepEqual(byRole.recover.demand, [pclFixture.afterChain[2], ...pclFixture.afterChain]);
        assert.ok(byRole.recover.request.endsWith(pclFixture.colorRead));
        assert.deepEqual(byRole.seeded.demand, pclFixture.seededReplies);
        assert.deepEqual(byRole['colors-after-pointer'].demand, pclFixture.seededReplies);
        assert.deepEqual(byRole['colors-after-chain'].demand, pclFixture.afterChain);
        assert.deepEqual(byRole['control-move'].demand, [pclFixture.movedCursor]);
        assert.equal(byRole.seeded.kind, 'control');
        assert.equal(byRole['control-move'].kind, 'control');
        assert.ok(byRole.set.request.includes(`\x1b]${setCode};rgb:aa/bb/cc`));
        assert.ok(byRole.reset.request.includes(`\x1b]${resetCode}\x07`));
        for (const group of contract.groups) assert.ok(group.request.length <= 64, `${contract.caseId} ${group.role} stays within the exchange bound`);
    }
});

test('the seeded and chained literals follow the xterm slot order (pointer slots consume their values)', () => {
    assert.equal(pclFixture.seedColors.length, 5);
    assert.deepEqual(pclFixture.seedColors.map(([code]) => code), ['10', '11', '12', '17', '19']);
    // OSC 12 ; a ; b ; c ; d ; e ; f reaches 17 on its sixth value: 12=a, 13=b, 14=c, 15=d, 16=e, 17=f.
    assert.ok(pclFixture.chainSet.startsWith('\x1b]12;#010203;#040506;#070809;#0a0b0c;#0d0e0f;#101112'));
    assert.ok(pclFixture.afterChain[2].includes('rgb:0101/0202/0303') && pclFixture.afterChain[3].includes('rgb:1010/1111/1212'));
    assert.equal(pclFixture.afterChain[0], pclFixture.seededReplies[0]);
    assert.equal(pclFixture.afterChain[4], pclFixture.seededReplies[4]);
});

test('the pinned termless result is inconclusive without a reply and refuses a complete color reply', () => {
    for (const caseId of pclCaseIds) {
        assert.deepEqual(replayPclTermless(caseId, ''), { pass: false, response: '', observation: { outcome: 'inconclusive', reason: 'no-response', evidence: 'query' } });
        const code = pclPinned[caseId].setCode;
        assert.equal(replayPclTermless(caseId, `\x1b]${code};`).observation.reason, 'invalid-reply');
        assert.throws(() => replayPclTermless(caseId, `\x1b]${code};rgb:11/22/33\x1b\\`), /pinned code would continue/);
        assert.throws(() => replayPclTermless(caseId, `\x1b]${code};RGB:1/2/3\x07`), /pinned code would continue/);
    }
    assert.throws(() => replayPclTermless('extensions.nope', ''), /Unknown pointer-color case/);
    assert.throws(() => replayPclTermless(pclCaseIds[0], null), /reply text/);
});

test('the pinned terminal result is the unverified-effect inconclusive over the journaled cursor', () => {
    const fg = replayPclTerminal(pclCaseIds[0], [0, 0]), bg = replayPclTerminal(pclCaseIds[1], [3, 4]);
    assert.deepEqual(fg.observation, { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'consumed', note: 'Cursor answered; pointer foreground restoration was not measured' });
    assert.equal(bg.note, 'Cursor answered; pointer background restoration was not measured');
    assert.equal(replayPclTerminal(pclCaseIds[0], null).note, 'No cursor response after OSC 113');
    assert.equal(fg.pass, false);
    assert.throws(() => replayPclTerminal(pclCaseIds[0], [1]), /Invalid pointer-color cursor/);
    assert.throws(() => replayPclTerminal('extensions.nope', [0, 0]), /Unknown pointer-color case/);
});

test('a fully ignored run grades unsupported for both cases', () => {
    for (const caseId of pclCaseIds) {
        const verdict = syntheticPclGrade(caseId);
        assert.equal(verdict.outcome, 'unsupported');
        assert.match(verdict.note, /ignored/);
    }
});

test('a reply to a pointer request, a changed color or a changed screen makes the effect inconclusive, never supported', () => {
    for (const caseId of pclCaseIds) {
        const code = pclPinned[caseId].setCode;
        const answered = gradeWith(caseId, (id, role, read) => (id === caseId && role === 'pinned' ? { ...read, replies: [`\x1b]${code};rgb:1111/2222/3333\x1b\\`] } : null));
        assert.equal(answered.outcome, 'inconclusive');
        assert.match(answered.note, /needs its own supported contract/);
        const moved = gradeWith(caseId, (id, role, read) => (id === caseId && role === 'colors-after-pointer' ? { ...read, replies: [...read.replies.slice(0, 2), '\x1b]12;rgb:0000/0000/0000\x1b\\', ...read.replies.slice(3)] } : null));
        assert.equal(moved.outcome, 'inconclusive');
        const shifted = gradeWith(caseId, (id, role, read) => (id === caseId && role === 'colors-after-chain'
            ? { ...read, replies: [...read.replies.slice(0, 3), '\x1b]17;rgb:1212/1212/1212\x1b\\', ...read.replies.slice(4)] } : null));
        assert.equal(shifted.outcome, 'inconclusive');
        const redrawn = gradeWith(caseId, (id, role, read) => (id === caseId && role === 'reset-st' ? { ...read, screen: { ...read.screen, cursorRow: 4 } } : null));
        assert.equal(redrawn.outcome, 'inconclusive');
    }
});

test('a failing control makes the effect inconclusive with the control named', () => {
    for (const caseId of pclCaseIds) {
        const dead = gradeWith(caseId, (id, role, read) => (id === caseId && role === 'control-move' ? { ...read, replies: [] } : null));
        assert.equal(dead.outcome, 'inconclusive');
        assert.match(dead.note, /Setup control failed/);
        const unseeded = gradeWith(caseId, (id, role, read) => (id === caseId && role === 'seeded' ? { ...read, replies: read.replies.slice(1) } : null));
        assert.match(unseeded.note, /Setup control failed/);
    }
});

test('observation building and grading refuse missing or malformed reads', () => {
    const [contract] = pclContracts;
    assert.throws(() => pclObservation(contract, [], [], 'n'), /read captures/);
    assert.throws(() => gradePclCase(contract, null), /read captures/);
    assert.throws(() => gradePclCase(contract, { initial: null }), /read captures/);
    const reads = syntheticPclScreens(contract);
    const observation = pclObservation(contract, reads.map(read => read.screen), reads.map(read => read.replies), nonce);
    assert.equal(gradePclCase(contract, observation).outcome, 'unsupported');
    assert.throws(() => gradePclCase(contract, observation, 2), /Unknown pointer-color contracts revision/);
    assert.deepEqual(Object.keys(syntheticPclExchanges).sort(), ['chain', 'colors-after-chain', 'colors-after-pointer', 'control-move', 'pinned', 'query-after-set', 'query-st', 'recover', 'reset',
        'reset-argument', 'reset-st', 'seeded', 'set', 'set-hex']);
});

test('a color a set changed and a later reset restored is still caught: every mutating request is judged on its own five-color read', () => {
    const answered = (caseId, role, reply) => gradeWith(caseId, (item, name, read) => (item === caseId && name === role
        ? { ...read, replies: [...read.replies.slice(0, 2), reply, ...read.replies.slice(3)] } : null));
    for (const caseId of pclCaseIds) {
        for (const role of ['set', 'set-hex', 'reset', 'reset-st', 'reset-argument']) {
            const changed = answered(caseId, role, '\x1b]12;rgb:0000/0000/0000\x1b\\');
            assert.equal(changed.outcome, 'inconclusive', `${role}: a cursor-color change right after the request is not hidden by the later reads`);
        }
        const chained = gradeWith(caseId, (item, name, read) => (item === caseId && name === 'recover' ? { ...read, replies: [...read.replies.slice(0, 5), '\x1b]19;rgb:0000/0000/0000\x1b\\'] } : null));
        assert.equal(chained.outcome, 'inconclusive', 'the final color read after the recovery queries is judged too');
    }
});
