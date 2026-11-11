// Versioned probe contracts for the runtime-color reset qualification: extensions.osc104-reset-palette, extensions.osc110-reset-fg,
// extensions.osc111-reset-bg and extensions.osc112-reset-cursor (terminfo.dev@4.0.0).
//
// Pinned definition (bundle SHA-256 aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6). Every case is one
// `colorResetProbe(setCode, resetCode[, index])` registration. Its TERMLESS path: feedCapture the BEL-spelled query (`ESC]4;0;?BEL`,
// `ESC]10;?BEL`, `ESC]11;?BEL`, `ESC]12;?BEL`), feed the BEL-spelled set of `rgb:aa/bb/cc` (`rgb:12/34/56` when the original already is
// that color), feedCapture the query, feed the BEL-spelled reset (`ESC]104;0BEL`, `ESC]110BEL`, `ESC]111BEL`, `ESC]112BEL`) and
// feedCapture the query again. It grades from the three replies alone: supported iff the changed reply equals the requested color and
// the restored reply equals the original, unsupported when the mutation read back but the original did not return, inconclusive when
// the mutation or a readback was not established. The TERMINAL path of every case is `unmeasuredStateEffect`: it writes nothing,
// reads nothing and is inconclusive by design (evidence `none`); the stock grade stays that, and this suite never replaces it.
//
// The effect contract (docs/qa/terminfo/runtime-color-reset.md) is only ever stricter than the pinned termless criterion. An
// independent runtime-color model (color-reset-model.mjs) turns the exact bytes written so far into the overrides each stage must
// show, and the stage is read from four separately retained layers: the exact fenced query replies, the App's resolved colors and
// override metadata (readScreen binding and per-cell resolved colors), the stored cells and cursor, and the controlled offscreen
// render (theme and presentation details plus the PNG digest). Each experiment measures a baseline before it mutates anything,
// proves the intended override took effect in every layer, and only then grades the reset against that measured baseline.
//
// Scope: the owned macOS sidecar, default theme, steady synthetic render phase (clock 0, cursor phase fixed visible, scale 1). Blink
// cadence, desktop appearance, selection and pointer colors, Kitty named colors and alternate-screen or DECSTR clearing of overrides
// (neither clears them) are not claimed.
import { planFeed, planRead } from './graded-plan.mjs';
import { clearColors } from './color-cases.mjs';
import { expandResolvedColors, checkStoredColorCoherence } from './resolved-colors.mjs';
import { snapshotHash } from './agent-observer.mjs';
import { applyColorWrites, noOverrides, effectiveColors, channels16, opaqueArgb, overrideIndices, queriedColor, hexOf } from './color-reset-model.mjs';

export const colorResetContractsVersion = 1;
export const colorResetSuiteId = 'ntilde-owned-runtime-color-reset-graded-v1';
export const colorResetNoncePrefix = 'NTILDE_CR_';
export const colorResetPrefix = 'crst';
/** The measured reset every scene begins with: DECSTR (which leaves runtime colors alone), origin, margins, IRM and SGR off, screen cleared, cursor hidden. */
export const colorResetBase = '\x1b[!p\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[2J\x1b[H\x1b[?25l';
/** Bind and finish bytes: the nonce binds the pane; finish clears every runtime color explicitly before the alternate screen is left. */
export const colorResetBind = '\x1b[?1049h' + colorResetBase;
export const colorResetFinish = clearColors + '\x1b[?1049l\x1b[!p';

const esc = '\x1b', bel = '\x07', st = '\x1b\\';
const at = (row, col) => `\x1b[${row + 1};${col + 1}H`;
const osc = (body, term = st) => `${esc}]${body}${term}`;
const sceneText = 'HMWqg_|';
const literalFg = 0xf0f0f0, literalBg = 0x102030;

/** One plan step: an exchange writes the exact query bytes, then retains the raw reply (or its explicit absence) before the DA barrier. */
export const planExchange = (bytes, spec) => ({ feed: bytes, query: spec });

/** The exact bytes of one query: the pinned BEL spelling or the ST spelling of the controls. */
export const queryBytes = (spec, term) => osc(`${spec.code};${spec.code === 4 ? spec.index + ';' : ''}?`, term);

/** One stage: bytes written, then every query asked, then one read. `kind` says what the stage is for: a baseline and an override are
 * controls (they must hold before a reset can be graded), anything else is graded. `cells` are extra explicit cells the stage's
 * writes leave behind, expected from that stage on. */
const stage = (id, kind, writes = [], cells = []) => ({ id, kind, writes, cells });

/** The scene of an experiment: five rows of text under defaults, a palette color, a literal color and as backgrounds, and the cursor. */
function sceneBytes(scene) {
    const row = (index, sgr) => at(index, 4) + sgr + sceneText + '\x1b[0m';
    return clearColors + colorResetBase
        + row(1, '') + row(2, `\x1b[38;5;${scene.fgIndex}m`) + row(3, '\x1b[38;2;240;240;240m')
        + row(4, `\x1b[48;5;${scene.bgIndex}m`) + row(5, '\x1b[48;2;16;32;48m')
        + (scene.cursor?.setup ?? '') + at(8, 8);
}

/** The explicit cells every scene holds: where, and how each channel is stored (a default marker, a palette index or literal RGB). */
function sceneCells(scene) {
    return [
        { id: 'default', row: 1, col: 4, fg: { source: 'default' }, bg: { source: 'default' } },
        { id: 'palette-fg', row: 2, col: 4, fg: { source: 'palette', index: scene.fgIndex }, bg: { source: 'default' } },
        { id: 'literal-fg', row: 3, col: 4, fg: { source: 'literal', rgb: literalFg }, bg: { source: 'default' } },
        { id: 'palette-bg', row: 4, col: 4, fg: { source: 'default' }, bg: { source: 'palette', index: scene.bgIndex } },
        { id: 'literal-bg', row: 5, col: 4, fg: { source: 'default' }, bg: { source: 'literal', rgb: literalBg } },
    ];
}

const steadyBlock = Object.freeze({ setup: '\x1b[2 q\x1b[?25h', visible: true, style: 0, blink: false });
const steadyBeamHidden = Object.freeze({ setup: '\x1b[6 q\x1b[?25h\x1b[?25l', visible: false, style: 1, blink: false });

const pinnedColor = 'rgb:aa/bb/cc';
const isolated = Object.freeze({ foreground: '#175bad', background: '#20e060', cursor: '#e0a020' });
const slotRgb = index => ((index * 37 + 11) % 256) << 16 | ((index * 91 + 53) % 256) << 8 | ((255 - ((index * 13) % 256) + 256) % 256);
const slotSpec = index => `${index};${hexOf(slotRgb(index))}`;
const slotsSet = indices => osc('4;' + indices.map(slotSpec).join(';'));
const allSlots = Array.from({ length: 256 }, (_, index) => index);

/** An experiment: its scene, the queries asked after every stage, their terminator and its stages. */
function experiment(spec) {
    const scene = { fgIndex: 33, bgIndex: 34, cursor: null, ...spec.scene };
    return Object.freeze({ role: 'control', term: st, pixels: true, ...spec, scene, stages: Object.freeze(spec.stages.map(Object.freeze)) });
}

/** The pinned experiment of a case: the pinned bytes, three reads (baseline, override, reset), nothing else written. */
function pinnedExperiment(caseId, setCode, resetCode, index, scene = {}) {
    const where = index === undefined ? '' : `${index};`;
    const query = setCode === 4 ? { code: 4, index } : { code: setCode };
    return experiment({ id: 'pinned', caseId, role: 'pinned', term: bel, queries: [query], scene,
        label: `the pinned fixture: ${setCode === 4 ? 'slot 0' : 'the color'} set to ${pinnedColor}, then reset with the exact pinned spelling`,
        stages: [stage('baseline', 'baseline'), stage('override', 'override', [osc(`${setCode};${where}${pinnedColor}`, bel)]),
            stage('reset', 'reset', [osc(`${resetCode}${index === undefined ? '' : ';' + index}`, bel)])] });
}

/** Overrides of the other two dynamic colors, written with the one under test, so a reset can be shown to leave them alone. */
const dynamicSets = targets => targets.map(([code, name]) => osc(`${code};${isolated[name]}`));

const osc104 = 'extensions.osc104-reset-palette', osc110 = 'extensions.osc110-reset-fg', osc111 = 'extensions.osc111-reset-bg', osc112 = 'extensions.osc112-reset-cursor';
const dynamics = [[10, 'foreground'], [11, 'background'], [12, 'cursor']];
const allThree = [{ code: 10 }, { code: 11 }, { code: 12 }];

/** The experiments of a dynamic-color case: pinned, isolation (the other two overrides survive), and SGR default (39 or 49) versus the reset. */
function dynamicExperiments(caseId, code, resetCode, name, sgrDefault, sgrSet, extras) {
    const others = dynamics.filter(([item]) => item !== code), mine = dynamics.filter(([item]) => item === code);
    const sgrKind = code === 10 ? 'foreground' : 'background';
    const sgrCells = [{ id: 'sgr-explicit', row: 10, col: 4, ...(code === 10 ? { fg: { source: 'palette', index: 33 }, bg: { source: 'default' } }
        : { fg: { source: 'default' }, bg: { source: 'palette', index: 34 } }) },
    { id: 'sgr-default', row: 10, col: 5, fg: { source: 'default' }, bg: { source: 'default' } }];
    const penCell = { id: 'pen', row: 12, col: 4, ...(code === 10 ? { fg: { source: 'palette', index: 33 }, bg: { source: 'default' } }
        : { fg: { source: 'default' }, bg: { source: 'palette', index: 34 } }) };
    return [
        pinnedExperiment(caseId, code, resetCode, undefined, extras?.scene),
        experiment({ id: 'isolation', caseId, queries: allThree, scene: extras?.scene,
            label: `the ${name} override reset (ST spelling) while the other two dynamic colors stay overridden`,
            stages: [stage('baseline', 'baseline'), stage('override', 'override', [...dynamicSets(others), ...dynamicSets(mine)]),
                stage('reset', 'reset', [osc(String(resetCode))])] }),
        ...(sgrDefault === null ? [] : [experiment({ id: 'sgr-default', caseId, queries: [{ code }], pixels: false,
            label: `SGR ${sgrDefault} changes the active pen, not the runtime ${sgrKind} default, and OSC ${resetCode} does not change the pen`,
            stages: [stage('baseline', 'baseline'), stage('override', 'override', [osc(`${code};${isolated[name]}`)]),
                stage('sgr', 'sgr', [at(10, 4) + `\x1b[${sgrSet}mA\x1b[${sgrDefault}mB\x1b[0m` + at(8, 8)], sgrCells),
                stage('reset', 'reset', [at(12, 4) + `\x1b[${sgrSet}m` + osc(String(resetCode)) + 'C\x1b[0m' + at(8, 8)], [penCell])] })]),
    ];
}

/** The cursor case: pinned, isolation, and a hidden steady-beam cursor whose shape and visibility must not move. */
function cursorExperiments() {
    return [
        pinnedExperiment(osc112, 12, 112, undefined, { cursor: steadyBlock }),
        experiment({ id: 'isolation', caseId: osc112, queries: allThree, scene: { cursor: steadyBlock },
            label: 'the cursor override reset (ST spelling) while foreground and background stay overridden; the cursor keeps its steady block shape and visibility',
            stages: [stage('baseline', 'baseline'), stage('override', 'override', [...dynamicSets(dynamics.filter(([code]) => code !== 12)), ...dynamicSets([[12, 'cursor']])]),
                stage('reset', 'reset', [osc('112')])] }),
        experiment({ id: 'hidden-beam', caseId: osc112, queries: [{ code: 12 }], scene: { cursor: steadyBeamHidden }, pixels: false,
            label: 'a hidden steady-beam cursor: the cursor color is set and reset, the shape stays beam, the cursor stays hidden',
            stages: [stage('baseline', 'baseline'), stage('override', 'override', [osc(`12;${isolated.cursor}`)]), stage('reset', 'reset', [osc('112')])] }),
    ];
}

/** The palette case: slot 0 pinned; individual slots across the base, bright, cube, extended and gray ranges reset one, several, then the rest; the whole palette. */
function paletteExperiments() {
    const slots = [1, 9, 16, 200, 255], wide = [0, 15, 16, 100, 231, 232, 255];
    return [
        pinnedExperiment(osc104, 4, 104, 0, { fgIndex: 0, bgIndex: 0 }),
        experiment({ id: 'individual-slots', caseId: osc104, queries: slots.map(index => ({ code: 4, index })), scene: { fgIndex: 1, bgIndex: 200 },
            label: 'five slots overridden (1, 9, 16, 200, 255); reset slot 1 alone, then 9 and 200 together, then the remaining 16 and 255, each time with the unrelated slots still overridden',
            stages: [stage('baseline', 'baseline'), stage('override', 'override', [slotsSet(slots)]),
                stage('reset-one', 'reset', [osc('104;1')]), stage('reset-two', 'reset', [osc('104;9;200')]), stage('reset-rest', 'reset', [osc('104;16;255')])] }),
        experiment({ id: 'whole-palette', caseId: osc104, queries: wide.map(index => ({ code: 4, index })), scene: { fgIndex: 100, bgIndex: 231 }, term: bel,
            label: 'all 256 slots overridden, then reset with the parameterless whole-palette spelling ESC]104 BEL (a control: the pinned fixture resets slot 0 only)',
            stages: [stage('baseline', 'baseline'), stage('override', 'override', [slotsSet(allSlots)]), stage('reset', 'reset', [osc('104', bel)])] }),
    ];
}

const caseDefinitions = [
    { caseId: osc104, setCode: 4, resetCode: 104, name: 'palette', experiments: paletteExperiments() },
    { caseId: osc110, setCode: 10, resetCode: 110, name: 'foreground', experiments: dynamicExperiments(osc110, 10, 110, 'foreground', 39, '38;5;33') },
    { caseId: osc111, setCode: 11, resetCode: 111, name: 'background', experiments: dynamicExperiments(osc111, 11, 111, 'background', 49, '48;5;34') },
    { caseId: osc112, setCode: 12, resetCode: 112, name: 'cursor', experiments: cursorExperiments() },
];

/** The five pinned feeds of one case, in the order the pinned code issues them, with how it issues each. */
function pinnedFeedsOf({ setCode, resetCode, experiments }) {
    const pinned = experiments.find(item => item.role === 'pinned');
    const [first] = pinned.queries, query = queryBytes(first, bel);
    const feeds = [query, pinned.stages[1].writes[0], query, pinned.stages[2].writes[0], query];
    return { feeds, kinds: ['capture', 'feed', 'capture', 'feed', 'capture'], pinned, setCode, resetCode };
}

/** Every plan step of one experiment. */
function experimentPlan(item) {
    const plan = [planFeed(sceneBytes(item.scene))];
    for (const step of item.stages) {
        for (const bytes of step.writes) plan.push(planFeed(bytes));
        for (const spec of item.queries) plan.push(planExchange(queryBytes(spec, item.term), spec));
        plan.push(planRead('state'));
    }
    return plan;
}

function makeContract(definition) {
    const { feeds, kinds, pinned } = pinnedFeedsOf(definition);
    const experiments = Object.freeze(definition.experiments);
    const roles = experiments.flatMap(item => item.stages.map(step => `${item.id}/${step.id}`));
    const pinnedRoles = pinned.stages.map(step => `${pinned.id}/${step.id}`);
    return Object.freeze({
        caseId: definition.caseId, probeId: definition.caseId, id: definition.caseId.slice('extensions.'.length), sequence: feeds[3],
        setCode: definition.setCode, resetCode: definition.resetCode,
        experiments, roles: Object.freeze(roles), pinnedRoles: Object.freeze(pinnedRoles), pinnedPath: 'termless',
        pinnedFeeds: Object.freeze(feeds), pinnedKinds: Object.freeze(kinds),
        pinnedExpected: `OSC ${definition.resetCode} restores the prior OSC ${definition.setCode} color after a verified change`,
        pinnedCriterion: `the termless colorResetProbe(${definition.setCode}, ${definition.resetCode}${definition.setCode === 4 ? ', 0' : ''}) feeds the BEL-spelled query, the set of ${pinnedColor}, the query, the BEL-spelled reset and the query again, and is supported iff the changed reply equals the requested color and the restored reply equals the original`,
        plan: Object.freeze(experiments.flatMap(experimentPlan)),
        terminalFixture: { writes: [], demand: 'none: the pinned terminal callback is unmeasuredStateEffect', minimum: { rows: 1, cols: 1 },
            note: 'inconclusive by design (evidence none): it writes nothing and reads nothing; the stock grade is carried unchanged and is not replaced by this suite' },
        appearanceScope: 'exact fenced query replies, the App-resolved colors and override metadata, stored default markers, palette-indexed and literal cells, the cursor '
            + 'and the controlled offscreen render at a fixed synthetic clock; real-time cadence, desktop appearance, selection and pointer colors, Kitty named colors and '
            + 'alternate-screen or DECSTR clearing of overrides are not claimed',
        evidence: 'owned-runtime-color-layers',
        grade: observation => gradeCase(definition.caseId, experiments, observation),
        observed: observation => briefCase(experiments, observation),
    });
}

export const colorResetContracts = Object.freeze(caseDefinitions.map(makeContract));
export const colorResetCaseIds = Object.freeze(colorResetContracts.map(item => item.caseId));

export { planFeeds as colorResetFeeds, planReads as colorResetReads } from './graded-plan.mjs';

// ---------------------------------------------------------------------------------------------------------------------------
// Observation and grading.

const check = (description, passed) => ({ description, passed: Boolean(passed) });
const sameJson = (left, right) => JSON.stringify(left) === JSON.stringify(right);

/** The measured baseline colors of one stage's three App layers. */
const colorsOfTheme = theme => ({ foreground: theme.foregroundArgb, background: theme.backgroundArgb, cursor: theme.cursorArgb, palette: [...theme.ansiPaletteArgb] });

/** Compare queried replies to the colors the model demands. A reply carries its 16-bit channels. */
function queriesMatch(queries, effective) {
    return queries.every(query => query.reply !== null && sameJson(query.reply.channels16, channels16(queriedColor(effective, query.spec))));
}

/** The expected binding of readScreen at a stage: the measured baseline with the effective colors and the model's flags. */
function bindingMatches(binding, base, effective, overrides) {
    return binding.foregroundArgb === effective.foreground && binding.backgroundArgb === effective.background
        && sameJson(binding.ansiPaletteArgb, effective.palette) && binding.foregroundOverride === (overrides.foreground !== null)
        && binding.backgroundOverride === (overrides.background !== null) && sameJson(binding.paletteOverrideIndices, overrideIndices(overrides))
        && binding.reverseVideo === base.reverseVideo && binding.themeName === base.themeName && binding.minimumContrast === base.minimumContrast;
}

/** The theme fields that are overrides, and the ones the App derives from them (cursor text, selection paint) and never reports as sources. */
const derivedKeys = ['cursorTextArgb', 'effectiveSelectionBackgroundArgb', 'effectiveSelectionForegroundArgb'];
const withoutDerived = theme => Object.fromEntries(Object.entries(theme).filter(([key]) => !derivedKeys.includes(key)));
function themeMatches(theme, baseTheme, effective, overrides) {
    const expected = { ...baseTheme, foregroundArgb: effective.foreground, backgroundArgb: effective.background, cursorArgb: effective.cursor,
        ansiPaletteArgb: effective.palette, foregroundOverride: overrides.foreground !== null, backgroundOverride: overrides.background !== null,
        cursorOverride: overrides.cursor !== null, paletteOverrideIndices: overrideIndices(overrides) };
    return snapshotHash(withoutDerived(theme)) === snapshotHash(withoutDerived(expected));
}

/** One explicit cell's expected resolved channel: a default marker resolves to the effective default, a palette index to its effective slot, literal RGB to itself. */
function channelMatches(actual, wanted, effective, defaultKey) {
    if (actual.source !== wanted.source) return false;
    if (wanted.source === 'default') return actual.argb === effective[defaultKey];
    if (wanted.source === 'palette') return actual.index === wanted.index && actual.argb === effective.palette[wanted.index];
    return actual.argb === opaqueArgb(wanted.rgb);
}

function cellsMatch(screen, cells, effective) {
    const resolved = expandResolvedColors(screen.resolvedColors, screen.rows * screen.cols, screen.colorBinding);
    return cells.every(cell => {
        const value = resolved[cell.row * screen.cols + cell.col];
        return channelMatches(value.fg, cell.fg, effective, 'foreground') && channelMatches(value.bg, cell.bg, effective, 'background');
    });
}

/** The lines the screen must hold at a stage: five scene rows, plus what the sgr and pen stages wrote. */
function expectedLines(rows, steps, upTo) {
    const lines = Array.from({ length: rows }, () => '');
    for (let row = 1; row <= 5; row++) lines[row] = '    ' + sceneText;
    if (steps.slice(0, upTo + 1).some(step => step.id === 'sgr')) lines[10] = '    AB';
    if (steps.slice(0, upTo + 1).some(step => step.id === 'reset' && step.cells.length)) lines[12] = '    C';
    return lines;
}

/** The mode the scene's cursor configuration must read back as. */
const cursorWanted = scene => scene.cursor ?? { visible: false, style: null, blink: null };

function cursorMatches(screen, theme, wanted) {
    const phase = theme.presentation ?? null;
    return screen.cursorRow === 8 && screen.cursorCol === 8 && screen.cursorVisible === wanted.visible
        && (wanted.style === null || (screen.cursorMode.style === wanted.style && screen.cursorMode.blink === wanted.blink))
        && phase !== null && phase.cursorRow === 8 && phase.cursorCol === 8 && phase.cursorVisible === wanted.visible;
}

/** Group the frames of the case's reads by experiment and stage. */
export function colorResetObservation(item, reads) {
    if (!Array.isArray(reads) || reads.length !== item.roles.length) throw new Error('Missing runtime-color read captures.');
    let at = 0;
    return Object.fromEntries(item.experiments.map(spec => [spec.id, spec.stages.map(step => {
        const read = reads[at++];
        return { id: step.id, kind: step.kind, screen: read.screen, queries: read.queries, theme: read.details.theme,
            presentation: read.details.presentation, pixelSha256: read.pixelSha256 };
    })]));
}

/** Grade one experiment: every stage's four layers against the model; controls (baseline, override) before assertions (resets). */
function gradeExperiment(spec, stages) {
    const base = stages[0], baseline = colorsOfTheme(base.theme);
    const writes = overridesByStageOf(spec);
    const controls = [], assertions = [];
    const wanted = cursorWanted(spec.scene);
    const cells = sceneCells(spec.scene);
    controls.push(check('the baseline has no runtime override in any layer', writes[0].palette.size === 0 && !base.theme.foregroundOverride && !base.theme.backgroundOverride
        && !base.theme.cursorOverride && base.theme.paletteOverrideIndices.length === 0 && !base.screen.colorBinding.foregroundOverride
        && !base.screen.colorBinding.backgroundOverride && base.screen.colorBinding.paletteOverrideIndices.length === 0));
    controls.push(check('the baseline readScreen binding equals the render theme (two App layers agree)', base.screen.colorBinding.foregroundArgb === baseline.foreground
        && base.screen.colorBinding.backgroundArgb === baseline.background && sameJson(base.screen.colorBinding.ansiPaletteArgb, baseline.palette)));
    controls.push(check('the scene is as written: text rows, steady scene colors and no reverse video', sameJson(base.screen.lines, expectedLines(base.screen.rows, spec.stages, 0))
        && base.screen.colorBinding.reverseVideo === false));
    stages.forEach((observed, index) => {
        const overrides = writes[index];
        const effective = effectiveColors(baseline, overrides);
        const target = observed.kind === 'baseline' || observed.kind === 'override' ? controls : assertions;
        const label = `${spec.id}/${observed.id}`;
        let stepCells = cells;
        for (const earlier of spec.stages.slice(0, index + 1)) stepCells = [...stepCells, ...earlier.cells];
        target.push(check(`${label}: the exact query replies are the model's colors`, queriesMatch(observed.queries, effective)),
            check(`${label}: the App's resolved colors and override metadata match`, bindingMatches(observed.screen.colorBinding, base.screen.colorBinding, effective, overrides)),
            check(`${label}: the render theme sources and override flags match`, themeMatches(observed.theme, base.theme, effective, overrides)),
            check(`${label}: default markers, palette-indexed and literal cells resolve to the model`, cellsMatch(observed.screen, stepCells, effective)),
            check(`${label}: stored text, cursor position, shape and visibility are as written`, sameJson(observed.screen.lines, expectedLines(observed.screen.rows, spec.stages, index))
                && cursorMatches(observed.screen, { presentation: observed.presentation }, wanted)
                && (wanted.style === null || observed.presentation.cursorStyle === wanted.style)));
        if (overrides.palette.size === 0 && overrides.foreground === null && overrides.background === null && overrides.cursor === null) {
            target.push(check(`${label}: the complete theme, derived values included, equals the baseline`, snapshotHash(observed.theme) === snapshotHash(base.theme)));
        }
    });
    // The override must be a distinct, visible change before a reset can be judged.
    stages.forEach((observed, index) => {
        if (observed.kind !== 'override') return;
        const effective = effectiveColors(baseline, writes[index]);
        const changed = [effective.foreground !== baseline.foreground, effective.background !== baseline.background, effective.cursor !== baseline.cursor,
            ...effective.palette.map((color, slot) => color !== baseline.palette[slot])];
        controls.push(check(`${spec.id}/${observed.id}: the override differs from the measured baseline`, writes[index] && changed.some(Boolean)));
        controls.push(check(`${spec.id}/${observed.id}: every overridden target differs from its baseline`, targetsChanged(writes[index], effective, baseline)));
        if (spec.pixels) controls.push(check(`${spec.id}/${observed.id}: the controlled render differs from the baseline render`, observed.pixelSha256 !== base.pixelSha256));
    });
    const last = stages.length - 1;
    if (spec.pixels && writes[last].palette.size === 0 && writes[last].foreground === null && writes[last].background === null && writes[last].cursor === null) {
        assertions.push(check(`${spec.id}: the final controlled render is byte-identical to the baseline render`, stages[last].pixelSha256 === base.pixelSha256));
    }
    return { id: spec.id, role: spec.role, label: spec.label, controls, assertions, outcome: outcomeOf(controls, assertions) };
}

function targetsChanged(overrides, effective, baseline) {
    return [...overrides.palette.keys()].every(slot => effective.palette[slot] !== baseline.palette[slot])
        && (overrides.foreground === null || effective.foreground !== baseline.foreground)
        && (overrides.background === null || effective.background !== baseline.background)
        && (overrides.cursor === null || effective.cursor !== baseline.cursor);
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

function gradeCase(caseId, experiments, observation) {
    const results = experiments.map(spec => gradeExperiment(spec, observation[spec.id]));
    const unsupported = results.filter(item => item.outcome === 'unsupported'), inconclusive = results.filter(item => item.outcome === 'inconclusive');
    const failed = items => items.map(item => `${item.id}: ${[...item.controls, ...item.assertions].filter(entry => !entry.passed).map(entry => entry.description).join('; ')}`).join(' | ');
    if (unsupported.length) return { outcome: 'unsupported', note: failed(unsupported), experiments: results };
    if (inconclusive.length) return { outcome: 'inconclusive', note: failed(inconclusive), experiments: results };
    return { outcome: 'supported', experiments: results };
}

/** The compact observed record of a case: each stage's replies, override metadata and the target colors. */
function briefCase(experiments, observation) {
    return Object.fromEntries(experiments.map(spec => [spec.id, { role: spec.role, label: spec.label, term: spec.term === bel ? 'BEL' : 'ST',
        stages: observation[spec.id].map(stage => ({ stage: stage.id,
            replies: stage.queries.map(query => ({ query: query.spec, reply: query.reply?.raw?.replaceAll(esc, String.raw`\e`) ?? null })),
            binding: { foreground: hexOf(stage.screen.colorBinding.foregroundArgb), background: hexOf(stage.screen.colorBinding.backgroundArgb),
                foregroundOverride: stage.screen.colorBinding.foregroundOverride, backgroundOverride: stage.screen.colorBinding.backgroundOverride,
                paletteOverrideIndices: stage.screen.colorBinding.paletteOverrideIndices },
            theme: { cursor: hexOf(stage.theme.cursorArgb), cursorText: hexOf(stage.theme.cursorTextArgb), cursorOverride: stage.theme.cursorOverride },
            cursor: { row: stage.screen.cursorRow, col: stage.screen.cursorCol, visible: stage.screen.cursorVisible, style: stage.presentation.cursorStyle },
            pixelSha256: stage.pixelSha256 })) }]));
}

/** Independent contract grader entry. */
export function gradeColorResetCase(item, observation, revision = colorResetContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown runtime-color reset contracts revision ${revision}.`);
    if (!observation || item.experiments.some(spec => !Array.isArray(observation[spec.id]) || observation[spec.id].length !== spec.stages.length)) {
        throw new Error(`Missing ${item.caseId} read captures.`);
    }
    return item.grade(observation);
}

/** The fixed fixture facts other modules and tests read. */
export const colorResetFixture = Object.freeze({ pinnedColor, isolated, slotRgb, allSlots, literalFg, literalBg, sceneText, steadyBlock, steadyBeamHidden, sceneBytes, sceneCells, bel, st });
/** The independent model, exposed so tests can cross-check it. */
export const colorResetModel = Object.freeze({ overridesByStageOf, effectiveColors, queriedColor, expectedLines, gradeExperiment });

/** Every read of the suite in order: which case, experiment and stage it is, and the queries asked before it. */
export const colorResetReadPlan = Object.freeze(colorResetContracts.flatMap(item => item.experiments.flatMap(spec => spec.stages.map(step => Object.freeze({
    caseId: item.caseId, experimentId: spec.id, stageId: step.id, queries: Object.freeze(spec.queries) })))));

/** A runtime-color screen carries attributes, stored details (with overline), the cursor mode and resolved colors, and every resolved channel
 * must cohere with the stored default markers, palette indices and literal bytes of the same snapshot. */
export function validateColorResetScreen(screen) {
    if (!screen?.cursorMode || !screen.colorBinding || !screen.resolvedColors || !screen.cellDetails || !screen.attributeRuns) throw new Error('Incomplete runtime-color screen.');
    checkStoredColorCoherence(screen, true);
}
