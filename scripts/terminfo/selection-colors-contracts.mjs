// Versioned probe contracts for the selection-color qualification: extensions.osc17-highlight-bg, extensions.osc19-highlight-fg,
// extensions.osc117-reset-highlight-bg and extensions.osc119-reset-highlight-fg (terminfo.dev@4.0.0).
//
// Pinned definitions (bundle SHA-256 aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6). The set cases are
// `oscColorQueryProbe(id, code)` registrations: `queryOnly(probe(...))` with `termlessObservationEvidence: "query"`. The TERMLESS path
// feedCaptures the single BEL-spelled query (`ESC]17;?BEL`, `ESC]19;?BEL`) and grades from that one reply alone (oscReplyResult):
// supported iff a complete `ESC]…;rgb:{1-4}/{1-4}/{1-4}` reply terminated by BEL or ST is present, inconclusive (`invalid-reply`) when
// the raw capture carries the `ESC]…;` prefix without a complete reply, inconclusive (`no-response`) otherwise. The TERMINAL path
// re-asks the same query behind a sentinel; it is preserved as metadata and never run as a verdict. The reset cases pin their whole
// registration object verbatim: the TERMLESS path feeds `ESC]117\x07X` (resp. `ESC]119\x07X`) and reads cell (0,0), returning
// `notTestedResult` (`notTested.reason: "no-semantic-observable"`) whatever the cell holds; the TERMINAL path writes
// `ESC[1;1H ESC[2K` and `ESC]117 BEL`, queries the cursor and returns `unverifiedEffect` (inconclusive, evidence `consumed`). Both
// reset paths are inconclusive by construction; they are replayed and preserved, never replaced.
//
// The effect contract (docs/qa/terminfo/selection-colors.md) is only ever stricter than the pinned criteria. An independent
// selection-color model (selection-colors-model.mjs) turns the exact bytes written so far into the overrides each stage must show, and
// the stage is read from four separately retained layers: the exact fenced query replies (an unset selection source demands the
// explicit ABSENCE of a reply behind the exchange's completed ordering barrier, never an invented RGB value), the App's resolved
// colors and override metadata, the stored cells and cursor, and the controlled offscreen render with the established synthetic
// selection `(2,4)..(4,10)` and its original PNG. Each experiment measures a pristine baseline before it mutates anything, proves the
// intended override took effect in every layer, rejects stale values with a second distinguishable value, and only then grades the
// reset against that measured baseline. A case qualifies only when a qualified positive override-query control passed somewhere in it.
//
// Scope: the owned macOS sidecar, default theme (selection sources unset), steady synthetic render phase (clock 0, cursor hidden,
// scale 1). Pointer colors, ordinary foreground/background/cursor resets, clipboard behavior, other themes or platforms, physical
// mouse selection, native desktop appearance, remote delivery and stock grades are not claimed.
import { planFeed, planRead } from './graded-plan.mjs';
import { clearColors } from './color-cases.mjs';
import { expandResolvedColors, checkStoredColorCoherence } from './resolved-colors.mjs';
import { snapshotHash } from './agent-observer.mjs';
import { applyColorWrites, noOverrides, effectiveColors, channels16, opaqueArgb, overrideIndices, queriedColor, hexOf, parseColorSpec, cellsMatch as cellsMatchImpl } from './selection-colors-model.mjs';

export const selectionColorsContractsVersion = 1;
export const selectionColorsSuiteId = 'ntilde-owned-selection-colors-graded-v1';
export const selectionColorsNoncePrefix = 'NTILDE_SL_';
export const selectionColorsPrefix = 'selc';
/** The measured reset every scene begins with: DECSTR (which leaves runtime colors alone), origin, margins, IRM and SGR off, screen cleared, cursor hidden. */
export const selectionColorsBase = '\x1b[!p\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[2J\x1b[H\x1b[?25l';
/** Bind and finish bytes: the nonce binds the pane; finish clears every runtime color explicitly before the alternate screen is left. */
export const selectionColorsBind = '\x1b[?1049h' + selectionColorsBase;
export const selectionColorsFinish = clearColors + '\x1b[?1049l\x1b[!p';

const esc = '\x1b', bel = '\x07', st = '\x1b\\';
const at = (row, col) => `\x1b[${row + 1};${col + 1}H`;
const osc = (body, term = st) => `${esc}]${body}${term}`;
const sceneText = 'HMWqg_|';
const controlFg = 0xf0f0f0, controlBg = 0x101010, literalBg = 0x102030;
/** The two distinguishable override values; the second exists to reject stale-value observations. */
const overrideA = 'rgb:aa/bb/cc', overrideB = 'rgb:12/34/56';
/** An unrelated palette slot (not drawn by the scene) overridden beside the selection colors, so a selection reset reaching the palette is caught. */
const isolatedSlot = { code: 4, index: 5 };
const isolatedSlotSet = osc('4;5;#d0c010');

/** One plan step: an exchange writes the exact query bytes, then retains the raw reply (or its explicit absence) before the DA barrier. */
export const planExchange = (bytes, spec) => ({ feed: bytes, query: spec });

/** The exact ST-spelled bytes of one query spec. */
export const queryBytes = spec => osc(`${spec.code};${spec.code === 4 ? spec.index + ';' : ''}?`);
/** The exact BEL-spelled bytes of one query spec (the pinned spelling). */
export const belQueryBytes = spec => osc(`${spec.code};${spec.code === 4 ? spec.index + ';' : ''}?`, bel);

/** One stage: bytes written, then every query asked, then one read. `kind` says what the stage is for: a baseline and an override are
 * controls (they must hold before a reset can be graded); a `second` stage is graded (a stale value must be rejected); a `reset` is
 * graded. `rendered` marks a stage whose scene draws something the stage changes, so the controlled render must differ from the
 * previous stage's render; `keepsDrawn` marks one that leaves an override the scene draws, so the render must also differ from the
 * baseline render. */
const stage = (id, kind, writes = [], cells = [], rendered = false, keepsDrawn = false) => ({ id, kind, writes, cells, rendered, keepsDrawn });

/** The scene of an experiment: an unselected explicit-RGB control row, the synthetic selection `(2,4)..(4,10)` over a blank sample row,
 * a default-colored text row and a palette-colored text row, an unselected literal row, and the hidden cursor. Rows are 0-based. */
function sceneBytes(scene) {
    const row = (index, sgr) => at(index, 4) + sgr + sceneText + '\x1b[0m';
    return clearColors + selectionColorsBase
        + row(1, `\x1b[38;2;240;240;240;48;2;16;16;16m`)
        + row(3, '')
        + row(4, '\x1b[38;5;33;48;5;34m')
        + row(5, `\x1b[38;2;240;240;240;48;2;16;32;48m`)
        + at(scene.cursor[0], scene.cursor[1]);
}

/** The explicit cells every scene holds: where, and how each channel is stored (a default marker, a palette index or literal RGB). */
function sceneCells() {
    return [
        { id: 'control-literal', row: 1, col: 4, fg: { source: 'literal', rgb: controlFg }, bg: { source: 'literal', rgb: controlBg } },
        { id: 'selected-default', row: 3, col: 4, fg: { source: 'default' }, bg: { source: 'default' } },
        { id: 'selected-palette', row: 4, col: 4, fg: { source: 'palette', index: 33 }, bg: { source: 'palette', index: 34 } },
        { id: 'unselected-literal', row: 5, col: 4, fg: { source: 'literal', rgb: controlFg }, bg: { source: 'literal', rgb: literalBg } },
    ];
}

/** The established render-only synthetic selection of the owned color fixtures; the real UI selection is never touched. */
export const selectionColorsSelection = Object.freeze({ startRow: 2, startCol: 4, endRow: 4, endCol: 10 });

/** The lines the screen must hold at a stage: the scene rows, plus the pinned `X` of the reset cases' fixture. */
function expectedLines(rows, steps, upTo) {
    const lines = Array.from({ length: rows }, () => '');
    for (const row of [1, 3, 4, 5]) lines[row] = '    ' + sceneText;
    if (steps.slice(0, upTo + 1).some(step => step.writes.some(bytes => bytes.endsWith('X')))) lines[0] = 'X';
    return lines;
}

/** The exact pinned termless fixture bytes of one reset case: the reset, then `X` at the cursor. */
export const pinnedResetBytes = code => osc(String(code), bel) + 'X';

/** An experiment: its scene, the queries asked after every stage, their terminator, its cursor home and its stages. */
function experiment(spec) {
    return Object.freeze({ role: 'control', term: st, pixels: true, cursor: [8, 8], ...spec, stages: Object.freeze(spec.stages.map(Object.freeze)) });
}

/** The set cases (OSC 17 / 19): the pinned query in the pristine default state, the ST set/query/reset cycle with a stale-rejecting
 * second value, and the BEL spelling as a labeled control. */
function setExperiments(caseId, code, resetCode, name) {
    return [
        experiment({ id: 'pinned', caseId, role: 'pinned', term: bel, queries: [{ code }],
            label: `the pinned fixture: one BEL-spelled query of the ${name} in the pristine default state, where the source is unset`,
            stages: [stage('baseline', 'baseline')] }),
        experiment({ id: 'set-query', caseId, queries: [{ code }],
            label: `the ${name} set with the ST spelling to two distinguishable values and reset: each reply must equal the newest value (never a stale one), every layer must show the override, and the reset must restore the measured unset default`,
            stages: [stage('baseline', 'baseline'), stage('override-a', 'override', [osc(`${code};${overrideA}`)]),
                stage('override-b', 'second', [osc(`${code};${overrideB}`)]), stage('reset', 'reset', [osc(String(resetCode))], [], true)] }),
        experiment({ id: 'bel-spelling', caseId, term: bel, queries: [{ code }], pixels: false,
            label: `a labeled control: the BEL spellings of the same ${name} set, query and reset`,
            stages: [stage('baseline', 'baseline'), stage('override', 'override', [osc(`${code};${overrideA}`, bel)]),
                stage('reset', 'reset', [osc(String(resetCode), bel)])] }),
    ];
}

/** The reset cases (OSC 117 / 119): the pinned fixture on the default state, the exact pinned spelling resetting a proven override,
 * and the coupled independence experiment with both reset orders. The observation queries are the channels the reset governs
 * (OSC 17 / OSC 19); OSC 117/119 themselves have no query form. */
function resetExperiments(caseId, code, otherCode, name, otherName) {
    const channel = code === 117 ? 17 : 19, otherChannel = code === 117 ? 19 : 17;
    const otherReset = code === 117 ? 119 : 117;
    return [
        experiment({ id: 'pinned', caseId, role: 'pinned', term: bel, queries: [{ code: channel }], cursor: [0, 0],
            label: `the pinned fixture: feed ${JSON.stringify(pinnedResetBytes(code))} and read cell (0,0), on the default state, with the ${name} query asked beside it`,
            stages: [stage('reset', 'reset', [pinnedResetBytes(code)])] }),
        experiment({ id: 'cycle', caseId, queries: [{ code: channel }, { code: otherChannel }],
            label: `the ${name} override set (ST) while the ${otherName} stays unset, then reset with the exact pinned BEL spelling: the measured unset default must return in every layer`,
            stages: [stage('baseline', 'baseline'), stage('override', 'override', [osc(`${channel};${overrideA}`)], [], true),
                stage('reset', 'reset', [osc(String(code), bel)], [], true)] }),
        experiment({ id: 'independence', caseId, queries: [{ code: 10 }, { code: 11 }, { code: 12 }, isolatedSlot, { code: channel }, { code: otherChannel }],
            label: `${name} and ${otherName} overrides and an unrelated palette slot set together; reset only the ${name} (ST): the ${otherName} override and the slot must survive; then reset the ${otherName} (BEL): only the slot survives; the two stages exercise both reset orders and end at the measured default`,
            stages: [stage('baseline', 'baseline'),
                stage('override', 'override', [osc(`${otherChannel};${overrideB}`), osc(`${channel};${overrideA}`), isolatedSlotSet], [], true),
                stage(`reset-${name.replace(/\s+/g, '-')}`, 'reset', [osc(String(code))], [], true, true),
                stage(`reset-${otherName.replace(/\s+/g, '-')}`, 'reset', [osc(String(otherReset), bel)], [], true)] }),
    ];
}

const osc17 = 'extensions.osc17-highlight-bg', osc19 = 'extensions.osc19-highlight-fg';
const osc117 = 'extensions.osc117-reset-highlight-bg', osc119 = 'extensions.osc119-reset-highlight-fg';

const caseDefinitions = [
    { caseId: osc17, code: 17, resetCode: 117, name: 'selection background', experiments: setExperiments(osc17, 17, 117, 'selection background') },
    { caseId: osc19, code: 19, resetCode: 119, name: 'selection foreground', experiments: setExperiments(osc19, 19, 119, 'selection foreground') },
    { caseId: osc117, code: 117, resetCode: 117, otherCode: 19, name: 'selection background', otherName: 'selection foreground', experiments: resetExperiments(osc117, 117, 19, 'selection background', 'selection foreground') },
    { caseId: osc119, code: 119, resetCode: 119, otherCode: 17, name: 'selection foreground', otherName: 'selection background', experiments: resetExperiments(osc119, 119, 17, 'selection foreground', 'selection background') },
];

/** The pinned fixture feeds of one case, in the order the pinned code issues them, with how it issues each. */
function pinnedFeedsOf(definition) {
    if (definition.resetCode === definition.code) {
        return { feeds: [pinnedResetBytes(definition.code)], kinds: ['feed'], pinnedRole: 'pinned/reset' };
    }
    return { feeds: [belQueryBytes({ code: definition.code })], kinds: ['capture'], pinnedRole: 'pinned/baseline' };
}

/** Every plan step of one experiment. */
function experimentPlan(item) {
    const plan = [planFeed(sceneBytes(item.scene ?? { cursor: item.cursor }))];
    for (const step of item.stages) {
        for (const bytes of step.writes) plan.push(planFeed(bytes));
        for (const spec of item.queries) plan.push(planExchange(item.term === bel ? belQueryBytes(spec) : queryBytes(spec), spec));
        plan.push(planRead('state'));
    }
    return plan;
}

function makeContract(definition) {
    const { feeds, kinds, pinnedRole } = pinnedFeedsOf(definition);
    const experiments = Object.freeze(definition.experiments);
    const roles = experiments.flatMap(item => item.stages.map(step => `${item.id}/${step.id}`));
    const pinnedRoles = [pinnedRole];
    const isResetCase = definition.resetCode === definition.code;
    return Object.freeze({
        caseId: definition.caseId, probeId: definition.caseId, id: definition.caseId.slice('extensions.'.length),
        code: definition.code, resetCode: definition.resetCode, isResetCase,
        experiments, roles: Object.freeze(roles), pinnedRoles: Object.freeze(pinnedRoles), pinnedPath: 'termless',
        pinnedFeeds: Object.freeze(feeds), pinnedKinds: Object.freeze(kinds),
        pinnedExpected: isResetCase
            ? `OSC ${definition.code} is fed with the exact pinned fixture bytes and cell (0,0) is read; the pinned code reports no semantic observable whatever the cell holds`
            : `OSC ${definition.code};?BEL is fed once and the reply must be a complete rgb: color report`,
        pinnedCriterion: isResetCase
            ? `the termless fixture feeds ${JSON.stringify(pinnedResetBytes(definition.code))}, reads cell (0,0) and returns notTestedResult (pass false, notTested.reason no-semantic-observable): it never decides`
            : `the termless oscColorQueryProbe feedCaptures ${JSON.stringify(belQueryBytes({ code: definition.code }))} once and is supported iff a complete rgb:{1-4}/{1-4}/{1-4} reply (BEL or ST terminated) is present; otherwise inconclusive (invalid-reply or no-response)`,
        plan: Object.freeze(experiments.flatMap(experimentPlan)),
        terminalFixture: isResetCase ? {
            writes: ['\x1b[1;1H\x1b[2K', osc(String(definition.code), bel)], demand: 'cursor-position-report',
            note: 'inconclusive by construction (evidence consumed): the pinned terminal path writes the pinned bytes, queries the cursor and returns unverifiedEffect whatever the cursor answers; the stock grade is carried unchanged and is not replaced by this suite',
        } : {
            writes: [belQueryBytes({ code: definition.code })], demand: 'query',
            note: 'the pinned terminal path re-asks the same query behind a sentinel and grades by the same oscReplyResult; in the default state the source is unset and no reply can arrive; it is preserved as metadata and is never run as a verdict by this suite',
        },
        appearanceScope: 'exact fenced query replies (unset selection sources demand explicit absence behind the completed ordering barrier), the App-resolved colors and override metadata, stored default markers, palette-indexed and literal cells, the cursor '
            + 'and the controlled offscreen render at a fixed synthetic clock with the established synthetic selection (2,4)..(4,10); pointer colors, ordinary foreground/background/cursor resets, clipboard behavior, physical mouse selection, native desktop appearance, other themes or platforms and remote delivery are not claimed',
        evidence: 'owned-stored-cells-app-resolved-colors-exact-replies-and-controlled-pixels',
        grade: observation => gradeCase(definition, experiments, observation),
        observed: observation => briefCase(experiments, observation),
    });
}

export const selectionColorsContracts = Object.freeze(caseDefinitions.map(makeContract));
export const selectionColorsCaseIds = Object.freeze(selectionColorsContracts.map(item => item.caseId));

export { planFeeds as selectionColorsFeeds, planReads as selectionColorsReads } from './graded-plan.mjs';

// ---------------------------------------------------------------------------------------------------------------------------
// Observation and grading.

const check = (description, passed) => ({ description, passed: Boolean(passed) });
const sameJson = (left, right) => JSON.stringify(left) === JSON.stringify(right);

/** The measured baseline colors of one stage's App layers: the render theme sources, with the nullable selection sources kept apart
 * from the derived paints (which are compared only when no override remains). */
const sourcesOfTheme = theme => ({ foreground: theme.foregroundArgb, background: theme.backgroundArgb, cursor: theme.cursorArgb,
    selectionBackground: theme.selectionBackgroundArgb, selectionForeground: theme.selectionForegroundArgb, palette: [...theme.ansiPaletteArgb] });

/** The replies that arrived against the colors the model demands. An unset selection source demands the explicit absence of a reply:
 * the exchange returned only after its completed ordering barrier, so a settled null reply is established evidence, never a timeout.
 * A set source demands a concrete reply with the exact 16-bit channels. */
function queriesMatch(queries, effective) {
    return queries.every(query => {
        const expected = queriedColor(effective, query.spec);
        if (expected === null) return query.reply === null;
        return query.reply !== null && sameJson(query.reply.channels16, channels16(expected));
    });
}

/** The expected binding of readScreen at a stage: the measured baseline with the effective colors and the model's flags. */
function bindingMatches(binding, base, effective, overrides) {
    const override = key => overrides[key] !== null;
    return binding.foregroundArgb === effective.foreground && binding.backgroundArgb === effective.background
        && sameJson(binding.ansiPaletteArgb, effective.palette) && override('foreground') === binding.foregroundOverride
        && override('background') === binding.backgroundOverride && sameJson(binding.paletteOverrideIndices, overrideIndices(overrides))
        && binding.reverseVideo === base.reverseVideo && binding.themeName === base.themeName && binding.minimumContrast === base.minimumContrast;
}

/** The theme fields the App derives from sources (cursor text, selection paints); they are compared only when no override remains. */
const derivedKeys = new Set(['cursorTextArgb', 'effectiveSelectionBackgroundArgb', 'effectiveSelectionForegroundArgb']);
const withoutDerived = theme => Object.fromEntries(Object.entries(theme).filter(([key]) => !derivedKeys.has(key)));

/** The render theme sources and override flags at a stage: the measured baseline theme with the model's overrides laid over it.
 * A selection source with no override keeps its measured value, which is `null` here (the default theme sets none). */
function themeMatches(theme, baseTheme, effective, overrides) {
    const expected = { ...baseTheme, foregroundArgb: effective.foreground, backgroundArgb: effective.background, cursorArgb: effective.cursor,
        ansiPaletteArgb: effective.palette, selectionBackgroundArgb: effective.selectionBackground, selectionForegroundArgb: effective.selectionForeground,
        foregroundOverride: overrides.foreground !== null, backgroundOverride: overrides.background !== null,
        cursorOverride: overrides.cursor !== null, selectionBackgroundOverride: overrides.selectionBackground !== null,
        selectionForegroundOverride: overrides.selectionForeground !== null, paletteOverrideIndices: overrideIndices(overrides) };
    return snapshotHash(withoutDerived(theme)) === snapshotHash(withoutDerived(expected));
}

/** One explicit cell's expected resolved channel: a default marker resolves to the effective default, a palette index to its effective
 * slot, literal RGB to itself. Selection overrides never change cell resolution: they paint the render, not the cells. */
const cellsMatchProxy = (screen, cells, effective) => cellsMatchImpl(screen, cells, effective, expandResolvedColors);

/** What the controlled render drew, sampled from the original PNG parent-side (grading supplies the per-stage object):
 * the untouched background cell, whether the unselected control region is byte-identical to the baseline's, the selected blank
 * region (its solid color when it is one color, and whether its bytes equal the baseline's), and the selected text ink
 * (its dominant non-background color, whether its bytes equal the baseline's, and how many pixels carry an active override ink). */
/** The distinguishable override values as opaque ARGB, for the render checks. */
const firstValueArgb = opaqueArgb(parseColorSpec(overrideA)), secondValueArgb = opaqueArgb(parseColorSpec(overrideB));
/** Which override value's ink coverage a stage demands, or 0 for any other color (failing closed). */
const inkCoverageOf = (render, effective, overrides) => {
    if (overrides.selectionForeground === null) return null;
    if (effective.selectionForeground === firstValueArgb) return render.selectedInk.firstValueCoverage;
    if (effective.selectionForeground === secondValueArgb) return render.selectedInk.secondValueCoverage;
    return 0;
};

function renderMatches(render, effective, overrides) {
    if (!render || render.background !== effective.background || !render.controlUnchanged) return false;
    if (overrides.selectionBackground !== null) return render.selectionBlank.solid === opaqueArgb(overrides.selectionBackground);
    if (!render.selectionBlank.matchesBaseline) return false;
    if (overrides.selectionForeground !== null) {
        // The selected ink must carry whichever distinguishable value this stage set; any other color fails closed.
        return inkCoverageOf(render, effective, overrides) > 10;
    }
    return render.selectedInk.dominantMatchesBaseline;
}

/** The mode the scene's cursor configuration must read back as: hidden at its home. */
const cursorWanted = item => ({ row: item.cursor[0], col: item.cursor[1], visible: false });

function cursorMatches(screen, theme, wanted, steps, upTo) {
    const phase = theme.presentation ?? null;
    const written = steps.slice(0, upTo + 1).some(step => step.writes.some(bytes => bytes.endsWith('X')));
    const col = written ? wanted.col + 1 : wanted.col;
    return screen.cursorRow === wanted.row && screen.cursorCol === col && screen.cursorVisible === wanted.visible
        && phase !== null && phase.cursorRow === wanted.row && phase.cursorCol === col && phase.cursorVisible === wanted.visible;
}

/** Group the frames of the case's reads by experiment and stage. */
export function selectionColorsObservation(item, reads) {
    if (!Array.isArray(reads) || reads.length !== item.roles.length) throw new Error('Missing selection-color read captures.');
    let at = 0;
    return Object.fromEntries(item.experiments.map(spec => [spec.id, spec.stages.map(step => {
        const read = reads[at++];
        return { id: step.id, kind: step.kind, screen: read.screen, queries: read.queries, theme: read.details.theme,
            presentation: read.details.presentation, pixelSha256: read.pixelSha256, render: read.render };
    })]));
}

/** The model's overrides are all empty. */
const noneOverridden = overrides => overrides.palette.size === 0 && overrides.foreground === null && overrides.background === null
    && overrides.cursor === null && overrides.selectionBackground === null && overrides.selectionForeground === null;

/** The checks of one stage against the model: the four App layers, the stored text and the cursor, and (with no override left) the
 * complete theme including the derived selection paints and cursor text. */
function stageChecks({ spec, base, observed, index, overrides, effective, cells, wanted }) {
    const label = `${spec.id}/${observed.id}`;
    let stepCells = cells;
    for (const earlier of spec.stages.slice(0, index + 1)) stepCells = [...stepCells, ...earlier.cells];
    const checks = [check(`${label}: the exact query replies are the model's colors, with an unset selection source replying nothing`, queriesMatch(observed.queries, effective)),
        check(`${label}: the App's resolved colors and override metadata match`, bindingMatches(observed.screen.colorBinding, base.screen.colorBinding, effective, overrides)),
        check(`${label}: the render theme sources and override flags match`, themeMatches(observed.theme, base.theme, effective, overrides)),
        check(`${label}: default markers, palette-indexed and literal cells resolve to the model`, cellsMatchProxy(observed.screen, stepCells, effective)),
        check(`${label}: the controlled render draws the effective background, the selection paint the model demands, and an unchanged unselected control`, renderMatches(observed.render, effective, overrides)),
        check(`${label}: stored text, cursor position and visibility are as written`, sameJson(observed.screen.lines, expectedLines(observed.screen.rows, spec.stages, index))
            && cursorMatches(observed.screen, { presentation: observed.presentation }, wanted, spec.stages, index))];
    if (noneOverridden(overrides)) checks.push(check(`${label}: the complete theme, derived selection paints and cursor text included, equals the baseline`, snapshotHash(observed.theme) === snapshotHash(base.theme)));
    return checks;
}

/** The controls an override stage must meet before a reset can be judged: a distinct change in every overridden target, visible in the render. */
function overrideControls({ spec, base, observed, overrides, effective, baseline }) {
    const label = `${spec.id}/${observed.id}`;
    const changed = [effective.selectionBackground !== baseline.selectionBackground, effective.selectionForeground !== baseline.selectionForeground,
        effective.foreground !== baseline.foreground, effective.background !== baseline.background, effective.cursor !== baseline.cursor,
        ...effective.palette.map((color, slot) => color !== baseline.palette[slot])];
    const controls = [check(`${label}: the override differs from the measured baseline`, changed.some(Boolean)),
        check(`${label}: every overridden target differs from its baseline`, targetsChanged(overrides, effective, baseline))];
    if (spec.pixels) controls.push(check(`${label}: the controlled render differs from the baseline render`, observed.pixelSha256 !== base.pixelSha256));
    return controls;
}

/** The baseline controls: no runtime override in any layer, two App layers agreeing, the scene as written and no reverse video. */
function baselineControls(spec, base, baseline) {
    const binding = base.screen.colorBinding, theme = base.theme;
    return [check('the baseline has no runtime override in any layer', !theme.foregroundOverride && !theme.backgroundOverride && !theme.cursorOverride
            && !theme.selectionBackgroundOverride && !theme.selectionForegroundOverride && theme.paletteOverrideIndices.length === 0
            && !binding.foregroundOverride && !binding.backgroundOverride && binding.paletteOverrideIndices.length === 0
            && theme.selectionBackgroundArgb === null && theme.selectionForegroundArgb === null),
        check('the baseline readScreen binding equals the render theme (two App layers agree)', binding.foregroundArgb === baseline.foreground
            && binding.backgroundArgb === baseline.background && sameJson(binding.ansiPaletteArgb, baseline.palette)),
        check('the scene is as written: text rows, steady scene colors and no reverse video', sameJson(base.screen.lines, expectedLines(base.screen.rows, spec.stages, 0))
            && binding.reverseVideo === false)];
}

/** Grade one experiment: every stage's four layers against the model; controls (baseline, override) before assertions (second, resets). */
/** The graded (non-control) assertions of one `second`/`reset` stage: stale rejection, render difference and keeps-drawn. */
function stageAssertions(spec, observed, index, stages, base, assertions) {
    const step = spec.stages[index], previous = stages[index - 1];
    if (observed.kind === 'second') {
        assertions.push(check(`${spec.id}/${observed.id}: the second value rejects a stale observation`, previous.queries[0]?.reply !== null
            && observed.queries[0]?.reply !== null && !sameJson(previous.queries[0].reply.channels16, observed.queries[0].reply.channels16)));
        if (spec.pixels) assertions.push(check(`${spec.id}/${observed.id}: the second value changed the controlled render`, observed.pixelSha256 !== previous.pixelSha256));
    }
    if (spec.pixels && step.rendered) {
        assertions.push(check(`${spec.id}/${observed.id}: the stage changed the controlled render`, observed.pixelSha256 !== previous.pixelSha256));
    }
    if (spec.pixels && step.keepsDrawn) {
        assertions.push(check(`${spec.id}/${observed.id}: the overrides that remain are still drawn (the render is not the baseline render)`, observed.pixelSha256 !== base.pixelSha256));
    }
}

function gradeExperiment(spec, stages) {
    const base = stages[0], baseline = sourcesOfTheme(base.theme), writes = overridesByStageOf(spec);
    const wanted = cursorWanted(spec), cells = sceneCells();
    const controls = baselineControls(spec, base, baseline);
    const assertions = [];
    for (const [index, observed] of stages.entries()) {
        const overrides = writes[index], effective = effectiveColors(baseline, overrides);
        const target = observed.kind === 'baseline' || observed.kind === 'override' ? controls : assertions;
        target.push(...stageChecks({ spec, base, observed, index, overrides, effective, cells, wanted }));
        if (observed.kind === 'override') controls.push(...overrideControls({ spec, base, observed, overrides, effective, baseline }));
        if (observed.kind !== 'baseline' && observed.kind !== 'override') stageAssertions(spec, observed, index, stages, base, assertions);
    }
    const last = stages.length - 1;
    if (spec.pixels && noneOverridden(writes[last])) {
        assertions.push(check(`${spec.id}: the final controlled render is byte-identical to the baseline render`, stages[last].pixelSha256 === base.pixelSha256));
    }
    return { id: spec.id, role: spec.role, label: spec.label, controls, assertions, outcome: outcomeOf(controls, assertions) };
}

function targetsChanged(overrides, effective, baseline) {
    return [...overrides.palette.keys()].every(slot => effective.palette[slot] !== baseline.palette[slot])
        && (overrides.foreground === null || effective.foreground !== baseline.foreground)
        && (overrides.background === null || effective.background !== baseline.background)
        && (overrides.cursor === null || effective.cursor !== baseline.cursor)
        && (overrides.selectionBackground === null || effective.selectionBackground !== baseline.selectionBackground)
        && (overrides.selectionForeground === null || effective.selectionForeground !== baseline.selectionForeground);
}

/** Invalid controls are inconclusive; with valid controls any failed assertion is unsupported. */
function outcomeOf(controls, assertions) {
    if (controls.some(item => !item.passed)) return 'inconclusive';
    return assertions.every(item => item.passed) ? 'supported' : 'unsupported';
}

/** The model's overrides after each stage of an experiment (a stage's own writes included). */
function overridesByStageOf(spec) {
    let state = noOverrides();
    return spec.stages.map(step => {
        for (const bytes of step.writes) state = applyColorWrites(state, bytes);
        return state;
    });
}

/** A case needs a qualified positive override-query control before an absent reply can be evidence of an unset default: at least one
 * exchange with a non-null expected color must have arrived. Whether the reply also matched is already every stage's first check, so
 * this only has to find one override-active stage whose exchange carried a concrete reply. */
function hasPositiveQueryControl(experiments, observation) {
    return experiments.some(spec => overridesByStageOf(spec).some((state, index) =>
        (state.selectionBackground !== null || state.selectionForeground !== null)
        && observation[spec.id][index].queries.some(query => query.reply !== null)));
}

/** The failed-checks note of one outcome class. */
function failedChecks(results, outcome) {
    return results.filter(item => item.outcome === outcome)
        .map(item => `${item.id}: ${[...item.controls, ...item.assertions].filter(entry => !entry.passed).map(entry => entry.description).join('; ')}`).join(' | ');
}

function gradeCase(definition, experiments, observation) {
    const results = experiments.map(spec => gradeExperiment(spec, observation[spec.id]));
    if (results.some(item => item.outcome === 'unsupported')) return { outcome: 'unsupported', note: failedChecks(results, 'unsupported'), experiments: results };
    if (results.some(item => item.outcome === 'inconclusive')) return { outcome: 'inconclusive', note: failedChecks(results, 'inconclusive'), experiments: results };
    if (!hasPositiveQueryControl(experiments, observation)) {
        return { outcome: 'inconclusive', note: 'no qualified positive override-query control: an absent reply cannot be graded as an unset default', experiments: results };
    }
    return { outcome: 'supported', experiments: results };
}

/** The compact observed record of a case: each stage's replies, override metadata and the target colors. A nullable selection source
 * or paint is preserved as `null`, never rendered as a hex value. */
const nullableHex = value => value === null ? null : hexOf(value);
function briefCase(experiments, observation) {
    return Object.fromEntries(experiments.map(spec => [spec.id, { role: spec.role, label: spec.label, term: spec.term === bel ? 'BEL' : 'ST',
        stages: observation[spec.id].map(stage => ({ stage: stage.id,
            replies: stage.queries.map(query => ({ query: query.spec, reply: query.reply?.raw?.replaceAll(esc, String.raw`\e`) ?? null })),
            binding: { foregroundOverride: stage.screen.colorBinding.foregroundOverride, backgroundOverride: stage.screen.colorBinding.backgroundOverride,
                paletteOverrideIndices: stage.screen.colorBinding.paletteOverrideIndices },
            theme: { selectionBackground: nullableHex(stage.theme.selectionBackgroundArgb), selectionForeground: nullableHex(stage.theme.selectionForegroundArgb),
                selectionBackgroundOverride: stage.theme.selectionBackgroundOverride, selectionForegroundOverride: stage.theme.selectionForegroundOverride,
                effectiveSelectionBackground: nullableHex(stage.theme.effectiveSelectionBackgroundArgb), effectiveSelectionForeground: nullableHex(stage.theme.effectiveSelectionForegroundArgb) },
            cursor: { row: stage.screen.cursorRow, col: stage.screen.cursorCol, visible: stage.screen.cursorVisible },
            pixelSha256: stage.pixelSha256 })) }]));
}

/** Independent contract grader entry. */
export function gradeSelectionColorsCase(item, observation, revision = selectionColorsContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown selection-color contracts revision ${revision}.`);
    if (!observation || item.experiments.some(spec => !Array.isArray(observation[spec.id]) || observation[spec.id].length !== spec.stages.length)) {
        throw new Error(`Missing ${item.caseId} read captures.`);
    }
    return item.grade(observation);
}

/** A selection-color screen carries attributes, stored details (with overline), the cursor mode and resolved colors, and every
 * resolved channel must cohere with the stored default markers, palette indices and literal bytes of the same snapshot. */
export function validateSelectionColorsScreen(screen) {
    if (!screen?.cursorMode || !screen.colorBinding || !screen.resolvedColors || !screen.cellDetails || !screen.attributeRuns) throw new Error('Incomplete selection-color screen.');
    checkStoredColorCoherence(screen, true);
}

/** The fixed fixture facts other modules and tests read. */
export const selectionColorsFixture = Object.freeze({ overrideA, overrideB, controlFg, controlBg, literalBg, sceneText, sceneBytes, sceneCells,
    expectedLines, selectionColorsSelection, pinnedResetBytes, isolatedSlot, isolatedSlotSet, bel, st });
/** The independent model, exposed so tests can cross-check it. */
export const selectionColorsModel = Object.freeze({ overridesByStageOf, effectiveColors, queriedColor, expectedLines, gradeExperiment, overrideIndices, channels16 });

/** Every read of the suite in order: which case, experiment and stage it is, and the queries asked before it. */
export const selectionColorsReadPlan = Object.freeze(selectionColorsContracts.flatMap(item => item.experiments.flatMap(spec => spec.stages.map(step => Object.freeze({
    caseId: item.caseId, experimentId: spec.id, stageId: step.id, queries: Object.freeze(spec.queries) })))));

