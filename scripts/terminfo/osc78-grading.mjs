// Parent-side report builder for the OSC 7 / OSC 8 qualification. Re-derives every conclusion from the retained parent journal (paired complete screen,
// pane-metadata and session-status observations), refuses any tampering, and requires the pinned probes' own results to agree with their replay over the
// journal. extensions.osc7-cwd is inconclusive by design (it carries no fixture and reads no state): its verdict is preserved and a separately identified
// effect contract carries the graded metadata verdict. extensions.osc8's pinned termless probe decides over the stored cells and link runs, so its verdict is
// the pinned conclusion, preserved beside the pinned terminal path (inconclusive by design) and the separately identified effect contract. Every effect
// verdict is derived from the independent oracle in osc78-contracts.mjs, never from the production implementation.
import { snapshotHash } from './agent-observer.mjs';
import { pristineEditing } from './editing-capture.mjs';
import { validateFrame, validateMetadataSnapshot } from './metadata.mjs';
import { colorBindingHash } from './resolved-colors.mjs';
import { planFeeds, planReads, requireReplayAgreement, validateExactFeeds } from './graded-plan.mjs';
import { osc78AdapterId } from './osc78-grading-collector.mjs';
import { expectedOsc78Screen, makeOsc78Experiments, osc78CaseIds, osc78ContractsVersion, osc78Fixture, osc78Minimum, osc78NonceRow, osc78Pinned, osc78Prefix,
    osc78SuiteId, osc78TermlessContext, osc7CaseId, osc7EffectContractId, osc7Forms, osc8CaseId, osc8EffectContractId, osc8Forms, replayOsc78Pinned, simulateOsc78 } from './osc78-contracts.mjs';

const label = 'OSC 7/8';
const canonical = value => JSON.stringify(value, (_, inner) => (inner && typeof inner === 'object' && !Array.isArray(inner)
    ? Object.fromEntries(Object.entries(inner).sort(([a], [b]) => (a < b ? -1 : 1))) : inner));
const same = (a, b) => canonical(a) === canonical(b);
const check = (value, message) => { if (!value) throw new Error(message); };
const derivedFrom = 'ntilde-owned-left-right-margins-grading-v1';
const scope = 'Pinned terminfo.dev@4.0.0 extensions.osc7-cwd (no fixture bytes: a declared-capability report and a no-readback result, both inconclusive by design) and '
    + 'extensions.osc8 (termless: stored cells and hyperlink metadata of the pinned fixture; terminal: cursor position, inconclusive by design) replayed over '
    + 'parent-journaled observations and preserved, beside separately identified effect contracts graded from paired complete stored cells, pane metadata '
    + '(title, host working directory, hyperlink runs with snapshot-local identities) and session status by an independent oracle: OSC 7 reported working-directory '
    + 'metadata only, never the child process working directory or the existence of the named path; OSC 8 stored hyperlink-bearing cells only, never click handling, '
    + 'URL opening, URI safety policy or underline rendering. Protocol handling in a disposable direct-local App only: no shell, remote delivery, OSC title, '
    + 'OSC 133 P, OSC 633, semantic-prompts aggregate or stock grade claim.';

/** The status the session reports at a prompt (it becomes idle after 60 silent seconds); both are the prompt state. */
const promptStatuses = new Set(['awaitingInput', 'idle']);

// ---------- observations ----------

/** The facts the oracle predicts, plus the context the preservation checks compare. */
export function observeFrame(frame) {
    const { screen, metadata, status } = frame.before, buffer = metadata.buffer;
    return { status: promptStatuses.has(status.status) ? 'prompt' : status.status, confidence: status.confidence, command: status.currentCommand,
        accepting: buffer.acceptingCommandInput, commandStart: buffer.commandStart, outputStart: buffer.outputStart,
        cursor: [screen.cursorRow, screen.cursorCol], title: metadata.oscTitle, displayTitle: metadata.displayTitle, cwd: metadata.workingDirectory,
        links: buffer.links.map(({ row, col, length, identity, uri, id }) => ({ row, col, length, identity, uri, id })),
        alternateScreen: buffer.alternateScreen, scrollbackLines: buffer.scrollbackLines, historyGeneration: buffer.historyGeneration,
        colorBinding: colorBindingHash(screen.colorBinding) };
}

/** Identities are snapshot-local: two lists describe the same cells when their runs match exactly and the same-link relation (the partition of the runs
 * by identity) is the same. The numbers themselves are never compared across snapshots nor to the OSC id. */
function sameRuns(expected, observed) {
    if (expected.length !== observed.length) return false;
    const geometry = list => list.map(({ row, col, length, uri, id }) => ({ row, col, length, uri, id }));
    const pattern = list => list.map(run => list.findIndex(other => other.identity === run.identity));
    return same(geometry(expected), geometry(observed)) && same(pattern(expected), pattern(observed));
}

const factsOfState = state => ({ cwd: state.cwd, title: state.title, links: state.runs });

/** Compare one read with the oracle: the whole screen, title, host cwd and every hyperlink run; unrelated state must be the prepared baseline's. */
export function evaluateRead(frame, state, bound) {
    const observed = observeFrame(frame), boundObserved = observeFrame(bound);
    const screenMatches = snapshotHash(frame.before.screen) === snapshotHash(expectedOsc78Screen(bound.before.screen, state));
    const preserved = observed.displayTitle === state.title && observed.status === 'prompt' && observed.confidence === 'precise' && observed.command === null
        && observed.accepting === false && observed.commandStart === null && observed.outputStart === null && observed.alternateScreen === false
        && observed.scrollbackLines === 0 && observed.historyGeneration === boundObserved.historyGeneration && observed.colorBinding === boundObserved.colorBinding;
    const required = { title: { expected: state.title, observed: observed.title, match: observed.title === state.title },
        cwd: { expected: state.cwd, observed: observed.cwd, match: observed.cwd === state.cwd },
        links: { expected: state.runs, observed: observed.links, match: sameRuns(state.runs, observed.links) } };
    const passed = screenMatches && preserved && Object.values(required).every(entry => entry.match);
    return { passed, screenMatches, preserved, required, observed };
}

/** The target transition of each case: what its bytes alone are documented to do, relative to the state before them. */
const targets = {
    [osc7CaseId]: (prior, post) => post.cwd !== prior.cwd,
    [osc8CaseId]: (prior, post) => prior.links.length === 0 && post.links.length > 0,
};

// ---------- journal ----------

/** The baseline the stage bytes establish; at the end the screen must equal the initial one and the prepared title/cwd must be back. */
export function matchesOsc78Baseline(snapshot, first, runId, finish) {
    const fixture = osc78Fixture(runId), observed = observeFrame({ before: snapshot });
    const screen = structuredClone(first.screen);
    if (!finish) { screen.lines[osc78NonceRow] = fixture.nonce; screen.cursorRow = osc78NonceRow; screen.cursorCol = fixture.nonce.length; }
    const initialIndices = first.screen.colorBinding.paletteOverrideIndices;
    const indices = snapshot.screen.colorBinding.paletteOverrideIndices;
    const colors = finish ? colorBindingHash(snapshot.screen.colorBinding) === colorBindingHash(first.screen.colorBinding)
        : same(indices, [...initialIndices, 7].sort((a, b) => a - b));
    return snapshotHash(snapshot.screen.lines) === snapshotHash(screen.lines) && snapshot.screen.cursorRow === screen.cursorRow
        && snapshot.screen.cursorCol === screen.cursorCol && colors && observed.status === 'prompt' && observed.confidence === 'precise' && observed.command === null
        && observed.accepting === false && observed.commandStart === null && observed.outputStart === null && observed.title === fixture.title
        && observed.displayTitle === fixture.title && observed.cwd === fixture.cwd && observed.links.length === 0 && observed.alternateScreen === false
        && observed.scrollbackLines === 0 && (!finish || snapshotHash(snapshot.screen) === snapshotHash(first.screen));
}

/** The plan the observer accepts: one entry per read. */
export function osc78ObserverPlan(snapshot, runId) {
    check(snapshot.screen.rows >= osc78Minimum.rows && snapshot.screen.cols >= osc78Minimum.cols, `${label} fixture needs a larger grid.`);
    return makeOsc78Experiments(runId).flatMap(item => item.plan.filter(step => step.read).map(() => ({})));
}

function validateJournal(frames, experiments, runId) {
    const count = 3 + experiments.reduce((sum, item) => sum + planReads(item), 0);
    check(/^[a-f\d]{32}$/.test(runId ?? '') && Array.isArray(frames) && frames.length === count && count <= 64, `Incomplete ${label} snapshot sequence.`);
    const first = validateMetadataSnapshot(frames[0]?.before, undefined, true);
    check(pristineEditing(first.screen) && first.screen.rows >= osc78Minimum.rows && first.screen.cols >= osc78Minimum.cols, `${label} fixture needs a pristine private main screen.`);
    for (const [index, frame] of frames.entries()) validateFrame(frame, index, count - 1, first, { caseId: null, stageId: null }, osc78Prefix, true);
    const bindings = [[frames[1].before, false], [frames.at(-1).before, true]];
    check(bindings.every(([snapshot, finish]) => matchesOsc78Baseline(snapshot, first, runId, finish)), `${label} baseline binding or restoration failed.`);
    check(frames.every(frame => frame.before.screen.colorBinding !== undefined), `${label} snapshots need their resolved color binding.`);
    return first;
}

/** Every read frame carries the barrier, cursor and kind of the plan step it follows. */
function crossCheck(contract, recorded, readFrames) {
    const expected = [];
    let fed = 0;
    for (const step of contract.plan) {
        if (step.feed !== undefined) fed++;
        else expected.push({ kind: step.read, barrier: recorded.barriers[fed - 1] });
    }
    for (const [position, want] of expected.entries()) {
        const frame = readFrames[position], record = recorded.reads[position];
        check(frame.barrier === want.barrier, `${label} ${contract.caseId} barrier differs from its read frame.`);
        check(same(record.cursor, [frame.before.screen.cursorRow, frame.before.screen.cursorCol]), `${label} ${contract.caseId} cursor differs from its read frame.`);
        check(record.kind === want.kind, `Wrong ${contract.caseId} read kind (${label}).`);
    }
}

/** Validate a retained worker run record's shape against the contracts. */
function validateRun(run, experiments, adapter) {
    const digest = value => /^[a-f\d]{64}$/.test(value ?? '');
    const identity = adapter?.id === osc78AdapterId && run.adapterId === osc78AdapterId && digest(adapter.bundleSha256) && digest(adapter.upstreamBundleSha256)
        && adapter.derivedFromAdapterId === derivedFrom;
    check(identity, `Invalid ${label} adapter identity.`);
    check(same(run.probeIds, [...osc78CaseIds]) && Array.isArray(run.experiments) && run.experiments.length === experiments.length
        && Array.isArray(run.pinned) && run.pinned.length === osc78CaseIds.length, `${label} run does not cover the contracted cases.`);
    for (const [position, contract] of experiments.entries()) {
        const recorded = run.experiments[position];
        check(recorded.caseId === contract.caseId, `${label} experiment order differs from the contracts.`);
        validateExactFeeds(contract, recorded.feeds, 'contract');
        const barriers = recorded.barriers;
        check(Array.isArray(barriers) && barriers.length === planFeeds(contract).length && barriers.every(barrier => /^\x1b\[\?[\d;]+c$/.test(barrier ?? ''))
            && Array.isArray(recorded.reads) && recorded.reads.length === planReads(contract), `Invalid ${contract.caseId} capture record (${label}).`);
    }
}

// ---------- grading ----------

const byName = (evaluated, name) => evaluated.find(item => item.name === name);
const firstRead = (evaluated, name) => byName(evaluated, name).reads[0];
const readOf = (item, name) => item.reads.find(entry => entry.label === name);
const noLinks = read => read.evaluation.observed.links.length === 0;

/** The validity controls shared by both cases: the measured prior state, the unmarked bytes (and every gate) must not produce the target transition. */
function validityOf(caseId, evaluated, prior, ordinary) {
    const target = targets[caseId];
    return [{ id: 'prior-state', held: prior.evaluation.passed },
        { id: 'unmarked-control', held: ordinary.evaluation.passed && !target(prior.evaluation.observed, ordinary.evaluation.observed) },
        ...evaluated.filter(item => item.role === 'gate').map(item => ({ id: item.name, held: item.reads[0].evaluation.passed && !target(item.priorObserved, item.reads[0].evaluation.observed) }))];
}

/** The requirements of extensions.osc7-cwd: the reported cwd follows each URI, is fresh on the second update, decodes an encoded space and returns to the baseline. */
function osc7Required(evaluated, prior) {
    const [, , first, second, restored] = byName(evaluated, 'effect').reads, fresh = (a, b) => a.evaluation.observed.cwd !== b.evaluation.observed.cwd;
    return [{ id: 'first-update', held: first.evaluation.passed && fresh(first, prior) },
        { id: 'fresh-second-update', held: second.evaluation.passed && fresh(second, first) && fresh(second, prior) },
        { id: 'restoration-to-baseline', held: restored.evaluation.passed && restored.evaluation.observed.cwd === prior.evaluation.observed.cwd },
        { id: 'encoded-space', held: firstRead(evaluated, 'space').evaluation.passed }];
}

/** The requirements of extensions.osc8: the pinned fixture's runs, closed-link text, the id parameter, two distinct links and no stale metadata. */
function osc8Required(evaluated, prior) {
    const [, , pinned, later] = byName(evaluated, 'effect').reads;
    return [{ id: 'pinned-fixture-effect', held: pinned.evaluation.passed && targets[osc8CaseId](prior.evaluation.observed, pinned.evaluation.observed) },
        { id: 'closed-link-later-text', held: later.evaluation.passed },
        { id: 'id-parameter', held: firstRead(evaluated, 'id').evaluation.passed },
        { id: 'two-links-not-merged', held: firstRead(evaluated, 'two-links').evaluation.passed },
        { id: 'no-stale-links', held: byName(evaluated, 'stale').reads.every(entry => entry.evaluation.passed) && noLinks(readOf(byName(evaluated, 'stale'), 'cleared')) }];
}

/** The outcome of one case's effect contract from its evaluated experiments. The OSC 7 stock read is a true baseline (no OSC 7 sent), so it is a validity control.
 * The OSC 8 stock read already carries the pinned fixture under test, so a mismatch there is the effect failing (a required read), never an invalid control. */
function gradeCase(caseId, evaluated) {
    const [prior, ordinary] = byName(evaluated, 'effect').reads, stock = firstRead(evaluated, 'stock'), isOsc7 = caseId === osc7CaseId;
    const validity = validityOf(caseId, evaluated, prior, ordinary);
    const required = isOsc7 ? osc7Required(evaluated, prior) : osc8Required(evaluated, prior);
    if (isOsc7) validity.push({ id: 'prepared-baseline', held: stock.evaluation.passed });
    else required.unshift({ id: 'stock-pinned-fixture', held: stock.evaluation.passed && targets[caseId](prior.evaluation.observed, stock.evaluation.observed) });
    const invalid = validity.filter(item => !item.held), failed = required.filter(item => !item.held);
    let outcome = 'supported';
    if (invalid.length) outcome = 'inconclusive';
    else if (failed.length) outcome = 'unsupported';
    const notes = [`Stock read (${isOsc7 ? 'the prepared baseline' : 'the exact pinned fixture'}): ${stock.evaluation.passed ? 'every fact as predicted' : 'did not match the oracle'}.`];
    return { outcome, validity, required, ...(invalid.length ? { invalid: invalid.map(item => item.id) } : {}), ...(failed.length ? { failed: failed.map(item => item.id) } : {}), notes };
}

const runsOnly = links => links.map(({ row, col, length, uri, id }) => ({ row, col, length, uri, id }));

/** The OSC 7 facts a reader wants beside the verdict, from the evaluated reads only (never graded). */
function osc7Facts(evaluated, runId) {
    const forms = osc7Forms(runId), observedCwd = (name, labelName) => readOf(byName(evaluated, name), labelName).evaluation.observed.cwd;
    const effect = byName(evaluated, 'effect'), cwd = labelName => observedCwd('effect', labelName);
    const transmitted = Object.fromEntries(Object.values(forms).map(form => [form.name, { uri: form.uri, expectedPath: form.path }]));
    return { transmitted,
        observedCwd: { baseline: cwd('prior'), afterOrdinaryText: cwd('ordinary'), first: cwd('first'), second: cwd('second'), restored: cwd('restored'),
            encodedSpace: observedCwd('space', 'encoded'), foreignAuthority: observedCwd('authority', 'post'), stTerminatedFirst: observedCwd('st-terminator', 'post'),
            afterTitleChange: observedCwd('gate-title', 'post') },
        freshness: { baselineFirstSecondDistinct: new Set([cwd('prior'), cwd('first'), cwd('second')]).size === 3 },
        hostComponent: { exposed: false, note: 'The pane metadata carries the decoded path only; the file URI authority is dropped by documented design, so the host component of the transmitted URI is not reported.' },
        titleAfterUpdates: { first: readOf(effect, 'first').evaluation.observed.title, second: readOf(effect, 'second').evaluation.observed.title },
        notObserved: ['child process working directory', 'existence of the named path', 'remote-host validity'] };
}

/** The OSC 8 facts a reader wants beside the verdict, from the evaluated reads only (never graded). */
function osc8Facts(evaluated, frames, runId) {
    const links = osc8Forms(runId), observed = (name, labelName) => readOf(byName(evaluated, name), labelName).evaluation.observed;
    const rowText = (item, labelName, row) => frames[readOf(byName(evaluated, item), labelName).snapshot].before.screen.lines[row];
    const pinned = observed('effect', 'pinned'), id = observed('id', 'linked'), two = observed('two-links', 'linked');
    const identities = [['stock', 'pinned'], ['effect', 'pinned'], ['stale', 'first'], ['stale', 'second']].map(([item, labelName]) => ({ experiment: item, read: labelName, identities: observed(item, labelName).links.map(run => run.identity) }));
    return { pinnedFixture: { rowText: rowText('effect', 'pinned', 0), runs: runsOnly(pinned.links), cursor: pinned.cursor, closedLinkWitness: { char: rowText('effect', 'pinned', 0)?.[5] ?? null, linked: pinned.links.some(run => run.col <= 5 && 5 < run.col + run.length) },
        textBeforeLink: { char: rowText('effect', 'pinned', 0)?.[0] ?? null, linked: pinned.links.some(run => run.col === 0) } },
    idParameter: { sent: links.idValue, observed: id.links.map(run => run.id), uriSent: links.idUri, uriObserved: id.links.map(run => run.uri) },
    twoLinks: { runs: runsOnly(two.links), distinctIdentities: new Set(two.links.map(run => run.identity)).size },
    laterText: { row: 4, text: rowText('effect', 'later', 4), runsUnchanged: same(runsOnly(observed('effect', 'later').links), runsOnly(pinned.links)) },
    stale: { afterPlainOverwrite: runsOnly(observed('stale', 'cleared').links), afterSecondFixture: runsOnly(observed('stale', 'second').links), secondUriSent: links.secondFixtureUri },
    sameAnchor: { runs: runsOnly(observed('same-anchor', 'post').links), identities: observed('same-anchor', 'post').links.map(run => run.identity) },
    stTerminator: { runs: runsOnly(observed('st-terminator', 'post').links) },
    snapshotLocalIdentity: { perRead: identities, lifetime: 'snapshot-local, numbered in order of first appearance; never compared across reads and never equal to the OSC id parameter', equalsOscId: false },
    notObserved: ['click handling', 'browser launch', 'URI safety policy', 'rendered underline appearance'] };
}

/** Replay the contracted experiments over the journal: the oracle's prediction and the observation of every read. */
function evaluateExperiments(frames, run, experiments, fixture, runId) {
    const bound = frames[1], evaluatedByCase = new Map(osc78CaseIds.map(caseId => [caseId, []]));
    let frameIndex = 2;
    for (const [position, contract] of experiments.entries()) {
        const recorded = run.experiments[position], readFrames = recorded.reads.map(() => frames[frameIndex++]);
        crossCheck(contract, recorded, readFrames);
        const feeds = [], reads = [];
        for (const step of contract.plan) {
            if (step.feed !== undefined) { feeds.push(step.feed); continue; }
            const frame = readFrames[reads.length];
            reads.push({ label: step.read, snapshot: frame.index, cursor: recorded.reads[reads.length].cursor,
                evaluation: evaluateRead(frame, simulateOsc78(feeds, fixture, runId), bound) });
        }
        // Gates and observed variants have no read of their own before the bytes under test: their prior state is the oracle's prepared baseline.
        const priorObserved = ['gate', 'observed'].includes(contract.role) ? factsOfState(simulateOsc78([fixture.stage], fixture, runId)) : null;
        evaluatedByCase.get(contract.owner).push({ id: contract.caseId, name: contract.name, role: contract.role, feeds, barriers: recorded.barriers, reads, priorObserved });
    }
    return evaluatedByCase;
}

/** The pinned results the worker recorded must equal the independent replay over the stock-context read; OSC 7 is inconclusive by design. */
function checkPinned(run, caseId, stockFrame) {
    const recorded = run.pinned.find(item => item.caseId === caseId), pinned = osc78Pinned[caseId];
    check(recorded && same(recorded.termless.feeds, pinned.termlessFeeds) && same(recorded.terminal.writes, pinned.terminalWrites), `Missing pinned ${caseId} grading result.`);
    const replay = replayOsc78Pinned(caseId, stockFrame.before.screen, stockFrame.before.metadata.buffer.links);
    for (const path of ['termless', 'terminal']) {
        check(same(recorded[path].result, replay[path]), `Pinned ${caseId} ${path} grading does not match its observed evidence.`);
    }
    check(recorded.terminal.result.observation.outcome === 'inconclusive', `Pinned ${caseId} terminal path must be inconclusive by design.`);
    if (caseId === osc7CaseId) check(recorded.termless.result.observation.outcome === 'inconclusive', `Pinned ${caseId} must be inconclusive by design.`);
    return recorded;
}

/** The report entry of the pinned verdict: the termless replay is the pinned conclusion where it can decide; the terminal path is preserved beside it. */
function conclusionOf(caseId, recorded) {
    const outcome = recorded.termless.result.observation.outcome;
    if (caseId === osc7CaseId) {
        return { outcome, evidence: 'owned-pane-metadata-and-session-status', grading: 'pinned-declaration-only-preserved-by-design', note: recorded.termless.result.note, scope };
    }
    return { outcome, evidence: 'owned-stored-cells-and-pane-metadata', grading: 'pinned-termless-replay-over-stored-cells', note: recorded.terminal.result.observation.note
        ? `Terminal path (cursor position): ${recorded.terminal.result.observation.note}` : null, scope };
}

/** Where the pinned verdicts and the effect contract differ, stated rather than rewritten. */
function divergencesOf(caseId, recorded, grade) {
    const items = [];
    const pinned = recorded.termless.result.observation.outcome;
    if (pinned !== grade.outcome) items.push({ pinnedOutcome: pinned, contractOutcome: grade.outcome, kind: pinned === 'inconclusive' ? 'pinned-inconclusive-by-design' : 'pinned-and-effect-differ' });
    if (caseId === osc8CaseId && recorded.terminal.result.observation.outcome !== pinned) {
        items.push({ termlessOutcome: pinned, terminalOutcome: recorded.terminal.result.observation.outcome, kind: 'terminal-path-inconclusive-by-design' });
    }
    return items;
}

/** One case of the report: the preserved pinned verdict, the separately identified effect verdict and the observed facts. */
function buildCase(caseId, evaluated, frames, recorded, contractsVersion, runId) {
    const grade = gradeCase(caseId, evaluated), effectId = caseId === osc7CaseId ? osc7EffectContractId : osc8EffectContractId;
    const facts = caseId === osc7CaseId ? osc7Facts(evaluated, runId) : osc8Facts(evaluated, frames, runId);
    const divergences = divergencesOf(caseId, recorded, grade), pinnedDivergence = divergences.find(item => item.pinnedOutcome !== undefined);
    return { caseId, pinnedFixture: { fixtureBytes: osc78Pinned[caseId].fixtureBytes, termlessFeeds: osc78Pinned[caseId].termlessFeeds, terminalWrites: osc78Pinned[caseId].terminalWrites },
        observed: facts, experiments: evaluated,
        pinned: { pass: recorded.termless.result.pass, note: recorded.termless.result.note ?? null, observation: recorded.termless.result.observation,
            termless: recorded.termless.result, terminal: recorded.terminal.result },
        contractResult: { outcome: grade.outcome },
        effectContract: { id: effectId, version: contractsVersion, outcome: grade.outcome, ...grade, attribution: 'separately identified Ntilde effect contract; not the pinned probe' },
        divergences, ...(pinnedDivergence ? { divergence: pinnedDivergence } : {}), conclusion: conclusionOf(caseId, recorded) };
}

/** What was actually restored: the prepared title/cwd baseline and the initial cells, never the unobservable original null metadata. */
function restorationOf(frames, runId) {
    const fixture = osc78Fixture(runId), initial = observeFrame(frames[0]), final = observeFrame(frames.at(-1));
    return { declared: 'the prepared title/cwd baseline, closed links, the initial stored cells and the initial runtime palette', preparedTitle: fixture.title, preparedCwd: fixture.cwd,
        initial: { oscTitle: initial.title, workingDirectory: initial.cwd }, final: { oscTitle: final.title, workingDirectory: final.cwd },
        preparedBaselineRestored: final.title === fixture.title && final.cwd === fixture.cwd && final.links.length === 0,
        originalMetadataRestored: final.title === initial.title && final.cwd === initial.cwd,
        note: 'The first observation is the state the preceding suites left (the metadata API cannot show the original state of the App); this suite restores its own prepared baseline, '
            + 'so neither the as-found metadata nor the original unprepared metadata is claimed restored. Disposal of the App and worker does not retroactively prove metadata restoration.' };
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export function buildOsc78GradingReport(frames, run, runId, adapter, { contractsVersion = osc78ContractsVersion, replayModule = null } = {}) {
    const experiments = makeOsc78Experiments(runId), fixture = osc78Fixture(runId);
    validateJournal(frames, experiments, runId);
    validateRun(run, experiments, adapter);
    const evaluatedByCase = evaluateExperiments(frames, run, experiments, fixture, runId);
    const cases = osc78CaseIds.map(caseId => {
        const evaluated = evaluatedByCase.get(caseId), stockFrame = frames[firstRead(evaluated, 'stock').snapshot];
        const recorded = checkPinned(run, caseId, stockFrame);
        // The retained bundle's own termless probe must reproduce the live result when the module is available.
        if (replayModule) {
            requireReplayAgreement({ caseId }, replayModule.ntildeRunOsc78Termless(caseId, osc78TermlessContext(caseId, stockFrame.before.screen, stockFrame.before.metadata.buffer.links)), recorded.termless.result);
        }
        return buildCase(caseId, evaluated, frames, recorded, contractsVersion, runId);
    });
    return { schemaVersion: 1, suiteId: osc78SuiteId, runId, contractsVersion, adapter, scope, initialSnapshot: 0, boundSnapshot: 1, restoredSnapshot: frames.length - 1,
        restoration: restorationOf(frames, runId), cases, snapshots: frames };
}

/** Count conclusions by pinned/effect outcome pair. */
export function osc78Counts(report) {
    const counts = {};
    for (const item of report.cases) {
        const key = `${item.conclusion.outcome}/${item.effectContract.outcome}`;
        counts[key] = (counts[key] ?? 0) + 1;
    }
    return counts;
}
