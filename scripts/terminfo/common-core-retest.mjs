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
