import assert from 'node:assert/strict';
import { test } from 'node:test';
import { kclContracts, kclFixture, kclPinnedQuery, kclSuiteId, kclEffectContractId, kclNoncePrefix, kclPrefix, kclPlanSeed, kclFinish, kclReads, kclFeeds, validateKclFeeds,
    parseKittyColor, parseOsc21, replayKclPinned, resolveDemand, baselineOf, kclContractsVersion, gradeKclCase } from './kcl-contracts.mjs';
import { syntheticKclGrade, syntheticKclExchanges } from './fixtures/kcl-evidence.mjs';

const [contract] = kclContracts;
const caseId = contract.caseId;
const st = '\x1b\\';
const da = '\x1b[?62;4;22c';
const osc21 = (...fields) => `\x1b]21;${fields.join(';')}${st}`;
const roles = contract.groups.map(group => group.role);
/** A mutation of one role's replies and/or screen. */
const change = (role, edit) => (id, current, read) => (current === role ? { ...read, ...edit(read) } : null);

test('identity, plan size and the exchange limits', () => {
    assert.equal(kclSuiteId, 'ntilde-owned-kitty-color-graded-v1');
    assert.equal(kclEffectContractId, 'ntilde-kitty-color-control-effect-v1');
    assert.equal(kclNoncePrefix, 'NTILDE_KC_');
    assert.equal(kclPrefix, 'kcl');
    assert.equal(kclContractsVersion, 1);
    assert.equal(caseId, 'extensions.osc21-kitty-color');
    assert.ok(kclReads(contract) <= 61, 'at most 61 plan reads (64 captures per suite with start, bind and finish)');
    assert.equal(kclReads(contract), contract.groups.length + 1);
    assert.ok(kclFeeds(contract).length > contract.groups.length);
    for (const group of contract.groups) assert.ok(group.request.length <= 64, `${group.role} request fits one fenced exchange`);
    assert.equal(new Set(roles).size, roles.length, 'roles are unique');
    assert.deepEqual(contract.roles, ['initial', ...roles]);
    assert.equal(kclFeeds(contract).filter((_, index) => contract.plan.filter(step => step.feed)[index].capture).length, contract.groups.length);
});

test('the pinned group is the exact pinned request on the untouched host default, read before any override', () => {
    const pinned = contract.groups.find(group => group.role === 'pinned');
    assert.equal(pinned.request, kclPinnedQuery);
    assert.equal(kclPinnedQuery, '\x1b]21;foreground=?\x1b\\');
    assert.deepEqual(pinned.setup, []);
    assert.equal(contract.pinnedRead, contract.roles.indexOf('pinned'));
    assert.ok(contract.pinnedRead < contract.roles.indexOf('setup-A-main'), 'before any color is set');
    assert.deepEqual(contract.pinnedFeeds, [kclPinnedQuery]);
});

test('roles: the baseline and setup groups are controls, the key and encoding groups decide, the quirks are policy', () => {
    const kind = Object.fromEntries(contract.groups.map(group => [group.role, group.kind]));
    for (const role of ['baseline-main', 'baseline-selection', 'baseline-palette', 'setup-A-main', 'setup-A-rest', 'selection-set']) assert.equal(kind[role], 'control', role);
    for (const role of ['pinned', 'key-foreground', 'combined-order', 'bel-terminated', 'encoding-rgb-4', 'unknown-padding', 'reset-bare-key', 'restore-palette']) assert.equal(kind[role], 'protocol', role);
    for (const role of ['encoding-sharp-4', 'encoding-rgbi-range', 'empty-value', 'color-name', 'alpha-suffix', 'set-only-silent', 'oversize-dropped', 'field-cap', 'key-case', 'unmodeled-keys', 'interleaved-unknown']) assert.equal(kind[role], 'policy', role);
    assert.ok(contract.groups.filter(group => group.kind === 'protocol').length >= 30);
});

test('the oracle reads numeric colors as kitty\'s text defines them', () => {
    assert.equal(parseKittyColor('#112233'), '#112233');
    assert.equal(parseKittyColor('#ABCDEF'), '#abcdef');
    assert.equal(parseKittyColor('#3a7'), '#30a070', 'most significant bits: #3a7 is #3000a0007000');
    assert.equal(parseKittyColor('#123456789'), '#124578');
    assert.equal(parseKittyColor('rgb:f/0/8'), '#ff0088');
    assert.equal(parseKittyColor('rgb:12/ab/ef'), '#12abef');
    assert.equal(parseKittyColor('rgb:1234/5678/9abc'), '#12569a');
    assert.equal(parseKittyColor('rgbi:1/0/0.2'), '#ff0033');
    assert.equal(parseKittyColor('rgbi:2/0/0'), '#ff0000', 'intensities outside 0..1 are clipped');
    for (const refused of ['', 'red', '#12345', '#1234567', '#112233@0.5', 'rgb:1/2', 'rgb:12345/0/0', 'rgbi:x/0/0', '#gg0000']) assert.equal(parseKittyColor(refused), null, refused);
});

test('OSC 21 replies parse into their key/value fields and nothing else', () => {
    assert.deepEqual(parseOsc21(osc21('foreground=rgb:00/00/00', 'selection_foreground=')), [['foreground', 'rgb:00/00/00'], ['selection_foreground', '']]);
    for (const refused of [null, '', 'x', '\x1b]21;foreground=rgb:00/00/00', '\x1b]21;foreground=rgb:00/00/00\x07', osc21('novalue'), osc21('=x'), `${osc21('a=b')}tail`, `x${osc21('a=b')}`, '\x1b]20;a=b' + st]) {
        assert.equal(parseOsc21(refused), null, JSON.stringify(refused));
    }
});

test('pinned replay is the pinned code: supported for a valid or undefined foreground, inconclusive otherwise, never unsupported', () => {
    const supported = replayKclPinned(caseId, 'termless', { replies: osc21('foreground=rgb:cc/cc/cc') });
    assert.deepEqual(Object.keys(supported), ['pass', 'response', 'note', 'observation', 'assertions']);
    assert.deepEqual(supported.observation, { outcome: 'supported', evidence: 'query', note: 'Foreground query recognized; setting colors and visible rendering were not tested' });
    assert.equal(supported.assertions[0].observed, osc21('foreground=rgb:cc/cc/cc'));
    assert.equal(replayKclPinned(caseId, 'termless', { replies: osc21('foreground=') }).pass, true, 'an explicitly undefined value passes');
    assert.equal(replayKclPinned(caseId, 'termless', { replies: osc21('foreground=RGB:1/2/3') }).pass, true, 'the pinned value pattern is case-insensitive');
    assert.equal(replayKclPinned(caseId, 'termless', { replies: osc21('foreground=rgb:1/2') }).pass, false);
    assert.equal(replayKclPinned(caseId, 'termless', { replies: '\x1b]21;foreground=rgb:1/2/3\x07' }).pass, true, 'a BEL terminator is a frame');
    for (const replies of [osc21('foreground=red'), osc21('foreground=rgb:12345/0/0'), osc21('foreground=rgb:0/0/0', 'foreground=rgb:0/0/0'), osc21('background=rgb:0/0/0'), osc21('unknown=Zm9v')]) {
        const result = replayKclPinned(caseId, 'termless', { replies });
        assert.equal(result.pass, false);
        assert.deepEqual(result.observation, { outcome: 'inconclusive', evidence: 'query', note: 'OSC 21 replied without one verifiable foreground value; no support conclusion' });
        assert.equal('assertions' in result, false);
    }
    for (const replies of ['', '\x1b[?62;4;22c', '\x1b]21;foreground=rgb:0/0/0']) {
        const none = replayKclPinned(caseId, 'termless', { replies });
        assert.deepEqual(none.observation, { outcome: 'inconclusive', evidence: 'query', reason: 'no-response', note: 'No OSC 21 reply; color rendering was not tested' });
        assert.equal(none.response, replies);
    }
});

test('the terminal path searches replies plus the DA barrier and grades only the matched frame', () => {
    const frame = osc21('foreground=rgb:cc/cc/cc');
    const matched = replayKclPinned(caseId, 'terminal', { replies: frame, barrier: da });
    assert.equal(matched.response, frame, 'the matched frame, not the barrier');
    assert.equal(matched.observation.outcome, 'supported');
    const silent = replayKclPinned(caseId, 'terminal', { replies: '', barrier: da });
    assert.equal(silent.response, da, 'the raw text answered, barrier included');
    assert.equal(silent.observation.reason, 'no-response');
    assert.throws(() => replayKclPinned('other', 'terminal'), /Unknown kitty color case/);
    assert.throws(() => replayKclPinned(caseId, 'third'), /Unknown pinned path/);
});

test('the seed and finish bytes are bound to the run identity', () => {
    const runId = 'a'.repeat(32);
    assert.ok(kclPlanSeed(runId).includes(kclNoncePrefix + runId));
    assert.throws(() => kclPlanSeed('short'), /Invalid kitty color fixture run identity/);
    assert.ok(kclFinish(runId).endsWith('\x1b[?1049l') && kclFinish(runId).includes('KC-base-aaaaaaaa'));
    assert.throws(() => validateKclFeeds(contract, []), /do not match/);
    assert.doesNotThrow(() => validateKclFeeds(contract, kclFeeds(contract)));
    assert.deepEqual(kclFixture.baselineKeys, ['foreground', 'background', 'cursor', 'selection_background', 'selection_foreground', '5', '200']);
});

test('the hand-written reply table agrees with the oracle for every group, and the untouched run is supported', () => {
    assert.deepEqual(syntheticKclGrade(), { outcome: 'supported' });
    const baseline = baselineOf({ 'baseline-main': { replies: syntheticKclExchanges['baseline-main'] }, 'baseline-selection': { replies: syntheticKclExchanges['baseline-selection'] },
        'baseline-palette': { replies: syntheticKclExchanges['baseline-palette'] } });
    assert.equal(baseline.selection_foreground, '');
    for (const group of contract.groups.filter(entry => entry.shape === null)) {
        assert.deepEqual(resolveDemand(group, baseline), syntheticKclExchanges[group.role], group.role);
    }
});

test('an unmeasured baseline makes a baseline-relative demand unresolvable', () => {
    const group = contract.groups.find(entry => entry.role === 'pinned');
    assert.equal(resolveDemand(group, null), null);
    assert.equal(resolveDemand(group, {}), null);
    assert.equal(baselineOf({}), null);
    assert.equal(baselineOf({ 'baseline-main': { replies: [] } }), null);
});

test('a wrong reply to a protocol group is unsupported', () => {
    for (const [role, edit] of [
        ['pinned', read => ({ replies: [osc21('foreground=rgb:cd/cc/cc')] })],
        ['key-cursor', read => ({ replies: [osc21('cursor=rgb:a1/b2/c4')] })],
        ['combined-order', read => ({ replies: [osc21('foreground=rgb:10/20/30', 'cursor=rgb:a1/b2/c3', '5=rgb:44/55/66', '200=rgb:12/34/56', 'background=rgb:11/22/33')] })],
        ['bel-terminated', read => ({ replies: [read.replies[0].replace(st, '\x07')] })],
        ['two-sequences', read => ({ replies: [read.replies[1], read.replies[0]] })],
        ['unknown-padding', read => ({ replies: [osc21('unknown=YQ==', 'unknown=YWI=', 'unknown=YWJj')] })],
        ['unknown-index', read => ({ replies: [] })],
        ['encoding-rgb-1', read => ({ replies: [osc21('background=rgb:ff/00/8')] })],
        ['reset-bare-key', read => ({ replies: [osc21('foreground=rgb:10/20/30', 'background=rgb:11/22/33')] })],
        ['set-readback-legacy', read => ({ replies: [read.replies[0].replace('0a0a', '0a0b')] })],
        ['restore-palette', read => ({ replies: [...read.replies, ...read.replies] })],
    ]) {
        const grade = syntheticKclGrade(caseId, undefined, change(role, edit));
        assert.equal(grade.outcome, 'unsupported', role);
        assert.match(grade.note, new RegExp(role), role);
    }
});

test('a failed setup control is inconclusive and a missing seed is inconclusive', () => {
    for (const role of ['baseline-main', 'baseline-selection', 'baseline-palette', 'setup-A-main', 'setup-A-rest', 'selection-set']) {
        const grade = syntheticKclGrade(caseId, undefined, change(role, () => ({ replies: [] })));
        assert.equal(grade.outcome, 'inconclusive', role);
        assert.match(grade.note, /Setup control failed/);
    }
    const garbled = syntheticKclGrade(caseId, undefined, change('baseline-main', () => ({ replies: [osc21('foreground=rgb:cc/cc/cc', 'background=rgb:1e/1e/1e', 'cursor=red')] })));
    assert.equal(garbled.outcome, 'inconclusive', 'a baseline value that is not rgb:rr/gg/bb is not a baseline');
    const seedless = syntheticKclGrade(caseId, undefined, (id, role, read) => (role === 'initial' ? { ...read, screen: { ...read.screen, lines: ['x', ...read.screen.lines.slice(1)] } } : null));
    assert.equal(seedless.outcome, 'inconclusive');
    assert.match(seedless.note, /attributed seed/);
});

test('a request that changes the screen, the cursor or a mode is unsupported even when its reply is right', () => {
    const moved = syntheticKclGrade(caseId, undefined, change('key-background', read => ({ screen: { ...read.screen, cursorRow: 3 } })));
    assert.equal(moved.outcome, 'unsupported');
    assert.match(moved.note, /must change nothing but colors/);
    const margins = syntheticKclGrade(caseId, undefined, change('combined-order', read => ({ screen: { ...read.screen, bufferState: { ...read.screen.bufferState, scrollTop: 0 } } })));
    assert.equal(margins.outcome, 'unsupported');
});

test('policy groups record a mismatch but never decide', () => {
    for (const role of ['encoding-sharp-4', 'encoding-rgbi-range', 'empty-value', 'color-name', 'alpha-suffix', 'set-only-silent', 'oversize-dropped', 'field-cap', 'key-case', 'unmodeled-keys', 'interleaved-unknown']) {
        assert.deepEqual(syntheticKclGrade(caseId, undefined, change(role, () => ({ replies: [osc21('foreground=rgb:99/99/99')] }))), { outcome: 'supported' }, role);
    }
});

test('the grader refuses a revision it does not know and missing captures', () => {
    assert.throws(() => gradeKclCase(contract, {}, 2), /Unknown kitty color contracts revision/);
    assert.throws(() => gradeKclCase(contract, {}), /Missing extensions.osc21-kitty-color read captures/);
});
