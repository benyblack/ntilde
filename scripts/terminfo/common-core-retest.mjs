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

const ghosttyLostCases = [
    'modes.alt-screen.exit', 'sgr.bold', 'sgr.faint', 'sgr.italic', 'sgr.underline.single',
];

export const retestReasons = {
    'window-movement': 'all Core cases invalidated by native-window movement',
    'capture-stability': 'iTerm2 scrollback cases invalidated by the cursor repaint during the static-frame control',
    'probe-timeout': 'the five Ghostty Core cases that lost their supported verdict to a probe timeout',
};

/** Derive the selected cases from their retained status and measurement reason. */
export function focusedRetestCases(raw, coreIds, kind = 'window-movement') {
    if (kind === 'window-movement') return invalidatedWindowCases(raw, coreIds);
    if (raw?.schemaVersion !== 4 || raw.source?.kind !== 'live' || !Array.isArray(raw.results))
        throw new Error('Retest requires a retained schema-4 live run.');
    let cases;
    if (kind === 'capture-stability' && raw.subject?.id === 'iterm2') {
        cases = raw.results.filter(
            (row) => coreIds.includes(row.featureId) && row.featureId.startsWith('scrollback.') &&
                row.status === 'probe-invalid' && row.reason?.includes('two captures of one static frame were not byte-stable'),
        ).map((row) => row.featureId).sort();
        if (cases.length !== 7) throw new Error('Expected all seven iTerm2 static-frame invalidations.');
    } else if (kind === 'probe-timeout' && raw.subject?.id === 'ghostty') {
        cases = raw.results.filter(
            (row) => coreIds.includes(row.featureId) && ghosttyLostCases.includes(row.featureId) &&
                row.status === 'probe-todo' && row.reason === 'Probe exceeded 30000ms',
        ).map((row) => row.featureId).sort();
        if (cases.length !== ghosttyLostCases.length) throw new Error('Expected all five Ghostty timeout rows.');
    } else {
        throw new Error('Retest kind does not match its baseline terminal.');
    }
    if (new Set(cases).size !== cases.length) throw new Error('Retest selection contains duplicate cases.');
    return cases;
}
