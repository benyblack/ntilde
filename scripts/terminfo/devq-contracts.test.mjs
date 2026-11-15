import assert from 'node:assert/strict';
import { test } from 'node:test';
import { devqContracts, devqCaseIds, devqFixture, devqSessionTerm, replayDevqPinned, xresOracle, xresRequest, XtermColorModel, termNameHex, devqPlanSeed, devqReset, devqReseed,
    devqSeed, devqFinish, devqColorReset, devqBaseTitle, gradeDevqCase, devqObservation, termFeaturesCaseId, titleReply } from './devq-contracts.mjs';
import { syntheticDevqExchanges, syntheticDevqGrade, syntheticDevqScreens } from './fixtures/devq-evidence.mjs';
import { planFeeds, planReads } from './graded-plan.mjs';

const st = '\x1b\\';
const term = '787465726D2D323536636F6C6F72';

test('the four pinned cases are contracted and device.term-features is documented only', () => {
    assert.deepEqual([...devqCaseIds], ['device.xtgetxres', 'device.xtreportcolors', 'device.xtwinops-20', 'device.xtwinops-21']);
    assert.deepEqual(devqContracts.map(item => item.caseId), [...devqCaseIds]);
    assert.equal(termFeaturesCaseId, 'device.term-features');
    assert.equal(devqCaseIds.includes(termFeaturesCaseId), false, 'the legacy env probe emits no request and has no case');
    // 3 frames (start, bind, finish) + reads must stay inside the 64-capture suite budget.
    assert.ok(3 + devqContracts.reduce((sum, item) => sum + planReads(item), 0) <= 64);
});

test('the pinned feeds are the exact bytes the pinned code sends', () => {
    const byId = Object.fromEntries(devqContracts.map(item => [item.caseId, item]));
    assert.deepEqual(byId['device.xtgetxres'].pinnedFeeds, ['\x1bP+Q7465726d4e616d65\x1b\\']);
    assert.deepEqual(byId['device.xtreportcolors'].pinnedFeeds, ['\x1b[#R']);
    assert.deepEqual(byId['device.xtwinops-20'].pinnedFeeds, ['\x1b]1;test-icon\x07', '\x1b[20t']);
    assert.deepEqual(byId['device.xtwinops-21'].pinnedFeeds, ['\x1b]2;test-title\x07', '\x1b[21t']);
    for (const item of devqContracts) {
        assert.equal(item.pinnedRead, item.roles.indexOf('pinned'));
        const pinnedGroup = item.groups[item.pinnedRead - 1];
        assert.equal(pinnedGroup.role, 'pinned');
        assert.equal(pinnedGroup.request, item.pinnedFeeds.at(-1), 'the pinned group sends exactly the pinned query');
        assert.equal(pinnedGroup.setup, item.pinnedSetup ? item.pinnedFeeds[0] : null);
    }
});

test('the plan is a reseed, an initial cells read, and per group an optional setup, a captured request and a report read', () => {
    for (const item of devqContracts) {
        assert.deepEqual(item.plan[0], { feed: devqReseed });
        assert.deepEqual(item.plan[1], { read: 'cells' });
        assert.equal(planReads(item), 1 + item.groups.length);
        assert.equal(planFeeds(item).length, 1 + item.groups.length + item.groups.filter(group => group.setup !== null).length + (item.caseId === 'device.xtreportcolors' ? 1 : 0));
        if (item.caseId === 'device.xtreportcolors') assert.equal(item.plan.at(-1).feed, devqColorReset, 'the recoloring case ends with an explicit colour reset');
        assert.equal(item.plan.filter(step => step.capture).length, item.groups.length);
        assert.equal(new Set(item.roles).size, item.roles.length, 'roles are unique');
    }
});

test('the pinned replay mirrors deviceReplyResult for each spec', () => {
    const value = `\x1bP1+R7465726d4e616d65=${term}${st}`;
    const supported = replayDevqPinned('device.xtgetxres', value);
    assert.equal(supported.pass, true);
    assert.deepEqual(supported.observation, { outcome: 'supported', evidence: 'query' });
    assert.deepEqual(supported.assertions, [{ kind: 'positive', expected: 'XTGETXRES returns a complete status-1 termName resource value', observed: value }]);
    const refused = replayDevqPinned('device.xtgetxres', `\x1bP0+R7465726d4e616d65${st}`);
    assert.deepEqual(refused.observation, { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'query', note: 'Requested setting or name refused; other settings or names unmeasured' });
    assert.equal(replayDevqPinned('device.xtgetxres', `\x1bP1+R7465726D4E616D65=${term}${st}`).pass, true, 'the pinned pattern is case-insensitive');
    assert.equal(replayDevqPinned('device.xtgetxres', `\x1bP1+R7465726d4e616d65=${st}`).observation.reason, 'invalid-reply', 'a value-less status-1 frame is the malformed case');
    assert.equal(replayDevqPinned('device.xtgetxres', '\x1b[?62;4;22c' + value).observation.reason, 'no-response', 'a frame after the primary DA is not a reply');
    assert.deepEqual(replayDevqPinned('device.xtgetxres').observation, { outcome: 'inconclusive', reason: 'no-response', evidence: 'query' });

    const icon = replayDevqPinned('device.xtwinops-20', `\x1b]Ltest-icon${st}`);
    assert.equal(icon.pass, true);
    assert.equal(replayDevqPinned('device.xtwinops-20', '\x1b]Ltest-icon\x07').pass, true, 'a BEL terminator is also the pinned frame');
    const wrong = replayDevqPinned('device.xtwinops-20', `\x1b]Lother${st}`);
    assert.deepEqual(wrong.observation, { outcome: 'unsupported', evidence: 'query' });
    assert.deepEqual(wrong.assertions, [{ kind: 'negative', expected: 'OSC 1 + CSI 20 t round trip returns the exact icon label', observed: `\x1b]Lother${st}` }]);
    assert.equal(replayDevqPinned('device.xtwinops-20', '\x1b]L').observation.reason, 'invalid-reply');
    assert.equal(replayDevqPinned('device.xtwinops-21', `\x1b]ltest-title${st}`).pass, true);
    assert.equal(replayDevqPinned('device.xtwinops-21', `\x1b]lother${st}`).observation.reason, 'invalid-reply', 'a wrong window title has no contradiction branch');
    assert.throws(() => replayDevqPinned('device.other'), /Unknown device-query case/);
});

test('the pinned XTREPORTCOLORS pattern cannot match xterm\'s own reply, which carries a ? marker', () => {
    const xterm = replayDevqPinned('device.xtreportcolors', '\x1b[?0;0#Q');
    assert.equal(xterm.pass, false);
    assert.deepEqual(xterm.observation, { outcome: 'inconclusive', reason: 'invalid-reply', evidence: 'query' });
    const unmarked = replayDevqPinned('device.xtreportcolors', '\x1b[0;0#Q');
    assert.equal(unmarked.pass, true, 'a reply with no ? marker is what the pinned pattern demands');
    assert.deepEqual(unmarked.assertions, [{ kind: 'positive', expected: 'CSI # R returns a complete CSI Pm # Q frame', observed: '\x1b[0;0#Q' }]);
    assert.equal(replayDevqPinned('device.xtreportcolors', '\x1b[#Q').pass, true, 'the pinned class also allows no parameters');
});

test('the XTGETXRES oracle is written from xterm\'s do_dcs case Q, not the parser', () => {
    assert.deepEqual(xresOracle(termNameHex), [`\x1bP1+R${termNameHex}=${term}${st}`]);
    assert.equal(devqSessionTerm, 'xterm-256color');
    assert.deepEqual(xresOracle('7465726D4E616D65'), [`\x1bP1+R7465726D4E616D65=${term}${st}`], 'the name is echoed as spelled');
    assert.deepEqual(xresOracle('666f6f'), [`\x1bP0+R666f6f${st}`]);
    assert.deepEqual(xresOracle('7465726d6e616d65'), [`\x1bP0+R7465726d6e616d65${st}`], 'names are case-sensitive');
    assert.deepEqual(xresOracle(`${termNameHex};${termNameHex}`), [`\x1bP1+R${termNameHex}=${term};${termNameHex}=${term}${st}`]);
    assert.deepEqual(xresOracle(`${termNameHex};666f6f;${termNameHex}`), [`\x1bP1+R${termNameHex}=${term};666f6f${st}`], 'stops after the first unknown name, status of the first');
    assert.deepEqual(xresOracle(`666f6f;${termNameHex}`), [`\x1bP0+R666f6f${st}`]);
    for (const bad of ['', '5', 'zz', '74;', ';74', '20', '7f', 'c3a9', Array.from({ length: 17 }, () => termNameHex).join(';')]) assert.deepEqual(xresOracle(bad), [], JSON.stringify(bad));
    assert.equal(xresRequest('5'), '\x1bP+Q5\x1b\\');
    assert.deepEqual(xresOracle(termNameHex, 'screen'), [`\x1bP1+R${termNameHex}=${Buffer.from('screen').toString('hex').toUpperCase()}${st}`], 'the value follows the session TERM it is given');
});

test('the xterm palette-stack model follows xtermPushColors, xtermPopColors and xtermReportColors', () => {
    const model = new XtermColorModel();
    assert.equal(model.report(), '\x1b[?0;0#Q');
    model.set('bg', '#112233'); model.push(0);
    assert.equal(model.report(), '\x1b[?1;1#Q');
    model.set('bg', '#445566'); model.push(0);
    assert.equal(model.report(), '\x1b[?2;2#Q');
    model.pop(0);
    assert.equal(model.report(), '\x1b[?1;2#Q', 'the deepest never shrinks');
    assert.equal(model.colors.bg, '#445566');
    model.pop(0); model.pop(0);
    assert.equal(model.report(), '\x1b[?0;2#Q', 'a pop with nothing below changes nothing');
    model.set('bg', '#aaaaaa'); model.push(4);
    assert.equal(model.report(), '\x1b[?4;2#Q', 'a numbered push sets the depth to the slot and leaves the deepest');
    model.pop(4);
    assert.equal(model.report(), '\x1b[?3;2#Q', 'a numbered pop sets the depth to the slot below');
    model.push(11); model.pop(11);
    assert.equal(model.report(), '\x1b[?3;2#Q', 'slots past ten are ignored');
    for (let index = 0; index < 12; index++) model.push(0);
    assert.equal(model.report(), '\x1b[?10;10#Q', 'the ten-slot bound');
    assert.equal(model.query('bg'), '\x1b]11;rgb:aaaa/aaaa/aaaa' + st);
});

test('the colour-stack groups are the model\'s own, with hand-checked replies', () => {
    const item = devqContracts.find(entry => entry.caseId === 'device.xtreportcolors');
    const group = role => item.groups.find(entry => entry.role === role);
    assert.deepEqual(group('pinned').demand, ['\x1b[?0;0#Q']);
    assert.equal(group('push-1').request, '\x1b[#P\x1b[#R');
    assert.deepEqual(group('push-2').demand, ['\x1b[?2;2#Q']);
    assert.deepEqual(group('pop-1').demand, ['\x1b[?1;2#Q', '\x1b]11;rgb:aaaa/bbbb/cccc' + st, '\x1b]4;5;rgb:dddd/eeee/ffff' + st]);
    assert.deepEqual(group('pop-2').demand, ['\x1b[?0;2#Q', '\x1b]11;rgb:1111/2222/3333' + st, '\x1b]4;5;rgb:4444/5555/6666' + st]);
    assert.deepEqual(group('slot-store').demand, ['\x1b[?4;2#Q']);
    assert.deepEqual(group('slot-restore').demand, ['\x1b[?3;2#Q', '\x1b]11;rgb:1111/2222/3333' + st, '\x1b]4;5;rgb:4444/5555/6666' + st]);
    assert.deepEqual(group('fill').demand, ['\x1b[?10;10#Q']);
    assert.deepEqual(group('drain').demand, ['\x1b[?0;10#Q'], 'every slot is stored, so ten pops reach depth 0 and the deepest stays 10');
    assert.equal(group('set-base').setup, '\x1b]11;#112233\x1b\\\x1b]4;5;#445566\x1b\\');
    assert.equal(group('kitty-independent').request.includes('\x1b]30001\x1b\\'), true);
    assert.equal(group('spellings').kind, 'policy');
});

test('hand-written fixture replies agree with every contracted group (two independent oracles)', () => {
    for (const item of devqContracts) {
        for (const group of item.groups) assert.deepEqual([...group.demand], syntheticDevqExchanges[item.caseId][group.role], `${item.caseId}/${group.role}`);
        assert.deepEqual(Object.keys(syntheticDevqExchanges[item.caseId]), item.groups.map(group => group.role));
    }
    assert.deepEqual(titleReply('l', 'x'), '\x1b]lx\x1b\\');
});

test('the title groups cover distinct state, OSC 0, empty and semicolon labels, the title stack and stability', () => {
    for (const [caseId, code, other] of [['device.xtwinops-20', 'L', 'l'], ['device.xtwinops-21', 'l', 'L']]) {
        const item = devqContracts.find(entry => entry.caseId === caseId);
        assert.deepEqual(item.groups.map(group => group.role), ['pinned', 'distinct', 'osc0-both', 'empty', 'semicolons', 'stack', 'stable', 'control-character', 'spellings']);
        const distinct = item.groups.find(group => group.role === 'distinct');
        assert.deepEqual(distinct.demand.map(reply => reply[2]), [code, other], 'the own report is asked first, then the other');
        assert.equal(item.groups.find(group => group.role === 'stack').demand.length, 3);
        assert.equal(item.groups.find(group => group.role === 'control-character').kind, 'policy');
        assert.equal(item.groups.find(group => group.role === 'spellings').kind, 'policy');
    }
});

test('the fixture bytes', () => {
    assert.equal(devqPlanSeed('a'.repeat(32)), devqReset + devqSeed('NTILDE_DQ_' + 'a'.repeat(32)));
    assert.throws(() => devqPlanSeed('x'), /Invalid device-query fixture run identity/);
    assert.equal(devqColorReset, '\x1b]104\x1b\\\x1b]110\x1b\\\x1b]111\x1b\\\x1b]112\x1b\\\x1b]117\x1b\\\x1b]119\x1b\\');
    const finish = devqFinish('a'.repeat(32));
    assert.equal(finish, devqReset + devqColorReset + `\x1b]0;${devqBaseTitle('a'.repeat(32))}\x1b\\` + '\x1b[?1049l');
    assert.equal(devqBaseTitle('abcdef0123456789abcdef0123456789'), 'DQ-base-abcdef01');
    assert.deepEqual(devqFixture.minimum, { rows: 8, cols: 48 });
});

test('a faithful synthetic observation is supported for all four cases', () => {
    for (const caseId of devqCaseIds) assert.deepEqual(syntheticDevqGrade(caseId), { outcome: 'supported' }, caseId);
});

test('a wrong status, label, colour, count or order is unsupported; a failed control is inconclusive; policy never decides', () => {
    const wrong = (caseId, role, replies) => syntheticDevqGrade(undefined === caseId ? 'device.xtgetxres' : caseId, undefined, (id, current, read) => (id === caseId && current === role ? { ...read, replies } : read));
    assert.match(wrong('device.xtgetxres', 'pinned', []).note, /was not answered/);
    assert.match(wrong('device.xtgetxres', 'unknown', ['\x1bP1+R666f6f=41\x1b\\']).note, /differs from the reply oracle/);
    assert.match(wrong('device.xtgetxres', 'case-and-class', ['\x1bP0+R7465726d6e616d65\x1b\\']).note, /reply count differs/);
    assert.equal(wrong('device.xtgetxres', 'pinned', []).outcome, 'unsupported');
    assert.equal(wrong('device.xtreportcolors', 'pinned', ['\x1b[0;0#Q']).outcome, 'unsupported', 'the marker-less reply is not xterm\'s');
    assert.equal(wrong('device.xtreportcolors', 'push-1', ['\x1b[?1;0#Q']).outcome, 'unsupported');
    assert.equal(wrong('device.xtreportcolors', 'spellings', []).outcome, 'supported');
    const control = wrong('device.xtreportcolors', 'set-base', ['\x1b]11;rgb:0000/0000/0000\x1b\\', '\x1b]4;5;rgb:4444/5555/6666\x1b\\']);
    assert.equal(control.outcome, 'inconclusive');
    assert.match(control.note, /Setup control failed/);
    assert.equal(wrong('device.xtwinops-20', 'pinned', ['\x1b]Lwrong\x1b\\']).outcome, 'unsupported');
    assert.equal(wrong('device.xtwinops-21', 'stack', ['\x1b]lstk-1\x1b\\', '\x1b]lstk-1\x1b\\', '\x1b]lstk-1\x1b\\']).outcome, 'unsupported');
    assert.equal(wrong('device.xtwinops-21', 'control-character', ['\x1b]la\tb\x1b\\']).outcome, 'supported');
});

test('every exchange must leave the screen, cursor, rendition, margins, modes and active screen unchanged', () => {
    for (const [label, change] of [
        ['a changed cell', screen => { screen.lines[2] = 'X'; }],
        ['a moved cursor', screen => { screen.cursorRow = 3; }],
        ['a changed margin', screen => { screen.bufferState.scrollTop = 0; }],
        ['a left-right margin', screen => { screen.horizontalMargins = { ...screen.horizontalMargins, leftRightMarginMode: true }; }],
        ['a hidden cursor', screen => { screen.cursorVisible = !screen.cursorVisible; }],
    ]) {
        const result = syntheticDevqGrade('device.xtwinops-21', undefined, (id, role, read) => {
            if (id === 'device.xtwinops-21' && role === 'stable') change(read.screen);
            return read;
        });
        assert.equal(result.outcome, 'unsupported', label);
        assert.match(result.note, /a device report must change nothing/, label);
    }
});

test('an unmeasured seed is inconclusive, never a verdict', () => {
    const result = syntheticDevqGrade('device.xtgetxres', undefined, (id, role, read) => {
        if (id === 'device.xtgetxres' && role === 'initial') read.screen.lines[0] = 'not the nonce';
        return read;
    });
    assert.equal(result.outcome, 'inconclusive');
    assert.match(result.note, /attributed seed/);
});

test('grading refuses missing captures and unknown revisions', () => {
    const item = devqContracts[0];
    const reads = syntheticDevqScreens(item);
    const observation = devqObservation(item, reads.map(read => read.screen), reads.map(read => read.replies), 'NTILDE_DQ_' + 'a'.repeat(32));
    assert.throws(() => gradeDevqCase(item, observation, 2), /Unknown device-query contracts revision/);
    assert.throws(() => gradeDevqCase(item, { ...observation, pinned: undefined }), /Missing device.xtgetxres read captures/);
    assert.throws(() => devqObservation(item, reads.slice(1).map(read => read.screen), reads.map(read => read.replies), 'x'), /Missing device-query read captures/);
});

test('every request fits the fence\'s 64-character exchange limit and every demanded reply list stays inside its bounds', () => {
    for (const item of devqContracts) {
        for (const group of item.groups) {
            assert.ok(group.request.length <= 64, `${item.caseId}/${group.role} request is ${group.request.length} characters`);
            assert.ok(group.demand.length <= 8 && group.demand.join('').length <= 400, `${item.caseId}/${group.role} replies`);
        }
    }
});
