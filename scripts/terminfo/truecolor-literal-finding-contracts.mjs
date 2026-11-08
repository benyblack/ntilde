// Versioned contracts for the truecolor-literal FINDING suite. This is NOT a
// terminfo.dev case: there is no pinned probe, no stock grade and no `sgr.*` id.
// It records an Ntilde-specific finding about five exact direct-RGB values that the
// VT parser once rewrote into palette indices (Windows Terminal Campbell remap):
//   0,55,218 and 58,150,221 -> index 4, 12,12,12 -> 0, 204,204,204 -> 7,
//   242,242,242 -> 15.
// A direct-RGB request is a literal: it must be stored as literal bytes (palette
// index and default marker cleared) and keep resolving to the requested RGB when the
// palette entry it used to alias is changed by OSC 4.
//
// Layers stay distinct. Stored: literal ARGB, palette index, default marker per cell.
// Resolved: the App's storage-to-theme RGB bound to the per-frame recorded palette
// (the OSC 4 mutation is itself visible in that binding). Rendered appearance is NOT
// claimed. Forms tested per value and channel: semicolon `38;2;r;g;b`, colon with the
// omitted colorspace id `38:2::r:g:b` and colon with an explicit colorspace id
// `38:2:0:r:g:b`. No other syntax is claimed.
//
// Unsupported ("the literal was aliased or drifted") needs valid controls: neutral
// baseline, an explicit indexed positive control that follows the OSC 4 mutation and
// restores, a mutation that changed exactly the aliased entry, DECSCNM off, and the
// unaffected channel and unrelated attributes clear. Anything else is inconclusive.
import { planFeed, planRead, validateExactFeeds } from './graded-plan.mjs';
import { sgrAttrsReset, validateSgrAttrsScreen } from './sgr-attrs-contracts.mjs';
import { sgrColorCellAt, attributesClear, isDefault, isPalette, isLiteral, neutral, inconclusive, argbRgb } from './sgr-color-core.mjs';

/** Observation screens are validated exactly like the attribute suite's. */
export const sgrColorCellAtScreenCheck = validateSgrAttrsScreen;

/** Contracts v1 is the first measured semantics of this finding. */
export const truecolorFindingContractsVersion = 1;
/** Identity of this suite. */
export const truecolorFindingSuiteId = 'ntilde-owned-truecolor-literal-finding-v1';
/** The nonce prefix binding worker output to this suite. */
export const truecolorFindingNoncePrefix = 'NTILDE_TF_';
/** The measured reset: the attribute suite's, then every palette override cleared (OSC 104). */
export const truecolorFindingReset = sgrAttrsReset + '\x1b]104\x07';
/** The OSC 4 value written into the aliased entry: magenta, distinct from every requested value. */
export const mutationRgb = Object.freeze({ r: 255, g: 0, b: 255 });
const mutationArgb = 0xffff00ff;

/** The five exact values the parser aliased, and the index each became. */
export const snappedValues = Object.freeze([
    Object.freeze({ r: 0, g: 55, b: 218, index: 4 }), Object.freeze({ r: 58, g: 150, b: 221, index: 4 }),
    Object.freeze({ r: 12, g: 12, b: 12, index: 0 }), Object.freeze({ r: 204, g: 204, b: 204, index: 7 }),
    Object.freeze({ r: 242, g: 242, b: 242, index: 15 }),
]);

const sameRgb = (a, b) => a !== null && b !== null && a.r === b.r && a.g === b.g && a.b === b.b;
const forms = Object.freeze(['semicolon', 'colon-omitted-colorspace', 'colon-explicit-colorspace']);
const formCells = Object.freeze({ semicolon: 2, 'colon-omitted-colorspace': 3, 'colon-explicit-colorspace': 4 });

/** One finding case: channel x exact value. */
function findingCase(channel, value) {
    const { r, g, b, index } = value;
    const p = channel === 'fg' ? 38 : 48;
    const other = channel === 'fg' ? 'bg' : 'fg';
    const caseId = `finding.truecolor-literal.${channel}.${r}-${g}-${b}`;
    const fixture = `B\x1b[${p};5;${index}mI\x1b[0m\x1b[${p};2;${r};${g};${b}mS\x1b[0m`
        + `\x1b[${p}:2::${r}:${g}:${b}mC\x1b[0m\x1b[${p}:2:0:${r}:${g}:${b}mD\x1b[0m`;
    const mutate = `\x1b]4;${index};rgb:ff/00/ff\x07`;
    const restore = `\x1b]104;${index}\x07`;
    const requested = Object.freeze({ r, g, b });
    return Object.freeze({
        caseId, kind: 'finding', channel, other, index, requested, fixtureCells: 5,
        pinnedFeeds: Object.freeze([fixture]), fixture, mutate, restore,
        plan: Object.freeze([planFeed(truecolorFindingReset), planFeed(fixture), planRead('cells'),
            planFeed(mutate), planRead('cells'), planFeed(restore), planRead('cells')]),
        criterion: `direct RGB ${r},${g},${b} stays a stored literal and keeps resolving to itself when palette entry ${index} is changed`,
        scope: `stored literal ARGB bytes ${r},${g},${b} with the palette index and default marker cleared, resolved ${channel} RGB unchanged by an OSC 4 `
            + `change of entry ${index}, in the semicolon, colon-omitted-colorspace and colon-explicit-colorspace forms; an explicit indexed `
            + 'positive control follows the same change; rendered appearance is not claimed',
        grade(observation) {
            const { before, mutated, restored, bindings, reverseVideo, baseline: runBaseline } = observation;
            const baseline = before[0];
            if (![before, mutated, restored].every(group => group.every((cell, position) => cell.char === 'BISCD'[position]))) {
                return inconclusive('The finding fixture cells were not exposed');
            }
            if (reverseVideo !== false) return inconclusive('DECSCNM reverse video was not proven off, so stored colors cannot be separated from it');
            if (!neutral(baseline)) return inconclusive('Neutral baseline control was not measured (a color or attribute was initially active)');
            const paletteOf = binding => binding.ansiPaletteArgb;
            const [first, second, third] = bindings;
            // Every read is compared with the run's own recorded start palette: an unrelated entry that changed and stayed
            // changed across all reads (or a pre-existing override) must not pass as a pristine palette.
            if (runBaseline.paletteOverrideIndices.length !== 0 || JSON.stringify(paletteOf(first)) !== JSON.stringify(paletteOf(runBaseline))
                || JSON.stringify(paletteOf(third)) !== JSON.stringify(paletteOf(runBaseline))
                || paletteOf(second).some((argb, position) => position !== index && argb !== paletteOf(runBaseline)[position])) {
                return inconclusive('A read palette differs from the run\'s recorded baseline palette outside the contracted mutation');
            }
            // The mutation control: exactly the aliased entry changed, to the contracted magenta, and was restored.
            const onlyEntryChanged = paletteOf(first).every((argb, position) => position === index
                ? paletteOf(second)[position] === mutationArgb && argb !== mutationArgb
                : paletteOf(second)[position] === argb);
            if (first.paletteOverrideIndices.length !== 0 || JSON.stringify(second.paletteOverrideIndices) !== JSON.stringify([index])
                || !onlyEntryChanged || JSON.stringify(paletteOf(third)) !== JSON.stringify(paletteOf(first))
                || third.paletteOverrideIndices.length !== 0) {
                return inconclusive('The OSC 4 palette-mutation control was not applied and restored as contracted');
            }
            if (sameRgb(argbRgb(paletteOf(first)[index]), requested)) {
                return inconclusive('The aliased palette entry already equals the requested RGB, so resolution cannot separate storage from appearance');
            }
            const [indexedBefore, indexedMutated, indexedRestored] = [before[1], mutated[1], restored[1]];
            const targets = [before, mutated, restored].flatMap(group => [group[1], group[2], group[3], group[4]]);
            if (!targets.every(cell => isDefault(cell, other) && attributesClear(cell))) {
                return inconclusive('The unaffected channel or unrelated attributes drifted on the fixture cells, so the setup control is invalid');
            }
            // The indexed positive control follows the palette: palette storage, resolved to the entry, then to magenta, then restored.
            const entryRgb = binding => argbRgb(paletteOf(binding)[index]);
            if (!isPalette(indexedBefore, channel, index) || !isPalette(indexedMutated, channel, index) || !isPalette(indexedRestored, channel, index)
                || !sameRgb(indexedBefore[channel], entryRgb(first)) || !sameRgb(indexedMutated[channel], mutationRgb)
                || !sameRgb(indexedRestored[channel], entryRgb(first))) {
                return inconclusive('The explicit indexed positive control did not follow the palette mutation and restore as contracted');
            }
            const findings = {};
            let allLiteral = true, allStable = true, anyDrift = false;
            for (const form of forms) {
                const position = formCells[form];
                const group = [before[position], mutated[position], restored[position]];
                const literal = group.every(cell => isLiteral(cell, channel, requested));
                const aliased = group.every(cell => isPalette(cell, channel, index));
                const resolved = group.map(cell => cell[channel]);
                const stable = resolved.every(color => sameRgb(color, requested));
                findings[form] = { stored: literal ? 'literal' : aliased ? `palette:${index}` : 'other',
                    resolvedBefore: resolved[0], resolvedMutated: resolved[1], resolvedRestored: resolved[2],
                    initialMatchesRequested: sameRgb(resolved[0], requested), changedByMutation: !sameRgb(resolved[0], resolved[1]) };
                allLiteral &&= literal;
                allStable &&= stable;
                anyDrift ||= !literal && !aliased;
            }
            if (anyDrift) return { outcome: 'inconclusive', note: 'A fixture cell was neither the requested literal nor the aliased palette index',
                layers: { stored: 'inconclusive', resolved: 'not-evaluated' }, findings };
            if (allLiteral && allStable) return { outcome: 'supported', layers: { stored: 'supported', resolved: 'supported' }, findings };
            return { outcome: 'unsupported', layers: { stored: allLiteral ? 'supported' : 'unsupported', resolved: allStable ? 'supported' : 'unsupported' },
                note: allLiteral ? 'The stored literal bytes did not keep resolving to the requested RGB'
                    : `Direct RGB ${r},${g},${b} was stored as palette index ${index} instead of literal bytes`, findings };
        },
        observed(observation) {
            const brief = cell => ({ char: cell.char, flags: cell.flags, fgSource: cell.fgSource, fgIndex: cell.fgIndex, foregroundArgb: cell.foregroundArgb,
                bgSource: cell.bgSource, bgIndex: cell.bgIndex, backgroundArgb: cell.backgroundArgb, fg: cell.fg, bg: cell.bg });
            return { before: observation.before.map(brief), mutated: observation.mutated.map(brief), restored: observation.restored.map(brief),
                reverseVideo: observation.reverseVideo };
        },
    });
}

/** The ten cases: foreground then background for each exact value. */
export const truecolorFindingContracts = Object.freeze(
    ['fg', 'bg'].flatMap(channel => snappedValues.map(value => findingCase(channel, value))));

/** Contract case ids in suite order. */
export const truecolorFindingCaseIds = Object.freeze(truecolorFindingContracts.map(contract => contract.caseId));

/** Project the three read groups of one case into the grader's observation. */
export function truecolorFindingObservation(contract, screens, baseline) {
    if (!Array.isArray(screens) || screens.length !== 3) throw new Error('Missing truecolor-finding read captures.');
    const reverse = screens.map(screen => screen.colorBinding.reverseVideo);
    return { before: Array.from({ length: contract.fixtureCells }, (_, col) => sgrColorCellAt(screens[0], 0, col)),
        mutated: Array.from({ length: contract.fixtureCells }, (_, col) => sgrColorCellAt(screens[1], 0, col)),
        restored: Array.from({ length: contract.fixtureCells }, (_, col) => sgrColorCellAt(screens[2], 0, col)),
        bindings: screens.map(screen => screen.colorBinding), baseline,
        reverseVideo: reverse.every(value => value === false) ? false : reverse.some(value => value === true) ? true : null };
}

/** Independent contract grader entry. */
export function gradeTruecolorFindingCase(contract, observation, revision = truecolorFindingContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown truecolor-finding contracts revision ${revision}.`);
    if (![observation?.before, observation?.mutated, observation?.restored].every(group => Array.isArray(group) && group.length === contract.fixtureCells)
        || !Array.isArray(observation.bindings) || observation.bindings.length !== 3 || !observation.baseline) {
        throw new Error(`Missing ${contract.caseId} read captures.`);
    }
    return contract.grade(observation);
}

export { planFeeds as truecolorFindingFeeds, planReads as truecolorFindingReads } from './graded-plan.mjs';

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateTruecolorFindingFeeds(contract, feeds) {
    validateExactFeeds(contract, feeds, 'contract');
}
