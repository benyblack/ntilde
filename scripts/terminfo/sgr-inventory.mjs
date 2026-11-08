// Compact inventory of the 32 pinned terminfo.dev@4.0.0 SGR case ids and the extension evidence that covers each. It is
// derived from the versioned contract modules (never hand-counted) and is extension evidence coverage, not a stock
// compatibility score: stock collector grades for these ids stay inconclusive and are tracked separately.
import { asciiSgrContracts } from './ascii-sgr-contracts.mjs';
import { sgrColorsContracts } from './sgr-colors-contracts.mjs';
import { sgrRgbContracts } from './sgr-rgb-contracts.mjs';
import { sgrAttrsContracts } from './sgr-attrs-contracts.mjs';
import { sgrPaletteContracts } from './sgr-palette-contracts.mjs';
import { sgrTruecolorContracts } from './sgr-truecolor-contracts.mjs';
import { sgrUnderlineContracts } from './sgr-underline-contracts.mjs';
import { sgrUnderlineColorContracts } from './sgr-underline-color-contracts.mjs';
import { sgrResetContracts } from './sgr-reset-contracts.mjs';
import { sgrClosureContracts } from './sgr-closure-contracts.mjs';

/** The qualification suites in merge order, with the publication that documents each. */
export const sgrInventorySuites = Object.freeze([
    { suite: 'ascii-sgr', contracts: asciiSgrContracts, doc: '2026-11-05-ascii-sgr-qualification.md' },
    { suite: 'sgr-colors', contracts: sgrColorsContracts, doc: '2026-11-06-sgr-colors-qualification.md' },
    { suite: 'sgr-rgb', contracts: sgrRgbContracts, doc: '2026-11-07-sgr-rgb-qualification.md' },
    { suite: 'sgr-attrs', contracts: sgrAttrsContracts, doc: '2026-11-08-sgr-attributes-qualification.md' },
    { suite: 'sgr-palette', contracts: sgrPaletteContracts, doc: '2026-11-08-sgr-palette-qualification.md' },
    { suite: 'sgr-truecolor', contracts: sgrTruecolorContracts, doc: '2026-11-08-sgr-truecolor-qualification.md' },
    { suite: 'sgr-underline', contracts: sgrUnderlineContracts, doc: '2026-11-08-sgr-underline-qualification.md' },
    { suite: 'sgr-underline-color', contracts: sgrUnderlineColorContracts, doc: '2026-11-08-sgr-underline-color-qualification.md' },
    { suite: 'sgr-reset', contracts: sgrResetContracts, doc: '2026-11-08-sgr-selective-reset-qualification.md' },
    { suite: 'sgr-closure', contracts: sgrClosureContracts, doc: '2026-11-08-sgr-closure-qualification.md' },
]);

/** The exact fixture bytes a contract replays for its pinned case (the feed after the measured reset). */
const fixtureOf = contract => contract.pinnedFeeds ? [...contract.pinnedFeeds]
    : contract.fixture !== undefined ? [contract.fixture]
        : contract.plan.filter(step => step.feed).slice(1, 2).map(step => step.feed);

/** Build the inventory: per id the covering suites and fixtures, plus overlap groups, with no identities merged. */
export function buildSgrInventory() {
    const cases = new Map();
    for (const { suite, contracts, doc } of sgrInventorySuites) {
        for (const contract of contracts.filter(item => item.caseId.startsWith('sgr.'))) {
            const entry = cases.get(contract.caseId) ?? { id: contract.caseId, coverage: [] };
            entry.coverage.push({ suite, doc, fixture: fixtureOf(contract) });
            cases.set(contract.caseId, entry);
        }
    }
    const ids = [...cases.keys()].sort();
    const byFixture = new Map();
    for (const id of ids) {
        for (const { fixture } of cases.get(id).coverage) {
            const key = JSON.stringify(fixture);
            byFixture.set(key, [...new Set([...(byFixture.get(key) ?? []), id])]);
        }
    }
    return {
        schemaVersion: 1,
        scope: 'Extension evidence coverage of the pinned terminfo.dev@4.0.0 SGR case ids. Not a stock compatibility score: stock collector grades are tracked separately and unchanged.',
        pinned: { package: 'terminfo.dev@4.0.0', bundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6', probeHash: '16a690f34c0e' },
        total: ids.length,
        cases: ids.map(id => ({ id, status: 'qualified', remainingGap: null, ...cases.get(id) })),
        unqualified: [],
        pendingSelectiveReset: [],
        overlaps: {
            idsCoveredBySeveralSuites: ids.filter(id => cases.get(id).coverage.length > 1),
            idsSharingFixtureBytes: [...byFixture.values()].filter(group => group.length > 1),
            note: 'Overlaps are identified, never merged: each id keeps its own contract, evidence and verdict.',
        },
    };
}
