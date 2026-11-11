import assert from 'node:assert/strict';
import test from 'node:test';
import { expectedOsc78Screen, makeOsc78Experiments, osc7Forms, osc78CaseIds, osc78DeclaredCapabilities, osc78ExperimentCount, osc78Fixture, osc78Minimum, osc78NonceRow, osc78Pinned,
    osc78ReadCount, osc78TerminalContext, osc78TermlessContext, osc7CaseId, osc8CaseId, osc8Forms, osc8PinnedUri, osc8StoredCells, replayOsc78Pinned, replayOsc7Pinned,
    replayOsc8Terminal, replayOsc8Termless, simulateOsc78 } from './osc78-contracts.mjs';

const runId = 'a'.repeat(32), short = 'aaaaaaaa';
const fixture = osc78Fixture(runId);
const stateAfter = (experiment, label) => {
    const contract = makeOsc78Experiments(runId).find(item => item.caseId === experiment), feeds = [];
    for (const step of contract.plan) {
        if (step.feed !== undefined) feeds.push(step.feed);
        else if (step.read === label) return simulateOsc78(feeds, fixture, runId);
    }
    throw new Error(`No read ${experiment}/${label}`);
};

test('the fixture binds a 32-hex run, keeps the nonce below every fixture row and closes links before it starts', () => {
    assert.throws(() => osc78Fixture('short'), /Invalid OSC 7\/8 fixture run identity/);
    assert.equal(fixture.nonce, `NTILDE_LK_${runId}`);
    assert.ok(fixture.stage.endsWith(`\x1b[${osc78NonceRow + 1};1H${fixture.nonce}`));
    assert.ok(fixture.reset.startsWith('\x1b]8;;\x07'), 'a link left open by an earlier suite is closed first');
    assert.ok(fixture.reset.includes(`\x1b]2;LK-base-${short}\x07`) && fixture.reset.includes('\x1b]7;file://localhost/ntilde-qa/osc78-base\x07'));
    assert.ok(fixture.bind.endsWith('\x1b]4;7;rgb:12/34/56\x07') && fixture.finish.endsWith('\x1b]104;7\x07'));
    assert.deepEqual(osc78Minimum, { rows: 12, cols: 48 });
});

test('the pinned OSC 8 terminal writes are exactly the pinned termless feed; the pinned OSC 7 probe has no bytes at all', () => {
    const osc8 = osc78Pinned[osc8CaseId];
    assert.equal(osc8.terminalWrites.join(''), osc8.termlessFeeds[0]);
    assert.equal(osc8.termlessFeeds[0], `\x1b[1;1H\x1b[2KA\x1b]8;;${osc8PinnedUri}\x07LINK\x1b]8;;\x07Z`);
    assert.equal(osc8PinnedUri, 'https://example.com/osc8-proof');
    const osc7 = osc78Pinned[osc7CaseId];
    assert.deepEqual([osc7.termlessFeeds, osc7.terminalWrites, osc7.fixtureBytes], [[], [], false]);
    assert.deepEqual([...osc78DeclaredCapabilities], ['hyperlinks', 'osc7']);
});

test('the plan has 14 experiments and 23 reads inside the 64-capture ceiling, each starting from the exact stage bytes, and opens or requests no URL', () => {
    const experiments = makeOsc78Experiments(runId);
    assert.equal(experiments.length, osc78ExperimentCount);
    assert.equal(experiments.reduce((sum, item) => sum + item.plan.filter(step => step.read).length, 0), osc78ReadCount);
    assert.ok(osc78ReadCount + 3 <= 64);
    assert.deepEqual([...new Set(experiments.map(item => item.owner))], [...osc78CaseIds]);
    assert.equal(new Set(experiments.map(item => item.caseId)).size, experiments.length);
    for (const item of experiments) assert.equal(item.plan[0].feed, fixture.stage, item.caseId);
    const bytes = experiments.flatMap(item => item.plan.filter(step => step.feed !== undefined).map(step => step.feed)).join('');
    const urls = [];
    for (let start = bytes.indexOf('http'); start >= 0; start = bytes.indexOf('http', start + 4)) {
        const ends = ['\x07', '\x1b'].map(char => bytes.indexOf(char, start)).filter(index => index >= 0);
        urls.push(bytes.slice(start, Math.min(...ends)));
    }
    assert.ok(urls.length >= 8, String(urls.length));
    for (const url of urls) assert.ok(url.startsWith('https://example.com/') || url.startsWith('https://example.invalid/'), url);
    for (const forbidden of ['\x1b]52;', '\x1b]9;N', '\x1b]9;O', '\x1b]1337;']) assert.ok(!bytes.includes(forbidden), `no clipboard, notification or image bytes: ${forbidden}`);
});

test('the expected OSC 7 paths are independent literals: the authority is dropped and an encoded space is decoded', () => {
    const forms = osc7Forms(runId);
    assert.deepEqual(Object.fromEntries(Object.values(forms).map(form => [form.name, form.path])), {
        base: '/ntilde-qa/osc78-base', first: `/ntilde-qa/osc7-${short}/first`, second: `/ntilde-qa/osc7-${short}/second`,
        space: `/ntilde-qa/osc7 space/${short}`, authority: `/ntilde-qa/osc7-authority/${short}` });
    assert.equal(forms.space.uri, `file://localhost/ntilde-qa/osc7%20space/${short}`);
    assert.equal(forms.authority.uri, `file://remote-host.example/ntilde-qa/osc7-authority/${short}`);
    assert.equal(stateAfter('extensions.osc7-cwd/space', 'encoded').cwd, `/ntilde-qa/osc7 space/${short}`);
    assert.equal(stateAfter('extensions.osc7-cwd/authority', 'post').cwd, `/ntilde-qa/osc7-authority/${short}`);
    assert.equal(stateAfter('extensions.osc7-cwd/effect', 'ordinary').cwd, '/ntilde-qa/osc78-base');
    assert.equal(stateAfter('extensions.osc7-cwd/effect', 'first').cwd, `/ntilde-qa/osc7-${short}/first`);
    assert.equal(stateAfter('extensions.osc7-cwd/effect', 'second').cwd, `/ntilde-qa/osc7-${short}/second`);
    assert.equal(stateAfter('extensions.osc7-cwd/effect', 'restored').cwd, '/ntilde-qa/osc78-base');
    const gate = stateAfter('extensions.osc7-cwd/gate-title', 'post');
    assert.deepEqual([gate.title, gate.cwd], [`LK-title-${short}`, '/ntilde-qa/osc78-base']);
});

test('the oracle predicts the pinned OSC 8 cells: ALINKZ, one run over columns 1-4, no id, the witness and the leading A unlinked, and the later text unlinked', () => {
    const pinned = stateAfter('extensions.osc8/effect', 'pinned');
    assert.deepEqual(pinned.runs, [{ row: 0, col: 1, length: 4, identity: 0, uri: osc8PinnedUri, id: null }]);
    assert.equal(pinned.rows[0], 'ALINKZ'); assert.deepEqual(pinned.cursor, [0, 6]);
    assert.deepEqual(stateAfter('extensions.osc8/effect', 'ordinary').runs, []);
    assert.equal(stateAfter('extensions.osc8/effect', 'ordinary').rows[0], 'ALINKZ');
    assert.deepEqual(stateAfter('extensions.osc8/effect', 'later').runs, pinned.runs);
    assert.equal(stateAfter('extensions.osc8/effect', 'later').rows[4], 'later');
    assert.deepEqual(stateAfter('extensions.osc8/gate-close', 'post').runs, []);
    const links = osc8Forms(runId);
    assert.deepEqual(stateAfter('extensions.osc8/id', 'linked').runs, [{ row: 1, col: 1, length: 4, identity: 0, uri: links.idUri, id: links.idValue }]);
    assert.deepEqual(stateAfter('extensions.osc8/two-links', 'linked').runs.map(run => [run.col, run.length, run.identity, run.uri]),
        [[1, 3, 0, links.oneUri], [4, 3, 1, links.twoUri]], 'two distinct links stay two runs with distinct snapshot-local identities');
    assert.deepEqual(stateAfter('extensions.osc8/stale', 'cleared').runs, []);
    assert.deepEqual(stateAfter('extensions.osc8/stale', 'second').runs.map(run => run.uri), [links.secondFixtureUri]);
    assert.deepEqual(stateAfter('extensions.osc8/same-anchor', 'post').runs.map(run => [run.col, run.identity]), [[0, 0], [3, 0]]);
});

test('the oracle refuses any feed it was not contracted for', () => {
    for (const feed of ['\x1b]7;file://localhost/unknown\x07', '\x1b]52;c;AAAA\x07', '\x1b]8;bogus;https://example.invalid/x\x07', '\x1b[2J', '\x1b]7;file://localhost/ntilde-qa/osc78-base']) {
        assert.throws(() => simulateOsc78([feed], fixture, runId), /does not understand a contracted feed/, JSON.stringify(feed));
    }
});

test('the expected whole screen keeps every stored fact of the bound baseline except rows and cursor', () => {
    const bound = { rows: 12, cols: 48, lines: Array(12).fill(''), cursorRow: 9, cursorCol: 42, colorBinding: { x: 1 }, marker: 'kept' };
    const screen = expectedOsc78Screen(bound, stateAfter('extensions.osc8/effect', 'pinned'));
    assert.equal(screen.lines[0], 'ALINKZ'); assert.deepEqual([screen.cursorRow, screen.cursorCol], [0, 6]);
    assert.deepEqual([screen.marker, screen.colorBinding], ['kept', { x: 1 }]);
    assert.equal(bound.lines[0], '', 'the bound screen is not modified');
});

const cells = (chars, uris) => [...chars].map((char, index) => ({ char, uri: uris[index] }));
const U = osc8PinnedUri;

test('the independent pinned OSC 8 termless replay decides exactly as the pinned code does: supported, unsupported or inconclusive', () => {
    const supported = replayOsc8Termless(cells('ALINKZ', [null, U, U, U, U, null]));
    assert.deepEqual([supported.pass, supported.observation.outcome, supported.assertions[0].kind], [true, 'supported', 'positive']);
    assert.equal(JSON.parse(supported.response).links[1].uri, U);
    for (const [name, uris] of [['link leaked past the close', [null, U, U, U, U, U]], ['link before the text', [U, U, U, U, U, null]], ['wrong URI', [null, U, U, U, U + 'x', null]]]) {
        const result = replayOsc8Termless(cells('ALINKZ', uris));
        assert.deepEqual([result.pass, result.observation.outcome, result.assertions[0].kind], [false, 'unsupported', 'negative'], name);
        assert.match(result.observation.note, /differs from the exact requested link or leaked past close/);
    }
    for (const [name, chars, uris] of [['no metadata', 'ALINKZ', [null, null, null, null, null, null]], ['wrong text', 'ALINKX', [null, U, U, U, U, null]], ['short link', 'ALINKZ', [null, U, U, U, null, null]]]) {
        const result = replayOsc8Termless(cells(chars, uris));
        assert.deepEqual([result.pass, result.observation.outcome, result.observation.note], [false, 'inconclusive', 'Linked text was not observable'], name);
        assert.equal(result.assertions, undefined);
    }
});

test('the pinned terminal-path replay never decides: the cursor position, a missing reply and a grid below 1x7 are all inconclusive', () => {
    const geometry = { rows: 12, cols: 48 };
    assert.deepEqual(replayOsc8Terminal(geometry, { row: 1, col: 7 }), { pass: false, response: '1;7',
        observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'consumed', note: 'Cursor movement cannot verify OSC 8 link metadata or click behavior' } });
    assert.equal(replayOsc8Terminal(geometry, null).observation.reason, 'no-response');
    assert.match(replayOsc8Terminal({ rows: 12, cols: 6 }, { row: 1, col: 7 }).observation.note, /needs at least 1x7, measured 12x6/);
    const osc7 = replayOsc7Pinned();
    assert.deepEqual([osc7.termless.observation.outcome, osc7.termless.observation.evidence, osc7.terminal.observation.outcome, osc7.terminal.observation.evidence],
        ['inconclusive', 'legacy', 'inconclusive', 'none']);
    assert.equal(osc7.termless.response, '{"declared":true}');
    assert.equal(osc7.terminal.response, undefined, 'the pinned OSC 7 terminal path reads nothing');
});

test('the replay contexts refuse any feed, write or read outside the pinned fixture', async () => {
    const screen = { lines: ['ALINKZ'], rows: 12, cols: 48, cursorRow: 0, cursorCol: 6 }, links = [{ row: 0, col: 1, length: 4, identity: 0, uri: U, id: null }];
    const termless = osc78TermlessContext(osc8CaseId, screen, links);
    assert.throws(() => termless.getCell(0, 0), /read differs from the contract/, 'before the feed');
    assert.throws(() => termless.feed('\x1b]8;;https://example.com/other\x07'), /feed differs from the contract/);
    termless.feed(osc78Pinned[osc8CaseId].termlessFeeds[0]);
    assert.deepEqual([termless.getCell(0, 5).char, termless.getHyperlinkAt(0, 0), termless.getHyperlinkAt(0, 2)], ['Z', null, U]);
    assert.throws(() => termless.getCell(1, 0), /read differs/); assert.throws(() => termless.getHyperlinkAt(0, 6), /read differs/);
    assert.throws(() => termless.feed(osc78Pinned[osc8CaseId].termlessFeeds[0]), /feed differs/, 'a second feed');
    assert.equal(termless.fedCount, 1); assert.ok(termless.capabilities.extensions.has('hyperlinks') && termless.capabilities.extensions.has('osc7'));
    const osc7 = osc78TermlessContext(osc7CaseId, screen, []);
    assert.throws(() => osc7.feed('\x1b]7;file://localhost/x\x07'), /feed differs/); assert.throws(() => osc7.getCell(0, 0), /read differs/);
    const terminal = osc78TerminalContext(osc8CaseId, screen);
    assert.throws(() => terminal.queryCursorPosition(), /queried the cursor early/);
    assert.throws(() => terminal.write('x'), /write differs/);
    for (const bytes of osc78Pinned[osc8CaseId].terminalWrites) terminal.write(bytes);
    assert.deepEqual(await terminal.queryCursorPosition(), { row: 1, col: 7 });
    assert.deepEqual([terminal.rows, terminal.cols, terminal.writtenCount], [12, 48, 2]);
    assert.throws(() => osc78TerminalContext(osc7CaseId, screen).write('x'), /write differs/);
});

test('the stored cells of a stock read are the characters and the URI of the run covering each cell', () => {
    const screen = { lines: ['ALINKZ'] }, links = [{ row: 0, col: 1, length: 4, identity: 0, uri: U, id: null }, { row: 3, col: 0, length: 6, identity: 1, uri: 'x', id: null }];
    assert.deepEqual(osc8StoredCells(screen, links).map(cell => [cell.char, cell.uri]), [['A', null], ['L', U], ['I', U], ['N', U], ['K', U], ['Z', null]]);
    assert.deepEqual(replayOsc78Pinned(osc8CaseId, { ...screen, rows: 12, cols: 48, cursorRow: 0, cursorCol: 6 }, links).termless.observation.outcome, 'supported');
    assert.deepEqual(replayOsc78Pinned(osc7CaseId, { ...screen, rows: 12, cols: 48, cursorRow: 0, cursorCol: 6 }, []), replayOsc7Pinned());
});
