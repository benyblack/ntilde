import assert from 'node:assert/strict';
import { test } from 'node:test';
import { xcsContracts, xcsCaseIds, xcsPinnedScripts, replayXcsPinned, resolveDemand, XtermColorStackModel, xcsPlanSeed, xcsFinish, xcsColorReset, validateXcsFeeds, xcsFixture } from './xcs-contracts.mjs';
import { syntheticXcsGrade, syntheticXcsExchanges } from './fixtures/xcs-evidence.mjs';
import { planFeeds, planReads } from './graded-plan.mjs';

const esc = '\x1b';
const byId = Object.fromEntries(xcsContracts.map(item => [item.caseId, item]));

test('the contracts cover exactly the two pinned cases with unique roles and bounded requests', () => {
    assert.deepEqual([...xcsCaseIds], ['modes.xtpushcolors', 'modes.xtpopcolors']);
    for (const contract of xcsContracts) {
        assert.equal(new Set(contract.roles).size, contract.roles.length, `${contract.caseId} roles are unique`);
        assert.equal(contract.roles[contract.pinnedRead], 'pinned');
        for (const group of contract.groups) assert.ok(group.request.length <= 64, `${contract.caseId}/${group.role} request fits the 64-byte exchange limit`);
        assert.equal(planReads(contract), contract.roles.length, 'one read per role');
        assert.deepEqual(contract.plan.at(-1), { feed: xcsColorReset }, 'every case ends with the explicit color reset');
    }
});

test('the exact pinned sequences are the pinned bytes', () => {
    assert.deepEqual(xcsPinnedScripts['modes.xtpushcolors'].termless, [['feedCapture', `${esc}[#P`, 'replies'], ['feedCapture', `${esc}[c`, 'barrier'], ['feed', `${esc}[#Q`]]);
    assert.deepEqual(xcsPinnedScripts['modes.xtpushcolors'].terminal, [['write', `${esc}[#P`], ['cursor'], ['write', `${esc}[#Q`]]);
    assert.deepEqual(xcsPinnedScripts['modes.xtpopcolors'].termless, [['feed', `${esc}[#P`], ['feedCapture', `${esc}[#Q`, 'replies'], ['feedCapture', `${esc}[c`, 'barrier']]);
    assert.deepEqual(xcsPinnedScripts['modes.xtpopcolors'].terminal, [['write', `${esc}[#P`], ['write', `${esc}[#Q`], ['cursor']]);
    // The captured pinned request of each case is exactly its pinned sequence (the fence appends the DA1 the pinned probe also sends).
    assert.equal(byId['modes.xtpushcolors'].groups.find(group => group.role === 'pinned').request, `${esc}[#P`);
    assert.equal(byId['modes.xtpushcolors'].groups.find(group => group.role === 'pinned').after, `${esc}[#Q`);
    assert.deepEqual(byId['modes.xtpopcolors'].groups.find(group => group.role === 'pinned').setup, [`${esc}[#P`]);
    assert.equal(byId['modes.xtpopcolors'].groups.find(group => group.role === 'pinned').request, `${esc}[#Q`);
});

test('the pinned replay is always inconclusive and never supported or unsupported', () => {
    const barrier = `${esc}[?62;4;22c`;
    for (const caseId of xcsCaseIds) {
        const silent = replayXcsPinned(caseId, 'termless', { replies: '', barrier });
        assert.deepEqual(silent, { pass: false, response: barrier, observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'consumed',
            note: 'DA1 responsiveness does not measure saved/restored stack state' } });
        const noisy = replayXcsPinned(caseId, 'termless', { replies: `${esc}[?0;0#Q`, barrier });
        assert.equal(noisy.observation.reason, 'invalid-reply');
        assert.equal(noisy.observation.outcome, 'inconclusive');
        const terminal = replayXcsPinned(caseId, 'terminal', { position: { row: 1, col: 1 } });
        assert.deepEqual(terminal, { pass: false, response: '{"row":1,"col":1}', observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'consumed',
            note: 'Cursor responsiveness does not measure saved/restored state' } });
        assert.equal(replayXcsPinned(caseId, 'terminal', { position: null }).observation.reason, 'no-response');
    }
    assert.throws(() => replayXcsPinned('modes.nope', 'termless'), /Unknown xterm color-stack case/);
    assert.throws(() => replayXcsPinned('modes.xtpushcolors', 'both'), /Unknown pinned path/);
});

test('the model oracle matches the hand-written literal replies of the fixture for every role', () => {
    const base = syntheticXcsExchanges['modes.xtpushcolors'].baseline;
    for (const contract of xcsContracts) {
        for (const group of contract.groups.filter(item => item.shape === null)) {
            assert.deepEqual(resolveDemand(group, base), syntheticXcsExchanges[contract.caseId][group.role], `${contract.caseId}/${group.role}`);
        }
    }
    assert.equal(resolveDemand({ demand: [{ baseline: 1 }] }, undefined), null, 'an unmeasured baseline cannot resolve a demand');
});

test('the model follows the documented stack: LIFO, copy semantics, numbered slots without moving the top', () => {
    const model = new XtermColorStackModel();
    model.set({ bg: '#111111' }); model.push(0);
    model.set({ bg: '#222222' }); model.push(0);
    model.set({ bg: '#333333' });
    model.pop(0); assert.equal(model.colors.bg, '#222222');
    model.pop(0); assert.equal(model.colors.bg, '#111111');
    model.pop(0); assert.equal(model.colors.bg, '#111111', 'a pop with nothing stored below changes nothing');
    model.set({ bg: '#444444' }); model.push(3); model.set({ bg: '#555555' }); model.pop(3);
    assert.equal(model.colors.bg, '#444444');
    assert.deepEqual(model.query(['bg']), [`${esc}]11;rgb:4444/4444/4444${esc}\\`]);
    assert.deepEqual(new XtermColorStackModel().query(['fg', 'p5']), [{ baseline: 0 }, { baseline: 3 }]);
});

test('every case grades supported on the hand-written replies', () => {
    for (const caseId of xcsCaseIds) assert.deepEqual(syntheticXcsGrade(caseId), { outcome: 'supported' }, caseId);
});

test('a wrong restored color, a stray reply or a changed screen on a protocol group is unsupported', () => {
    const wrongColor = (caseId, role) => (id, at, read) => (id === caseId && at === role ? { ...read, replies: [read.replies[0], read.replies[1].replace(/rgb:(.)/, (_, digit) => `rgb:${digit === '0' ? '1' : '0'}`), ...read.replies.slice(2)] } : null);
    const outcome = (caseId, role, mutate = wrongColor(caseId, role)) => syntheticXcsGrade(caseId, undefined, mutate);
    for (const [caseId, role] of [['modes.xtpushcolors', 'pop-recovers-pushed'], ['modes.xtpushcolors', 'snapshot-is-copy'], ['modes.xtpushcolors', 'restore-slot-2'],
        ['modes.xtpopcolors', 'pop-restores-default'], ['modes.xtpopcolors', 'pop-lifo-1'], ['modes.xtpopcolors', 'restore-slot-1']]) {
        assert.equal(outcome(caseId, role).outcome, 'unsupported', `${caseId}/${role}`);
    }
    // A push or pop that answers (a stray reply before the colors) is a mismatch on the silent groups.
    const stray = (caseId, role) => (id, at, read) => (id === caseId && at === role ? { ...read, replies: [`${esc}[?0;0#Q`, ...read.replies] } : null);
    assert.equal(outcome('modes.xtpushcolors', 'push-silent', stray('modes.xtpushcolors', 'push-silent')).outcome, 'unsupported');
    assert.equal(outcome('modes.xtpopcolors', 'pinned', (id, at, read) => (id === 'modes.xtpopcolors' && at === 'pinned' ? { ...read, replies: [`${esc}[?0;0#Q`] } : null)).outcome, 'unsupported');
    // A changed cursor on an exchange is a mismatch.
    const moved = (caseId, role) => (id, at, read) => { if (id === caseId && at === role) read.screen.cursorCol = 3; return id === caseId && at === role ? read : null; };
    assert.equal(outcome('modes.xtpushcolors', 'pinned', moved('modes.xtpushcolors', 'pinned')).outcome, 'unsupported');
});

test('a failed control is inconclusive, never a verdict', () => {
    const broken = (caseId, role) => (id, at, read) => (id === caseId && at === role ? { ...read, replies: read.replies.slice(1) } : null);
    for (const [caseId, role] of [['modes.xtpushcolors', 'set-A'], ['modes.xtpushcolors', 'baseline'], ['modes.xtpopcolors', 'push-A'], ['modes.xtpopcolors', 'store-slot-1']]) {
        const grade = syntheticXcsGrade(caseId, undefined, broken(caseId, role));
        assert.equal(grade.outcome, 'inconclusive', `${caseId}/${role}`);
        assert.match(grade.note, /Setup control failed/);
    }
});

test('policy groups never decide a verdict', () => {
    for (const [caseId, role] of [['modes.xtpushcolors', 'extended-palette'], ['modes.xtpopcolors', 'extended-palette'], ['modes.xtpopcolors', 'pop-empty']]) {
        const wrong = (id, at, read) => (id === caseId && at === role ? { ...read, replies: [] } : null);
        assert.deepEqual(syntheticXcsGrade(caseId, undefined, wrong), { outcome: 'supported' }, `${caseId}/${role}`);
        assert.equal(byId[caseId].groups.find(group => group.role === role).kind, 'policy');
    }
});

test('a pop restores the untouched host default, not the colors in force: the baseline reply is the oracle', () => {
    const stale = (id, at, read) => (id === 'modes.xtpopcolors' && at === 'pop-restores-default' ? { ...read, replies: syntheticXcsExchanges['modes.xtpopcolors']['set-A'] } : null);
    assert.equal(syntheticXcsGrade('modes.xtpopcolors', undefined, stale).outcome, 'unsupported');
    const group = byId['modes.xtpopcolors'].groups.find(item => item.role === 'pop-restores-default');
    assert.deepEqual(group.demand, [{ baseline: 0 }, { baseline: 1 }, { baseline: 2 }, { baseline: 3 }]);
});

test('the seed, finish and feed validation are exact', () => {
    const runId = 'b'.repeat(32);
    assert.ok(xcsPlanSeed(runId).includes('NTILDE_XCS_' + runId));
    assert.throws(() => xcsPlanSeed('short'), /Invalid xterm color-stack fixture run identity/);
    assert.ok(xcsFinish(runId).endsWith('\x1b[?1049l') && xcsFinish(runId).includes(xcsColorReset));
    const contract = byId['modes.xtpushcolors'];
    validateXcsFeeds(contract, planFeeds(contract));
    assert.throws(() => validateXcsFeeds(contract, planFeeds(contract).slice(1)), /do not match/);
    assert.equal(xcsFixture.minimum.rows, 8);
});

test('the first failure in execution order decides: a protocol mismatch is not masked by a later control it damaged', () => {
    // A push that moves the cursor (a protocol mismatch) leaves the cursor moved in every later read, so the next control also differs from the seed state.
    const damaged = (caseId, from) => {
        const order = byId[caseId].roles;
        return (id, role, read) => {
            if (id === caseId && order.indexOf(role) >= order.indexOf(from)) read.screen.cursorCol = 4;
            return id === caseId && order.indexOf(role) >= order.indexOf(from) ? read : null;
        };
    };
    const protocol = syntheticXcsGrade('modes.xtpushcolors', undefined, damaged('modes.xtpushcolors', 'push-silent'));
    assert.equal(protocol.outcome, 'unsupported', 'the push-silent protocol group failed first, before the later set-B control');
    assert.match(protocol.note, /push-silent/);
    const control = syntheticXcsGrade('modes.xtpushcolors', undefined, damaged('modes.xtpushcolors', 'set-A'));
    assert.equal(control.outcome, 'inconclusive', 'a control that fails before any protocol group makes the case inconclusive');
    assert.match(control.note, /set-A/);
});

test('no demand depends on where a numbered store or restore leaves the stack position', () => {
    // xterm's text says numbered operations store/restore "without pushing/popping"; where they leave the top is not documented, so the contract decides only the colors read back after them. Run the
    // contract's own numbered sequences through a model whose numbered operations leave the position alone and one that moves it to the slot (xterm's source, per the production): the replies agree.
    class Stays extends XtermColorStackModel {
        push(slot) { const used = this.used; super.push(slot); if (slot > 0) this.used = used; }
        pop(slot) { const used = this.used; super.pop(slot); if (slot > 0) this.used = used; }
    }
    const sequences = [
        // modes.xtpushcolors: store 2 (A), set B, store 1 (B), set C, restore 2, restore 1.
        [['set', { fg: '#102030' }], ['push', 2], ['set', { fg: '#c0d0e0' }], ['push', 1], ['set', { fg: '#050607' }], ['pop', 2], ['pop', 1]],
        // modes.xtpopcolors: store 1 (A), set B, restore 1.
        [['set', { fg: '#102030' }], ['push', 1], ['set', { fg: '#c0d0e0' }], ['pop', 1]],
    ];
    for (const sequence of sequences) {
        const moves = new XtermColorStackModel(), stays = new Stays(), seen = [];
        for (const [op, value] of sequence) {
            for (const model of [moves, stays]) model[op](value);
            seen.push([moves.query(['fg']), stays.query(['fg'])]);
        }
        for (const [left, right] of seen) assert.deepEqual(left, right);
    }
    for (const contract of xcsContracts) {
        const numbered = contract.groups.filter(group => /^(store|restore)-slot/.test(group.role));
        assert.ok(numbered.length >= 2, `${contract.caseId} decides numbered slots only through the colors read back`);
        for (const group of numbered) assert.match(group.request, /\x1b\[\d+#[PQ]/, `${group.role} uses a numbered spelling`);
        for (const group of numbered) assert.deepEqual(group.request.match(/\x1b\[\d*#[PQ]/g)?.length, 1, 'a numbered group holds exactly one stack operation, and it is the numbered one');
    }
});
