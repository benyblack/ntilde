import assert from 'node:assert/strict';
import { test } from 'node:test';
import { osc5Contracts, osc5Fixture, osc5PinnedQuery, osc5SuiteId, osc5EffectContractId, osc5NoncePrefix, osc5Prefix, osc5PlanSeed, osc5Finish, osc5Reads, osc5Feeds, validateOsc5Feeds,
    parseOsc5Color, replayOsc5Pinned, resolveDemand, baselineOf, osc5ContractsVersion, gradeOsc5Case } from './osc5-contracts.mjs';
import { syntheticOsc5Grade, syntheticOsc5Exchanges } from './fixtures/osc5-evidence.mjs';

const [contract] = osc5Contracts;
const caseId = contract.caseId;
const st = '\x1b\\';
const da = '\x1b[?62;4;22c';
const osc5Frame = (selector, value) => `\x1b]5;${selector};${value}${st}`;
const roles = contract.groups.map(group => group.role);
/** A mutation of one role's replies and/or screen. */
const change = (role, edit) => (id, current, read) => (current === role ? { ...read, ...edit(read) } : null);

test('identity, plan size and the exchange limits', () => {
    assert.equal(osc5SuiteId, 'ntilde-owned-osc5-special-colors-graded-v1');
    assert.equal(osc5EffectContractId, 'ntilde-osc5-special-colors-effect-v1');
    assert.equal(osc5NoncePrefix, 'NTILDE_O5_');
    assert.equal(osc5Prefix, 'scol');
    assert.equal(osc5ContractsVersion, 1);
    assert.equal(caseId, 'extensions.osc5-special-color');
    assert.ok(osc5Reads(contract) <= 61, 'at most 61 plan reads (64 captures per suite with start, bind and finish)');
    assert.equal(osc5Reads(contract), contract.groups.length + 1);
    assert.ok(osc5Feeds(contract).length > contract.groups.length);
    for (const group of contract.groups) assert.ok(group.request.length <= 128, `${group.role} request fits one fenced exchange`);
    assert.equal(new Set(roles).size, roles.length, 'roles are unique');
    assert.deepEqual(contract.roles, ['initial', ...roles]);
    assert.equal(osc5Feeds(contract).filter((_, index) => contract.plan.filter(step => step.feed)[index].capture).length, contract.groups.length);
});

test('the pinned group is the exact pinned request on the untouched host default, read before any override', () => {
    const pinned = contract.groups.find(group => group.role === 'pinned');
    assert.equal(pinned.request, osc5PinnedQuery);
    assert.equal(osc5PinnedQuery, '\x1b]5;0;?\x07');
    assert.deepEqual(pinned.setup, []);
    assert.equal(contract.pinnedRead, contract.roles.indexOf('pinned'));
    assert.ok(contract.pinnedRead < contract.roles.indexOf('setup-A'), 'before any color is set');
    assert.deepEqual(contract.pinnedFeeds, [osc5PinnedQuery]);
});

test('roles: the baseline and setup groups are controls, the protocol groups decide, the quirks are policy', () => {
    const kind = Object.fromEntries(contract.groups.map(group => [group.role, group.kind]));
    for (const role of ['baseline-special', 'baseline-other', 'setup-A']) assert.equal(kind[role], 'control', role);
    for (const role of ['pinned', 'set-then-query', 'per-slot', 'encodings', 'bel-terminated', 'two-sequences', 'reset-one', 'reset-all', 'cross-dynamic-set', 'cross-dynamic-read', 'cross-palette-set', 'cross-palette-read', 'cross-kitty-set', 'cross-kitty-read', 'restore']) {
        assert.equal(kind[role], 'protocol', role);
    }
    for (const role of ['unrelated-selector', 'malformed', 'set-only-silent', 'reset-noise', 'oversize-dropped']) assert.equal(kind[role], 'policy', role);
});

test('the oracle reads numeric colors as the legacy color text defines them', () => {
    assert.equal(parseOsc5Color('#112233'), '#112233');
    assert.equal(parseOsc5Color('#ABCDEF'), '#abcdef');
    assert.equal(parseOsc5Color('#3a7'), '#33aa77', 'short forms scale like OSC 10: each one-digit channel is n*255/15');
    assert.equal(parseOsc5Color('#123456789'), '#124578', 'nine-digit forms keep the top eight bits (floor equals the MSB here)');
    assert.equal(parseOsc5Color('rgb:f/0/8'), '#ff0088');
    assert.equal(parseOsc5Color('rgb:12/ab/ef'), '#12abef');
    assert.equal(parseOsc5Color('rgb:1234/5678/9abc'), '#12569a');
    assert.equal(parseOsc5Color('rgbi:1/0/0.2'), '#ff0033');
    assert.equal(parseOsc5Color('rgbi:2/0/0'), '#ff0000', 'intensities outside 0..1 are clipped');
    for (const refused of ['', 'red', '#12345', '#1234567', 'rgb:1/2', 'rgb:12345/0/0', 'rgbi:x/0/0', '#gg0000']) assert.equal(parseOsc5Color(refused), null, refused);
});

test('pinned replay is the pinned code: supported for one complete index-0 frame, inconclusive otherwise, never unsupported', () => {
    const supported = replayOsc5Pinned(caseId, 'termless', { replies: osc5Frame(0, 'rgb:cccc/cccc/cccc') });
    assert.deepEqual(Object.keys(supported), ['pass', 'response', 'observation', 'assertions']);
    assert.deepEqual(supported.observation, { outcome: 'supported', evidence: 'query' });
    assert.equal(supported.assertions[0].observed, osc5Frame(0, 'rgb:cccc/cccc/cccc'));
    assert.equal(replayOsc5Pinned(caseId, 'termless', { replies: '\x1b]5;0;rgb:1/2/3\x07' }).pass, true, 'a BEL terminator is a frame');
    assert.equal(replayOsc5Pinned(caseId, 'termless', { replies: osc5Frame(0, 'rgb:12345/0/0') }).pass, false, 'five hex digits are not a channel');
    assert.equal(replayOsc5Pinned(caseId, 'termless', { replies: osc5Frame(1, 'rgb:1/2/3') }).pass, false, 'the pinned frame echoes selector 0 only');
    const invalid = replayOsc5Pinned(caseId, 'termless', { replies: '\x1b]5;0;rgb:1/2' });
    assert.equal(invalid.pass, false);
    assert.deepEqual(invalid.observation, { outcome: 'inconclusive', evidence: 'query', reason: 'invalid-reply' });
    assert.equal('note' in invalid, false, 'the pinned code sets no note');
    for (const replies of ['', da, 'x']) {
        const none = replayOsc5Pinned(caseId, 'termless', { replies });
        assert.deepEqual(none.observation, { outcome: 'inconclusive', evidence: 'query', reason: 'no-response' });
        assert.equal(none.response, replies);
    }
});

test('the terminal path searches replies plus the DA barrier and grades the whole raw text', () => {
    const frame = osc5Frame(0, 'rgb:cccc/cccc/cccc');
    const matched = replayOsc5Pinned(caseId, 'terminal', { replies: frame, barrier: da });
    assert.equal(matched.observation.outcome, 'supported');
    assert.equal(matched.assertions[0].observed, frame, 'the matched frame, not the barrier');
    const silent = replayOsc5Pinned(caseId, 'terminal', { replies: '', barrier: da });
    assert.equal(silent.response, da, 'the raw text answered, barrier included');
    assert.equal(silent.observation.reason, 'no-response');
    assert.throws(() => replayOsc5Pinned('other', 'terminal'), /Unknown special-color case/);
    assert.throws(() => replayOsc5Pinned(caseId, 'third'), /Unknown pinned path/);
});

test('the seed and finish bytes are bound to the run identity', () => {
    const runId = 'a'.repeat(32);
    assert.ok(osc5PlanSeed(runId).includes(osc5NoncePrefix + runId));
    assert.throws(() => osc5PlanSeed('short'), /Invalid special-color fixture run identity/);
    assert.ok(osc5Finish(runId).endsWith('\x1b[?1049l') && osc5Finish(runId).includes('O5-base-aaaaaaaa'));
    assert.throws(() => validateOsc5Feeds(contract, []), /do not match/);
    assert.doesNotThrow(() => validateOsc5Feeds(contract, osc5Feeds(contract)));
    assert.deepEqual(osc5Fixture.slots, [0, 1, 2, 3, 4]);
});

test('the hand-written reply table agrees with the oracle for every group, and the untouched run is supported', () => {
    assert.deepEqual(syntheticOsc5Grade(), { outcome: 'supported' });
    const baseline = baselineOf({ 'baseline-special': { replies: syntheticOsc5Exchanges['baseline-special'] }, 'baseline-other': { replies: syntheticOsc5Exchanges['baseline-other'] } });
    for (const group of contract.groups) {
        assert.deepEqual(resolveDemand(group, baseline), syntheticOsc5Exchanges[group.role], group.role);
    }
});

test('an unmeasured baseline makes a baseline-relative demand unresolvable', () => {
    const group = contract.groups.find(entry => entry.role === 'pinned');
    assert.equal(resolveDemand(group, null), null);
    assert.equal(resolveDemand(group, {}), null);
    assert.equal(baselineOf({}), null);
    assert.equal(baselineOf({ 'baseline-special': { replies: [] } }), null);
    assert.deepEqual(Object.keys(baselineOf({ 'baseline-special': { replies: syntheticOsc5Exchanges['baseline-special'] } })), ['0', '1', '2', '3', '4'], 'later missing legacy defaults preserve the measured special colors');
});

test('a wrong reply to a protocol group is unsupported', () => {
    for (const [role, edit] of [
        ['pinned', read => ({ replies: [osc5Frame(0, 'rgb:cdcd/cccc/cccc')] })],
        ['per-slot', read => ({ replies: [read.replies[0], read.replies[2], read.replies[1]] })],
        ['encodings', read => ({ replies: [osc5Frame(0, 'rgb:ffff/0000/8889'), read.replies[1], read.replies[2]] })],
        ['bel-terminated', read => ({ replies: [read.replies[0].replace(st, '\x07')] })],
        ['two-sequences', read => ({ replies: [read.replies[1], read.replies[0]] })],
        ['reset-one', read => ({ replies: [read.replies[0], read.replies[1], read.replies[1]] })],
        ['reset-all', read => ({ replies: read.replies.slice(0, 4) })],
        ['restore', read => ({ replies: [...read.replies, ...read.replies] })],
    ]) {
        const grade = syntheticOsc5Grade(caseId, undefined, change(role, edit));
        assert.equal(grade.outcome, 'unsupported', role);
        assert.match(grade.note, new RegExp(role), role);
    }
});

test('a special color that follows the other service\'s set is the interchange the isolation groups refuse', () => {
    const leak = syntheticOsc5Grade(caseId, undefined, change('cross-dynamic-set', read => ({ replies: [osc5Frame(0, 'rgb:abab/cdcd/efef')] })));
    assert.equal(leak.outcome, 'unsupported');
    assert.match(leak.note, /cross-dynamic-set/);
    // The reverse direction: a special-color set that leaks into the other service's readback.
    const reverse = syntheticOsc5Grade(caseId, undefined, change('cross-dynamic-read', read => ({ replies: ['\x1b]10;rgb:1010/1010/1010\x1b\\'] })));
    assert.equal(reverse.outcome, 'unsupported');
    assert.match(reverse.note, /cross-dynamic-read/);
});

test('a failed setup control is inconclusive and a missing seed is inconclusive', () => {
    for (const role of ['baseline-special', 'setup-A']) {
        const grade = syntheticOsc5Grade(caseId, undefined, change(role, () => ({ replies: [] })));
        assert.equal(grade.outcome, 'inconclusive', role);
        assert.match(grade.note, /Setup control failed/);
    }
    const seedless = syntheticOsc5Grade(caseId, undefined, (id, role, read) => (role === 'initial' ? { ...read, screen: { ...read.screen, lines: ['x', ...read.screen.lines.slice(1)] } } : null));
    assert.equal(seedless.outcome, 'inconclusive');
    assert.match(seedless.note, /attributed seed/);
});

test('a request that changes the screen, the cursor or a mode is unsupported even when its reply is right', () => {
    const moved = syntheticOsc5Grade(caseId, undefined, change('set-then-query', read => ({ screen: { ...read.screen, cursorRow: 3 } })));
    assert.equal(moved.outcome, 'unsupported');
    assert.match(moved.note, /must change nothing but the special colors/);
    const margins = syntheticOsc5Grade(caseId, undefined, change('per-slot', read => ({ screen: { ...read.screen, bufferState: { ...read.screen.bufferState, scrollTop: 0 } } })));
    assert.equal(margins.outcome, 'unsupported');
});

test('policy groups record a mismatch but never decide', () => {
    for (const role of ['unrelated-selector', 'malformed', 'set-only-silent', 'reset-noise', 'oversize-dropped']) {
        const mutated = syntheticOsc5Grade(caseId, undefined, change(role, () => ({ replies: [osc5Frame(0, 'rgb:9999/9999/9999')] })));
        assert.deepEqual(mutated, { outcome: 'supported' }, role);
    }
});

test('a collateral color mutation inside any group is rejected, even when the special replies are right', () => {
    // An OSC 5 operation that also changed an unrelated palette slot (or the theme) would recolor the seed
    // row's resolved colors: the group's color binding pins that, and the grader refuses it.
    const recolored = syntheticOsc5Grade(caseId, undefined, change('set-then-query', read => {
        const palette = [...read.screen.colorBinding.ansiPaletteArgb];
        palette[201] = 0xff112233;
        return { screen: { ...read.screen, colorBinding: { ...read.screen.colorBinding, ansiPaletteArgb: palette } } };
    }));
    assert.equal(recolored.outcome, 'unsupported');
    assert.match(recolored.note, /changed the palette, the theme or the render state/);
});

test('the grader refuses a revision it does not know and missing captures', () => {
    assert.throws(() => gradeOsc5Case(contract, {}, 2), /Unknown special-color contracts revision/);
    assert.throws(() => gradeOsc5Case(contract, {}), /Missing extensions.osc5-special-color read captures/);
});

test('every deciding encoding group scales to whole bytes, so the unspecified rounding of a partial byte cannot decide a verdict', () => {
    const floor = value => {
        const wide = /^rgb:([0-9a-f]{1,4})\/([0-9a-f]{1,4})\/([0-9a-f]{1,4})$/i.exec(value), intensity = /^rgbi:([\d.]+)\/([\d.]+)\/([\d.]+)$/.exec(value);
        const channels = wide ? wide.slice(1).map(part => Number.parseInt(part, 16) * 255 / (16 ** part.length - 1))
            : intensity?.slice(1).map(part => Math.min(1, Number(part)) * 255);
        if (channels) assert.ok(channels.every(Number.isInteger), `${value} must have integral scaled channels before truncation`);
        return channels ? `#${channels.map(channel => channel.toString(16).padStart(2, '0')).join('')}` : null;
    };
    let checked = 0;
    for (const group of contract.groups.filter(item => item.role === 'encodings' && item.kind === 'protocol')) {
        const matches = group.request.matchAll(/([0-4]);([^;?]+);\1;\?/g);
        for (const [, selector, value] of matches) {
            if (value === '?') continue;
            const truncated = floor(value);
            if (truncated !== null) assert.equal(parseOsc5Color(value), truncated, `${group.role}: selector ${selector} value ${value} must scale to whole bytes`);
            checked++;
        }
    }
    assert.ok(checked >= 3);
});

test('a later failed legacy baseline cannot hide an earlier pinned protocol failure', () => {
    const result = syntheticOsc5Grade(caseId, undefined, (id, role, read) => ['pinned', 'baseline-other'].includes(role) ? { ...read, replies: [] } : null);
    assert.equal(result.outcome, 'unsupported');
    assert.match(result.note, /pinned request was not answered/);
});

test('finish clears special colors even when interrupted before the plan reset', () => {
    assert.ok(osc5Finish('a'.repeat(32)).includes('\x1b]105\x1b\\'));
});

for (const role of ['cross-dynamic-set', 'cross-palette-set', 'cross-kitty-set']) {
    test(`${role} rejects collateral palette mutation despite correct special-color replies`, () => {
        const result = syntheticOsc5Grade(caseId, undefined, change(role, read => {
            read.screen.colorBinding.ansiPaletteArgb[201] = 0xff112233;
            return { screen: read.screen };
        }));
        assert.equal(result.outcome, 'unsupported');
        assert.match(result.note, new RegExp(role));
    });
}
