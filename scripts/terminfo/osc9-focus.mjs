// The parent's focus actions for the OSC 9 notification experiments. The disposable App is made the frontmost process (`refocus`) or Finder is
// activated instead (`defocus`) through osascript, verified by asking System Events which process is frontmost, and every attempt is retained. The whole
// action stays inside the owner socket's five-second inactivity limit: a slow or denied automation request ends as a retained, unsuccessful record
// (the observed window state, not this record, is what the contract grades), never as a lost response.
export const focusBudget = Object.freeze({ commandMs: 1000, settleMs: 500, queryMs: 700, retryWindowMs: 1500 });
const frontmostQuery = 'tell application "System Events" to get unix id of first process whose frontmost is true';

/** One focus action. `run(args, timeoutMs)` returns { status, signal, stdout, stderr }; `sleep(ms)` and `now()` are injectable so the bound is testable. */
export async function runFocusAction({ action, appPid, run, sleep, now = Date.now }) {
    if (!['defocus', 'refocus'].includes(action) || !Number.isSafeInteger(appPid)) throw new Error('Invalid OSC 9 focus action.');
    const script = action === 'defocus' ? 'tell application "Finder" to activate'
        : `tell application "System Events" to set frontmost of (first process whose unix id is ${appPid}) to true`;
    const startedAt = new Date(now()).toISOString(), began = now(), attempts = [];
    for (let attempt = 0; attempt < 3 && (attempt === 0 || now() - began <= focusBudget.retryWindowMs); attempt++) {
        const result = run(['-e', script], focusBudget.commandMs);
        await sleep(focusBudget.settleMs);
        const query = run(['-e', frontmostQuery], focusBudget.queryMs);
        const frontmostPid = query.status === 0 && /^\d+$/.test(String(query.stdout).trim()) ? Number(String(query.stdout).trim()) : null;
        attempts.push({ exitCode: result.status ?? null, signal: result.signal ?? null, stderr: String(result.stderr ?? '').slice(0, 256), frontmostPid });
        if (frontmostPid !== null && (action === 'refocus') === (frontmostPid === appPid)) break;
    }
    return { action, method: 'osascript', script, appPid, attempts, startedAt, finishedAt: new Date(now()).toISOString() };
}
