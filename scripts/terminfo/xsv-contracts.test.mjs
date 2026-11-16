import assert from 'node:assert/strict';
import { test } from 'node:test';
import { xsvContracts, xsvCaseIds, xsvPinnedScripts, replayXsvPinned, XtermSavedModesModel, xsvPlanSeed, xsvFinish, xsvBaseline, xsvRestoreBaseline, validateXsvFeeds, xsvFixture, xsvProbeRow }
    from './xsv-contracts.mjs';
import { syntheticXsvGrade, syntheticXsvExpectation } from './fixtures/xsv-evidence.mjs';
import { planFeeds, planReads } from './graded-plan.mjs';

const esc = '\x1b';
const byId = Object.fromEntries(xsvContracts.map(item => [item.caseId, item]));
const group = (caseId, role) => byId[caseId].groups.find(item => item.role === role);
/** Change the hand-written expectation of one role only. */
const change = (caseId, role, edit) => (id, at, expected) => (id === caseId && at === role ? edit(expected) : null);

test('the contracts cover exactly the two pinned cases with unique roles, bounded requests and a restored baseline after every group', () => {
    assert.deepEqual([...xsvCaseIds], ['modes.xtsave', 'modes.xtrestore']);
    for (const contract of xsvContracts) {
        assert.equal(new Set(contract.roles).size, contract.roles.length, `${contract.caseId} roles are unique`);
        assert.equal(contract.roles[contract.pinnedRead], 'pinned');
        for (const item of contract.groups) {
            assert.ok(item.request.length <= 64, `${contract.caseId}/${item.role} request fits the 64-byte exchange limit`);
            for (const feed of item.setup) assert.ok(feed.length <= 64, `${contract.caseId}/${item.role} setup feed is short`);
            assert.ok(item.after.endsWith(xsvRestoreBaseline), `${contract.caseId}/${item.role} ends in the baseline`);
        }
        assert.equal(planReads(contract), contract.roles.length, 'one read per role');
        assert.deepEqual(contract.plan.at(-1), { feed: xsvRestoreBaseline });
    }
    // The whole suite stays inside the owner observer's capture budget (64 per suite, start, bind and finish included).
    assert.ok(xsvContracts.reduce((sum, contract) => sum + planReads(contract), 0) + 3 <= 64);
});

test('every case starts from the baseline modes and an erased probe row, so a later case never inherits an earlier case\'s state', () => {
    for (const contract of xsvContracts) {
        const [first] = contract.plan;
        for (const mode of ['?6l', '?69l', '?7h', '?25h']) assert.ok(first.feed.includes(`${esc}[${mode}`), `${contract.caseId} reseed sets ${mode}`);
        assert.ok(first.feed.includes(`${esc}[${xsvProbeRow + 1};1H${esc}[2K`), `${contract.caseId} reseed erases row ${xsvProbeRow + 1}`);
        assert.ok(first.feed.indexOf(`${esc}[2K`) < first.feed.indexOf(`${esc}[2;6r`), 'the erase precedes the margins and the home');
        assert.ok(first.feed.endsWith(`${esc}[1;1H`), 'the reseed homes the cursor');
    }
});

test('the exact pinned sequences are the pinned bytes', () => {
    assert.deepEqual(xsvPinnedScripts['modes.xtsave'].termless, [['feedCapture', `${esc}[?7s`, 'replies'], ['feedCapture', `${esc}[c`, 'barrier'], ['feed', `${esc}[?7r`]]);
    assert.deepEqual(xsvPinnedScripts['modes.xtsave'].terminal, [['write', `${esc}[?7s`], ['cursor'], ['write', `${esc}[?7r`]]);
    assert.deepEqual(xsvPinnedScripts['modes.xtrestore'].termless, [['feed', `${esc}[?7s`], ['feedCapture', `${esc}[?7r`, 'replies'], ['feedCapture', `${esc}[c`, 'barrier']]);
    assert.deepEqual(xsvPinnedScripts['modes.xtrestore'].terminal, [['write', `${esc}[?7s`], ['write', `${esc}[?7r`], ['cursor']]);
    // The captured pinned request of each case is exactly its pinned sequence (the fence appends the DA1 the pinned probe also sends).
    assert.equal(group('modes.xtsave', 'pinned').request, `${esc}[?7s`);
    assert.ok(group('modes.xtsave', 'pinned').after.startsWith(`${esc}[?7r`));
    assert.deepEqual(group('modes.xtrestore', 'pinned').setup, [`${esc}[?7s`]);
    assert.equal(group('modes.xtrestore', 'pinned').request, `${esc}[?7r`);
});

test('the pinned replay is always inconclusive and never supported or unsupported', () => {
    const barrier = `${esc}[?62;4;22c`;
    for (const caseId of xsvCaseIds) {
        const silent = replayXsvPinned(caseId, 'termless', { replies: '', barrier });
        assert.deepEqual(silent, { pass: false, response: barrier, observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'consumed',
            note: 'DA1 responsiveness does not measure saved/restored stack state' } });
        const noisy = replayXsvPinned(caseId, 'termless', { replies: `${esc}[?7;1$y`, barrier });
        assert.equal(noisy.observation.reason, 'invalid-reply');
        assert.equal(noisy.observation.outcome, 'inconclusive');
        const terminal = replayXsvPinned(caseId, 'terminal', { position: { row: 1, col: 1 } });
        assert.deepEqual(terminal, { pass: false, response: '{"row":1,"col":1}', observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'consumed',
            note: 'Cursor responsiveness does not measure saved/restored state' } });
        assert.equal(replayXsvPinned(caseId, 'terminal', { position: null }).observation.reason, 'no-response');
    }
    assert.throws(() => replayXsvPinned('modes.nope', 'termless'), /Unknown xterm saved-mode case/);
    assert.throws(() => replayXsvPinned('modes.xtsave', 'both'), /Unknown pinned path/);
});

test('the model oracle demands the hand-written state of the fixture for every role', () => {
    const names = { 7: 'wrap', 6: 'origin', 69: 'margin', 25: 'cursor' };
    for (const contract of xsvContracts) {
        for (const item of contract.groups) {
            const hand = { wrap: true, origin: false, margin: false, cursor: true, ...syntheticXsvExpectation(contract.caseId, item.role) };
            for (const [mode, key] of Object.entries(names)) assert.equal(item.expect[mode], hand[key], `${contract.caseId}/${item.role} ${key}`);
            assert.equal(item.probe, hand.probe === true, `${contract.caseId}/${item.role} probe character`);
            if (item.cursor === 'edge') assert.deepEqual(hand.at?.[0], xsvProbeRow);
            else if (item.cursor !== null) assert.deepEqual(item.cursor, hand.at ?? [0, 0], `${contract.caseId}/${item.role} cursor`);
        }
    }
});

test('the model follows the documented cache: one level per mode, listed modes only, a second save replaces the first', () => {
    const model = new XtermSavedModesModel();
    assert.deepEqual(model.live, xsvBaseline);
    model.save(7); model.put(7, false); model.put(25, false);
    model.restore(7);
    assert.equal(model.live[7], true);
    assert.equal(model.live[25], false, 'only the listed mode was restored');
    model.put(7, false); model.save(7); model.put(7, true); model.restore(7);
    assert.equal(model.live[7], false, 'the second save replaced the first');
    model.put(7, true); model.restore(7);
    assert.equal(model.live[7], false, 'a restore leaves the cache filled');
    model.save(9999); model.restore(9999);
    assert.throws(() => model.restore(6), /restored with nothing saved/);
    model.rebase();
    assert.deepEqual(model.live, xsvBaseline);
});

test('every case grades supported on the hand-written state', () => {
    for (const caseId of xsvCaseIds) assert.deepEqual(syntheticXsvGrade(caseId), { outcome: 'supported' }, caseId);
});

test('a mode that does not return to its saved value on a protocol group is unsupported, in both polarities', () => {
    const cases = [];
    for (const caseId of xsvCaseIds) {
        cases.push([caseId, 'wrap-restores-default', expected => ({ ...expected, wrap: false })], [caseId, 'wrap-restores-changed', expected => ({ ...expected, wrap: true })],
            [caseId, 'origin-restores-default', expected => ({ ...expected, origin: true })], [caseId, 'origin-restores-changed', expected => ({ ...expected, origin: false })],
            [caseId, 'margin-mode-restores-default', expected => ({ ...expected, margin: true })], [caseId, 'margin-mode-restores-changed', expected => ({ ...expected, margin: false })],
            [caseId, 'cursor-restores-default', expected => ({ ...expected, cursor: false })], [caseId, 'cursor-restores-changed', expected => ({ ...expected, cursor: true })],
            [caseId, 'only-listed-modes', expected => ({ ...expected, cursor: true })], [caseId, 'several-modes', expected => ({ ...expected, wrap: true })],
            [caseId, 'pinned', expected => ({ ...expected, wrap: false })]);
    }
    cases.push(['modes.xtsave', 'second-save-replaces', expected => ({ ...expected, wrap: false })]);
    for (const [caseId, role, edit] of cases) {
        const grade = syntheticXsvGrade(caseId, undefined, null, change(caseId, role, edit));
        assert.equal(grade.outcome, 'unsupported', `${caseId}/${role}`);
        assert.match(grade.note, new RegExp(role));
    }
});

test('a stray reply, a moved cursor, a changed seed cell, margins, or probe row is unsupported on a protocol group', () => {
    const stray = (caseId, role) => (id, at, read) => (id === caseId && at === role ? { ...read, replies: [`${esc}[?7;1$y`] } : null);
    assert.equal(syntheticXsvGrade('modes.xtsave', undefined, stray('modes.xtsave', 'wrap-restores-default')).outcome, 'unsupported');
    assert.equal(syntheticXsvGrade('modes.xtrestore', undefined, stray('modes.xtrestore', 'pinned')).outcome, 'unsupported');
    assert.equal(syntheticXsvGrade('modes.xtsave', undefined, null, change('modes.xtsave', 'not-scosc', expected => ({ ...expected, at: [4, 4] }))).outcome, 'unsupported',
        'a save that replaced the SCO cursor');
    assert.equal(syntheticXsvGrade('modes.xtrestore', undefined, null, change('modes.xtrestore', 'not-decstbm-or-scorc', expected => ({ ...expected, at: [2, 2] }))).outcome, 'unsupported',
        'a restore that restored the SCO cursor');
    const edited = (caseId, role, edit) => (id, at, read) => (id === caseId && at === role ? { ...read, screen: edit(read.screen) } : null);
    const wiped = edited('modes.xtsave', 'cursor-restores-default', screen => ({ ...screen, lines: screen.lines.map((line, row) => (row === 1 ? '' : line)) }));
    assert.equal(syntheticXsvGrade('modes.xtsave', undefined, wiped).outcome, 'unsupported', 'the seed text outside the probe row changed');
    const margins = edited('modes.xtrestore', 'wrap-restores-default', screen => ({ ...screen, bufferState: { ...screen.bufferState, scrollTop: 0, scrollBottom: screen.rows - 1 } }));
    assert.equal(syntheticXsvGrade('modes.xtrestore', undefined, margins).outcome, 'unsupported', 'the sequence was taken for DECSTBM');
    const narrowed = edited('modes.xtsave', 'margin-mode-restores-default', screen => ({ ...screen, horizontalMargins: { ...screen.horizontalMargins, leftMargin: 2 } }));
    assert.equal(syntheticXsvGrade('modes.xtsave', undefined, narrowed).outcome, 'unsupported', 'the horizontal margins changed');
    const dirty = edited('modes.xtsave', 'only-listed-modes', screen => ({ ...screen, lines: screen.lines.map((line, row) => (row === xsvProbeRow ? 'x' : line)) }));
    assert.equal(syntheticXsvGrade('modes.xtsave', undefined, dirty).outcome, 'unsupported', 'a cell on the probe row nothing wrote');
});

test('the wrap probe reads the stored cell and the pending wrap, not only the flag', () => {
    for (const caseId of xsvCaseIds) {
        for (const role of ['wrap-effect-on', 'wrap-effect-off']) {
            const item = group(caseId, role);
            assert.equal(item.probe, true);
            assert.equal(item.cursor, 'edge');
            assert.ok(item.request.endsWith('z'), `${caseId}/${role} writes the probe character last`);
            const pending = syntheticXsvGrade(caseId, undefined, null, change(caseId, role, expected => ({ ...expected, pending: !expected.pending })));
            assert.equal(pending.outcome, 'unsupported', `${caseId}/${role} pending wrap`);
            assert.match(pending.note, /pending wrap/);
            const missing = syntheticXsvGrade(caseId, undefined, null, change(caseId, role, expected => ({ ...expected, probe: false })));
            assert.equal(missing.outcome, 'unsupported', `${caseId}/${role} probe character`);
        }
    }
});

test('a failed control is inconclusive, never a verdict', () => {
    for (const caseId of xsvCaseIds) {
        for (const role of ['modes-change', 'baseline']) {
            const grade = syntheticXsvGrade(caseId, undefined, null, change(caseId, role, expected => (role === 'baseline' ? { ...expected, wrap: false } : { ...expected, origin: false })));
            assert.equal(grade.outcome, 'inconclusive', `${caseId}/${role}`);
            assert.match(grade.note, /Setup control failed/);
        }
    }
});

test('policy groups never decide a verdict', () => {
    const policies = ['unlisted-values', 'restore-twice', 'cursor-1048', 'origin-restore-homes'];
    for (const caseId of xsvCaseIds) {
        for (const role of policies) {
            assert.equal(group(caseId, role).kind, 'policy');
            const grade = syntheticXsvGrade(caseId, undefined, null, change(caseId, role, expected => ({ ...expected, wrap: !(expected.wrap ?? true), at: [5, 5] })));
            assert.deepEqual(grade, { outcome: 'supported' }, `${caseId}/${role}`);
        }
    }
});

test('the seed, finish and feed validation are exact', () => {
    const runId = 'b'.repeat(32);
    assert.ok(xsvPlanSeed(runId).includes('NTILDE_XSV_' + runId));
    assert.throws(() => xsvPlanSeed('short'), /Invalid xterm saved-mode fixture run identity/);
    assert.ok(xsvFinish(runId).endsWith('\x1b[?1049l') && xsvFinish(runId).includes(`${esc}[?7h`));
    const contract = byId['modes.xtsave'];
    validateXsvFeeds(contract, planFeeds(contract));
    assert.throws(() => validateXsvFeeds(contract, planFeeds(contract).slice(1)), /do not match/);
    assert.equal(xsvFixture.minimum.rows, 8);
    assert.equal(xsvProbeRow, 7);
});

test('the first failure in execution order decides: a protocol mismatch is not masked by a later control it damaged', () => {
    // A mode the save left changed (a protocol mismatch) is read back in a later control too, so the first failure must be the one reported.
    const protocol = syntheticXsvGrade('modes.xtsave', undefined, null, (id, role, expected) => (id === 'modes.xtsave' && role === 'pinned' ? { ...expected, wrap: false } : null));
    assert.equal(protocol.outcome, 'unsupported');
    assert.match(protocol.note, /pinned/);
    const control = syntheticXsvGrade('modes.xtsave', undefined, null, (id, role, expected) => (id === 'modes.xtsave' && role === 'modes-change' ? { ...expected, wrap: true } : null));
    assert.equal(control.outcome, 'inconclusive', 'a control that fails before any protocol group makes the case inconclusive');
    assert.match(control.note, /modes-change/);
});

test('each mode contributes a default and a changed polarity group in both cases', () => {
    for (const caseId of xsvCaseIds) {
        for (const name of ['wrap', 'origin', 'margin-mode', 'cursor']) {
            assert.equal(group(caseId, `${name}-restores-default`).kind, 'protocol');
            assert.equal(group(caseId, `${name}-restores-changed`).kind, 'protocol');
        }
    }
    const restores = byId['modes.xtsave'].groups.filter(item => item.role.endsWith('-restores-changed'));
    for (const item of restores) assert.ok(item.request.includes('s') && item.request.includes('r'), `${item.role} saves and restores in its request`);
    for (const item of byId['modes.xtrestore'].groups.filter(entry => entry.role.endsWith('-restores-changed'))) {
        assert.match(item.request, /^\x1b\[\?\d+r$/, `${item.role} restores alone in its request`);
        assert.ok(item.setup.some(feed => /\x1b\[\?\d+s/.test(feed)), `${item.role} saves in its setup`);
    }
});

test('probe cells an earlier case left on the probe row make the seed unproven, never a verdict', () => {
    for (const caseId of xsvCaseIds) {
        const grade = syntheticXsvGrade(caseId, undefined, (id, at, read) => {
            if (at !== 'initial') return null;
            return { ...read, screen: { ...read.screen, lines: read.screen.lines.map((line, row) => (row === xsvProbeRow ? 'abc' : line)) } };
        });
        assert.equal(grade.outcome, 'inconclusive', caseId);
        assert.match(grade.note, /attributed seed/);
    }
});
