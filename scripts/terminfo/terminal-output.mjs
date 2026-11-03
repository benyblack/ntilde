import { execFileSync } from 'node:child_process';
export const sttyTool = '/bin/stty';
export const ownedOutputPolicy = 'parent-verified -opost in proved private PTY for owned suites; previous mode restored before stock collection';
/** Require an explicit unambiguous disabled-output-processing fact from the system tool. */
export function validateOutputDescription(description) {
    if (typeof description !== 'string' || description.length > 4096) throw new Error('Invalid private PTY output description.');
    const tokens = new Set(description.split(/\s+/));
    if (!tokens.has('-opost') || tokens.has('opost')) throw new Error('Private PTY output translation is not disabled.');
}
/** Compare independently recorded kernel dimensions with the observed App grid. */
export function validateOutputGeometry(description, rows, cols) {
    validateOutputDescription(description);
    const match = /(?:^|[;\s])(\d+) rows;\s+(\d+) columns;/.exec(description);
    if (!match || Number(match[1]) !== rows || Number(match[2]) !== cols) throw new Error('Private PTY geometry does not match the observed App screen.');
}
/** Accept only a specific macOS PTY spelling; callers must separately prove its ownership. */
function ttyPath(tty) {
    if (typeof tty !== 'string' || !/^\/dev\/ttys\d+$/.test(tty)) throw new Error('Expected the proved private macOS worker PTY.');
}
function query(tty, args, run, timeoutMs) {
    ttyPath(tty);
    return run(sttyTool, ['-f', tty, ...args], { encoding: 'utf8', timeout: timeoutMs, maxBuffer: 4096 }).trim();
}
/** Independently read flags; an absent or contradictory fact never means untranslated output. */
export function readOwnedOutputMode(tty, timeoutMs = 1000, run = execFileSync) {
    const description = query(tty, ['-a'], run, timeoutMs);
    validateOutputDescription(description);
    return { tool: sttyTool, outputProcessing: false, description };
}
/** Caller must first prove the worker's exact PTY. Raw input alone still enables CRLF translation. */
export function prepareOwnedOutput(tty, run = execFileSync) {
    const saved = query(tty, ['-g'], run, 1000);
    if (!saved || saved.length > 4096 || /[\r\n]/.test(saved)) throw new Error('Invalid private PTY saved mode.');
    const restore = () => { query(tty, [saved], run, 1000); };
    try {
        query(tty, ['-opost'], run, 1000);
        readOwnedOutputMode(tty, 1000, run);
        return restore;
    } catch (error) {
        try { restore(); }
        catch (restoreError) { throw new AggregateError([error, restoreError], 'Private PTY output setup and restoration failed.'); }
        throw error;
    }
}
