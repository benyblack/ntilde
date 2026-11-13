import test from 'node:test';
import assert from 'node:assert/strict';
import { focusBudget, runFocusAction } from './osc9-focus.mjs';

/** A fake osascript whose clock advances by `cost` per call (default: the call's full timeout, the worst case) and whose frontmost process follows `outcomes`. */
function harness(outcomes, cost) {
    let clock = 0, queries = 0; const calls = [];
    return { calls, elapsed: () => clock, now: () => clock, sleep: async ms => { clock += ms; },
        run(args, timeoutMs) {
            clock += cost ?? timeoutMs; calls.push(args[1]);
            if (!args[1].includes('get unix id')) return { status: 0, signal: null, stdout: '', stderr: '' };
            const next = outcomes[Math.min(queries++, outcomes.length - 1)];
            return next === null ? { status: 1, signal: null, stdout: '', stderr: 'denied' } : { status: 0, signal: null, stdout: `${next}\n`, stderr: '' };
        } };
}

test('a focus action verifies the frontmost process and stops at the first success', async () => {
    const h = harness([77, 4242], 100);
    const record = await runFocusAction({ action: 'refocus', appPid: 4242, run: h.run, sleep: h.sleep, now: h.now });
    assert.deepEqual(record.attempts.map(item => item.frontmostPid), [77, 4242]);
    assert.ok(record.script.includes('unix id is 4242'));
    const defocus = await runFocusAction({ action: 'defocus', appPid: 4242, ...(({ run, sleep, now }) => ({ run, sleep, now }))(harness([99], 100)) });
    assert.equal(defocus.script, 'tell application "Finder" to activate'); assert.equal(defocus.attempts.length, 1);
});

test('retries are bounded inside the owner request deadline whatever each call costs', async () => {
    // The shared deadline reports at most two seconds at a time (createProcessBudget), so the model caps what it reports.
    for (const [cost, left, attempts] of [[100, 3900, 3], [400, 3900, 2], [undefined, 3900, 2], [100, 2000, 1], [100, 1500, 0], [100, 0, 0]]) {
        const h = harness([null], cost);
        const record = await runFocusAction({ action: 'refocus', appPid: 4242, run: h.run, sleep: h.sleep, now: h.now, remaining: () => Math.min(2000, Math.max(0, left - h.elapsed())) });
        assert.ok(record.attempts.every(item => item.frontmostPid === null));
        assert.equal(record.attempts.length, attempts, `cost ${cost} left ${left}`);
        assert.ok(h.elapsed() <= left, `cost ${cost} left ${left}: took ${h.elapsed()} ms`);
        assert.ok(h.elapsed() < 5000);
    }
    assert.equal(focusBudget.commandMs + focusBudget.settleMs + focusBudget.queryMs <= 2000, true);
});

test('no command waits longer than the deadline remainder, and the first success ends the action', async () => {
    const timeouts = [], h = harness([77, 4242], 100);
    const record = await runFocusAction({ action: 'refocus', appPid: 4242, run: (args, ms) => { timeouts.push(ms); return h.run(args, ms); }, sleep: h.sleep, now: h.now, remaining: () => Math.min(2000, 3900 - h.elapsed()) });
    assert.deepEqual(record.attempts.map(item => item.frontmostPid), [77, 4242]);
    assert.ok(timeouts.every(ms => ms >= 1 && ms <= focusBudget.commandMs), JSON.stringify(timeouts));
    assert.equal(record.budgetMs, 2000);
});

test('a malformed action or pid is refused before any command runs', async () => {
    const h = harness([1]);
    await assert.rejects(runFocusAction({ action: 'raise', appPid: 1, run: h.run, sleep: h.sleep }), /Invalid/);
    await assert.rejects(runFocusAction({ action: 'refocus', appPid: '1; do shell', run: h.run, sleep: h.sleep }), /Invalid/);
    assert.equal(h.calls.length, 0);
});
