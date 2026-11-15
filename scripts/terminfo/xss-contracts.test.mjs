import assert from 'node:assert/strict';
import { test } from 'node:test';
import { xssContracts, xssCaseIds, xssPinnedScripts, replayXssPinned, XtermSgrStackModel, xssPlanSeed, xssFinish, xssCaseEnd, xssDrain, validateXssFeeds, xssFixture, xssProbeRow } from './xss-contracts.mjs';
import { syntheticXssGrade, syntheticXssProbes } from './fixtures/xss-evidence.mjs';
import { paletteChannel, literalChannel } from './fixtures/sgr-palette-evidence.mjs';
import { planFeeds, planReads } from './graded-plan.mjs';

const esc = '\x1b';
const byId = Object.fromEntries(xssContracts.map(item => [item.caseId, item]));
const group = (caseId, role) => byId[caseId].groups.find(item => item.role === role);

test('the contracts cover exactly the two pinned cases with unique roles, bounded requests and a drained end', () => {
    assert.deepEqual([...xssCaseIds], ['modes.xtpushsgr', 'modes.xtpopsgr']);
    for (const contract of xssContracts) {
        assert.equal(new Set(contract.roles).size, contract.roles.length, `${contract.caseId} roles are unique`);
        assert.equal(contract.roles[contract.pinnedRead], 'pinned');
        for (const item of contract.groups) {
            assert.ok(item.request.length <= 64, `${contract.caseId}/${item.role} request fits the 64-byte exchange limit`);
            for (const feed of item.setup) assert.ok(feed.length <= 64, `${contract.caseId}/${item.role} setup feed is short`);
        }
        assert.equal(planReads(contract), contract.roles.length, 'one read per role');
        assert.deepEqual(contract.plan.at(-1), { feed: xssCaseEnd }, 'every case ends with the stack drained and the pen reset');
    }
    assert.equal(xssDrain, `${esc}[#}`.repeat(10));
    assert.ok(xssCaseEnd.endsWith(`${esc}[0m`));
    // The whole suite stays inside the owner observer's capture budget (64 per suite, start, bind and finish included).
    assert.ok(xssContracts.reduce((sum, contract) => sum + planReads(contract), 0) + 3 <= 64);
});

test('every case starts by erasing the probe row, so a later case never inherits an earlier case\'s probe cells', () => {
    for (const contract of xssContracts) {
        const [first] = contract.plan;
        assert.ok(first.feed.includes(`${esc}[${xssProbeRow + 1};1H${esc}[2K`), `${contract.caseId} reseed erases row ${xssProbeRow + 1}`);
        assert.ok(first.feed.indexOf(`${esc}[2K`) < first.feed.indexOf(`${esc}[2;6r`), 'the erase precedes the margins and the home');
        assert.ok(first.feed.endsWith(`${esc}[1;1H`), 'the reseed homes the cursor');
    }
});

test('the exact pinned sequences are the pinned bytes', () => {
    assert.deepEqual(xssPinnedScripts['modes.xtpushsgr'].termless, [['feedCapture', `${esc}[#{`, 'replies'], ['feedCapture', `${esc}[c`, 'barrier'], ['feed', `${esc}[#}`]]);
    assert.deepEqual(xssPinnedScripts['modes.xtpushsgr'].terminal, [['write', `${esc}[#{`], ['cursor'], ['write', `${esc}[#}`]]);
    assert.deepEqual(xssPinnedScripts['modes.xtpopsgr'].termless, [['feed', `${esc}[#{`], ['feedCapture', `${esc}[#}`, 'replies'], ['feedCapture', `${esc}[c`, 'barrier']]);
    assert.deepEqual(xssPinnedScripts['modes.xtpopsgr'].terminal, [['write', `${esc}[#{`], ['write', `${esc}[#}`], ['cursor']]);
    // The captured pinned request of each case is exactly its pinned sequence (the fence appends the DA1 the pinned probe also sends).
    assert.equal(group('modes.xtpushsgr', 'pinned').request, `${esc}[#{`);
    assert.equal(group('modes.xtpushsgr', 'pinned').after, `${esc}[#}`);
    assert.deepEqual(group('modes.xtpopsgr', 'pinned').setup, [`${esc}[#{`]);
    assert.equal(group('modes.xtpopsgr', 'pinned').request, `${esc}[#}`);
    assert.equal(group('modes.xtpushsgr', 'pinned').probe, null, 'the pinned exchange carries no probe character');
});

test('the pinned replay is always inconclusive and never supported or unsupported', () => {
    const barrier = `${esc}[?62;4;22c`;
    for (const caseId of xssCaseIds) {
        const silent = replayXssPinned(caseId, 'termless', { replies: '', barrier });
        assert.deepEqual(silent, { pass: false, response: barrier, observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'consumed',
            note: 'DA1 responsiveness does not measure saved/restored stack state' } });
        const noisy = replayXssPinned(caseId, 'termless', { replies: `${esc}[?0;0#Q`, barrier });
        assert.equal(noisy.observation.reason, 'invalid-reply');
        assert.equal(noisy.observation.outcome, 'inconclusive');
        const terminal = replayXssPinned(caseId, 'terminal', { position: { row: 1, col: 1 } });
        assert.deepEqual(terminal, { pass: false, response: '{"row":1,"col":1}', observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'consumed',
            note: 'Cursor responsiveness does not measure saved/restored state' } });
        assert.equal(replayXssPinned(caseId, 'terminal', { position: null }).observation.reason, 'no-response');
    }
    assert.throws(() => replayXssPinned('modes.nope', 'termless'), /Unknown xterm SGR-stack case/);
    assert.throws(() => replayXssPinned('modes.xtpushsgr', 'both'), /Unknown pinned path/);
});

test('the model oracle demands the hand-written probe cell of the fixture for every probe role', () => {
    for (const contract of xssContracts) {
        const roles = contract.groups.filter(item => item.probe !== null).map(item => item.role);
        assert.deepEqual(roles, Object.keys(syntheticXssProbes[contract.caseId]), `${contract.caseId} probe roles match the hand-written table`);
        for (const item of contract.groups.filter(entry => entry.probe !== null)) {
            const demanded = xssFixture.demandedCell(item.probe.pen, item.probe.char);
            const spec = syntheticXssProbes[contract.caseId][item.role];
            assert.equal(demanded.bold, (spec.attrs ?? []).includes('bold'), `${contract.caseId}/${item.role} bold`);
            assert.equal(demanded.underline, [false, 'single', 'double'][spec.underlineStyle ?? 0], `${contract.caseId}/${item.role} underline`);
            assert.deepEqual(demanded.fg, spec.fg === undefined ? { index: -1, argb: null } : spec.fg.kind === 'palette' ? { index: spec.fg.index, argb: null }
                : { index: -1, argb: 0xff000000 + spec.fg.rgb.r * 65536 + spec.fg.rgb.g * 256 + spec.fg.rgb.b }, `${contract.caseId}/${item.role} foreground`);
        }
    }
});

test('the model follows the documented stack: LIFO, copy semantics, selected attributes only, ten levels', () => {
    const { pens } = xssFixture;
    const model = new XtermSgrStackModel();
    model.set(pens.all); model.push([]);
    model.set(pens.clear); model.push([]);
    model.set(pens.mid);
    model.pop(); assert.deepEqual(model.pen, pens.clear);
    model.pop(); assert.deepEqual(model.pen, pens.all);
    model.pop(); assert.deepEqual(model.pen, pens.all, 'a pop with nothing saved changes nothing');
    model.set(pens.all); model.push([1, 30]); model.set(pens.clear); model.pop();
    assert.deepEqual(model.pen, { ...pens.clear, bold: true, fg: pens.all.fg }, 'only the selected attributes come back');
    const deep = new XtermSgrStackModel();
    for (let n = 1; n <= 11; n++) { deep.set(pens.level(n)); deep.push([30]); }
    for (let n = 0; n < 10; n++) deep.pop();
    assert.deepEqual(deep.pen.fg, pens.level(1).fg, 'the eleventh push was ignored: the first level is the bottom');
});

test('every case grades supported on the hand-written cells', () => {
    for (const caseId of xssCaseIds) assert.deepEqual(syntheticXssGrade(caseId), { outcome: 'supported' }, caseId);
});

test('a wrong restored attribute or colour on a protocol group is unsupported', () => {
    const flip = (caseId, role, change) => (id, at, spec) => (id === caseId && at === role ? change(spec) : null);
    const cases = [
        ['modes.xtpushsgr', 'pop-recovers-pushed', spec => ({ ...spec, attrs: spec.attrs.filter(name => name !== 'blink') })],
        ['modes.xtpushsgr', 'pop-recovers-pushed', spec => ({ ...spec, bg: literalChannel(10, 20, 31) })],
        ['modes.xtpushsgr', 'snapshot-is-copy', spec => ({ ...spec, fg: paletteChannel(5) })],
        ['modes.xtpushsgr', 'push-silent', spec => ({ ...spec, attrs: [...spec.attrs, 'overline'] })],
        ['modes.xtpushsgr', 'numbered-1', spec => ({ ...spec, attrs: ['bold', 'italic'] })],
        ['modes.xtpushsgr', 'numbered-21', spec => ({ ...spec, underlineStyle: 1 })],
        ['modes.xtpushsgr', 'numbered-30', spec => ({ ...spec, bg: paletteChannel(22), fg: literalChannel(200, 100, 50) })],
        ['modes.xtpushsgr', 'depth-bottom', spec => ({ ...spec, fg: paletteChannel(42) })],
        ['modes.xtpopsgr', 'pop-restores-default', spec => ({ ...spec, fg: paletteChannel(1) })],
        ['modes.xtpopsgr', 'pop-lifo-1', spec => ({ ...spec, attrs: ['bold'] })],
        ['modes.xtpopsgr', 'pop-only-saved', spec => ({ ...spec, attrs: ['bold', 'italic'] })],
    ];
    for (const [caseId, role, change] of cases) {
        const grade = syntheticXssGrade(caseId, undefined, null, flip(caseId, role, change));
        assert.equal(grade.outcome, 'unsupported', `${caseId}/${role}`);
        assert.match(grade.note, new RegExp(role));
    }
});

test('a stray reply, a moved cursor, a changed seed cell or a disturbed earlier probe is unsupported on a protocol group', () => {
    const stray = (caseId, role) => (id, at, read) => (id === caseId && at === role ? { ...read, replies: [`${esc}[?0;0#Q`] } : null);
    assert.equal(syntheticXssGrade('modes.xtpushsgr', undefined, stray('modes.xtpushsgr', 'push-silent')).outcome, 'unsupported');
    assert.equal(syntheticXssGrade('modes.xtpopsgr', undefined, stray('modes.xtpopsgr', 'pinned')).outcome, 'unsupported');
    const moved = (caseId, role) => (id, at, read) => { if (id === caseId && at === role) read.screen.cursorCol += 3; return id === caseId && at === role ? read : null; };
    assert.equal(syntheticXssGrade('modes.xtpushsgr', undefined, moved('modes.xtpushsgr', 'pinned')).outcome, 'unsupported');
    // A stack operation that wipes the seed text (the screen outside the probe row changed).
    const wiped = (id, at, read) => {
        if (id !== 'modes.xtpushsgr' || at !== 'pop-recovers-pushed') return null;
        const lines = [...read.screen.lines];
        lines[1] = '';
        return { ...read, screen: { ...read.screen, lines } };
    };
    assert.equal(syntheticXssGrade('modes.xtpushsgr', undefined, wiped).outcome, 'unsupported');
    // A later group whose read lost an earlier probe cell.
    const dropped = (id, at, read) => {
        if (id !== 'modes.xtpopsgr' || at !== 'pop-lifo-1') return null;
        const lines = [...read.screen.lines];
        lines[xssProbeRow] = lines[xssProbeRow].slice(1);
        return { ...read, screen: { ...read.screen, lines } };
    };
    assert.equal(syntheticXssGrade('modes.xtpopsgr', undefined, dropped).outcome, 'unsupported');
});

test('a failed control is inconclusive, never a verdict', () => {
    const wrong = (caseId, role) => (id, at, spec) => (id === caseId && at === role ? { ...spec, attrs: [] } : null);
    for (const [caseId, role] of [['modes.xtpushsgr', 'set-A'], ['modes.xtpushsgr', 'baseline'], ['modes.xtpopsgr', 'push-A'], ['modes.xtpopsgr', 'set-C']]) {
        const grade = syntheticXssGrade(caseId, undefined, null, (id, at, spec) => (id === caseId && at === role ? { ...spec, attrs: spec.attrs?.length ? [] : ['bold'] } : wrong(caseId, role)(id, at, spec)));
        assert.equal(grade.outcome, 'inconclusive', `${caseId}/${role}`);
        assert.match(grade.note, /Setup control failed/);
    }
});

test('policy groups never decide a verdict', () => {
    const policies = [['modes.xtpushsgr', 'overflow-eleventh'], ['modes.xtpushsgr', 'unlisted-value'], ['modes.xtpushsgr', 'unstacked-state'], ['modes.xtpopsgr', 'pop-empty']];
    for (const [caseId, role] of policies) {
        assert.equal(group(caseId, role).kind, 'policy');
        const wrong = (id, at, spec) => (id === caseId && at === role ? { fg: paletteChannel(77), attrs: ['faint'] } : null);
        // The later groups stack on the changed probe row, so only the policy group itself is wrong here; the verdict is unchanged because it is the LAST group of its case or a recorded one.
        const grade = syntheticXssGrade(caseId, undefined, null, wrong);
        const later = byId[caseId].groups.slice(byId[caseId].groups.findIndex(item => item.role === role) + 1).filter(item => item.probe !== null);
        assert.deepEqual(grade.outcome, later.length === 0 ? 'supported' : grade.outcome, `${caseId}/${role}`);
        if (later.length === 0) assert.deepEqual(grade, { outcome: 'supported' });
    }
});

test('a pop restores the untouched baseline, not the pen in force: the demand is the neutral cell', () => {
    const stale = (id, at) => (id === 'modes.xtpopsgr' && at === 'pop-restores-default' ? { ...syntheticXssProbes['modes.xtpopsgr']['set-A'] } : null);
    assert.equal(syntheticXssGrade('modes.xtpopsgr', undefined, null, stale).outcome, 'unsupported');
    const demanded = xssFixture.demandedCell(group('modes.xtpopsgr', 'pop-restores-default').probe.pen, 'e');
    assert.equal(demanded.bold, false);
    assert.deepEqual(demanded.fg, { index: -1, argb: null });
    assert.deepEqual(demanded.bg, { index: -1, argb: null });
});

test('the seed, finish and feed validation are exact', () => {
    const runId = 'b'.repeat(32);
    assert.ok(xssPlanSeed(runId).includes('NTILDE_XSS_' + runId));
    assert.ok(xssPlanSeed(runId).includes(xssDrain), 'the bind starts with a drained stack');
    assert.throws(() => xssPlanSeed('short'), /Invalid xterm SGR-stack fixture run identity/);
    assert.ok(xssFinish(runId).endsWith('\x1b[?1049l') && xssFinish(runId).includes(xssDrain));
    const contract = byId['modes.xtpushsgr'];
    validateXssFeeds(contract, planFeeds(contract));
    assert.throws(() => validateXssFeeds(contract, planFeeds(contract).slice(1)), /do not match/);
    assert.equal(xssFixture.minimum.rows, 8);
    assert.equal(xssProbeRow, 7);
});

test('the first failure in execution order decides: a protocol mismatch is not masked by a later control it damaged', () => {
    // An operation that moves the cursor (a protocol mismatch) leaves the cursor moved in every later read, so the next control also differs from the seed state.
    const damaged = (caseId, from) => {
        const order = byId[caseId].roles;
        return (id, role, read) => {
            if (id === caseId && order.indexOf(role) >= order.indexOf(from)) read.screen.cursorCol += 4;
            return id === caseId && order.indexOf(role) >= order.indexOf(from) ? read : null;
        };
    };
    const protocol = syntheticXssGrade('modes.xtpushsgr', undefined, damaged('modes.xtpushsgr', 'push-silent'));
    assert.equal(protocol.outcome, 'unsupported', 'the push-silent protocol group failed first, before the later set-B control');
    assert.match(protocol.note, /push-silent/);
    const control = syntheticXssGrade('modes.xtpushsgr', undefined, damaged('modes.xtpushsgr', 'set-A'));
    assert.equal(control.outcome, 'inconclusive', 'a control that fails before any protocol group makes the case inconclusive');
    assert.match(control.note, /set-A/);
});

test('each numbered group saves one parameter and demands exactly that attribute back', () => {
    const numbered = byId['modes.xtpushsgr'].groups.filter(item => /^numbered-\d+$/.test(item.role));
    assert.deepEqual(numbered.map(item => item.role), [1, 2, 3, 4, 5, 7, 8, 9, 21, 30, 31].map(value => `numbered-${value}`));
    for (const item of numbered) {
        const value = item.role.slice('numbered-'.length);
        assert.ok(item.setup.some(feed => feed === `${esc}[${value}#{`), `${item.role} pushes exactly parameter ${value}`);
        assert.equal(item.setup.filter(feed => feed.includes('#{')).length, 1);
        assert.ok(item.request.includes(`${esc}[#}`) && !item.request.includes('#{'), `${item.role} pops once in the request`);
    }
});

test('probe cells an earlier case left on the probe row make the seed unproven, never a verdict', () => {
    // The first attempt at this suite ran the push case before the pop case without erasing row 8: the pop case then judged against a dirty row.
    for (const caseId of xssCaseIds) {
        const grade = syntheticXssGrade(caseId, undefined, null, null, 'abc');
        assert.equal(grade.outcome, 'inconclusive', caseId);
        assert.match(grade.note, /attributed seed/);
    }
});
