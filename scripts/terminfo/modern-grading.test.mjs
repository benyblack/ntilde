import test from 'node:test';
import assert from 'node:assert/strict';
import { join } from 'node:path';
import { loadModernInputs, validateModernInputs, snapshotFiles, modernPublication } from './modern-inputs.mjs';
import { evaluateModern, checkedTrace } from './modern-grading.mjs';
import { modernRoot } from './modern-results.mjs';

const { requirements, mapping } = loadModernInputs(modernRoot);
const ruleOf = id => mapping.rows.find(row => row.id === id);
const query = (sequence, reply, raw = reply + '\x1b[?62;4;22c') => ({ sequence, match: [reply], reason: 'reply', raw, rawBase64: Buffer.from(raw).toString('base64') });
const traceEvidence = (id, queries, writes = []) => ({ raw: { rawReplies: { [id]: JSON.stringify({ writes, queries,
    events: [...writes.map(sequence => ({ kind: 'write', sequence })), ...queries.map(value => ({ kind: 'query', ...value }))] }) } } });
const grade = (id, evidence) => evaluateModern(ruleOf(id), evidence);

test('the frozen page and catalogue agree on 69 unique Modern rows and mapping membership', () => {
    assert.equal(requirements.rows.length, 69);
    assert.equal(new Set(requirements.rows.map(row => row.id)).size, 69);
    assert.deepEqual(mapping.rows.map(row => row.id), requirements.rows.map(row => row.id));
    assert.equal(requirements.upstream.commit, '3eea0f130d87d64eab33ab342c96e668daab0292');
    assert.match(mapping.scope, /not an upstream/);
});

test('recognition-only modes admit 1-3, reject permanent reset, and never infer delivery', () => {
    for (const state of [0, 1, 2, 3, 4]) {
        const evidence = traceEvidence('modes.bracketed-paste', [query('\x1b[?2004$p\x1b[c', `\x1b[?2004;${state}$y`)]);
        const result = grade('modes.bracketed-paste', evidence);
        assert.equal(result.conclusion, ['inconclusive', 'pass', 'pass', 'pass', 'fail'][state]);
        if (result.conclusion === 'pass') assert.match(result.observed.scope, /recognition only/);
    }
});

test('a reply for another mode or request cannot satisfy the row', () => {
    assert.equal(grade('modes.mouse-sgr', traceEvidence('modes.mouse-sgr', [query('\x1b[?1006$p\x1b[c', '\x1b[?1000;1$y')])).conclusion, 'inconclusive');
    assert.throws(() => grade('modes.mouse-sgr', traceEvidence('modes.mouse-sgr', [query('\x1b[?1000$p\x1b[c', '\x1b[?1000;1$y')])), /wrong mode request/);
});

test('changed bytes, query order, incomplete replies and replies after the barrier are refused', () => {
    const id = 'device.status-report', evidence = traceEvidence(id, [query('\x1b[5n\x1b[c', '\x1b[0n')]);
    const changed = JSON.parse(evidence.raw.rawReplies[id]);
    changed.queries[0].rawBase64 = Buffer.from('changed').toString('base64');
    // Deliberately change the event too: agreement between copies cannot substitute for the original byte encoding.
    changed.events[0].rawBase64 = changed.queries[0].rawBase64;
    assert.throws(() => checkedTrace({ rawReplies: { [id]: JSON.stringify(changed) } }, id), /reply bytes differ/);
    changed.queries = [];
    assert.throws(() => checkedTrace({ rawReplies: { [id]: JSON.stringify(changed) } }, id), /incomplete trace/);
    const late = traceEvidence(id, [query('\x1b[5n\x1b[c', '\x1b[0n', '\x1b[?62;4;22c\x1b[0n')]);
    assert.throws(() => grade(id, late), /follows the DA barrier/);
    const noMatch = traceEvidence(id, [{ ...query('\x1b[5n\x1b[c', '\x1b[0n'), reason: 'timeout' }]);
    assert.throws(() => grade(id, noMatch), /unqualified query/);
});

test('a wrong palette index and a swapped dynamic-color channel do not satisfy queries', () => {
    assert.equal(grade('extensions.osc4-palette', traceEvidence('extensions.osc4-palette', [query('\x1b]4;0;?\x07\x1b[c', '\x1b]4;1;rgb:0000/0000/0000\x1b\\')])).conclusion, 'inconclusive');
    assert.equal(grade('extensions.osc10-fg-color', traceEvidence('extensions.osc10-fg-color', [query('\x1b]10;?\x07\x1b[c', '\x1b]11;rgb:ffff/ffff/ffff\x1b\\')])).conclusion, 'inconclusive');
});

const caseEvidence = (id, item) => ({ runId: 'run', read: () => ({ runId: 'run', cases: [{ caseId: id, contractResult: { outcome: 'supported' }, ...item }] }) });
test('generic underline, the wrong style and lost flags cannot satisfy an exact style', () => {
    const id = 'sgr.underline.double';
    for (const target of [{ char: 'X', underlineStyle: 1, flags: 8 }, { char: 'X', underlineStyle: 2, flags: 0 }, { char: 'Y', underlineStyle: 2, flags: 8 }])
        assert.equal(grade(id, caseEvidence(id, { observed: { target } })).conclusion, 'inconclusive');
    assert.equal(grade(id, caseEvidence(id, { observed: { target: { char: 'X', underlineStyle: 2, flags: 8 } } })).conclusion, 'pass');
    assert.equal(grade(id, caseEvidence(id, { contractResult: { outcome: 'inconclusive' } })).conclusion, 'inconclusive');
});

test('truecolor needs both exact samples rather than one matching cell or only palette indices', () => {
    const id = 'sgr.fg.truecolor', x = { char: 'X', fg: { r: 255, g: 128, b: 0 } }, y = { char: 'Y', fg: { r: 17, g: 97, b: 201 } };
    assert.equal(grade(id, caseEvidence(id, { observed: { fixture: [x, y] } })).conclusion, 'pass');
    assert.equal(grade(id, caseEvidence(id, { observed: { fixture: [x, { ...y, fg: null, fgIndex: 67 }] } })).conclusion, 'inconclusive');
    assert.equal(grade(id, caseEvidence(id, { observed: { fixture: [{ ...x, fg: y.fg }, y] } })).conclusion, 'inconclusive');
});

test('stored cursor shape admits the exact steady-beam predicate without claiming native captures', () => {
    const id = 'cursor.shape';
    const specimen = cursorMode => ({ pinnedFeeds: ['\x1b[6 q'], observed: { groups: [{ id: 'termless', reads: [{ role: 'measured', cursorMode }] }] } });
    assert.equal(grade(id, caseEvidence(id, specimen({ style: 1, blink: false, isDefault: false }))).conclusion, 'pass');
    assert.equal(grade(id, caseEvidence(id, specimen({ style: 0, blink: false, isDefault: false }))).conclusion, 'inconclusive');
    const wrong = specimen({ style: 1, blink: false, isDefault: false });
    wrong.pinnedFeeds = ['\x1b[5 q'];
    assert.throws(() => grade(id, caseEvidence(id, wrong)), /wrong stored cursor fixture/);
});

test('underline RGB requires the exact colon spelling and exposed target/default/sibling values', () => {
    const id = 'sgr.underline.color';
    const specimen = { feeds: ['\x1b[4m\x1b[58:2::255:0:128mC'], observed: {
        control: [{ char: 'C', underlineColor: { r: 255, g: 0, b: 128 }, underlineSource: 'literal', underlineStyle: 1 },
            { char: 'P', underlineColor: { r: 95, g: 135, b: 175 } }],
        fixture: [{ char: 'B', underlineSource: 'default', underlineColor: null }],
    } };
    assert.equal(grade(id, caseEvidence(id, specimen)).conclusion, 'pass');
    const wrongColor = structuredClone(specimen); wrongColor.observed.control[0].underlineColor.r = 254;
    assert.equal(grade(id, caseEvidence(id, wrongColor)).conclusion, 'inconclusive');
    assert.throws(() => grade(id, caseEvidence(id, { ...specimen, feeds: ['\x1b[58;2;255;0;128mC'] })), /colon fixture/);
});

test('the aggregate truecolor alias explicitly identifies its measured foreground source', () => {
    const evidence = caseEvidence('sgr.fg.truecolor', { observed: { fixture: [{ char: 'X', fg: { r: 255, g: 128, b: 0 } }, { char: 'Y', fg: { r: 17, g: 97, b: 201 } }] } });
    assert.equal(ruleOf('extensions.truecolor').sourceId, 'sgr.fg.truecolor');
    assert.equal(grade('extensions.truecolor', evidence).conclusion, 'pass');
    assert.throws(() => grade('extensions.truecolor', caseEvidence('sgr.bg.truecolor', {})), /missing\/cross-run/);
});

test('every unresolved requirement retains its blocker despite an unrelated supported stock grade', () => {
    for (const rule of mapping.rows.filter(row => row.kind === 'blocked')) {
        const result = evaluateModern(rule, { raw: { observations: [{ featureId: rule.id, outcome: 'supported' }] } });
        assert.equal(result.conclusion, 'inconclusive', rule.id);
        assert.equal(result.blocker, rule.blocker);
        assert.ok(result.blocker.length > 60);
    }
    assert.match(ruleOf('sgr.blink').blocker, /real elapsed-time/);
    assert.match(ruleOf('modes.focus-tracking').blocker, /focus-out\/focus-in/);
    assert.match(ruleOf('extensions.clipboard-paste').blocker, /OS paste action/);
});

test('the exact DSR fixture can reuse a differently named source only with its positioned query', () => {
    const q = query('\x1b[6n', '\x1b[3;5R', '\x1b[3;5R');
    const evidence = traceEvidence('cursor.vpa', [q], ['\x1b[3;5H']);
    assert.equal(grade('cursor.position-report', evidence).conclusion, 'pass');
    const wrong = traceEvidence('cursor.vpa', [query('\x1b[6n', '\x1b[3;6R', '\x1b[3;6R')], ['\x1b[3;5H']);
    assert.equal(grade('cursor.position-report', wrong).conclusion, 'inconclusive');
});

test('the mapping never treats a raw reset recognition as enabled synchronized output', () => {
    assert.equal(grade('modes.synchronized-output', traceEvidence('modes.synchronized-output', [query('\x1b[?2026$p\x1b[c', '\x1b[?2026;2$y')])).conclusion, 'inconclusive');
});

test('changed criteria, duplicate mappings, changed source bytes and page membership are refused', () => {
    const files = snapshotFiles(join(modernRoot, modernPublication, 'upstream-snapshot.zip'));
    const changedCriterion = structuredClone(requirements);
    changedCriterion.rows[0].requirement = 'Only a cursor advance is enough';
    assert.throws(() => validateModernInputs(changedCriterion, mapping, files), /changed criterion/);
    const duplicate = structuredClone(mapping);
    duplicate.rows[1] = duplicate.rows[0];
    assert.throws(() => validateModernInputs(requirements, duplicate, files), /missing\/duplicate/);
    assert.throws(() => validateModernInputs(requirements, mapping, { ...files, 'features.json': Buffer.from('{}') }), /upstream member changed/);
    const changedPage = structuredClone(requirements);
    changedPage.rows[0].page = 'https://terminfo.dev/sgr/1-bold';
    assert.throws(() => validateModernInputs(changedPage, mapping, files), /live-page membership differs/);
    const changedHash = structuredClone(mapping);
    changedHash.rows[0].criterionSha256 = '0'.repeat(64);
    assert.throws(() => validateModernInputs(requirements, changedHash, files), /another criterion/);
});
