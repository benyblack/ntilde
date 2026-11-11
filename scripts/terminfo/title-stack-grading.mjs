// Parent-side report builder for the XTWINOPS title/icon stack qualification. Re-derives every conclusion from the retained parent journal (paired complete screen,
// pane-metadata and session-status observations), refuses any tampering, and requires the pinned probes' own results to agree with their replay over the journal.
// The pinned termless verdict (supported when all three observed titles are as expected, else inconclusive; that path never returns unsupported) is the conclusion;
// the pinned terminal path is inconclusive by design and preserved beside it. The separately identified effect contracts carry the graded owned verdicts, derived
// here from the independent oracle in title-stack-contracts.mjs, never from the production implementation.
import { snapshotHash } from './agent-observer.mjs';
import { pristineEditing } from './editing-capture.mjs';
import { validateMetadataSnapshot, validateMonotonicFrame } from './metadata.mjs';
import { colorBindingHash } from './resolved-colors.mjs';
import { planFeeds, planReads, requireReplayAgreement, validateExactFeeds } from './graded-plan.mjs';
import { titleStackAdapterId } from './title-stack-grading-collector.mjs';
import { expectedTitleStackScreen, makeTitleStackExperiments, replayTitleStackTerminal, replayTitleStackTermless, simulateTitleStack, titleStackCaseIds,
    titleStackContractsVersion, titleStackFixture, titleStackMinimum, titleStackNonceRow, titleStackPinned, titleStackPrefix, titleStackRoles, titleStackSuiteId,
    titleStackTermlessContext } from './title-stack-contracts.mjs';

const label = 'Title stack';
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const check = (value, message) => { if (!value) throw new Error(message); };
const derivedFrom = 'ntilde-owned-left-right-margins-grading-v1';
const scope = 'Pinned terminfo.dev@4.0.0 device.xtwinops-22 and device.xtwinops-23 termless code (feed a title, CSI 22;0t, feed another title, CSI 23;0t, getTitle() after each stage) '
    + 'and terminal-path code (the writes then a cursor query; inconclusive by design) replayed over parent-journaled observations and preserved, beside separately identified '
    + 'effect contracts graded from paired complete stored cells and the raw title, raw icon name, display title and actual tab text of the same observation by an independent '
    + 'title-stack oracle: each pinned fixture is executed from a measured prepared baseline whose own empty pop left the values alone, beside combined, title-only, icon-only, '
    + 'nested (LIFO), mixed-selector, empty-pop and bounded-depth experiments and alternate-screen, DECSTR and RIS boundary observations. A disposable direct-local App only: '
    + 'raw icon-name state is not host icon presentation, no window-title or icon report query is qualified, no direct-slot addressing, no stock grade.';

// ---------- observations ----------

/** The facts the oracle predicts, plus the context the preservation checks compare. */
export function observeFrame(frame) {
    const { screen, metadata } = frame.before, buffer = metadata.buffer;
    return { title: metadata.oscTitle, icon: buffer.requestedIconName ?? null, displayTitle: metadata.displayTitle, tabText: metadata.presentation.tabText,
        cursor: [screen.cursorRow, screen.cursorCol], cwd: metadata.workingDirectory, links: buffer.links.length, accepting: buffer.acceptingCommandInput,
        commandStart: buffer.commandStart, outputStart: buffer.outputStart, alternateScreen: buffer.alternateScreen, scrollbackLines: buffer.scrollbackLines,
        historyGeneration: buffer.historyGeneration, colorBinding: colorBindingHash(screen.colorBinding) };
}

/** What the oracle says each title fact must be: the raw title and icon exactly, the display title equal to a nonblank raw title, the tab text containing it. */
function titleFacts(state, observed) {
    return {
        title: { expected: state.title, observed: observed.title, match: observed.title === state.title },
        icon: { expected: state.icon, observed: observed.icon, match: observed.icon === state.icon },
        displayTitle: { expected: state.title, observed: observed.displayTitle, match: observed.displayTitle === state.title },
        tabText: { expected: `includes ${state.title}`, observed: observed.tabText, match: typeof observed.tabText === 'string' && observed.tabText.includes(state.title) },
    };
}

const lines = screen => screen.lines.map(line => line.trimEnd());

/** The cells and context a read must show. `strict` also fixes the cursor and every stored fact; `preserved` (after DECSTR) only the written cells; `cleared` (after RIS) blank cells at home. */
function screenFacts(frame, state, bound, fixture, scopeName, initial) {
    const observed = observeFrame(frame), boundObserved = observeFrame(bound), screen = frame.before.screen;
    const generation = scopeName === 'cleared' ? observed.historyGeneration >= boundObserved.historyGeneration : observed.historyGeneration === boundObserved.historyGeneration;
    const common = observed.links === 0 && observed.alternateScreen === false && observed.scrollbackLines === 0 && generation;
    if (scopeName === 'strict') {
        const matches = snapshotHash(screen) === snapshotHash(expectedTitleStackScreen(bound.before.screen, state));
        const preserved = common && observed.cwd === fixture.cwd && observed.accepting === false && observed.commandStart === null && observed.outputStart === null
            && observed.colorBinding === boundObserved.colorBinding;
        return { screenMatches: matches, preserved };
    }
    const expected = expectedTitleStackScreen(bound.before.screen, state);
    const cellsMatch = same(lines(screen), lines(expected));
    if (scopeName === 'preserved') return { screenMatches: cellsMatch, preserved: common };
    // RIS must clear the stored cells, not only their text: the cell details (attributes, colors) must be the pristine initial screen's.
    const cellsClear = same(screen.cellDetails, initial.before.screen.cellDetails);
    return { screenMatches: cellsMatch && cellsClear && observed.cursor[0] === 0 && observed.cursor[1] === 0, preserved: common };
}

/** Compare one read with the oracle: cells (and cursor and stored facts when strict) plus the four title facts. */
export function evaluateRead(frame, state, bound, fixture, scopeName = 'strict', initial = bound) {
    const observed = observeFrame(frame), required = titleFacts(state, observed), context = screenFacts(frame, state, bound, fixture, scopeName, initial);
    const passed = context.screenMatches && context.preserved && Object.values(required).every(entry => entry.match);
    return { passed, scope: scopeName, ...context, required, observed };
}

// ---------- journal ----------

/** The baseline the stage bytes establish; at the end the screen must equal the initial one and the prepared title and icon must be back, with an empty pop having left them alone. */
export function matchesTitleStackBaseline(snapshot, first, runId, finish) {
    const fixture = titleStackFixture(runId), frame = { before: snapshot }, observed = observeFrame(frame);
    const screen = structuredClone(first.screen);
    if (!finish) { screen.lines[titleStackNonceRow] = fixture.nonce; screen.cursorRow = titleStackNonceRow; screen.cursorCol = fixture.nonce.length; }
    const initialIndices = first.screen.colorBinding.paletteOverrideIndices;
    const indices = snapshot.screen.colorBinding.paletteOverrideIndices;
    const colors = finish ? colorBindingHash(snapshot.screen.colorBinding) === colorBindingHash(first.screen.colorBinding)
        : same(indices, [...initialIndices, 7].sort((a, b) => a - b));
    return snapshotHash(snapshot.screen.lines) === snapshotHash(screen.lines) && snapshot.screen.cursorRow === screen.cursorRow
        && snapshot.screen.cursorCol === screen.cursorCol && colors && observed.accepting === false && observed.commandStart === null && observed.outputStart === null
        && observed.title === fixture.title && observed.icon === fixture.title && observed.cwd === fixture.cwd && observed.links === 0
        && observed.alternateScreen === false && observed.scrollbackLines === 0 && (!finish || snapshotHash(snapshot.screen) === snapshotHash(first.screen));
}

/** The plan the observer accepts: one entry per read. */
export function titleStackObserverPlan(snapshot, runId) {
    check(snapshot.screen.rows >= titleStackMinimum.rows && snapshot.screen.cols >= titleStackMinimum.cols, 'Title-stack fixture needs a larger grid.');
    return makeTitleStackExperiments(runId).flatMap(item => item.plan.filter(step => step.read).map(() => ({})));
}

function validateJournal(frames, experiments, runId) {
    const reads = experiments.reduce((sum, item) => sum + planReads(item), 0), count = 3 + reads;
    check(/^[a-f\d]{32}$/.test(runId ?? '') && Array.isArray(frames) && frames.length === count && count <= 64, `Incomplete ${label} snapshot sequence.`);
    const first = validateMetadataSnapshot(frames[0]?.before, undefined, true);
    check(pristineEditing(first.screen) && first.screen.rows >= titleStackMinimum.rows && first.screen.cols >= titleStackMinimum.cols, `${label} fixture needs a pristine private main screen.`);
    for (const [index, frame] of frames.entries()) validateMonotonicFrame(frame, index, count - 1, first, { caseId: null, stageId: null }, titleStackPrefix, true);
    check(matchesTitleStackBaseline(frames[1].before, first, runId, false) && matchesTitleStackBaseline(frames.at(-1).before, first, runId, true),
        `${label} baseline binding or restoration failed.`);
    for (const [index, frame] of frames.entries()) {
        // RIS raises the history generation; nothing ever lowers it (the observer only accepts a generation at or above the first).
        check(index === 0 || frame.before.metadata.buffer.historyGeneration >= frames[index - 1].before.metadata.buffer.historyGeneration, `${label} history generation fell between consecutive observations.`);
    }
    for (const frame of frames.slice(1)) {
        check(frame.before.screen.colorBinding !== undefined && typeof frame.before.metadata.oscTitle === 'string' && typeof frame.before.metadata.buffer.requestedIconName === 'string',
            `${label} snapshots need their resolved color binding, raw title and raw icon name.`);
    }
    return first;
}

/** Every read frame carries the barrier, cursor and raw title/icon facts of the feed it follows. */
function crossCheck(contract, recorded, readFrames) {
    let feedSteps = 0, readSteps = 0;
    for (const step of contract.plan) {
        if (step.feed !== undefined) { feedSteps++; continue; }
        const frame = readFrames[readSteps], entry = recorded.reads[readSteps];
        check(frame.barrier === recorded.barriers[feedSteps - 1], `${label} ${contract.caseId} barrier differs from its read frame.`);
        check(same(entry.cursor, [frame.before.screen.cursorRow, frame.before.screen.cursorCol]), `${label} ${contract.caseId} cursor differs from its read frame.`);
        check(entry.kind === step.read, `Wrong ${contract.caseId} read kind (${label}).`);
        check(same(entry.facts, { title: frame.before.metadata.oscTitle, icon: frame.before.metadata.buffer.requestedIconName }), `${label} ${contract.caseId} title facts differ from its read frame.`);
        readSteps++;
    }
}

/** Validate a retained worker run record's shape against the contracts. */
function validateRun(run, experiments, adapter) {
    check(adapter && adapter.id === titleStackAdapterId && run.adapterId === titleStackAdapterId && /^[a-f\d]{64}$/.test(adapter.bundleSha256 ?? '')
        && /^[a-f\d]{64}$/.test(adapter.upstreamBundleSha256 ?? '') && adapter.derivedFromAdapterId === derivedFrom, `Invalid ${label} adapter identity.`);
    check(same(run.probeIds, [...titleStackCaseIds]) && Array.isArray(run.experiments) && run.experiments.length === experiments.length
        && Array.isArray(run.pinned) && same(run.pinned.map(item => item.caseId), [...titleStackCaseIds]), `${label} run does not cover the contracted cases.`);
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

/** Replay the contracted experiments over the journal: the oracle's prediction and the observation of every read. */
function evaluateExperiments(frames, run, experiments, fixture) {
    const bound = frames[1], evaluated = [];
    let frameIndex = 2;
    for (const [position, contract] of experiments.entries()) {
        const recorded = run.experiments[position], readFrames = recorded.reads.map(() => frames[frameIndex++]);
        crossCheck(contract, recorded, readFrames);
        const feeds = [], reads = [];
        for (const step of contract.plan) {
            if (step.feed !== undefined) { feeds.push(step.feed); continue; }
            const frame = readFrames[reads.length], state = simulateTitleStack(feeds, fixture);
            reads.push({ label: step.read, kind: step.kind, snapshot: frame.index, cursor: recorded.reads[reads.length].cursor,
                evaluation: evaluateRead(frame, state, bound, fixture, step.scope, frames[0]) });
        }
        evaluated.push({ id: contract.caseId, owner: contract.owner, name: contract.name, role: contract.role, feeds, barriers: recorded.barriers, reads });
    }
    return evaluated;
}

const setupHeld = group => group.every(item => item.reads.filter(entry => entry.kind === 'setup').every(entry => entry.evaluation.passed));

/** The outcome of one group of experiments: setup reads (prepared baselines, observed mutations) must hold for a mismatch to be a verdict. */
function gradeGroup(group, requiredIds) {
    const validity = [{ id: 'prepared-baselines-and-observed-mutations', held: setupHeld(group) }];
    const required = group.filter(item => requiredIds.has(item.name) || item.role === 'effect' || item.role === 'stock')
        .map(item => ({ id: item.name, held: item.reads.filter(entry => entry.kind === 'effect').every(entry => entry.evaluation.passed) }));
    const failed = required.filter(item => !item.held);
    let outcome = 'supported';
    if (!validity[0].held) outcome = 'inconclusive';
    else if (failed.length > 0) outcome = 'unsupported';
    return { outcome, validity, required, ...(failed.length ? { failed: failed.map(item => item.id) } : {}) };
}

const requiredNames = { 'device.xtwinops-22': new Set(['selectors', 'nested']), 'device.xtwinops-23': new Set(['empty', 'selectors', 'lifo', 'mixed']) };

/** The effect verdict and the separately reported supplemental groups of one case. */
function gradeCase(caseId, owned) {
    const required = owned.filter(item => ['stock', 'terminal', 'effect', 'required'].includes(item.role));
    const effect = gradeGroup(required, requiredNames[caseId]);
    const supplemental = Object.fromEntries(['limit', 'boundary'].map(role => [role === 'limit' ? 'limits' : 'boundaries',
        owned.filter(item => item.role === role)]).filter(([, group]) => group.length > 0)
        .map(([name, group]) => [name, { ...gradeGroup(group, new Set(group.map(item => item.name))), experiments: group.map(item => item.id) }]));
    return { effect, supplemental };
}

/** The title and icon observed at the effect reads of one experiment, in order, beside what the oracle predicted. */
function effectTable(item) {
    return item.reads.map(entry => ({ label: entry.label, kind: entry.kind, title: entry.evaluation.observed.title, icon: entry.evaluation.observed.icon,
        expectedTitle: entry.evaluation.required.title.expected, expectedIcon: entry.evaluation.required.icon.expected, displayTitle: entry.evaluation.observed.displayTitle,
        tabText: entry.evaluation.observed.tabText, held: entry.evaluation.passed }));
}

/** The facts a reader wants beside each verdict, derived from the evaluated reads only (never graded). */
function observedFacts(owned) {
    return { experiments: owned.map(item => ({ id: item.id, role: item.role, reads: effectTable(item) })) };
}

/** The pinned results the worker recorded must equal the replay over the stock-context reads. */
function checkPinned(run, caseId, frames, owned) {
    const recorded = run.pinned.find(item => item.caseId === caseId), pinned = titleStackPinned[caseId];
    check(recorded && same(recorded.termless.feeds, pinned.termlessFeeds) && same(recorded.terminal.writes, pinned.terminalWrites), `Missing pinned ${caseId} grading result.`);
    const stock = owned.find(item => item.name === 'stock');
    const titles = Object.fromEntries(pinned.reads.map(([name]) => [name, frames[stock.reads.find(entry => entry.label === name).snapshot].before.metadata.oscTitle]));
    const replay = { termless: replayTitleStackTermless(caseId, titles), terminal: replayTitleStackTerminal(caseId, true) };
    for (const path of ['termless', 'terminal']) {
        check(same(recorded[path].result, replay[path]), `Pinned ${caseId} ${path} grading does not match its observed evidence.`);
    }
    check(recorded.terminal.result.observation.outcome === 'inconclusive', `Pinned ${caseId} terminal path must be inconclusive by design.`);
    return { recorded, titles };
}

/** One case of the report: the pinned verdicts, the separately identified effect verdict, supplemental groups, divergences and the observed facts. */
function buildCase(caseId, owned, recorded, contractsVersion) {
    const roles = titleStackRoles[caseId], { effect, supplemental } = gradeCase(caseId, owned), pinnedOutcome = recorded.termless.result.observation.outcome;
    const divergences = [{ path: 'terminal', pinnedOutcome: 'inconclusive', kind: 'pinned-terminal-inconclusive-by-design',
        note: 'The terminal path only asks whether the cursor answered; it never measures a title.' }];
    if (pinnedOutcome !== effect.outcome) divergences.push({ path: 'termless', pinnedOutcome, effectOutcome: effect.outcome, kind: 'pinned-termless-vs-effect' });
    return { caseId, role: roles.role, criterion: roles.criterion, pinnedFixture: { termlessFeeds: [...titleStackPinned[caseId].termlessFeeds], terminalWrites: [...titleStackPinned[caseId].terminalWrites] },
        observed: observedFacts(owned), experiments: owned,
        pinned: { pass: recorded.termless.result.pass, note: recorded.termless.result.note, observation: recorded.termless.result.observation,
            termless: recorded.termless.result, terminal: recorded.terminal.result },
        contractResult: { outcome: effect.outcome },
        effectContract: { id: roles.contractId, version: contractsVersion, outcome: effect.outcome, ...effect, ...supplemental,
            attribution: 'separately identified Ntilde effect contract; not the pinned probe' },
        divergences,
        conclusion: { outcome: pinnedOutcome, evidence: 'owned-pane-metadata-title-and-icon', grading: 'pinned-termless-replayed-over-owned-observations',
            note: recorded.termless.result.note ?? 'The pinned termless probe observed the pushed, changed and restored titles.', scope } };
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export function buildTitleStackGradingReport(frames, run, runId, adapter, { contractsVersion = titleStackContractsVersion, replayModule = null } = {}) {
    const experiments = makeTitleStackExperiments(runId), fixture = titleStackFixture(runId);
    validateJournal(frames, experiments, runId);
    validateRun(run, experiments, adapter);
    const evaluated = evaluateExperiments(frames, run, experiments, fixture);
    const cases = titleStackCaseIds.map(caseId => {
        const owned = evaluated.filter(item => item.owner === caseId), { recorded, titles } = checkPinned(run, caseId, frames, owned);
        // The retained bundle's own termless probe must reproduce the live result when the module is available.
        if (replayModule) requireReplayAgreement({ caseId }, replayModule.ntildeRunTitleStackTermless(caseId, titleStackTermlessContext(caseId, titles)), recorded.termless.result);
        return buildCase(caseId, owned, recorded, contractsVersion);
    });
    return { schemaVersion: 1, suiteId: titleStackSuiteId, runId, contractsVersion, adapter, scope, initialSnapshot: 0, boundSnapshot: 1, restoredSnapshot: frames.length - 1,
        cases, snapshots: frames };
}

/** Count conclusions by pinned/effect outcome pair. */
export function titleStackCounts(report) {
    const counts = {};
    for (const item of report.cases) {
        const key = `${item.conclusion.outcome}/${item.effectContract.outcome}`;
        counts[key] = (counts[key] ?? 0) + 1;
    }
    return counts;
}
