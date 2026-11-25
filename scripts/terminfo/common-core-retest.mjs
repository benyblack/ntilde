/** Select every Core result invalidated by native-window movement; never select by desired outcome. */
export function invalidatedWindowCases(raw, coreIds) {
    if (raw?.schemaVersion !== 4 || raw.source?.kind !== 'live' || !Array.isArray(raw.results))
        throw new Error('Retest requires a retained schema-4 live run.');
    const candidates = raw.results.filter(
        (result) =>
            coreIds.includes(result.featureId) &&
            result.status === 'probe-invalid' &&
            typeof result.reason === 'string' &&
            result.reason.includes('identity or bounds changed during capture'),
    );
    const ids = candidates.map((result) => result.featureId).sort();
    if (!ids.length || new Set(ids).size !== ids.length)
        throw new Error('Retest requires unique cases invalidated by native-window movement.');
    return ids;
}

const ghosttyLostCases = new Set([
    'modes.alt-screen.exit', 'sgr.bold', 'sgr.faint', 'sgr.italic', 'sgr.underline.single',
]);

export const retestReasons = {
    'window-movement': 'all Core cases invalidated by native-window movement',
    'capture-stability': 'iTerm2 scrollback cases invalidated by the cursor repaint during the static-frame control',
    'probe-timeout': 'the five Ghostty Core cases that lost their supported verdict to a probe timeout',
    'capture-throughput': 'the five Ghostty Core timeouts after reducing native window census overhead',
    'capture-custody': 'iTerm2 Core cases that retained native frames before per-frame session custody was verified',
    'iterm-sgr-timeout': 'the nine iTerm2 SGR cases that exceeded their probe deadlines in the custody retest',
    'ntilde-capture': 'the nine Ntilde screenshot probe timeouts and missing visible-cursor control',
};

/** The complete allowed upstream diff for each focused capture adapter. */
export function retestAdapterPaths(kind) {
    const prefix = 'packages/terminfo.dev/src/';
    if (kind === 'capture-stability') return [prefix + 'serve.ts'];
    if (kind === 'capture-throughput') return [prefix + 'darwin-capture.ts'];
    if (kind === 'iterm-sgr-timeout') return [prefix + 'serve.test.ts', prefix + 'serve.ts'];
    if (kind === 'ntilde-capture') return [prefix + 'darwin-capture.test.ts', prefix + 'darwin-capture.ts', prefix + 'serve.ts'];
    if (kind === 'capture-custody')
        return ['darwin-capture.test.ts', 'darwin-capture.ts', 'iterm-window-owner.ts', 'serve.ts']
            .map((path) => prefix + path);
    return undefined;
}

/** Retain the earlier timeout run at its original publication URL. */
export function retestEvidenceDirectory(kind) {
    if (kind === 'capture-throughput') return 'timeout-retest';
    if (kind === 'capture-custody') return 'custody-retest';
    if (kind === 'iterm-sgr-timeout') return 'sgr-timeout-retest';
    if (kind === 'ntilde-capture') return 'capture-retest';
    return kind ? 'focused-retest' : 'movement-retest';
}

/** Derive the selected cases from their retained status and measurement reason. */
export function focusedRetestCases(raw, coreIds, kind = 'window-movement') {
    if (kind === 'window-movement') return invalidatedWindowCases(raw, coreIds);
    if (raw?.schemaVersion !== 4 || raw.source?.kind !== 'live' || !Array.isArray(raw.results))
        throw new Error('Retest requires a retained schema-4 live run.');
    const targeted = targetedRetestSelectors.get(kind);
    if (targeted) return targeted(raw, coreIds);
    let cases;
    if (kind === 'capture-stability' && raw.subject?.id === 'iterm2') {
        cases = raw.results.filter(
            (row) => coreIds.includes(row.featureId) && row.featureId.startsWith('scrollback.') &&
                row.status === 'probe-invalid' && row.reason?.includes('two captures of one static frame were not byte-stable'),
        ).map((row) => row.featureId).sort();
        if (cases.length !== 7) throw new Error('Expected all seven iTerm2 static-frame invalidations.');
    } else if (['probe-timeout', 'capture-throughput'].includes(kind) && raw.subject?.id === 'ghostty') {
        cases = raw.results.filter(
            (row) => coreIds.includes(row.featureId) && ghosttyLostCases.has(row.featureId) &&
                row.status === 'probe-todo' && row.reason === 'Probe exceeded 30000ms',
        ).map((row) => row.featureId).sort();
        if (cases.length !== ghosttyLostCases.size) throw new Error('Expected all five Ghostty timeout rows.');
    } else if (kind === 'capture-custody' && raw.subject?.id === 'iterm2') {
        cases = itermNativeCases(raw, coreIds);
    } else {
        throw new Error('Retest kind does not match its baseline terminal.');
    }
    if (new Set(cases).size !== cases.length) throw new Error('Retest selection contains duplicate cases.');
    return cases;
}

const targetedRetestSelectors = new Map([
    ['iterm-sgr-timeout', itermTimeoutCases],
    ['ntilde-capture', ntildeCaptureCases],
]);

/** Bind the Ntilde retry to the nine deadlines and the one named cursor-control refusal. */
function ntildeCaptureCases(raw, coreIds) {
    if (raw.subject?.id !== 'ntilde') throw new Error('Ntilde capture retest requires its own baseline.');
    const deadlines = raw.results.filter((row) => coreIds.includes(row.featureId) &&
        row.attempt?.scope === 'probe' && row.harnessOutcome === 'probe-exceeded' && row.status === 'probe-todo');
    const cursors = raw.results.filter((row) => row.featureId === 'cursor.hide' && coreIds.includes(row.featureId) &&
        row.attempt?.scope === 'probe' && row.status === 'probe-invalid' && row.reason?.includes('no visible-cursor control was established'));
    const cases = [...deadlines, ...cursors].map((row) => row.featureId).sort();
    if (deadlines.length !== 9 || cursors.length !== 1 || new Set(cases).size !== 10)
        throw new Error('Expected nine distinct Ntilde deadlines and one missing cursor control.');
    return cases;
}

/** Select the timed-out cases from the custody run, excluding other inconclusive rows. */
function itermTimeoutCases(raw, coreIds) {
    if (raw.subject?.id !== 'iterm2') throw new Error('iTerm2 timeout retest requires its own baseline.');
    const cases = raw.results.filter((row) =>
        coreIds.includes(row.featureId) && row.attempt?.scope === 'probe' &&
        row.harnessOutcome === 'probe-exceeded' && row.status === 'probe-todo',
    ).map((row) => row.featureId).sort();
    if (cases.length !== 9 || new Set(cases).size !== 9)
        throw new Error('Expected nine distinct iTerm2 probe-deadline timeouts.');
    return cases;
}

/** Select all affected cases irrespective of their first verdict. */
function itermNativeCases(raw, coreIds) {
    const cases = raw.results.filter((row) => coreIds.includes(row.featureId) && retainedNativeCase(raw, row))
        .map((row) => row.featureId).sort();
    if (cases.length === 0) throw new Error('Expected retained iTerm2 native captures.');
    return cases;
}

/** Follow each executed case's capture journal, including inconclusive cases and query criteria. */
function retainedNativeCase(raw, row) {
    if (row.attempt?.scope !== 'probe') return false;
    const journal = raw.rawReplies?.[row.rawReplyRef ?? row.featureId];
    return typeof journal === 'string' && JSON.parse(journal).captures?.some(
        ({ frame, trace }) => frame?.kind === 'pixels' && trace?.platform === 'darwin',
    );
}
