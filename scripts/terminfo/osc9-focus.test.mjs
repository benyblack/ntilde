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

test('retries are bounded inside the owner socket timeout, whether every call is instant, slow or takes its full timeout', async () => {
    for (const [cost, attempts] of [[100, 3], [400, 2], [undefined, 1]]) {
        const h = harness([null], cost);
        const record = await runFocusAction({ action: 'refocus', appPid: 4242, run: h.run, sleep: h.sleep, now: h.now });
        assert.ok(record.attempts.every(item => item.frontmostPid === null));
        assert.equal(record.attempts.length, attempts, `cost ${cost}`);
        assert.ok(h.elapsed() < 5000, `cost ${cost} took ${h.elapsed()} ms`);
    }
    assert.equal(focusBudget.commandMs + focusBudget.settleMs + focusBudget.queryMs <= 2500, true);
});

test('the action never outlives the owner request deadline it is given', async () => {
    for (const [left, attempts] of [[3900, 3], [2300, 1], [2000, 0], [0, 0]]) {
        const h = harness([null], 100);
        const record = await runFocusAction({ action: 'refocus', appPid: 4242, run: h.run, sleep: h.sleep, now: h.now, remaining: () => Math.max(0, left - h.elapsed()) });
        assert.equal(record.attempts.length, attempts, `left ${left}`);
        assert.equal(record.budgetMs, left); assert.ok(h.elapsed() <= left, `left ${left}, took ${h.elapsed()}`);
    }
    const timeouts = []; const h = harness([77], 100);
    await runFocusAction({ action: 'refocus', appPid: 4242, run: (args, ms) => { timeouts.push(ms); return h.run(args, ms); }, sleep: h.sleep, now: h.now, remaining: () => 2300 - h.elapsed() });
    assert.ok(timeouts.every(ms => ms >= 1 && ms <= 1000), JSON.stringify(timeouts));
});

test('a malformed action or pid is refused before any command runs', async () => {
    const h = harness([1]);
    await assert.rejects(runFocusAction({ action: 'raise', appPid: 1, run: h.run, sleep: h.sleep }), /Invalid/);
    await assert.rejects(runFocusAction({ action: 'refocus', appPid: '1; do shell', run: h.run, sleep: h.sleep }), /Invalid/);
    assert.equal(h.calls.length, 0);
});
