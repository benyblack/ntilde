// The parent's focus actions for the OSC 9 notification experiments. The disposable App is made the frontmost process (`refocus`) or Finder is
// activated instead (`defocus`) through osascript, verified by asking System Events which process is frontmost, and every attempt is retained. The whole
// action stays inside the owner socket's five-second inactivity limit: a slow or denied automation request ends as a retained, unsuccessful record
// (the observed window state, not this record, is what the contract grades), never as a lost response. The owner spends part of its own shared deadline on
// lineage checks before it dispatches the request, so the caller passes what is left of that deadline and no command or retry outlives it.
// The owner's shared deadline reports at most two seconds at a time, so a whole attempt (command, settle, query) must fit in that: an attempt is admitted only while the
// reported remainder covers all of it, and its commands never wait longer than the remainder.
export const focusBudget = Object.freeze({ commandMs: 700, settleMs: 400, queryMs: 500 });
const frontmostQuery = 'tell application "System Events" to get unix id of first process whose frontmost is true';

/** One focus action. `run(args, timeoutMs)` returns { status, signal, stdout, stderr }; `sleep(ms)` and `now()` are injectable so the bound is testable. `remaining()` is the
 * time left in the owner request's shared deadline as that deadline reports it (never negative, possibly capped); an attempt starts only while a whole attempt fits in it. */
export async function runFocusAction({ action, appPid, run, sleep, now = Date.now, remaining = () => Number.POSITIVE_INFINITY }) {
    if (!['defocus', 'refocus'].includes(action) || !Number.isSafeInteger(appPid)) throw new Error('Invalid OSC 9 focus action.');
    const script = action === 'defocus' ? 'tell application "Finder" to activate'
        : `tell application "System Events" to set frontmost of (first process whose unix id is ${appPid}) to true`;
    const startedAt = new Date(now()).toISOString(), attempts = [], budgetMs = remaining();
    const wholeAttempt = focusBudget.commandMs + focusBudget.settleMs + focusBudget.queryMs;
    for (let attempt = 0; attempt < 3 && remaining() >= wholeAttempt; attempt++) {
        const result = run(['-e', script], Math.min(focusBudget.commandMs, remaining()));
        await sleep(focusBudget.settleMs);
        const query = run(['-e', frontmostQuery], Math.max(1, Math.min(focusBudget.queryMs, remaining())));
        const frontmostPid = query.status === 0 && /^\d+$/.test(String(query.stdout).trim()) ? Number(String(query.stdout).trim()) : null;
        attempts.push({ exitCode: result.status ?? null, signal: result.signal ?? null, stderr: String(result.stderr ?? '').slice(0, 256), frontmostPid });
        if (frontmostPid !== null && (action === 'refocus') === (frontmostPid === appPid)) break;
    }
    return { action, method: 'osascript', script, appPid, attempts, budgetMs: Number.isFinite(budgetMs) ? budgetMs : null, startedAt, finishedAt: new Date(now()).toISOString() };
}
