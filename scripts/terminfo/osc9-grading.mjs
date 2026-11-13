import { snapshotHash } from './agent-observer.mjs';
import { pristineEditing } from './editing-capture.mjs';
import { validateFrame, validateMetadataSnapshot } from './metadata.mjs';
import { colorBindingHash } from './resolved-colors.mjs';
import { planFeeds, planReads, validateExactFeeds } from './graded-plan.mjs';
import { exposeOsc9Facts } from './osc9-collector.mjs';
import { osc9AdapterId } from './osc9-grading-collector.mjs';
import { osc9CaseIds, osc9ContractsVersion, osc9DeclaredCapabilities, osc9Experiments, osc9Fixture, osc9Minimum, osc9NonceRow,
    osc9NotificationContractId, osc9Pinned, osc9Prefix, osc9ProgressCaseId, osc9ProgressContractId, osc9SuiteId, progressSuffix } from './osc9-contracts.mjs';

const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const check = (v, message) => { if (!v) throw new Error(message); };
const glyphs = /[%⋯⏸✖]/;

/** What the pinned probes return, rebuilt from the documented pinned notes alone: nothing about them depends on any observation. */
export function replayOsc9PinnedStatic(caseId) {
    const descriptor = osc9Pinned[caseId];
    check(descriptor !== undefined, 'Unknown OSC 9 pinned case.');
    const note = descriptor.terminalNote;
    const terminal = { pass: false, note, observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'none', note } };
    if (!descriptor.termlessFunction) return { termlessFunction: false, termless: null, terminal };
    const termless = { pass: false, response: JSON.stringify({ declared: osc9DeclaredCapabilities.includes('osc9') }), note: descriptor.termlessNote,
        observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'legacy', note: descriptor.termlessNote } };
    return { termlessFunction: true, termless, terminal };
}

export function osc9ObserverPlan(snapshot, runId) {
    check(snapshot.screen.rows >= osc9Minimum.rows && snapshot.screen.cols >= osc9Minimum.cols, 'OSC 9 fixture needs a larger grid.');
    return osc9Experiments(runId).flatMap(item => item.plan.filter(step => step.read).map(() => ({})));
}

const noProgress = presentation => presentation.progressOwner === null && presentation.progressKind === null && presentation.progressPercent === null
    && presentation.progressBar === null;

/** The prepared baseline: nonce row, run title and cwd, progress withdrawn, toast hidden, the App's opt-ins on. Hidden toast text and window focus are not baseline state. */
export function matchesOsc9Baseline(snapshot, first, runId, finish) {
    const fixture = osc9Fixture(runId), screen = structuredClone(first.screen), metadata = snapshot.metadata, buffer = metadata.buffer, p = metadata.presentation;
    if (!finish) { screen.lines[osc9NonceRow] = fixture.nonce; screen.cursorRow = osc9NonceRow; screen.cursorCol = fixture.nonce.length; }
    const indices = first.screen.colorBinding.paletteOverrideIndices;
    const colors = finish ? colorBindingHash(snapshot.screen.colorBinding) === colorBindingHash(first.screen.colorBinding)
        : same(snapshot.screen.colorBinding.paletteOverrideIndices, [...indices, 7].sort((a, b) => a - b));
    return snapshotHash(snapshot.screen.lines) === snapshotHash(screen.lines) && snapshot.screen.cursorRow === screen.cursorRow
        && snapshot.screen.cursorCol === screen.cursorCol && colors && metadata.oscTitle === fixture.title && metadata.displayTitle === fixture.title
        && p.tabText.includes(fixture.title) && !glyphs.test(p.tabText) && noProgress(p) && !p.toastVisible
        && p.windowVisible && p.paneActive && !p.focusMode && p.notificationsEnabled && p.progressEnabled && p.tabTextVisible
        && metadata.workingDirectory === fixture.cwd && !buffer.acceptingCommandInput && buffer.commandStart === null && buffer.outputStart === null
        && buffer.links.length === 0 && !buffer.alternateScreen && buffer.scrollbackLines === 0
        && (!finish || snapshotHash(snapshot.screen) === snapshotHash(first.screen));
}

/** Compare everything except the channels the experiment qualifies (and the live window focus, which is recorded, not asserted). */
function invariant(snapshot, kind) {
    const metadata = structuredClone(snapshot.metadata), p = metadata.presentation;
    delete p.windowActive;
    const channels = kind === 'progress' ? ['progressOwner', 'progressKind', 'progressPercent', 'progressBar', 'tabText'] : [];
    // A bare "4" is notification text by documented policy, so a progress experiment may legitimately raise a toast: toast fields are recorded there, never compared.
    for (const key of [...channels, 'toastVisible', 'toastTitle', 'toastMessage']) delete p[key];
    return { screen: snapshot.screen, metadata, status: snapshot.status };
}

function restoredContext(snapshot) {
    const metadata = structuredClone(snapshot.metadata);
    // The finish write restores the original screen and cursor; bind is at the nonce row. Hidden toast text and window focus are not restored state.
    delete metadata.buffer.cursorRow; delete metadata.buffer.cursorCol;
    for (const key of ['windowActive', 'toastTitle', 'toastMessage']) delete metadata.presentation[key];
    return { metadata, status: snapshot.status };
}

function progressObservation(snapshot, expected, title) {
    const p = snapshot.metadata.presentation, stored = p.progressKind === null ? null : { kind: p.progressKind, percent: p.progressPercent };
    const ownerMatches = expected === null ? p.progressOwner === null : p.progressOwner === snapshot.metadata.paneId, suffix = progressSuffix(expected);
    const tabMatches = p.tabText.includes(title) && (suffix === '' ? !glyphs.test(p.tabText) : p.tabText.endsWith(suffix));
    const storedMatches = same(stored, expected);
    return { stored, owner: p.progressOwner, tabText: p.tabText, toastVisible: p.toastVisible, storedMatches, ownerMatches, tabMatches, passed: storedMatches && ownerMatches && tabMatches };
}

function toastObservation(snapshot, expected, title) {
    const p = snapshot.metadata.presentation;
    const matches = expected === null ? !p.toastVisible : p.toastVisible && p.toastTitle === title && p.toastMessage === expected;
    return { toast: { visible: p.toastVisible, title: p.toastVisible ? p.toastTitle : null, message: p.toastVisible ? p.toastMessage : null }, focus: exposeOsc9Facts(snapshot).focus, passed: matches };
}

const deliverable = focus => focus.notificationsEnabled && focus.windowVisible && focus.paneActive && !focus.focusMode && !focus.windowActive;
const suppressible = focus => focus.notificationsEnabled && focus.windowVisible && focus.paneActive && !focus.focusMode && focus.windowActive;

function expectedFor(contract, step) {
    if (contract.kind === 'progress') return step.kind === 'setup' ? contract.setup : contract.expected;
    return step.kind === 'effect' ? contract.expected : null;
}

function readFacts(contract, snapshot, step, fixtureTitle) {
    const expected = expectedFor(contract, step);
    return contract.kind === 'progress' ? progressObservation(snapshot, expected, fixtureTitle) : toastObservation(snapshot, expected, fixtureTitle);
}

function checkActions(contract, recorded) {
    const names = contract.plan.filter(step => step.action !== undefined).map(step => step.action), actions = recorded.actions ?? [];
    check(actions.length === names.length && actions.every((item, index) => item.action === names[index] && item.response?.action === names[index]), 'OSC 9 action record changed.');
    return actions;
}

/** Evaluate contracted observations in journal order. */
function evaluateExperiments(frames, run, runId, contracts) {
    const bound = frames[1].before, evaluations = [], title = osc9Fixture(runId).title;
    let frameIndex = 2;
    for (const [index, contract] of contracts.entries()) {
        const recorded = run.experiments[index];
        check(recorded?.caseId === contract.caseId, 'OSC 9 experiment identity changed.');
        validateExactFeeds(contract, recorded.feeds, 'OSC 9 contract');
        check(Array.isArray(recorded.barriers) && recorded.barriers.length === planFeeds(contract).length
            && recorded.barriers.every(value => /^\x1b\[\?[\d;]+c$/.test(value ?? '')) && recorded.reads?.length === planReads(contract), 'Invalid OSC 9 capture.');
        const actions = checkActions(contract, recorded);
        let feeds = 0, read = 0; const observations = [];
        for (const step of contract.plan) {
            if (step.feed !== undefined) { feeds++; continue; }
            if (step.read === undefined) continue;
            const frame = frames[frameIndex++], entry = recorded.reads[read++], snapshot = frame.before;
            check(entry.kind === step.read && frame.barrier === recorded.barriers[feeds - 1] && same(entry.cursor, [snapshot.screen.cursorRow, snapshot.screen.cursorCol])
                && same(entry.facts, exposeOsc9Facts(snapshot)), 'OSC 9 read identity changed.');
            const preserved = same(invariant(snapshot, contract.kind), invariant(bound, contract.kind));
            check(preserved, 'Unrelated OSC 9 state drifted.');
            observations.push({ label: step.read, kind: step.kind, snapshot: frame.index, ...readFacts(contract, snapshot, step, title), preserved });
        }
        evaluations.push({ caseId: contract.caseId, owner: contract.owner, name: contract.name, role: contract.role, expected: contract.expected, observations, ...(actions.length ? { actions } : {}) });
    }
    return evaluations;
}

const settled = item => item.observations.every(entry => entry.preserved) && item.observations[0].passed;

/** A progress experiment whose target differs from its setup and whose effect read still shows the setup is stale, not a negative. */
function progressStale(item, contract) {
    const [before, after] = item.observations;
    return !same(contract.setup, contract.expected) && same(before.stored, after.stored) && after.passed === false;
}

function progressVerdict(experiments, contracts) {
    const valid = experiments.every(settled);
    const relevant = experiments.filter(item => item.role !== 'policy'), stale = relevant.some(item => progressStale(item, contracts.find(value => value.caseId === item.caseId)));
    const controlsHeld = valid && experiments.filter(item => item.role === 'control').every(item => item.observations[1].passed);
    const matched = relevant.every(item => item.observations[1].passed);
    let outcome = 'inconclusive';
    if (controlsHeld && !stale) outcome = matched ? 'supported' : 'unsupported';
    return { id: osc9ProgressContractId, outcome, controlsHeld, stale, reason: outcome === 'inconclusive' ? (stale ? 'stale-observation' : 'validity-control-failed') : null,
        protocolExperiments: relevant.map(item => ({ name: item.name, role: item.role, passed: item.observations[1].passed })) };
}

function notificationVerdict(experiments) {
    const protocol = experiments.filter(item => item.role !== 'policy'), gated = protocol.filter(item => item.observations[0].focus !== undefined);
    const valid = experiments.every(settled);
    // The delivery control: every protocol observation must have started from a hidden toast with the window inactive, both at control time and at delivery.
    const preconditions = gated.map(item => ({ name: item.name, control: deliverable(item.observations[0].focus), delivery: deliverable(item.observations[1].focus) }));
    const preconditionsMet = preconditions.every(item => item.control && item.delivery);
    const hidden = experiments.find(item => item.name === 'delivered')?.observations.find(entry => entry.kind === 'hidden');
    const controlsHeld = valid && experiments.filter(item => item.role === 'control').every(item => item.observations[1].passed) && hidden?.passed === true;
    let outcome = 'inconclusive', reason = null;
    if (!preconditionsMet) reason = 'inactive-window-precondition-unmet';
    else if (!controlsHeld) reason = 'validity-control-failed';
    else outcome = protocol.every(item => item.observations[1].passed) ? 'supported' : 'unsupported';
    return { id: osc9NotificationContractId, outcome, controlsHeld, preconditionsMet, reason, preconditions,
        protocolExperiments: protocol.map(item => ({ name: item.name, role: item.role, passed: item.observations[1].passed })) };
}

function policyRecords(experiments) {
    return experiments.filter(item => item.role === 'policy').map(item => {
        const [before, after] = item.observations, focus = after.focus;
        const applicable = item.name !== 'focused-suppressed' || (suppressible(before.focus) && suppressible(focus));
        return { name: item.name, graded: false, applicable, held: before.passed, matchesDocumentedPolicy: after.passed,
            observed: after.stored !== undefined ? after.stored : after.toast, expected: item.expected };
    });
}

/** Re-derive all reads, refusing changed identities, incomplete frames, feeds, ordering and restoration. */
export function buildOsc9Report(frames, run, runId, adapter) {
    const contracts = osc9Experiments(runId), count = 3 + contracts.reduce((n, item) => n + planReads(item), 0);
    check(Array.isArray(frames) && frames.length === count && count <= 64, 'Incomplete OSC 9 journal.');
    const first = validateMetadataSnapshot(frames[0]?.before, undefined, true);
    check(pristineEditing(first.screen) && first.screen.rows >= osc9Minimum.rows && first.screen.cols >= osc9Minimum.cols, 'Invalid OSC 9 initial screen.');
    frames.forEach((frame, index) => validateFrame(frame, index, count - 1, first, null, osc9Prefix, true));
    check(matchesOsc9Baseline(frames[1].before, first, runId, false) && matchesOsc9Baseline(frames.at(-1).before, first, runId, true), 'OSC 9 baseline or restoration failed.');
    check(same(restoredContext(frames[1].before), restoredContext(frames.at(-1).before)), 'OSC 9 session or metadata restoration failed.');
    check(adapter?.id === osc9AdapterId && adapter.derivedFromAdapterId === 'ntilde-owned-left-right-margins-grading-v1'
        && /^[a-f\d]{64}$/.test(adapter.bundleSha256 ?? '') && /^[a-f\d]{64}$/.test(adapter.upstreamBundleSha256 ?? '') && run?.adapterId === adapter.id
        && same(run.probeIds, osc9CaseIds) && Array.isArray(run.experiments) && run.experiments.length === contracts.length
        && same(run.pinned?.map(item => item.caseId), osc9CaseIds), 'Invalid OSC 9 run identity.');
    const evaluations = evaluateExperiments(frames, run, runId, contracts);
    const cases = osc9CaseIds.map(id => {
        const experiments = evaluations.filter(item => item.owner === id), pinned = run.pinned.find(item => item.caseId === id), replay = replayOsc9PinnedStatic(id);
        check(same({ termlessFunction: pinned?.termlessFunction, termless: pinned?.termless, terminal: pinned?.terminal }, replay), 'Pinned OSC 9 result differs from its replay.');
        const effectContract = id === osc9ProgressCaseId ? progressVerdict(experiments, contracts.filter(item => item.owner === id))
            : notificationVerdict(experiments);
        return { caseId: id, effectContract, experiments, policy: policyRecords(experiments),
            pinned: { ...pinned, qualifiedOutcome: 'inconclusive' },
            divergences: [{ path: 'terminal', outcome: 'inconclusive' }, ...(pinned.termlessFunction ? [{ path: 'termless', outcome: 'inconclusive' }] : [{ path: 'termless', outcome: 'absent' }]),
                ...(effectContract.outcome !== 'inconclusive' ? [{ path: 'effect', pinnedOutcome: 'inconclusive', effectOutcome: effectContract.outcome }] : [])] };
    });
    return { schemaVersion: 1, suiteId: osc9SuiteId, contractsVersion: osc9ContractsVersion, runId, adapter, cases, snapshots: frames };
}

export function osc9Counts(report) {
    const counts = {};
    for (const item of report.cases) {
        const key = `${item.pinned.qualifiedOutcome}/${item.effectContract.outcome}`;
        counts[key] = (counts[key] ?? 0) + 1;
    }
    return counts;
}

