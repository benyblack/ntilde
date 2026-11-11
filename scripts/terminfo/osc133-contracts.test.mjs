import assert from 'node:assert/strict';
import test from 'node:test';
import { expectedOsc133Screen, makeOsc133Experiments, osc133CaseIds, osc133Fixture, osc133Minimum, osc133NonceRow, osc133Pinned, osc133Prefix, osc133Prompt, osc133Roles,
    replayOsc133Pinned, simulateOsc133 } from './osc133-contracts.mjs';

const runId = 'a'.repeat(32), fixture = osc133Fixture(runId), experiments = makeOsc133Experiments(runId);
const byId = id => experiments.find(item => item.caseId === id);
const state = (id, label) => {
    const feeds = [];
    for (const step of byId(id).plan) {
        if (step.feed !== undefined) feeds.push(step.feed);
        else if (step.read === label) return simulateOsc133(feeds, fixture);
    }
    throw new Error(`no read ${id} ${label}`);
};

test('the pinned fixtures are the exact terminfo.dev@4.0.0 bytes of the four cases', () => {
    assert.deepEqual([...osc133CaseIds], ['extensions.osc133-a', 'extensions.osc133-b', 'extensions.osc133-c', 'extensions.osc133-d']);
    assert.deepEqual(osc133Pinned['extensions.osc133-a'].termlessFeeds, ['\x1b]133;A\x07X']);
    assert.deepEqual(osc133Pinned['extensions.osc133-b'].terminalWrites, ['\x1b[1;1H\x1b[2K', '\x1b]133;B\x07X']);
    assert.deepEqual(osc133Pinned['extensions.osc133-c'].terminalWrites, ['\x1b[1;1H\x1b[2K', '\x1b]133;C\x07X']);
    assert.deepEqual(osc133Pinned['extensions.osc133-d'].terminalWrites, ['\x1b[1;1H\x1b[2K', '\x1b]133;D;0\x07X'], 'only D carries the pinned exit payload');
    for (const caseId of osc133CaseIds) {
        assert.equal(osc133Pinned[caseId].expectedChar, 'X');
        assert.equal(osc133Pinned[caseId].expectedCursorColumn, 2);
        assert.equal(osc133Pinned[caseId].termlessFeeds[0], osc133Pinned[caseId].terminalWrites[1]);
    }
    assert.deepEqual(Object.values(osc133Roles).map(role => role.role), ['prompt start', 'command-input start', 'command execution start', 'command completion']);
});

test('the fixture establishes a completed-cycle baseline, a nonce below every fixture row and a withdrawn runtime color', () => {
    assert.ok(fixture.reset.includes('\x1b]133;D;0\x07\x1b]133;A\x07'), 'a completed D then A resets every lifecycle fact');
    assert.ok(osc133NonceRow > 6 && osc133NonceRow < osc133Minimum.rows, 'the pinned ESC[1;1H ESC[2K can never erase the nonce');
    assert.ok(fixture.stage.endsWith(`\x1b[${osc133NonceRow + 1};1H${fixture.nonce}`));
    assert.ok(fixture.bind.startsWith(fixture.stage) && fixture.bind.endsWith('\x1b]4;7;rgb:12/34/56\x07'));
    assert.ok(fixture.finish.startsWith(fixture.reset) && fixture.finish.endsWith('\x1b]104;7\x07'));
    assert.throws(() => osc133Fixture('short'), /Invalid OSC 133 fixture run identity/);
    assert.notEqual(osc133Fixture('b'.repeat(32)).nonce, fixture.nonce);
});

test('the plan is fixed: 32 experiments, 46 reads, every one starting from the stage bytes and inside the 64-capture ceiling', () => {
    assert.equal(experiments.length, 32);
    const reads = experiments.reduce((sum, item) => sum + item.plan.filter(step => step.read).length, 0);
    assert.equal(reads, 46);
    assert.ok(reads + 3 <= 64, 'start, bind and finish count against the observer ceiling');
    assert.equal(new Set(experiments.map(item => item.caseId)).size, experiments.length);
    for (const item of experiments) {
        assert.equal(item.plan[0].feed, fixture.stage, item.caseId);
        assert.ok(osc133CaseIds.includes(item.owner) && item.caseId === `${item.owner}/${item.name}`);
        assert.ok(item.plan.at(-1).read !== undefined, 'every experiment ends in a read');
        assert.ok(['stock', 'effect', 'required', 'gate', 'disclosure', 'observed'].includes(item.role));
    }
    assert.deepEqual(experiments.filter(item => item.role === 'stock').map(item => item.owner), [...osc133CaseIds]);
    assert.equal(osc133Prefix, 'shi');
});

test('each case executes its pinned fixture twice: from the prepared baseline (stock) and from a measured prior state beside an unmarked control', () => {
    for (const caseId of osc133CaseIds) {
        const stock = byId(`${caseId}/stock`).plan.filter(step => step.feed !== undefined).map(step => step.feed);
        assert.deepEqual(stock, [fixture.stage, ...osc133Pinned[caseId].terminalWrites]);
        const effect = byId(`${caseId}/effect`).plan, feeds = effect.filter(step => step.feed !== undefined).map(step => step.feed);
        assert.deepEqual(feeds.slice(-2), osc133Pinned[caseId].terminalWrites, 'the effect experiment ends with the exact pinned writes');
        assert.deepEqual(effect.filter(step => step.read).map(step => step.read), ['prior', 'ordinary', 'pinned']);
        const control = feeds.slice(-4, -2);
        assert.deepEqual(control, ['\x1b[1;1H\x1b[2K', 'X'], 'the unmarked control is the pinned bytes without the marker');
        assert.ok(!feeds.slice(-4, -2).some(feed => feed.includes('\x1b]133;')));
    }
});

test('every other marker is a gate beside each case, and no gate marker is the case\'s own', () => {
    for (const caseId of osc133CaseIds) {
        const own = osc133Roles[caseId].marker;
        const gates = experiments.filter(item => item.owner === caseId && ['gate', 'disclosure'].includes(item.role));
        assert.equal(gates.length, caseId.endsWith('-a') ? 3 : 3);
        const letters = gates.map(item => /\x1b\]133;([ABCD])/.exec(item.plan.filter(step => step.feed !== undefined).at(-1).feed)[1]);
        assert.deepEqual([...letters].sort(), 'ABCD'.split('').filter(letter => letter !== own));
    }
    assert.deepEqual(experiments.filter(item => item.role === 'disclosure').map(item => item.caseId), ['extensions.osc133-a/disclose-d']);
});

test('the oracle: A leaves a running command for the prompt state and clears the command text; its effect on the unterminated anchors is not specified', () => {
    const prior = state('extensions.osc133-a/effect', 'prior'), after = state('extensions.osc133-a/effect', 'pinned');
    assert.equal(prior.status, 'running'); assert.equal(prior.command, 'echo a-run');
    assert.deepEqual([after.status, after.command], ['prompt', null]);
    for (const field of ['accepting', 'commandStart', 'outputStart']) assert.equal(after.certain[field], false, field);
    assert.deepEqual(after.commandStart, prior.commandStart, 'the unspecified anchors are carried as an implementation-policy prediction');
    for (const field of ['status', 'command']) assert.equal(after.certain[field], true);
});

test('the oracle: B anchors the command line at the cursor, opens the input window and exposes no command', () => {
    const boundary = state('extensions.osc133-b/boundary', 'input');
    assert.deepEqual([boundary.commandStart, boundary.accepting, boundary.status, boundary.command, boundary.outputStart], [[3, osc133Prompt.length], true, 'prompt', null, null]);
    assert.equal(boundary.rows[3], `${osc133Prompt}echo b-input`);
    assert.deepEqual(boundary.cursor, [3, osc133Prompt.length + 'echo b-input'.length]);
    assert.ok(Object.values(boundary.certain).every(Boolean));
    assert.deepEqual(state('extensions.osc133-b/effect', 'pinned').commandStart, [0, 0], 'the pinned B lands at the homed cursor');
});

test('the oracle: C closes the window, runs the command named by its payload (not the typed text) and starts output on the next row; a bare C names none', () => {
    const accepted = state('extensions.osc133-c/payload', 'accepted');
    assert.deepEqual([accepted.status, accepted.command, accepted.accepting, accepted.commandStart, accepted.outputStart], ['running', 'echo c-payload', false, [2, osc133Prompt.length], [3, 0]]);
    assert.equal(accepted.rows[2], `${osc133Prompt}echo c-typed`, 'the typed input line is not the command identity');
    assert.ok(Object.values(accepted.certain).every(Boolean));
    assert.deepEqual(state('extensions.osc133-c/payload', 'output').rows[3], 'c-output');
    const bare = state('extensions.osc133-c/effect', 'pinned');
    assert.deepEqual([bare.status, bare.command, bare.accepting, bare.certain.outputStart], ['running', null, false, false], 'the output anchor after a cursor jump is policy, not graded');
    assert.deepEqual(state('extensions.osc133-c/gate-d', 'post').outputStart, null);
});

test('the oracle: D closes the window and drops both anchors and the command; its exit payload is not interpreted', () => {
    for (const read of ['pinned']) {
        const done = state('extensions.osc133-d/effect', read);
        assert.deepEqual([done.status, done.command, done.accepting, done.commandStart, done.outputStart], ['prompt', null, false, null, null]);
        assert.ok(Object.values(done.certain).every(Boolean));
    }
    const seven = state('extensions.osc133-d/exit-payload', 'post'), zero = state('extensions.osc133-d/effect', 'pinned');
    assert.deepEqual([seven.status, seven.command, seven.commandStart, seven.outputStart], [zero.status, zero.command, zero.commandStart, zero.outputStart]);
    assert.ok(!('exitCode' in seven), 'no exit status is predicted from the payload');
});

test('the oracle marks duplicate and out-of-order markers uncertain instead of inventing semantics', () => {
    const duplicateC = state('extensions.osc133-a/gate-c', 'post');
    for (const field of ['status', 'command', 'accepting', 'outputStart']) assert.equal(duplicateC.certain[field], false, field);
    const bWhileRunning = state('extensions.osc133-d/gate-b', 'post');
    assert.deepEqual([bWhileRunning.certain.status, bWhileRunning.certain.command, bWhileRunning.certain.commandStart, bWhileRunning.certain.accepting], [false, false, true, true]);
    const completedThenA = state('extensions.osc133-b/gate-a', 'post');
    assert.ok(Object.values(completedThenA.certain).every(Boolean), 'A at a clean prompt changes nothing');
});

test('consecutive cycles never carry the first cycle\'s anchors or command text', () => {
    const afterFirst = state('extensions.osc133-c/cycles', 'after-d'), second = state('extensions.osc133-c/cycles', 'second');
    assert.deepEqual([afterFirst.command, afterFirst.commandStart, afterFirst.outputStart, afterFirst.accepting], [null, null, null, false]);
    assert.deepEqual([second.command, second.commandStart, second.outputStart], ['echo c-two', [6, osc133Prompt.length], [7, 0]]);
    assert.equal(state('extensions.osc133-b/cycles', 'second').command, null, 'B alone exposes no command, and none leaks from cycle one');
    assert.deepEqual(state('extensions.osc133-b/cycles', 'second').commandStart, [6, osc133Prompt.length]);
});

test('the oracle models cells and the cursor, refuses bytes it does not understand and never changes the fixture row', () => {
    const baseline = simulateOsc133([fixture.stage], fixture);
    assert.deepEqual(baseline.rows, { [osc133NonceRow]: fixture.nonce });
    assert.deepEqual(baseline.cursor, [osc133NonceRow, fixture.nonce.length]);
    const homed = simulateOsc133([fixture.stage, '\x1b[1;1H\x1b[2K', 'X'], fixture);
    assert.equal(homed.rows[0], 'X'); assert.deepEqual(homed.cursor, [0, 1]); assert.equal(homed.rows[osc133NonceRow], fixture.nonce);
    assert.throws(() => simulateOsc133([fixture.stage, '\x1b[31m'], fixture), /does not understand/);
    const screen = expectedOsc133Screen({ rows: 12, cols: 60, lines: Array(12).fill(''), cursorRow: 0, cursorCol: 0, cursorVisible: true }, homed);
    assert.deepEqual([screen.lines[0], screen.cursorRow, screen.cursorCol], ['X', 0, 1]);
});

test('the pinned replay models promptConsumptionResult: always inconclusive, consumed when X is at (0,0) and the cursor sits in column 2', () => {
    const consumed = { lines: ['X', '', ''], cursorRow: 0, cursorCol: 1 };
    for (const caseId of osc133CaseIds) {
        const replay = replayOsc133Pinned(caseId, consumed);
        for (const path of ['termless', 'terminal']) {
            assert.equal(replay[path].pass, false); assert.equal(replay[path].observation.outcome, 'inconclusive');
            assert.equal(replay[path].observation.evidence, 'consumed'); assert.match(replay[path].note, /Marker was consumed; shell prompt integration was not observed/);
        }
    }
    const moved = replayOsc133Pinned('extensions.osc133-a', { lines: ['', '', ''], cursorRow: 0, cursorCol: 0 });
    assert.match(moved.termless.note, /Marker consumption was not confirmed/); assert.match(moved.terminal.note, /Marker consumption was not confirmed/);
});
