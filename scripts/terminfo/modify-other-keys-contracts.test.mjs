import assert from 'node:assert/strict';
import { test } from 'node:test';
import { makeModifyOtherKeysCases, modifyOtherKeysBenchmarkIds, modifyOtherKeysPlanId, routedInputV3SuiteId, xtqmodkeysQuery,
    modifyOtherKeysXtgettcapQuery, xtgettcapNotAdvertised, ownedModifyOtherKeysPolicy } from './modify-other-keys-cases.mjs';
import { oracleDisagreements, oracleHex, contractExperiments, gradeModifyOtherKeys, modifyOtherKeysCaseIds, modifyOtherKeysContractsVersion,
    modifyOtherKeysGradedSuiteId, modifyOtherKeysEffectContracts, replayModifyOtherKeysPinned, modifyOtherKeysRecordingContext, pinnedOutcome,
    servedDeclaration, modifyOtherKeysPinned, modifyOtherKeysReport } from './modify-other-keys-contracts.mjs';
import { inputModeReplyHex } from './input-state.mjs';
import { inputAttemptCount } from './input-plan.mjs';
import { queryPriorCounts } from './queries.mjs';

const cases = () => makeModifyOtherKeysCases(40, 140);
const mokCases = () => cases().filter(item => item.roles[modifyOtherKeysCaseIds[0]] !== undefined);
const MOK = modifyOtherKeysCaseIds[0];

test('the v3 plan keeps every routed-input event verbatim and appends the modifyOtherKeys cases', () => {
    const all = cases(), mok = mokCases();
    assert.equal(all.length, 118);
    assert.equal(mok.length, 58);
    assert.ok(all.every((item, index) => index === 0 || all[index - 1].id !== item.id), 'ids are unique');
    assert.ok(mok.every(item => item.id.startsWith('input.mok.')));
    assert.deepEqual([...modifyOtherKeysBenchmarkIds], [MOK]);
    assert.equal(contractExperiments(MOK).length, 58);
    // The routed-input subset carries the same ids, setups, events, literals and roles as the frozen v2 plan, with only the level added.
    assert.equal(oracleDisagreements(all).length, 0);
    // Reader/capture budgets: 64 captures per phase reader include start/bind/finish; the query suite owes every input and image challenge.
    assert.ok(all.length + 3 <= 128 && inputAttemptCount(all.length) <= 256);
    assert.equal(inputAttemptCount(all.length) + 30, queryPriorCounts.at(-1));
});

test('the declaration case proves the child XTGETTCAP refusal ahead of the mode replies', () => {
    const declaration = mokCases()[0];
    assert.equal(declaration.id, 'input.mok.declaration.xtgettcap');
    assert.equal(declaration.roles[MOK], 'label');
    assert.ok(declaration.setup.includes(modifyOtherKeysXtgettcapQuery));
    assert.equal(declaration.controlPrefixHex, Buffer.from(xtgettcapNotAdvertised, 'latin1').toString('hex'));
    assert.equal(declaration.modes.modifyOtherKeysLevel, 0);
});

test('levels 1 and 2 are required subcases of one benchmark id, never two ids', () => {
    const ids = mokCases().map(item => item.id);
    assert.ok(ids.some(id => id.includes('l1.')) && ids.some(id => id.includes('l2.')));
    assert.ok(ids.every(id => !id.includes('other-keys-3')), 'level 3 is out of scope');
    assert.deepEqual(Object.keys(modifyOtherKeysEffectContracts), [MOK]);
    assert.equal(modifyOtherKeysEffectContracts[MOK], 'ntilde-modify-other-keys-effect-v1');
    assert.equal(modifyOtherKeysPlanId, 'v3');
    assert.equal(routedInputV3SuiteId, 'ntilde-owned-routed-input-v3');
    assert.match(ownedModifyOtherKeysPolicy, /XTQMODKEYS/);
    assert.ok(xtqmodkeysQuery === '\x1b[?4m');
});

test('paired key-down/backend-text cases only ride a valid correlated event, and the oracle agrees with every literal', () => {
    const paired = mokCases().filter(item => item.event.kind === 'keyDown' && item.event.text !== undefined);
    assert.deepEqual(paired.map(item => item.id), ['input.mok.l2.space.shift.withText', 'input.mok.l2.shift.a.withText', 'input.mok.l2.normal.a.withText']);
    // Every paired case's declared event is one the fixture validation accepts: shift-only or unmodified, text the key's own US character.
    assert.ok(paired.every(item => item.event.modifiers === 0 || item.event.modifiers === 1));
    for (const item of paired) {
        const down = Buffer.from(item.expectedHex, 'hex').toString('latin1');
        assert.ok(item.expectedHex === Buffer.from(down, 'latin1').toString('hex'));
    }
    assert.equal(oracleDisagreements(mokCases()).length, 0);
});

test('the plan refuses a grid that cannot observe the routed pointer cells', () => {
    assert.throws(() => makeModifyOtherKeysCases(40, 96), /at least 97 columns/);
    assert.equal(makeModifyOtherKeysCases(6, 97).length, 118);
});

const hex = value => Buffer.from(value, 'latin1').toString('hex');
const report = event => hex(modifyOtherKeysReport(event));

test('the independent oracle reproduces the documented legacy controls, level 1 table and level 2 table', () => {
    // Disabled-mode legacy controls.
    assert.equal(report({ kind: 'keyDown', key: 'a', modifiers: 2 }), hex('\x01'));
    assert.equal(report({ kind: 'keyDown', key: 'a', modifiers: 4 }), hex('\x1ba'));
    assert.equal(report({ kind: 'keyDown', key: 'return', modifiers: 1 }), hex('\r'));
    assert.equal(report({ kind: 'keyDown', key: 'tab', modifiers: 1 }), hex('\x1b[Z'));
    assert.equal(report({ kind: 'keyDown', key: 'backspace', modifiers: 1 }), hex('\x7f'));
    assert.equal(report({ kind: 'keyDown', key: 'escape', modifiers: 2 }), hex('\x1b'));
    // Level 1: the X11 control aliases and Alt stay legacy; chords without well-known legacy behavior encode.
    assert.equal(report({ level: 1, kind: 'keyDown', key: 'a', modifiers: 2 }), hex('\x01'));
    assert.equal(report({ level: 1, kind: 'keyDown', key: 'leftBracket', modifiers: 2 }), hex('\x1b'));
    assert.equal(report({ level: 1, kind: 'keyDown', key: 'digit2', modifiers: 2 }), hex('\x00'));
    assert.equal(report({ level: 1, kind: 'keyDown', key: 'slash', modifiers: 2 }), hex('\x1f'));
    assert.equal(report({ level: 1, kind: 'keyDown', key: 'digit2', modifiers: 3 }), hex('\x00'));
    assert.equal(report({ level: 1, kind: 'keyDown', key: 'digit1', modifiers: 2 }), hex('\x1b[27;5;49~'));
    assert.equal(report({ level: 1, kind: 'keyDown', key: 'digit1', modifiers: 3 }), hex('\x1b[27;6;33~'));
    assert.equal(report({ level: 1, kind: 'keyDown', key: 'semicolon', modifiers: 2 }), hex('\x1b[27;5;59~'));
    assert.equal(report({ level: 1, kind: 'keyDown', key: 'a', modifiers: 4 }), hex('\x1ba'));
    assert.equal(report({ level: 1, kind: 'keyDown', key: 'return', modifiers: 1 }), hex('\x1b[27;2;13~'));
    assert.equal(report({ level: 1, kind: 'keyDown', key: 'tab', modifiers: 2 }), hex('\x1b[27;5;9~'));
    assert.equal(report({ level: 1, kind: 'keyDown', key: 'tab', modifiers: 1 }), hex('\x1b[Z'));
    assert.equal(report({ level: 1, kind: 'keyDown', key: 'escape', modifiers: 1 }), hex('\x1b'));
    assert.equal(report({ level: 1, kind: 'keyDown', key: 'backspace', modifiers: 4 }), hex('\x1b\x7f'));
    // Level 2: every Ctrl/Alt chord encodes; Shift-only printables stay text; the documented carve-outs hold.
    assert.equal(report({ level: 2, kind: 'keyDown', key: 'a', modifiers: 2 }), hex('\x1b[27;5;97~'));
    assert.equal(report({ level: 2, kind: 'keyDown', key: 'a', modifiers: 3 }), hex('\x1b[27;6;65~'));
    assert.equal(report({ level: 2, kind: 'keyDown', key: 'a', modifiers: 4 }), hex('\x1b[27;3;97~'));
    assert.equal(report({ level: 2, kind: 'keyDown', key: 'a', modifiers: 5 }), hex('\x1b[27;4;65~'));
    assert.equal(report({ level: 2, kind: 'keyDown', key: 'a', modifiers: 1 }), '');
    assert.equal(report({ level: 2, kind: 'keyDown', key: 'digit2', modifiers: 2 }), hex('\x1b[27;5;50~'));
    assert.equal(report({ level: 2, kind: 'keyDown', key: 'leftBracket', modifiers: 2 }), hex('\x1b[27;5;91~'));
    assert.equal(report({ level: 2, kind: 'keyDown', key: 'return', modifiers: 4 }), hex('\x1b[27;3;13~'));
    assert.equal(report({ level: 2, kind: 'keyDown', key: 'tab', modifiers: 4 }), hex('\x1b[27;3;9~'));
    assert.equal(report({ level: 2, kind: 'keyDown', key: 'tab', modifiers: 1 }), hex('\x1b[Z'));
    assert.equal(report({ level: 2, kind: 'keyDown', key: 'backspace', modifiers: 1 }), hex('\x1b[27;2;127~'));
    assert.equal(report({ level: 2, kind: 'keyDown', key: 'backspace', modifiers: 2 }), hex('\x7f'));
    assert.equal(report({ level: 2, kind: 'keyDown', key: 'backspace', modifiers: 3 }), hex('\x1b[27;6;8~'));
    assert.equal(report({ level: 2, kind: 'keyDown', key: 'escape', modifiers: 2 }), hex('\x1b[27;5;27~'));
    assert.equal(report({ level: 2, kind: 'keyDown', key: 'space', modifiers: 1 }), hex('\x1b[27;2;32~'));
    // Policy exclusions and competing protocols.
    assert.equal(report({ level: 2, kind: 'keyDown', key: 'a', modifiers: 8 }), '');
    assert.equal(report({ level: 2, kind: 'keyDown', key: 'a', modifiers: 6 }), '');
    assert.equal(report({ level: 2, kind: 'keyDown', key: 'a', modifiers: 2, kittyKeyboardFlags: 1 }), hex('\x1b[97;5u'));
    assert.equal(report({ kind: 'keyUp', key: 'a', modifiers: 2 }), '');
    assert.equal(report({ kind: 'text', text: '7' }), hex('7'));
    assert.equal(report({ kind: 'text', text: '7', reserved: true }), '');
    // Unknown keys never invent bytes.
    assert.equal(report({ level: 2, kind: 'keyDown', key: 'left', modifiers: 2 }), '');
});

test('the paired oracle reserves the follow-up text exactly when the key-down is handled', () => {
    const withText = (level, event) => oracleHex({ event, modes: { modifyOtherKeysLevel: level, kittyKeyboardFlags: 0 } });
    assert.equal(withText(2, { kind: 'keyDown', key: 'space', modifiers: 1, text: ' ' }), hex('\x1b[27;2;32~'));
    assert.equal(withText(2, { kind: 'keyDown', key: 'a', modifiers: 1, text: 'A' }), hex('A'));
    assert.equal(withText(2, { kind: 'keyDown', key: 'a', text: 'a' }), hex('a'));
});

test('grading requires every planned experiment in order and grades controls before required mismatches', () => {
    const experiments = contractExperiments(MOK, cases());
    const withMatched = value => experiments.map(item => ({ ...item, matched: typeof value === 'function' ? value(item) : value }));
    assert.deepEqual(gradeModifyOtherKeys(MOK, withMatched(true)), { outcome: 'supported' });
    const requiredIndex = experiments.findIndex(item => item.role === 'required');
    const broken = withMatched(true);
    broken[requiredIndex].matched = false;
    assert.deepEqual(gradeModifyOtherKeys(MOK, broken), { outcome: 'unsupported', failedRequired: [experiments[requiredIndex].id] });
    const controlBroken = withMatched(item => item.role !== 'control');
    assert.deepEqual(gradeModifyOtherKeys(MOK, controlBroken), { outcome: 'inconclusive', reason: 'control-mismatch',
        failedControls: controlBroken.filter(item => item.role === 'control' && !item.matched).map(item => item.id), failedRequired: [] });
    assert.throws(() => gradeModifyOtherKeys(MOK, experiments.slice(1)), /differ from the plan/);
    assert.throws(() => gradeModifyOtherKeys(MOK, experiments, 2), /Unknown/);
    assert.equal(modifyOtherKeysGradedSuiteId, 'ntilde-owned-modify-other-keys-benchmark-graded-v1');
    assert.equal(modifyOtherKeysContractsVersion, 1);
});

test('the pinned replay and recording context reproduce the pinned code exactly, and refuse every other read', () => {
    const pinned = modifyOtherKeysPinned[MOK];
    assert.match(pinned.criterion, /declaredModifyOtherKeys/);
    assert.equal(pinned.note, 'No generated input event and encoded report were validated');
    assert.deepEqual(pinned.feeds, []);
    for (const served of [{ declaredModifyOtherKeys: false }, { declaredModifyOtherKeys: true }, { declaredModifyOtherKeys: undefined }]) {
        const context = modifyOtherKeysRecordingContext(served);
        assert.equal(context.capabilities.extensions.has('modifyOtherKeys'), served.declaredModifyOtherKeys === true);
        assert.equal(context.readCount, 1);
        const replay = replayModifyOtherKeysPinned(served);
        assert.equal(replay.pass, false);
        assert.equal(replay.response, JSON.stringify({ declaredModifyOtherKeys: served.declaredModifyOtherKeys === true }));
        assert.equal(pinnedOutcome(replay), 'inconclusive');
        assert.throws(() => context.feed('\x1b'), /must not feed/);
        assert.throws(() => context.getMode('modifyOtherKeys'), /reads no mode/);
        assert.throws(() => context.getCell(0, 0), /must not read cells/);
        assert.throws(() => context.getCursor(), /must not read the cursor/);
        assert.throws(() => context.capabilities.extensions.has('hyperlinks'), /uncontracted capability/);
    }
});

test('the served declaration comes from the child XTGETTCAP capture, and anything else is refused', () => {
    assert.deepEqual(servedDeclaration(Buffer.from('\x1bP0+r\x1b\\' + '\x1b[?66;2$y', 'latin1').toString('hex')),
        { declaredModifyOtherKeys: false, source: 'child-xtgettcap-modifyOtherKeys' });
    assert.deepEqual(servedDeclaration(Buffer.from('\x1bP1+r6d6f646966794f746865724b657973\x1b\\', 'latin1').toString('hex')),
        { declaredModifyOtherKeys: true, source: 'child-xtgettcap-modifyOtherKeys' });
    assert.throws(() => servedDeclaration(Buffer.from('\x1b[?66;2$y', 'latin1').toString('hex')), /answered no XTGETTCAP declaration/);
    assert.throws(() => servedDeclaration(undefined), /answered no XTGETTCAP declaration/);
});
