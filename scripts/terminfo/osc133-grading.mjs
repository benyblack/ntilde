// Parent-side report builder for the OSC 133 qualification. Re-derives every conclusion from the retained parent journal (paired complete screen,
// pane-metadata and session-status observations), refuses any tampering, and requires the pinned probes' own results to agree with their replay over the
// journal. The pinned verdicts are inconclusive by design and are preserved as the conclusions; the separately identified effect contract carries the graded
// verdict, derived here from the independent oracle in osc133-contracts.mjs, never from the production implementation.
import { snapshotHash } from './agent-observer.mjs';
import { pristineEditing } from './editing-capture.mjs';
import { validateFrame, validateMetadataSnapshot } from './metadata.mjs';
import { colorBindingHash } from './resolved-colors.mjs';
import { planFeeds, planReads, requireReplayAgreement, validateExactFeeds } from './graded-plan.mjs';
import { osc133AdapterId } from './osc133-grading-collector.mjs';
import { consumptionResult, expectedOsc133Screen, makeOsc133Experiments, osc133CaseIds, osc133ContractsVersion, osc133EffectContractId, osc133Fixture, osc133Minimum,
    osc133NonceRow, osc133Pinned, osc133Prefix, osc133Roles, osc133SuiteId, osc133TermlessContext, replayOsc133Pinned, simulateOsc133 } from './osc133-contracts.mjs';

const label = 'OSC 133';
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const check = (value, message) => { if (!value) throw new Error(message); };
const derivedFrom = 'ntilde-owned-left-right-margins-grading-v1';
const scope = 'Pinned terminfo.dev@4.0.0 extensions.osc133-a, -b, -c and -d termless and terminal code (marker + X consumption only; always inconclusive by design) replayed '
    + 'over parent-journaled observations and preserved, beside a separately identified effect contract graded from paired complete stored cells, pane metadata '
    + '(title, cwd, input window, raw command and output anchors with their coordinate conventions) and session status (state, precision, current command) by an '
    + 'independent lifecycle oracle: each pinned fixture is executed from a measured prior state with an unmarked control and gating markers, beside canonical '
    + 'consecutive-cycle, payload, boundary and exit-payload observations. Protocol handling in a disposable direct-local App only: no Bash, Zsh, Fish, PowerShell, SSH or '
    + 'remote delivery claim, no OSC 133 P, OSC 633, semantic-prompts aggregate or command-assist claim, no event-stream (commandFinished) readback, no stock grade.';

/** The status the session reports at a prompt (it becomes idle after 60 silent seconds); both are the prompt state. */
const promptStatuses = new Set(['awaitingInput', 'idle']);

// ---------- observations ----------

/** One raw anchor under the documented coordinate convention: zero-based viewport row/col, absolute row = viewport row (no history), current generation, main screen. */
function anchor(value, buffer) {
    if (value === null) return null;
    const valid = buffer.scrollbackLines === 0 && buffer.rowsEvicted === 0 && value.alternateScreen === false
        && value.absoluteRow === value.row && value.generation === buffer.historyGeneration;
    return valid ? [value.row, value.col] : { invalid: [value.row, value.col, value.absoluteRow, value.generation, value.alternateScreen] };
}

/** The facts the oracle predicts, plus the context the preservation checks compare. */
export function observeFrame(frame) {
    const { screen, metadata, status } = frame.before, buffer = metadata.buffer;
    return { status: promptStatuses.has(status.status) ? 'prompt' : status.status, statusRaw: status.status, confidence: status.confidence,
        command: status.currentCommand, exitCode: status.exitCode, accepting: buffer.acceptingCommandInput,
        commandStart: anchor(buffer.commandStart, buffer), outputStart: anchor(buffer.outputStart, buffer),
        cursor: [screen.cursorRow, screen.cursorCol], title: metadata.oscTitle, displayTitle: metadata.displayTitle, cwd: metadata.workingDirectory,
        links: buffer.links.length, alternateScreen: buffer.alternateScreen, scrollbackLines: buffer.scrollbackLines,
        historyGeneration: buffer.historyGeneration, colorBinding: colorBindingHash(screen.colorBinding) };
}

const certainFields = ['status', 'command', 'accepting', 'commandStart', 'outputStart'];

/** Compare one read with the oracle: the whole screen, preserved title/cwd/colors, precise status, every certain field; uncertain fields are only recorded. */
export function evaluateRead(frame, state, bound, fixture) {
    const observed = observeFrame(frame), boundObserved = observeFrame(bound);
    const screenMatches = snapshotHash(frame.before.screen) === snapshotHash(expectedOsc133Screen(bound.before.screen, state));
    const preserved = observed.title === fixture.title && observed.displayTitle === fixture.title && observed.cwd === fixture.cwd && observed.links === 0
        && observed.alternateScreen === false && observed.scrollbackLines === 0 && observed.historyGeneration === boundObserved.historyGeneration
        && observed.colorBinding === boundObserved.colorBinding && observed.confidence === 'precise';
    const required = {}, policy = {};
    for (const field of certainFields) {
        const entry = { expected: state[field], observed: observed[field], match: same(state[field], observed[field]) };
        if (state.certain[field]) required[field] = entry; else policy[field] = entry;
    }
    const passed = screenMatches && preserved && Object.values(required).every(entry => entry.match);
    return { passed, screenMatches, preserved, required, policy, observed };
}

/** The target transition of each case: what its marker alone is documented to do, relative to the state before it. */
const targets = {
    'extensions.osc133-a': (prior, post) => prior.status === 'running' && post.status === 'prompt' && post.command === null,
    'extensions.osc133-b': (prior, post) => prior.accepting === false && post.accepting === true && post.commandStart !== null && !post.commandStart.invalid,
    'extensions.osc133-c': (prior, post) => prior.status === 'prompt' && post.status === 'running' && post.accepting === false,
    'extensions.osc133-d': (prior, post) => prior.status === 'running' && post.status === 'prompt' && post.accepting === false && post.commandStart === null && post.outputStart === null,
};

// ---------- journal ----------

/** The baseline the stage bytes establish; at the end the screen must equal the initial one and the prepared title/cwd must be back. */
export function matchesOsc133Baseline(snapshot, first, runId, finish) {
    const fixture = osc133Fixture(runId), frame = { before: snapshot }, observed = observeFrame(frame);
    const screen = structuredClone(first.screen);
    if (!finish) { screen.lines[osc133NonceRow] = fixture.nonce; screen.cursorRow = osc133NonceRow; screen.cursorCol = fixture.nonce.length; }
    const initialIndices = first.screen.colorBinding.paletteOverrideIndices;
    const indices = snapshot.screen.colorBinding.paletteOverrideIndices;
    const colors = finish ? colorBindingHash(snapshot.screen.colorBinding) === colorBindingHash(first.screen.colorBinding)
        : same(indices, [...initialIndices, 7].sort((a, b) => a - b));
    return snapshotHash(snapshot.screen.lines) === snapshotHash(screen.lines) && snapshot.screen.cursorRow === screen.cursorRow
        && snapshot.screen.cursorCol === screen.cursorCol && colors && observed.status === 'prompt' && observed.confidence === 'precise' && observed.command === null
        && observed.accepting === false && observed.commandStart === null && observed.outputStart === null && observed.title === fixture.title
        && observed.cwd === fixture.cwd && observed.links === 0 && observed.alternateScreen === false && observed.scrollbackLines === 0
        && (!finish || snapshotHash(snapshot.screen) === snapshotHash(first.screen));
}

/** The plan the observer accepts: one entry per read. */
export function osc133ObserverPlan(snapshot, runId) {
    check(snapshot.screen.rows >= osc133Minimum.rows && snapshot.screen.cols >= osc133Minimum.cols, 'OSC 133 fixture needs a larger grid.');
    return makeOsc133Experiments(runId).flatMap(item => item.plan.filter(step => step.read).map(() => ({})));
}

function validateJournal(frames, experiments, runId) {
    const reads = experiments.reduce((sum, item) => sum + planReads(item), 0), count = 3 + reads;
    check(/^[a-f\d]{32}$/.test(runId ?? '') && Array.isArray(frames) && frames.length === count && count <= 64, `Incomplete ${label} snapshot sequence.`);
    const first = validateMetadataSnapshot(frames[0]?.before, undefined, true);
    check(pristineEditing(first.screen) && first.screen.rows >= osc133Minimum.rows && first.screen.cols >= osc133Minimum.cols, `${label} fixture needs a pristine private main screen.`);
    for (const [index, frame] of frames.entries()) validateFrame(frame, index, count - 1, first, { caseId: null, stageId: null }, osc133Prefix, true);
    check(matchesOsc133Baseline(frames[1].before, first, runId, false) && matchesOsc133Baseline(frames.at(-1).before, first, runId, true),
        `${label} baseline binding or restoration failed.`);
    for (const frame of frames) check(frame.before.screen.colorBinding !== undefined, `${label} snapshots need their resolved color binding.`);
    return first;
}

/** Every read frame carries the barrier and cursor of the feed it follows. */
function crossCheck(contract, recorded, readFrames) {
    let feedSteps = 0, readSteps = 0;
    for (const step of contract.plan) {
        if (step.feed !== undefined) { feedSteps++; continue; }
        const frame = readFrames[readSteps];
        check(frame.barrier === recorded.barriers[feedSteps - 1], `${label} ${contract.caseId} barrier differs from its read frame.`);
        check(same(recorded.reads[readSteps].cursor, [frame.before.screen.cursorRow, frame.before.screen.cursorCol]), `${label} ${contract.caseId} cursor differs from its read frame.`);
        check(recorded.reads[readSteps].kind === step.read, `Wrong ${contract.caseId} read kind (${label}).`);
        readSteps++;
    }
}

/** Validate a retained worker run record's shape against the contracts. */
function validateRun(run, experiments, adapter) {
    check(adapter && adapter.id === osc133AdapterId && run.adapterId === osc133AdapterId && /^[a-f\d]{64}$/.test(adapter.bundleSha256 ?? '')
        && /^[a-f\d]{64}$/.test(adapter.upstreamBundleSha256 ?? '') && adapter.derivedFromAdapterId === derivedFrom, `Invalid ${label} adapter identity.`);
    check(same(run.probeIds, [...osc133CaseIds]) && Array.isArray(run.experiments) && run.experiments.length === experiments.length
        && Array.isArray(run.pinned) && run.pinned.length === osc133CaseIds.length, `${label} run does not cover the contracted cases.`);
    for (const [position, contract] of experiments.entries()) {
        const recorded = run.experiments[position];
        check(recorded.caseId === contract.caseId, `${label} experiment order differs from the contracts.`);
        validateExactFeeds(contract, recorded.feeds, 'contract');
        check(Array.isArray(recorded.barriers) && recorded.barriers.length === planFeeds(contract).length
            && recorded.barriers.every(barrier => /^\x1b\[\?[\d;]+c$/.test(barrier ?? '')) && Array.isArray(recorded.reads) && recorded.reads.length === planReads(contract),
        `Invalid ${contract.caseId} capture record (${label}).`);
    }
}

// ---------- grading ----------

/** The outcome of one case's effect contract from its evaluated experiments. */
function gradeCase(caseId, evaluated) {
    const target = targets[caseId], byName = name => evaluated.find(item => item.name === name);
    const effect = byName('effect'), notes = [];
    const priorShown = (item, prior) => target(prior, item.observed);
    const [prior, ordinary, pinned] = effect.reads;
    // Validity: the measured prior state is what the oracle says, the unmarked bytes change nothing, and no gating marker produces the target.
    const validity = [
        { id: 'prior-state', held: prior.evaluation.passed },
        { id: 'unmarked-control', held: ordinary.evaluation.passed && !priorShown(ordinary.evaluation, prior.evaluation.observed) },
        ...evaluated.filter(item => item.role === 'gate').map(item => ({ id: item.name, held: item.reads[0].evaluation.passed && !target(item.priorObserved, item.reads[0].evaluation.observed) })),
    ];
    const targetShown = target(prior.evaluation.observed, pinned.evaluation.observed);
    const required = [{ id: 'pinned-fixture-effect', held: pinned.evaluation.passed && targetShown },
        ...evaluated.filter(item => item.role === 'required').map(item => ({ id: item.name, held: item.reads.every(value => value.evaluation.passed) }))];
    const stock = byName('stock').reads[0];
    notes.push(`Pinned fixture on the prepared baseline: ${stock.evaluation.passed ? 'consumed with every certain field as predicted' : 'did not match the oracle'}.`);
    const invalid = validity.filter(item => !item.held), failed = required.filter(item => !item.held);
    let outcome = 'supported';
    if (invalid.length || !stock.evaluation.passed) outcome = 'inconclusive';
    else if (failed.length) outcome = 'unsupported';
    return { outcome, validity, required, targetShown, ...(invalid.length ? { invalid: invalid.map(item => item.id) } : {}), ...(failed.length ? { failed: failed.map(item => item.id) } : {}), notes };
}

const lastRead = (item, name) => item.reads.find(entry => entry.label === name);
const anchorsOf = value => ({ accepting: value.accepting, commandStart: value.commandStart, outputStart: value.outputStart });
const rowOf = (frames, read, anchor) => (Array.isArray(anchor) ? frames[read.snapshot].before.screen.lines[anchor[0]] ?? null : null);

/** What an ST-terminated copy of the pinned marker did, relative to the oracle's state before it. */
function terminatorVariant(caseId, evaluated) {
    const st = evaluated.find(item => item.name === 'st-terminator');
    return { stTerminator: { targetShown: targets[caseId](st.priorObserved, st.reads[0].evaluation.observed), certainFieldsMatch: st.reads[0].evaluation.passed } };
}

function promptStartFacts(evaluated, seen, prior, pinned) {
    const disclosure = evaluated.find(item => item.name === 'disclose-d');
    return { transition: { from: prior.status, to: pinned.status }, commandText: { before: prior.command, after: pinned.command }, anchorsAfterAWithoutD: anchorsOf(pinned),
        sharedObservableWithD: targets['extensions.osc133-a'](disclosure.priorObserved, disclosure.reads[0].evaluation.observed),
        gatingMarkers: { b: seen('gate-b', 'post').status, c: seen('gate-c', 'post').status } };
}

function inputStartFacts(evaluated, seen, frames, pinned) {
    const input = lastRead(evaluated.find(item => item.name === 'boundary'), 'input'), boundary = input.evaluation.observed.commandStart;
    const line = rowOf(frames, input, boundary);
    return { pinnedFixture: { accepting: pinned.accepting, commandStart: pinned.commandStart },
        boundary: { anchor: boundary, promptText: line?.slice(0, boundary[1]) ?? null, inputText: line?.slice(boundary[1]) ?? null, commandExposed: input.evaluation.observed.command },
        secondCycleAnchor: seen('cycles', 'second').commandStart };
}

function executionStartFacts(evaluated, seen, frames, pinned) {
    const accepted = seen('payload', 'accepted'), output = lastRead(evaluated.find(item => item.name === 'payload'), 'output');
    const second = seen('cycles', 'second');
    return { commandIdentity: { typedText: 'echo c-typed', payload: 'echo c-payload', exposed: accepted.command },
        outputAnchor: { atAccept: accepted.outputStart, afterOutput: output.evaluation.observed.outputStart, firstOutputRow: rowOf(frames, output, output.evaluation.observed.outputStart) },
        bareCPinnedFixture: { command: pinned.command, outputStartPolicy: pinned.outputStart },
        payloadShapes: { attribute: seen('payload-attribute', 'accepted').command, plainText: seen('payload-plain', 'accepted').command },
        secondCycle: { command: second.command, outputStart: second.outputStart } };
}

function completionFacts(seen, prior, pinned) {
    return { transition: { from: prior.status, to: pinned.status }, anchorsAfter: anchorsOf(pinned),
        exitPayload: { sent: 7, sessionStatusExitCode: seen('exit-payload', 'post').exitCode, interpreted: false,
            note: 'The snapshot and metadata expose no command exit status; the exit code of the child process is a separate fact and the commandFinished event stream is not read here.' } };
}

/** The facts a reader wants beside each verdict, derived from the evaluated reads only (never graded). */
function observedFacts(caseId, evaluated, frames) {
    const seen = (name, label) => lastRead(evaluated.find(item => item.name === name), label).evaluation.observed;
    const prior = seen('effect', 'prior'), pinned = seen('effect', 'pinned');
    const facts = {
        'extensions.osc133-a': () => promptStartFacts(evaluated, seen, prior, pinned),
        'extensions.osc133-b': () => inputStartFacts(evaluated, seen, frames, pinned),
        'extensions.osc133-c': () => executionStartFacts(evaluated, seen, frames, pinned),
        'extensions.osc133-d': () => completionFacts(seen, prior, pinned),
    }[caseId]();
    return { ...facts, variants: terminatorVariant(caseId, evaluated) };
}

/** Replay the contracted experiments over the journal: the oracle's prediction and the observation of every read. */
function evaluateExperiments(frames, run, experiments, fixture) {
    const bound = frames[1], evaluatedByCase = new Map(osc133CaseIds.map(caseId => [caseId, []]));
    let frameIndex = 2;
    for (const [position, contract] of experiments.entries()) {
        const recorded = run.experiments[position], readFrames = recorded.reads.map(() => frames[frameIndex++]);
        crossCheck(contract, recorded, readFrames);
        const feeds = [], reads = [];
        for (const step of contract.plan) {
            if (step.feed !== undefined) { feeds.push(step.feed); continue; }
            const frame = readFrames[reads.length];
            reads.push({ label: step.read, snapshot: frame.index, cursor: recorded.reads[reads.length].cursor, evaluation: evaluateRead(frame, simulateOsc133(feeds, fixture), bound, fixture) });
        }
        // The state immediately before the last marker an experiment emits, when no read of its own precedes it (gates, disclosures and variants): the oracle's.
        let priorObserved = null;
        if (['gate', 'disclosure', 'observed'].includes(contract.role)) {
            const state = simulateOsc133(feeds.slice(0, feeds.findLastIndex(feed => feed.includes('\x1b]133;'))), fixture);
            priorObserved = { status: state.status, command: state.command, accepting: state.accepting, commandStart: state.commandStart, outputStart: state.outputStart };
        }
        evaluatedByCase.get(contract.owner).push({ id: contract.caseId, name: contract.name, role: contract.role, feeds, barriers: recorded.barriers, reads, priorObserved });
    }
    return evaluatedByCase;
}

/** The pinned results the worker recorded must equal the replay over the stock-context read, and must be inconclusive by design. */
function checkPinned(run, caseId, stockFrame) {
    const recorded = run.pinned.find(item => item.caseId === caseId);
    check(recorded && same(recorded.termless.feeds, osc133Pinned[caseId].termlessFeeds) && same(recorded.terminal.writes, osc133Pinned[caseId].terminalWrites), `Missing pinned ${caseId} grading result.`);
    const replay = replayOsc133Pinned(caseId, stockFrame.before.screen);
    for (const path of ['termless', 'terminal']) {
        const live = recorded[path].result;
        check(live.pass === replay[path].pass && live.note === replay[path].note && same(live.observation, replay[path].observation), `Pinned ${caseId} ${path} grading does not match its observed evidence.`);
    }
    check(recorded.termless.result.observation.outcome === 'inconclusive' && recorded.terminal.result.observation.outcome === 'inconclusive', `Pinned ${caseId} must be inconclusive by design.`);
    return recorded;
}

/** One case of the report: the preserved pinned verdict, the separately identified effect verdict and the observed facts. */
function buildCase(caseId, evaluated, frames, recorded, contractsVersion) {
    const roles = osc133Roles[caseId], grade = gradeCase(caseId, evaluated);
    return { caseId, role: roles.role, marker: roles.marker, pinnedFixture: { termlessFeeds: osc133Pinned[caseId].termlessFeeds, terminalWrites: osc133Pinned[caseId].terminalWrites },
        observed: observedFacts(caseId, evaluated, frames), experiments: evaluated,
        pinned: { pass: recorded.termless.result.pass, note: recorded.termless.result.note, observation: recorded.termless.result.observation,
            termless: recorded.termless.result, terminal: recorded.terminal.result },
        contractResult: { outcome: grade.outcome },
        effectContract: { id: osc133EffectContractId, version: contractsVersion, outcome: grade.outcome, ...grade, attribution: 'separately identified Ntilde effect contract; not the pinned probe' },
        ...(grade.outcome === 'inconclusive' ? {} : { divergence: { pinnedOutcome: 'inconclusive', contractOutcome: grade.outcome, kind: 'pinned-inconclusive-by-design' } }),
        conclusion: { outcome: recorded.termless.result.observation.outcome, evidence: 'owned-pane-metadata-and-session-status', grading: 'pinned-consumption-preserved-by-design',
            note: consumptionResult(true).note, scope } };
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export function buildOsc133GradingReport(frames, run, runId, adapter, { contractsVersion = osc133ContractsVersion, replayModule = null } = {}) {
    const experiments = makeOsc133Experiments(runId), fixture = osc133Fixture(runId);
    validateJournal(frames, experiments, runId);
    validateRun(run, experiments, adapter);
    const evaluatedByCase = evaluateExperiments(frames, run, experiments, fixture);
    const cases = osc133CaseIds.map(caseId => {
        const evaluated = evaluatedByCase.get(caseId), stockFrame = frames[evaluated.find(item => item.name === 'stock').reads[0].snapshot];
        const recorded = checkPinned(run, caseId, stockFrame);
        // The retained bundle's own termless probe must reproduce the live result when the module is available.
        if (replayModule) requireReplayAgreement({ caseId }, replayModule.ntildeRunOsc133Termless(caseId, osc133TermlessContext(caseId, stockFrame.before.screen)), recorded.termless.result);
        return buildCase(caseId, evaluated, frames, recorded, contractsVersion);
    });
    return { schemaVersion: 1, suiteId: osc133SuiteId, runId, contractsVersion, adapter, scope, initialSnapshot: 0, boundSnapshot: 1, restoredSnapshot: frames.length - 1,
        cases, snapshots: frames };
}

/** Count conclusions by pinned/effect outcome pair. */
export function osc133Counts(report) {
    const counts = {};
    for (const item of report.cases) {
        const key = `${item.conclusion.outcome}/${item.effectContract.outcome}`;
        counts[key] = (counts[key] ?? 0) + 1;
    }
    return counts;
}
