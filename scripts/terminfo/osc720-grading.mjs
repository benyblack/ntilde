// Parent-side report builder for the rxvt OSC 720 qualification. It re-derives every conclusion from the retained paired journal, the worker's run log and the parent's retained wheel actions,
// refuses tampering, and replays both pinned paths. The pinned TERMINAL path (the one real terminal applications are graded by) can only ever be inconclusive; the pinned TERMLESS function is
// executed unchanged over a parent-built scrollback context mapped from the App's own observation. Neither is an effect verdict: the separately identified effect contract carries that, and any
// difference is recorded as a divergence, never absorbed.
import { snapshotHash } from './agent-observer.mjs';
import { pristineEditing } from './editing-capture.mjs';
import { validateMonotonicFrame, validateMetadataSnapshot } from './metadata.mjs';
import { planFeeds, planReads, validateExactFeeds } from './graded-plan.mjs';
import { exposeOsc720Facts } from './osc720-collector.mjs';
import { osc720AdapterId } from './osc720-grading-collector.mjs';
import { osc720CaseId, osc720CaseIds, osc720ContractsVersion, osc720EffectContractId, osc720Experiments, osc720Fixture, osc720Minimum, osc720NonceRow, osc720Pinned,
    osc720ActionDeltas, osc720Prefix, osc720ScrollbackState, osc720SuiteId } from './osc720-contracts.mjs';

const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const check = (value, message) => { if (!value) throw new Error(message); };
const derivedFrom = 'ntilde-owned-left-right-margins-grading-v1';
const isDigest = value => /^[a-f\d]{64}$/.test(value ?? '');

/** The outcome the pinned parserStateResult helper assigns: null is inconclusive, true supported, false unsupported. */
function stateOutcome(pass) {
    if (pass === null) return 'inconclusive';
    return pass ? 'supported' : 'unsupported';
}

/** Mirrors the pinned parserStateResult helper exactly (key order included); the real bundle replay at collection time proves the equality. */
function parserStateResult(pass, expected, state, note) {
    const response = JSON.stringify(state);
    return { pass: pass === true, response, ...(note && { note }),
        observation: { outcome: stateOutcome(pass), ...(pass === null && { reason: 'insufficient-evidence' }), evidence: 'parser-state', ...(note && { note }) },
        ...(pass !== null && { assertions: [{ kind: pass ? 'positive' : 'negative', expected, observed: response }] }) };
}
const criterion = 'OSC 720 moves the scrollback viewport up';

/** The pinned termless function over the same two states the live replay was served. */
export function replayOsc720TermlessStatic(rows, beforeFacts, afterFacts) {
    const before = osc720ScrollbackState(rows, beforeFacts);
    if (before.totalLines <= before.screenLines || before.viewportOffset <= 0) return parserStateResult(null, criterion, { before }, 'No scrollback viewport available');
    const after = osc720ScrollbackState(rows, afterFacts);
    return parserStateResult(after.viewportOffset < before.viewportOffset, criterion, { before, after });
}

/** The pinned terminal path: a constant unverifiedEffect whatever the cursor answer was. */
export function replayOsc720TerminalStatic() {
    const note = osc720Pinned.terminal.note;
    return { pass: false, note, observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'consumed', note } };
}

export function osc720ObserverPlan(snapshot, runId) {
    check(snapshot.screen.rows >= osc720Minimum.rows && snapshot.screen.cols >= osc720Minimum.cols, 'OSC 720 fixture needs a larger grid.');
    return osc720Experiments(runId).flatMap(item => item.plan.filter(step => step.read).map(() => ({})));
}

/** The prepared baseline: main screen, no history, the live view, the run nonce at the bind and exactly the first screen at the finish. */
export function matchesOsc720Baseline(snapshot, first, runId, finish) {
    const fixture = osc720Fixture(runId), screen = structuredClone(first.screen), metadata = snapshot.metadata, buffer = metadata.buffer;
    if (!finish) { screen.lines[osc720NonceRow] = fixture.nonce; screen.cursorRow = osc720NonceRow; screen.cursorCol = fixture.nonce.length; }
    return snapshotHash(snapshot.screen.lines) === snapshotHash(screen.lines) && snapshot.screen.cursorRow === screen.cursorRow && snapshot.screen.cursorCol === screen.cursorCol
        && buffer.scrollbackLines === 0 && !buffer.alternateScreen && buffer.links.length === 0 && same(metadata.viewport, { scrollOffset: 0, maxScrollOffset: 0 })
        && (!finish || snapshotHash(snapshot.screen) === snapshotHash(first.screen));
}

/** The state an experiment must not change: titles, links, shell anchors, progress, toast and the opt-ins. History, viewport, cursor and screen are the measured quantities. */
function unrelated(snapshot) {
    const { metadata } = snapshot, p = metadata.presentation, b = metadata.buffer;
    return { rows: b.rows, cols: b.cols, alternateScreen: b.alternateScreen, acceptingCommandInput: b.acceptingCommandInput, commandStart: b.commandStart, outputStart: b.outputStart,
        links: b.links, oscTitle: metadata.oscTitle, workingDirectory: metadata.workingDirectory, displayTitle: metadata.displayTitle,
        progress: [p.progressOwner, p.progressKind, p.progressPercent, p.progressBar], toast: p.toastVisible, notifications: [p.notificationsEnabled, p.progressEnabled] };
}

/** The view movement of a unchanged-screen, unchanged-history experiment: a positive delta moved away from the live line. */
function viewDirection(delta) {
    if (delta === 0) return 'none';
    return delta > 0 ? 'view-up' : 'view-down';
}

/** What changed between two observations of one experiment, in the quantities this qualification measures. */
export function classifyOsc720(before, after) {
    const screenSame = before.screenSha256 === after.screenSha256 && same(before.cursor, after.cursor);
    const historySame = before.scrollbackLines === after.scrollbackLines && before.historyGeneration === after.historyGeneration && before.rowsEvicted === after.rowsEvicted
        && before.historySha256 === after.historySha256;
    const delta = after.scrollOffset - before.scrollOffset;
    let kind = 'other';
    if (screenSame && historySame) kind = viewDirection(delta);
    else if (screenSame && before.scrollbackLines > 0 && after.scrollbackLines === 0 && after.historyGeneration > before.historyGeneration && after.scrollOffset === 0) kind = 'erase-saved';
    return { kind, delta, screenSame, historySame };
}

/** The complete stored screen except the quantities this qualification measures separately (retained history and its count): cells, attributes, cursor and its mode, margins, modes and colors. */
export function screenProjectionSha256(screen) {
    const projection = structuredClone(screen);
    delete projection.history;
    projection.bufferState.scrollbackLines = null;
    return snapshotHash(projection);
}

function readRecord(label, kind, snapshot, frameIndex) {
    const facts = exposeOsc720Facts(snapshot);
    return { label, kind, snapshot: frameIndex, scrollOffset: facts.viewport.scrollOffset, maxScrollOffset: facts.viewport.maxScrollOffset, scrollbackLines: facts.scrollbackLines,
        historyGeneration: facts.historyGeneration, rowsEvicted: facts.rowsEvicted, cursor: facts.cursor, screenSha256: screenProjectionSha256(snapshot.screen), historySha256: snapshotHash(snapshot.screen.history),
        pinnedTextVisible: snapshot.screen.lines.some(line => line.startsWith('scroll-29')), rows: facts.rows };
}

/** The offset the reference implementation reaches: scr_page(UP, n) moves the view up n rows and clamps at the oldest retained row. */
const expectedOffset = (before, count) => Math.min(before.scrollOffset + count, before.maxScrollOffset);

function expectationHeld(expected, before, after, classified) {
    if (expected.kind === 'none') return classified.kind === 'none';
    if (expected.kind === 'erase-saved') return classified.kind === 'erase-saved';
    if (expected.kind === 'view-up-any') return classified.kind === 'view-up';
    return classified.kind === 'view-up' && after.scrollOffset === expectedOffset(before, expected.count);
}

/** The fixed history every experiment starts from: pinned lines at the bottom of the live view, the viewport at the live line (or scrolled), consistent view bounds. */
function setupHeld(contract, before, reference) {
    const visible = before.pinnedTextVisible;
    const bounds = before.maxScrollOffset === before.scrollbackLines && before.scrollbackLines > 0 && (reference === null || before.scrollbackLines === reference.scrollbackLines);
    const position = contract.setup.scrolled ? before.scrollOffset > 0 && before.scrollOffset < before.maxScrollOffset : before.scrollOffset === 0;
    return visible && bounds && position;
}

function checkActions(contract, recorded) {
    const names = contract.plan.filter(step => step.action !== undefined).map(step => step.action), actions = recorded.actions ?? [];
    check(actions.length === names.length && actions.every((item, index) => item.action === names[index] && item.response?.action === names[index] && Array.isArray(item.response.notches)
        && item.response.notches.length > 0 && item.response.notches.every(notch => notch.eventDispatched === true && notch.postVerified === true && notch.request?.kind === 'mouseWheel'
            && notch.request.wheelDelta === osc720ActionDeltas[item.action])), 'OSC 720 action record changed.');
    return actions;
}

/** The journaled reads of one experiment: each must carry the barrier of its preceding feed, the worker's own cursor and facts, and leave unrelated state untouched. */
function readExperiment(contract, recorded, frames, firstFrame) {
    let feeds = 0, read = 0, frameIndex = firstFrame;
    const records = [], preserved = [];
    for (const step of contract.plan) {
        if (step.feed !== undefined) feeds++;
        if (step.read === undefined) continue;
        const frame = frames[frameIndex++], entry = recorded.reads[read++], snapshot = frame.before;
        check(entry.kind === step.read && frame.barrier === recorded.barriers[feeds - 1] && same(entry.cursor, [snapshot.screen.cursorRow, snapshot.screen.cursorCol])
            && same(entry.facts, exposeOsc720Facts(snapshot)), 'OSC 720 read identity changed.');
        preserved.push(same(unrelated(snapshot), unrelated(frames[1].before)));
        records.push(readRecord(step.read, step.kind, snapshot, frame.index));
    }
    return { records, preserved };
}

/** Evaluate every contracted experiment in journal order. */
function evaluateExperiments(frames, run, runId, contracts) {
    const evaluations = [];
    let frameIndex = 2, reference = null;
    for (const [index, contract] of contracts.entries()) {
        const recorded = run.experiments[index];
        check(recorded?.caseId === contract.caseId, 'OSC 720 experiment identity changed.');
        validateExactFeeds(contract, recorded.feeds, 'OSC 720 contract');
        check(Array.isArray(recorded.barriers) && recorded.barriers.length === planFeeds(contract).length
            && recorded.barriers.every(value => /^\x1b\[\?[\d;]+c$/.test(value ?? '')) && recorded.reads?.length === planReads(contract), 'Invalid OSC 720 capture.');
        const actions = checkActions(contract, recorded);
        const { records, preserved } = readExperiment(contract, recorded, frames, frameIndex);
        frameIndex += records.length;
        const [before, after] = records, classified = classifyOsc720(before, after);
        if (index === 0) reference = before;
        const held = setupHeld(contract, before, reference) && preserved.every(Boolean);
        evaluations.push({ caseId: contract.caseId, owner: contract.owner, name: contract.name, role: contract.role, probe: contract.probe, expected: contract.expected,
            held, preserved: preserved.every(Boolean), observations: records, observed: classified, passed: held && expectationHeld(contract.expected, before, after, classified),
            ...(actions.length ? { actions } : {}) });
    }
    return evaluations;
}

/** One effect verdict. A control that fails or a setup that did not hold is inconclusive; a uniform no-effect result is unsupported; any mixture is inconclusive, never a guess. */
function effectVerdict(experiments) {
    const protocol = experiments.filter(item => item.role === 'protocol'), controls = experiments.filter(item => item.role === 'control');
    const controlsHeld = experiments.every(item => item.held) && controls.every(item => item.passed);
    let outcome = 'inconclusive', reason = null;
    if (!controlsHeld) reason = 'validity-control-failed';
    else if (protocol.every(item => item.passed)) outcome = 'supported';
    else if (protocol.every(item => item.observed.kind === 'none')) outcome = 'unsupported';
    else reason = 'mixed-or-other-effect';
    return { id: osc720EffectContractId, version: osc720ContractsVersion, outcome, controlsHeld, reason, attribution: 'separately identified Ntilde effect contract; not the pinned probe',
        protocolExperiments: protocol.map(item => ({ name: item.name, passed: item.passed, observed: item.observed.kind })),
        controlExperiments: controls.map(item => ({ name: item.name, passed: item.passed, observed: item.observed.kind })) };
}

/** Verify the retained pinned results equal their static replay over the pinned-bare experiment's own observations. */
function verifyPinned(run, evaluations) {
    const pinnedBare = evaluations.find(item => item.name === 'pinned-bare'), [before, after] = pinnedBare.observations;
    const facts = record => ({ viewport: { scrollOffset: record.scrollOffset, maxScrollOffset: record.maxScrollOffset }, scrollbackLines: record.scrollbackLines });
    const termless = replayOsc720TermlessStatic(before.rows, facts(before), facts(after)), terminal = replayOsc720TerminalStatic();
    check(run.pinned?.caseId === osc720CaseId && same(run.pinned.termless, termless) && same(run.pinned.terminal, terminal), 'Pinned OSC 720 result differs from its replay.');
    return { termless, terminal };
}

/** Re-derive all reads, refusing changed identities, incomplete frames, feeds, ordering and restoration. */
export function buildOsc720Report(frames, run, runId, adapter) {
    const contracts = osc720Experiments(runId), count = 3 + contracts.reduce((n, item) => n + planReads(item), 0);
    check(Array.isArray(frames) && frames.length === count && count <= 64, 'Incomplete OSC 720 journal.');
    const first = validateMetadataSnapshot(frames[0]?.before, undefined, true, true);
    check(pristineEditing(first.screen) && first.screen.rows >= osc720Minimum.rows && first.screen.cols >= osc720Minimum.cols && first.metadata.viewport !== undefined, 'Invalid OSC 720 initial screen.');
    frames.forEach((frame, index) => validateMonotonicFrame(frame, index, count - 1, first, null, osc720Prefix, true));
    check(matchesOsc720Baseline(frames[1].before, first, runId, false) && matchesOsc720Baseline(frames.at(-1).before, first, runId, true), 'OSC 720 baseline or restoration failed.');
    check(adapter?.id === osc720AdapterId && adapter.derivedFromAdapterId === derivedFrom && isDigest(adapter.bundleSha256) && isDigest(adapter.upstreamBundleSha256)
        && run?.adapterId === adapter.id && same(run.probeIds, osc720CaseIds) && Array.isArray(run.experiments) && run.experiments.length === contracts.length, 'Invalid OSC 720 run identity.');
    const experiments = evaluateExperiments(frames, run, runId, contracts), pinned = verifyPinned(run, experiments), effectContract = effectVerdict(experiments);
    const pinnedBare = experiments.find(item => item.name === 'pinned-bare');
    const cases = [{ caseId: osc720CaseId, effectContract, experiments, pinned: { caseId: osc720CaseId, termless: pinned.termless, terminal: pinned.terminal, termlessInputs: pinnedBare.observations.map(item => item.snapshot),
        qualifiedOutcome: pinned.terminal.observation.outcome },
        divergences: [{ path: 'terminal', outcome: pinned.terminal.observation.outcome }, { path: 'termless-replay', outcome: pinned.termless.observation.outcome },
            ...(effectContract.outcome !== pinned.terminal.observation.outcome ? [{ path: 'effect', pinnedOutcome: pinned.terminal.observation.outcome, effectOutcome: effectContract.outcome }] : [])] }];
    return { schemaVersion: 1, suiteId: osc720SuiteId, contractsVersion: osc720ContractsVersion, runId, adapter, cases, snapshots: frames };
}

export function osc720Counts(report) {
    const counts = {};
    for (const item of report.cases) {
        const key = `${item.pinned.qualifiedOutcome}/${item.effectContract.outcome}`;
        counts[key] = (counts[key] ?? 0) + 1;
    }
    return counts;
}
