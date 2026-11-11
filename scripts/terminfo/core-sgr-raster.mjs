// Core SGR reset raster experiments (`sgr.selective-reset.bold`, `sgr.reset`, `reset.sgr`): the retained App criteria measure the
// appearance of one two-cell specimen (YY), not stored flags. Under controlled offscreen renders (the production draw path, a fixed
// font/palette/presentation phase and a hidden cursor) the experiment saves the neutral pen with DECSC, draws YY with each of the
// seven nonempty attribute subsets, restores with DECRC and redraws, then sets every attribute, sends the made-up SGR 99 and the
// reset under test, redrawing each time. Crops are exact decoded pixel rectangles derived from the draw's own grid metadata.
//
// Decision tree (the retained catalogue text, applied literally):
//   structural controls (coherent stored state, stable control region, ink in the neutral crop, repeatable all-set crop) fail
//       -> inconclusive, no verdict;
//   a subset, restoration or expected-crop separation does not qualify -> the exact DECRQSS SGR report of the state under test decides
//       (query-based conclusion; silent or invalid replies keep the named refusal);
//   qualified raster controls: SGR 99 changed the crop -> invalid; final crop equals the expected crop -> supported; equals neutral or
//       another identified subset crop -> unsupported; matches neither -> invalid.
// Expected behavior is specified here (attribute sets and the reset semantics of SGR 0 and 22), never taken from the production
// encoder or resolver. A stored-attribute reading of the very cells drawn is retained beside every crop as an independent fact.
import { screenSnapshot, snapshotHash, captureRequest, observerEndpoint, OwnedCellObserver } from './agent-observer.mjs';
import { validateGradedJournal } from './graded-plan.mjs';
import { expandCellDetails } from './cell-details.mjs';
import { pristineCursorBaseline } from './cursor.mjs';
import { parseChecksumReply } from './checksum-report.mjs';
import { validatePixelCapture, stablePixelContext, defaultCursorPolicy, totalPixelLimit, pngHash, retainedCapture } from './pixels-capture.mjs';
import { regionPixels } from './pixels.mjs';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

export const sgrRasterSuiteId = 'ntilde-core-sgr-raster-v1';
export const sgrRasterNonce = 'NTILDE_SR_';
export const sgrRasterDirectory = 'sr-pixels';
export const sgrRasterIds = Object.freeze(['sgr.selective-reset.bold', 'sgr.reset', 'reset.sgr']);
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const check = (condition, message) => { if (!condition) throw new Error('Core SGR raster: ' + message); };
const barrierPattern = /^\x1b\[\?[\d;]+c$/;

// ---- Specification: geometry, bytes and the attribute vocabulary ------------------------------------------------------------------
/** Zero-based physical position of the specimen's first cell and of the unchanged control text. */
export const specimen = Object.freeze({ row: 4, col: 12 });
export const controlText = Object.freeze({ row: 9, col: 12, text: 'CONTROL' });
/** The crop: the two specimen cells plus one blank cell on every side, so slanted or emboldened ink that leaves its cell is still compared. */
export const specimenRegion = Object.freeze({ row: specimen.row - 1, col: specimen.col - 1, rows: 3, cols: 4 });
export const controlRegion = Object.freeze({ row: controlText.row, col: controlText.col, rows: 1, cols: controlText.text.length });
const at = (row, col) => `\x1b[${row + 1};${col + 1}H`;
const specimenAt = at(specimen.row, specimen.col);
/** DECSTR, margins/modes/pen/screen cleared, home, cursor hidden: the same preparation the owned pixel suite uses. */
const prepare = '\x1b[!p\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[2J\x1b[H\x1b[?25l';
export const sgrRasterBind = '\x1b[?1049h' + prepare + sgrRasterNonce;
export const sgrRasterFinish = '\x1b[?1049l\x1b[!p';
export const decrqssRequest = '\x1bP$qm\x1b\\';
const decsc = '\x1b7', decrc = '\x1b8', draw = 'YY', madeUpSgr = '\x1b[99m';
/** The attributes this suite varies: SGR set code, the stored cell flag bit (TerminalCellFlags) and the DECRQSS code. */
const attributes = Object.freeze({
    bold: { set: 1, bit: 0x0001 }, faint: { set: 2, bit: 0x0040 }, italic: { set: 3, bit: 0x0004 }, inverse: { set: 7, bit: 0x0002 },
});
/** Stored flag bits of every attribute this suite never sets (underline, strikethrough, blink, hidden); each must stay clear. */
const otherAttributeBits = 0x0008 | 0x0010 | 0x0020 | 0x0080;

export const sgrRasterExperiments = Object.freeze([
    { id: 'sgr.selective-reset.bold', attributes: ['bold', 'faint', 'italic'], reset: '\x1b[22m', resetName: 'SGR 22', remaining: ['italic'], expected: 'italic-only' },
    { id: 'sgr.reset', attributes: ['bold', 'italic', 'inverse'], reset: '\x1b[0m', resetName: 'SGR 0', remaining: [], expected: 'neutral' },
    { id: 'reset.sgr', attributes: ['bold', 'italic', 'inverse'], reset: '\x1b[0m', resetName: 'SGR 0', remaining: [], expected: 'neutral' },
].map(item => Object.freeze(item)));

/** Every nonempty subset, in a fixed order (bit mask 1..7 over the experiment's attribute order). */
export function subsetsOf(experiment) {
    const list = [];
    for (let mask = 1; mask < 1 << experiment.attributes.length; mask++) list.push(experiment.attributes.filter((_, i) => mask & (1 << i)));
    return list;
}
const nameOf = subset => subset.join('+');
const sgrOn = subset => `\x1b[${subset.map(name => attributes[name].set).join(';')}m`;

/** The ordered plan of one experiment. `attrs` is the stored attribute set the drawn cells must show (null: the stage is not a control of it). */
export function experimentSteps(experiment) {
    const { id } = experiment, steps = [];
    // `attrs` is what the drawn cells must store; `storedControl` says whether a mismatch invalidates the observation path (a restoration or
    // reset stage's stored result is evidence of the operation under test, never a precondition).
    const stage = (role, feed, attrs, query, cursor, storedControl = true) => steps.push({ role: `${id}/${role}`, feed, attrs, query, cursor, storedControl });
    stage('neutral', prepare + at(controlText.row, controlText.col) + controlText.text + specimenAt + draw + specimenAt + decsc, [], true, [specimen.row, specimen.col]);
    for (const subset of subsetsOf(experiment)) {
        stage(`subset/${nameOf(subset)}`, specimenAt + sgrOn(subset) + draw, subset, false, [specimen.row, specimen.col + 2]);
        // The restoration under test: DECRC alone brings back the pen and the position; YY is then drawn where it left the cursor.
        stage(`restore/${nameOf(subset)}`, decrc + draw, [], false, [specimen.row, specimen.col + 2], false);
    }
    stage('all', specimenAt + sgrOn(experiment.attributes) + draw, experiment.attributes, true, [specimen.row, specimen.col + 2]);
    stage('sgr99', madeUpSgr + specimenAt + draw, experiment.attributes, true, [specimen.row, specimen.col + 2]);
    // The reset under test: the stored result is a supporting fact, never a control (an unsupported terminal leaves other attributes).
    stage('final', experiment.reset + specimenAt + draw, null, true, [specimen.row, specimen.col + 2], false);
    return steps;
}
export const sgrRasterSteps = Object.freeze(sgrRasterExperiments.flatMap(experimentSteps));
export const sgrRasterReadCount = sgrRasterSteps.length;
export const sgrRasterFrameCount = sgrRasterReadCount + 3;

// ---- DECRQSS: the exact request, the raw reply and its interpretation ------------------------------------------------------------
const knownCodes = new Set([1, 2, 3, 4, 5, 7, 8, 9, 53]);
/** Classify one raw DECRQSS reply (or its explicit absence) distinctly: silence, a status-0 refusal, another setting's reply, malformed bytes
 * and a valid SGR report. A valid report lists attribute codes; a leading 0 is the reset the report starts with. */
export function parseDecrqssSgr(reply) {
    if (reply === null) return { kind: 'absent' };
    let raw;
    try { raw = parseChecksumReply(reply?.raw).raw; } catch { return { kind: 'malformed', raw: String(reply?.raw ?? '').slice(0, 64) }; }
    if (raw === '\x1bP0$r\x1b\\') return { kind: 'invalid-request', raw };
    const match = raw.startsWith('\x1bP1$r') ? /^([\d;:]*)([a-zA-Z ]{1,2})$/.exec(raw.slice(5, -2)) : null;
    if (!match) return { kind: 'malformed', raw };
    if (match[2] !== 'm') return { kind: 'wrong-setting', raw };
    const params = match[1].split(';');
    if (params.some(p => !/^\d{1,3}$/.test(p))) return { kind: 'unrecognized-parameter', raw };
    const codes = params.map(Number), body = codes[0] === 0 ? codes.slice(1) : codes;
    if (body.some(code => !knownCodes.has(code)) || new Set(body).size !== body.length) return { kind: 'unrecognized-parameter', raw };
    return { kind: 'sgr', raw, codes: [...body].sort((a, b) => a - b) };
}
const codesOf = names => names.map(name => attributes[name].set).sort((a, b) => a - b);

// ---- Stored cell attributes (an independent supporting fact beside the pixels) ---------------------------------------------------
/** The stored attribute set of the two specimen cells, or null when the two cells disagree or carry an attribute this suite never sets. */
export function storedAttributes(screen) {
    const cells = expandCellDetails(screen.cellDetails, screen.rows * screen.cols, true);
    const pair = [0, 1].map(i => cells[specimen.row * screen.cols + specimen.col + i]);
    if (pair[0].flags !== pair[1].flags || pair[0].overline || pair[1].overline || (pair[0].flags & otherAttributeBits) || pair[0].underlineStyle) return null;
    return Object.keys(attributes).filter(name => pair[0].flags & attributes[name].bit);
}
function expectedLines(rows) {
    const lines = new Array(rows).fill('');
    lines[specimen.row] = ' '.repeat(specimen.col) + draw;
    lines[controlText.row] = ' '.repeat(controlText.col) + controlText.text;
    return lines;
}
/** A frame is coherent with its step when the stored text, cursor and cursor visibility are the plan's and the specimen cells store only attributes this suite sets. */
function coherent(frame, step) {
    const screen = frame.screen;
    if (!same(screen.lines, expectedLines(screen.rows)) || !same([screen.cursorRow, screen.cursorCol], step.cursor) || screen.cursorVisible !== false) return false;
    return storedAttributes(screen) !== null;
}

// ---- Grading ---------------------------------------------------------------------------------------------------------------------
const nonBlank = bytes => {
    let ink = 0;
    for (let offset = 4; offset < bytes.length; offset += 4) if (bytes.compare(bytes, 0, 4, offset, offset + 4) !== 0) ink++;
    return ink > 10;
};
const sameBytes = (a, b) => a.equals(b);

const structuralControls = Object.freeze(['stored-state-coherent-with-input', 'unchanged-control-region-stable', 'neutral-crop-has-ink', 'all-set-crop-repeats-its-subset-crop']);

/** The structural controls (the observation path itself) and the qualifying controls (the raster controls the criterion names). */
function evaluateControls(experiment, { crops, control, coherentStage, stored }) {
    const subsets = subsetsOf(experiment), role = name => `${experiment.id}/${name}`, steps = experimentSteps(experiment);
    const neutral = crops[role('neutral')], subsetCrop = subset => crops[role(`subset/${nameOf(subset)}`)];
    const controls = {};
    const record = (name, passed, detail) => { controls[name] = detail === undefined ? { passed } : { passed, detail }; };
    record('stored-state-coherent-with-input', steps.every(step => coherentStage[step.role] === true));
    const controlCrops = steps.map(step => control[step.role]);
    record('unchanged-control-region-stable', controlCrops.every(bytes => sameBytes(bytes, controlCrops[0])) && nonBlank(controlCrops[0]));
    record('neutral-crop-has-ink', nonBlank(neutral));
    record('all-set-crop-repeats-its-subset-crop', sameBytes(crops[role('all')], subsetCrop(experiment.attributes)));
    // The stored attributes of the cells the subsets and the all-set stages drew must be the input's.
    const wantedStored = step => Object.keys(attributes).filter(name => step.attrs.includes(name));
    const storedOk = steps.filter(step => step.storedControl).map(step => ({ stage: step.role, matches: same(stored[step.role], wantedStored(step)) }));
    record('stored-attributes-match-the-input-at-every-control-stage', storedOk.every(item => item.matches), storedOk);
    const changed = subsets.map(subset => !sameBytes(subsetCrop(subset), neutral));
    record('every-subset-changes-the-neutral-crop', changed.every(Boolean), subsets.map((subset, i) => ({ subset: nameOf(subset), changed: changed[i] })));
    // A restoration qualifies only when the pixels return exactly and the restored cells store no attribute either.
    const restored = subsets.map(subset => sameBytes(crops[role(`restore/${nameOf(subset)}`)], neutral) && same(stored[role(`restore/${nameOf(subset)}`)], []));
    record('every-DECRC-restore-reproduces-the-neutral-crop', restored.every(Boolean), subsets.map((subset, i) => ({ subset: nameOf(subset), exact: restored[i] })));
    if (experiment.expected === 'italic-only') {
        const only = subsetCrop(['italic']);
        const separation = subsets.filter(subset => nameOf(subset) !== 'italic').map(subset => ({ subset: nameOf(subset), distinct: !sameBytes(only, subsetCrop(subset)) }));
        record('italic-only-crop-differs-from-neutral-and-every-other-subset', !sameBytes(only, neutral) && separation.every(item => item.distinct), separation);
    }
    return { controls, qualifying: Object.keys(controls).filter(name => !structuralControls.includes(name)) };
}

/** Grade one experiment from its crops. `crops[role]` and `control[role]` are decoded RGBA rectangles, `coherentStage[role]` booleans,
 * `stored[role]` the stored attribute sets and `queries[role]` the parsed DECRQSS replies of the stages that asked. */
export function gradeExperiment(experiment, data) {
    const { crops, stored, queries } = data, role = name => `${experiment.id}/${name}`;
    const neutral = crops[role('neutral')], all = crops[role('all')];
    const subsetCrop = subset => crops[role(`subset/${nameOf(subset)}`)];
    const { controls, qualifying } = evaluateControls(experiment, data);
    const sgr99Unchanged = sameBytes(crops[role('sgr99')], all);
    const raster = rasterClassification(experiment, { neutral, final: crops[role('final')], subsetCrop, subsets: subsetsOf(experiment), sgr99Unchanged });
    const fallback = fallbackDecision(experiment, queries);
    const supporting = { storedFinal: stored[role('final')] ?? null, storedFinalMatchesExpectedReset: same(stored[role('final')], experiment.remaining),
        decrqssFinal: queries[role('final')] ?? null };
    const base = { id: experiment.id, resetUnderTest: experiment.resetName, rasterControls: { ...controls, 'sgr99-leaves-the-crop-unchanged': { passed: sgr99Unchanged } },
        raster, fallback, supporting };
    if (!structuralControls.every(name => controls[name].passed)) return { ...base, outcome: 'inconclusive', route: 'none', reason: 'observation-controls-invalid', decidedBy: 'structural-control-failure' };
    if (qualifying.every(name => controls[name].passed)) {
        if (!sgr99Unchanged) return { ...base, outcome: 'inconclusive', route: 'raster', reason: 'probe-invalid-sgr99-changed-crop', decidedBy: 'raster' };
        return { ...base, outcome: raster.outcome, ...(raster.reason ? { reason: raster.reason } : {}), route: 'raster', decidedBy: 'raster' };
    }
    const failed = qualifying.filter(name => !controls[name].passed);
    return { ...base, outcome: fallback.outcome, ...(fallback.reason ? { reason: fallback.reason } : {}), route: 'decrqss-fallback', decidedBy: 'query',
        fallbackReason: 'raster controls did not qualify: ' + failed.join(', '), claim: 'query-based conclusion; the raster experiment did not qualify and is not claimed' };
}

/** The raster classification of the final crop (used only when every raster control qualified). */
function rasterClassification(experiment, { neutral, final, subsetCrop, subsets, sgr99Unchanged }) {
    const expected = experiment.expected === 'neutral' ? neutral : subsetCrop(['italic']);
    if (sameBytes(final, expected)) return { outcome: 'supported', matched: experiment.expected, sgr99Unchanged };
    if (sameBytes(final, neutral)) return { outcome: 'unsupported', matched: 'neutral', sgr99Unchanged };
    const other = subsets.find(subset => sameBytes(final, subsetCrop(subset)));
    if (other) return { outcome: 'unsupported', matched: `subset:${nameOf(other)}`, sgr99Unchanged };
    return { outcome: 'inconclusive', reason: 'probe-invalid-final-crop-matches-no-admitted-crop', matched: null, sgr99Unchanged };
}

const refusalFor = { absent: 'decrqss-silent', 'invalid-request': 'decrqss-refused', malformed: 'decrqss-malformed-reply', 'wrong-setting': 'decrqss-wrong-setting-reply',
    'unrecognized-parameter': 'decrqss-unrecognized-parameter' };
/** The DECRQSS route: the reply of the state under test, qualified by the queries of the neutral, all-set and SGR 99 states. */
export function fallbackDecision(experiment, queries) {
    const role = name => `${experiment.id}/${name}`, finalReply = queries[role('final')];
    const controls = {
        'neutral-pen-reports-no-attributes': same(queries[role('neutral')]?.codes, []),
        'all-set-pen-reports-every-attribute': same(queries[role('all')]?.codes, codesOf(experiment.attributes)),
        'sgr99-leaves-the-reported-pen-unchanged': same(queries[role('sgr99')]?.codes, codesOf(experiment.attributes)),
    };
    if (finalReply?.kind !== 'sgr') return { outcome: 'inconclusive', reason: refusalFor[finalReply?.kind] ?? 'decrqss-missing', reply: finalReply ?? null, controls };
    if (!Object.values(controls).every(Boolean)) return { outcome: 'inconclusive', reason: 'decrqss-controls-failed', reply: finalReply, controls };
    const wanted = codesOf(experiment.remaining);
    return { outcome: same(finalReply.codes, wanted) ? 'supported' : 'unsupported', expectedCodes: wanted, reportedCodes: finalReply.codes, reply: finalReply, controls };
}

// ---- Journal validation and the report ---------------------------------------------------------------------------------------------
const screenOf = value => screenSnapshot(value, false, true, true, true);
function frameKind(index) {
    if (index === 0) return 'sr-start';
    if (index === 1) return 'sr-bind';
    return index === sgrRasterFrameCount - 1 ? 'sr-finish' : 'sr-read';
}
/** The geometry/render facts every frame must share, from the actual draw. */
function renderFacts(frame) {
    const { grid, font, theme } = frame.pixel.renderDetails;
    return { rows: frame.screen.rows, cols: frame.screen.cols, widthPx: frame.pixel.width, heightPx: frame.pixel.height, grid: structuredClone(grid),
        font: structuredClone(font), foregroundArgb: theme.foregroundArgb, backgroundArgb: theme.backgroundArgb, presentationTimeMs: frame.pixel.presentationTimeMs };
}
/** One retained exchange: the exact request bytes, the raw reply (or explicit absence) and the DA barrier that ordered it. */
function validateQuery(query) {
    check(query?.request === decrqssRequest && barrierPattern.test(query.barrier ?? '') && query.barrier.length <= 128 && Object.hasOwn(query, 'reply')
        && Object.keys(query).length === 3, 'invalid retained DECRQSS exchange');
    if (query.reply !== null) check(Object.keys(query.reply).length === 1 && same(parseChecksumReply(query.reply.raw), query.reply), 'invalid retained DECRQSS reply');
    return query;
}

/** One frame's binding to its position, its stored/after states and its retained render context; returns the decoded image. */
function validateFrame(frame, index, shared, output) {
    check(frame.kind === frameKind(index) && frame.index === index && frame.sha256 === snapshotHash(screenOf(frame.screen))
        && frame.afterSha256 === frame.sha256 && snapshotHash(screenOf(frame.afterScreen)) === frame.sha256, 'invalid frame binding');
    const { pixel } = frame;
    check(pixel.file === `${sgrRasterDirectory}/frame-${String(index).padStart(3, '0')}.png` && pixel.presentationTimeMs === 0
        && pixel.mode === 'render' && pixel.scale === 1 && pixel.downscaled === false && pixel.inlineOmitted === false, 'render parameters changed');
    check(stablePixelContext(pixel.renderDetails) === shared.context && same(renderFacts(frame), shared.facts), 'render context changed');
    check(!frame.screen.cursorMode.isDefault || defaultCursorPolicy(frame.screen, pixel.renderDetails) === shared.cursorPolicy, 'default cursor policy changed');
    return validatePixelCapture(retainedCapture(output, frame, sgrRasterDirectory), frame.screen, 0).image;
}

/** Journal-level validation: kinds, nonce binding, restoration, the pixel budget and every frame's retained PNG. */
function validateJournal(frames, runId, output) {
    validateGradedJournal(frames, { frameCount: sgrRasterFrameCount, prefix: 'sr', noncePrefix: sgrRasterNonce, runId, label: 'Core SGR raster', screenOf });
    check(frames.reduce((sum, frame) => sum + frame.pixel.width * frame.pixel.height, 0) <= totalPixelLimit, 'decoded pixel budget exhausted');
    const first = frames[0];
    check(pristineCursorBaseline(first.screen) && first.pixel.sha256 === frames.at(-1).pixel.sha256, 'restoration was not verified');
    const shared = { context: stablePixelContext(first.pixel.renderDetails), facts: renderFacts(first), cursorPolicy: defaultCursorPolicy(first.screen, first.pixel.renderDetails) };
    return { facts: shared.facts, images: frames.map((frame, index) => validateFrame(frame, index, shared, output)) };
}

/** One planned read: its crops, coherence, stored attributes and (when it asked) the parsed DECRQSS reply. */
function readStage(planned, frame, image, exchange, into) {
    const grid = frame.pixel.renderDetails.grid, role = planned.role;
    into.crops[role] = regionPixels(image, grid, specimenRegion);
    into.control[role] = regionPixels(image, grid, controlRegion);
    into.coherentStage[role] = coherent(frame, planned);
    into.stored[role] = storedAttributes(frame.screen);
    into.retained[role] = { frame: frame.index, png: frame.pixel.sha256, cropSha256: pngHash(into.crops[role]), controlSha256: pngHash(into.control[role]), stored: into.stored[role] };
    if (!planned.query) return;
    into.queries[role] = parseDecrqssSgr(exchange.reply);
    into.retained[role].decrqss = { ...exchange, parsed: into.queries[role] };
}

/** Replay the worker's ordered log against the plan, the journal and the images: every read must follow the barrier of its own write or exchange. */
function replaySteps(run, frames, images) {
    check(same(run.steps?.map(step => step.kind), planKinds()), 'recorded step order changed');
    check(same(run.steps.filter(step => step.kind === 'feed').map(step => step.bytes), sgrRasterSteps.map(step => step.feed)), 'fixture bytes changed');
    const into = { crops: {}, control: {}, coherentStage: {}, stored: {}, queries: {}, retained: {} }, reads = frames.slice(2, -1);
    let readIndex = 0, barrier = null, exchange = null;
    for (const step of run.steps) {
        if (step.kind === 'read') {
            const planned = sgrRasterSteps[readIndex], frame = reads[readIndex];
            check(step.role === planned.role && frame.role === planned.role && frame.barrier === barrier && same(frame.queries, planned.query ? [exchange] : [])
                && same(step.cursor, [frame.screen.cursorRow, frame.screen.cursorCol]), 'read order or exchange changed');
            readStage(planned, frame, images[readIndex + 2], exchange, into);
            readIndex++;
            continue;
        }
        check(barrierPattern.test(step.barrier ?? ''), 'missing write barrier');
        exchange = step.kind === 'query' ? validateQuery({ request: step.request, reply: step.reply, barrier: step.barrier }) : null;
        barrier = step.barrier;
    }
    check(readIndex === sgrRasterReadCount, 'incomplete reads');
    return into;
}

/** Revalidate the whole journal and every retained PNG, then grade each experiment independently of the worker's account. */
export function buildSgrRasterReport(frames, run, runId, output) {
    check(run?.suiteId === sgrRasterSuiteId && run.runId === runId, 'identity mismatch');
    const { facts, images } = validateJournal(frames, runId, output);
    const { retained, ...data } = replaySteps(run, frames, images);
    // The unchanged control text is the same pixels in every frame and experiment, and the neutral crop is the same pen on the same cells.
    const controls = Object.values(data.control), neutrals = sgrRasterExperiments.map(experiment => data.crops[`${experiment.id}/neutral`]);
    const stable = controls.every(bytes => bytes.equals(controls[0])) && neutrals.every(bytes => bytes.equals(neutrals[0]));
    const effects = {};
    for (const experiment of sgrRasterExperiments) {
        const graded = gradeExperiment(experiment, data);
        effects[experiment.id] = stable ? { ...graded, crossExperimentStable: true }
            : { ...graded, outcome: 'inconclusive', route: 'none', reason: 'observation-controls-invalid', decidedBy: 'structural-control-failure', crossExperimentStable: false };
    }
    return { schemaVersion: 1, suiteId: sgrRasterSuiteId, runId, geometry: facts, specimenRegion, controlRegion, decrqssRequest, effects, stages: retained,
        scope: 'Controlled offscreen renders of the production draw path at the recorded font, palette, geometry and presentation phase, with a hidden cursor; exact decoded-pixel comparison of the recorded crops. Not desktop appearance, other fonts or platforms, or animation cadence.',
        snapshots: frames };
}
/** The step kinds a complete run records, in order. */
function planKinds() {
    return sgrRasterSteps.flatMap(step => (step.query ? ['feed', 'query', 'read'] : ['feed', 'read']));
}

// ---- Collection ------------------------------------------------------------------------------------------------------------------
/** Execute every write, exchange and observation in order; restoration is attempted on every path. */
export async function collectSgrRaster({ config, fence, owner }) {
    const steps = [];
    const started = await owner('sr-start');
    check(started.screen.rows >= 12 && started.screen.cols >= 48 && pristineCursorBaseline(started.screen), 'requires a pristine owned baseline');
    let primary;
    try {
        await owner('sr-bind', await fence.write(sgrRasterBind + config.runId));
        for (const planned of sgrRasterSteps) {
            const barrier = await fence.write(planned.feed); // NOSONAR: fixture writes and reads must remain serial.
            steps.push({ kind: 'feed', bytes: planned.feed, barrier });
            let pending = [], last = barrier;
            if (planned.query) {
                const exchange = await fence.dcsReport(decrqssRequest); // NOSONAR: each reply orders after its fixture write.
                steps.push({ kind: 'query', role: planned.role, request: decrqssRequest, reply: exchange.reply, barrier: exchange.barrier });
                pending = [{ request: decrqssRequest, reply: exchange.reply, barrier: exchange.barrier }];
                last = exchange.barrier;
            }
            const observed = await owner('sr-read', last, { role: planned.role, queries: pending }); // NOSONAR: observation must finish before the next write.
            steps.push({ kind: 'read', role: planned.role, cursor: [observed.screen.cursorRow, observed.screen.cursorCol] });
        }
    } catch (error) { primary = error; }
    try {
        await owner('sr-finish', await fence.write(sgrRasterFinish));
    } catch (error) {
        if (primary) throw new AggregateError([primary, error], 'Core SGR raster collection and restoration failed.');
        throw error;
    }
    if (primary) throw primary;
    return { schemaVersion: 1, suiteId: sgrRasterSuiteId, runId: config.runId, steps };
}

/** The parent observer: each read journals one coherent state bracketing one controlled render of that very state. */
export class SgrRasterObserver {
    constructor({ output, captureRpc = captureRequest, ...options }) {
        this.output = output;
        this.captureRpc = captureRpc;
        const details = { ...options, prefix: 'sr', includeCellDetails: true, includeCursorMode: true, includeOverline: true };
        this.before = new OwnedCellObserver(details);
        this.after = new OwnedCellObserver(details);
        this.frames = [];
        this.next = 0;
    }
    get restored() { return this.before.restored && this.frames.at(-1)?.kind === 'sr-finish'; }
    get paneId() { return this.before.paneId; }
    /** The planned read this request must be (null for start, bind and finish). */
    planned(request) {
        const operation = request.kind.slice('sr-'.length);
        const expected = operation === 'read' ? sgrRasterSteps[this.next] : null;
        check(operation !== 'read' || (expected && request.role === expected.role), 'unexpected read');
        check(operation === 'read' || (request.role === undefined && request.queries === undefined), 'only reads carry roles and replies');
        check(operation !== 'finish' || this.next === sgrRasterReadCount, 'finish before the plan completed');
        return expected;
    }
    async render(result, budget) {
        this.after.paneId ??= this.paneId;
        const endpoint = (this.before.endpoint ?? observerEndpoint)(this.before.appData, this.before.ownerPid);
        const capture = await this.captureRpc(endpoint, this.paneId, 0, budget());
        const after = await this.after.capture(budget);
        check(snapshotHash(result.screen) === snapshotHash(after), 'capture changed the observed screen');
        const { bytes, details } = validatePixelCapture(capture, result.screen, 0);
        check(capture.width * capture.height * sgrRasterFrameCount <= totalPixelLimit, 'decoded pixel budget exhausted');
        const policy = defaultCursorPolicy(result.screen, details);
        check(!(this.defaultCursor && policy && policy !== this.defaultCursor), 'changed default cursor policy');
        this.defaultCursor ??= policy;
        const stable = stablePixelContext(details);
        check(!this.context || stable === this.context, 'changed render context');
        this.context ??= stable;
        return { capture, bytes, details, after };
    }
    async handleRequest(request, budget) {
        check(!this.busy && !this.restored, 'observation busy or complete');
        const expected = this.planned(request), queries = (request.queries ?? []).map(validateQuery);
        check(queries.length === (expected?.query ? 1 : 0), 'unexpected exchange count');
        this.busy = true;
        try {
            const result = await this.before.handle(request.kind, budget, request.barrier);
            const { capture, bytes, details, after } = await this.render(result, budget);
            const file = `${sgrRasterDirectory}/frame-${String(this.frames.length).padStart(3, '0')}.png`;
            const pixel = { file, sha256: pngHash(bytes), byteCount: bytes.length, width: capture.width, height: capture.height, rows: capture.rows, cols: capture.cols,
                mode: capture.mode, scale: capture.scale, downscaled: false, inlineOmitted: false, presentationTimeMs: 0, renderDetails: details };
            check(request.kind !== 'sr-finish' || pixel.sha256 === this.frames[0].pixel.sha256, 'baseline was not restored');
            if (!this.frames.length) mkdirSync(join(this.output, sgrRasterDirectory), { mode: 0o700 });
            writeFileSync(join(this.output, file), bytes, { flag: 'wx', mode: 0o600 });
            this.frames.push({ ...this.before.frames.at(-1), afterScreen: after, afterSha256: snapshotHash(after), role: expected?.role ?? null, queries, pixel });
            if (request.kind === 'sr-read') this.next++;
            return result;
        } finally { this.busy = false; }
    }
}
