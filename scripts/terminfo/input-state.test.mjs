import test from 'node:test';
import assert from 'node:assert/strict';
import { buildInputReport, inputModeQueries, inputModeReplyHex, defaultInputModes } from './input-state.mjs';
import { syntheticInputJournal, inputRunId } from './fixtures/input-journal.mjs';
test('fixed39 synthetic raw-byte observations require81 complete paired frames and80 fresh challenges', () => {
    const f = syntheticInputJournal(), report = buildInputReport(f.frames, f.actions, f.seal, inputRunId);
    assert.equal(report.observations.length, 39); assert.ok(report.observations.every(x => x.result === 'passed'));
    assert.match(report.scope, /No native keyboard/); assert.equal(f.frames.length, 81); assert.equal(f.seal.attempts, 80);
    assert.equal(inputModeQueries, '\x1b[?66$p\x1b[?9$p\x1b[?1000$p\x1b[?1002$p\x1b[?1003$p\x1b[?1006$p\x1b[?u');
    assert.equal(Buffer.from(inputModeReplyHex(defaultInputModes), 'hex').toString(), '\x1b[?66;2$y\x1b[?9;2$y\x1b[?1000;2$y\x1b[?1002;2$y\x1b[?1003;2$y\x1b[?1006;2$y\x1b[?0u');
    assert.equal(report.observations.find(x => x.featureId === 'input.mouse.x10.left.raw').actualHex, '1b5b4d208023');
});
for (const [name, mutate] of [
    ['missing frame', f => f.frames.pop()], ['missing action', f => f.actions.pop()],
    ['another pane', f => f.frames[2].paneId = '22222222-2222-2222-2222-222222222222'],
    ['mode reply missing', f => f.frames[2].capture.hex = ''],
    ['event bytes wrong', f => f.frames[5].capture.hex = '00'],
    ['reused challenge', f => Object.assign(f.frames[3].capture, { nonces: f.frames[2].capture.nonces, barrier: f.frames[2].capture.barrier })],
    ['altered paired state', f => f.frames[3].afterScreen.cursorCol++],
    ['dispatch not available', f => f.actions[0].observed.app.eventDispatched = false],
    ['post state unavailable', f => f.actions[0].observed.app.observationAvailable = false],
    ['another event', f => f.actions[0].observed.request.key = '8'],
    ['another profile', f => f.actions[1].observed.after.pane.profileName = 'personal'],
    ['another worker process', f => f.actions[0].observed.after.worker.pid++],
    ['missing worker marker', f => f.actions[0].observed.after.workerCommand = '/node'],
    ['post ownership lost', f => f.actions[0].observed.postVerified = false],
    ['partial App result', f => delete f.actions[0].observed.app.applicationKeypad],
    ['contradictory modifiers', f => f.actions[0].observed.app.modifiers = 4],
    ['pointer cell changed', f => f.actions[21].observed.app.column--],
    ['kernel geometry changed', f => f.actions[0].observed.after.terminalOutput.description = '11 rows; 120 columns; -opost'],
    ['late bytes', f => f.seal.failed = true], ['missing final attempt', f => f.seal.lastAttempt = null],
    ['partial final attempt', f => f.seal.lastAttempt.completed = false],
    ['unrestored modes', f => f.frames.at(-1).capture.hex = ''],
]) test(name + ' cannot complete the input report', () => {
    const f = syntheticInputJournal(); mutate(f);
    assert.throws(() => buildInputReport(f.frames, f.actions, f.seal, inputRunId));
});
test('handled/submitted remain App facts and never substitute for independently captured bytes', () => {
    const f = syntheticInputJournal();
    for (const action of f.actions) { action.observed.app.handled = false; action.observed.app.inputSubmitted = false; }
    assert.equal(buildInputReport(f.frames, f.actions, f.seal, inputRunId).observations.length, 39);
    f.frames[5].capture.hex = '';
    assert.throws(() => buildInputReport(f.frames, f.actions, f.seal, inputRunId));
});
