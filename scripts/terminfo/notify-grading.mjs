import { snapshotHash } from './agent-observer.mjs';
import { pristineEditing } from './editing-capture.mjs';
import { validateFrame, validateMetadataSnapshot } from './metadata.mjs';
import { colorBindingHash } from './resolved-colors.mjs';
import { planFeeds, planReads, validateExactFeeds } from './graded-plan.mjs';
import { exposeNotifyFacts } from './notify-collector.mjs';
import { notifyAdapterId } from './notify-grading-collector.mjs';
import { notifyCaseIds, notifyContractsVersion, notifyExperiments, notifyFixture, notifyMinimum, notifyNonceRow, notifyPinned, notifyPrefix, notifySuiteId,
    osc777CaseId, osc777ContractId, osc777PinnedWrites, osc99CaseId, osc99ContractId } from './notify-contracts.mjs';

const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const check = (v, message) => { if (!v) throw new Error(message); };
const glyphs = /[%⋯⏸✖]/;
const contractIds = Object.freeze({ [osc777CaseId]: osc777ContractId, [osc99CaseId]: osc99ContractId });

/** What the pinned OSC 777 terminal path returns, rebuilt from the documented pinned note and the journaled cursor alone: the verdict depends on no observation. */
export function replayOsc777PinnedStatic(cursor) {
    const note = notifyPinned[osc777CaseId].terminalNote;
    return { caseId: osc777CaseId, termlessFunction: false, termless: null, writes: [...osc777PinnedWrites], cursor: [cursor[0] + 1, cursor[1] + 1],
        terminal: { pass: false, response: `${cursor[0] + 1};${cursor[1] + 1}`, note, observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'query' } } };
}

export function notifyObserverPlan(snapshot, runId) {
    check(snapshot.screen.rows >= notifyMinimum.rows && snapshot.screen.cols >= notifyMinimum.cols, 'Notification fixture needs a larger grid.');
    return notifyExperiments(runId).flatMap(item => item.plan.filter(step => step.read).map(() => ({})));
}

const noProgress = presentation => presentation.progressOwner === null && presentation.progressKind === null && presentation.progressPercent === null
    && presentation.progressBar === null;

/** The prepared baseline: nonce row, run title and cwd, progress withdrawn, toast hidden, the App's opt-ins on. Hidden toast text and window focus are not baseline state. */
export function matchesNotifyBaseline(snapshot, first, runId, finish) {
    const fixture = notifyFixture(runId), screen = structuredClone(first.screen), metadata = snapshot.metadata, buffer = metadata.buffer, p = metadata.presentation;
    if (!finish) { screen.lines[notifyNonceRow] = fixture.nonce; screen.cursorRow = notifyNonceRow; screen.cursorCol = fixture.nonce.length; }
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

/** Attention markers (bell, activity, agent) follow the tab's live focus state and precede any progress suffix: compare the tab text only up to the run title. */
const tabCore = (tab, title) => { const at = tab.indexOf(title); return at < 0 ? tab : tab.slice(0, at + title.length); };

/** The state that must not change: everything except the toast fields, the live window focus and, for the one experiment that moves it, the cursor. */
function invariant(snapshot, title, cursor) {
    const metadata = structuredClone(snapshot.metadata), p = metadata.presentation, screen = structuredClone(snapshot.screen);
    delete p.windowActive;
    p.tabText = tabCore(p.tabText, title);
    for (const key of ['toastVisible', 'toastTitle', 'toastMessage']) delete p[key];
    if (cursor !== null) {
        screen.cursorRow = cursor[0]; screen.cursorCol = cursor[1];
        if (metadata.buffer?.cursorRow !== undefined) { metadata.buffer.cursorRow = cursor[0]; metadata.buffer.cursorCol = cursor[1]; }
    }
    return { screen, metadata, status: snapshot.status };
}

function restoredContext(snapshot, title) {
    const metadata = structuredClone(snapshot.metadata);
    metadata.presentation.tabText = tabCore(metadata.presentation.tabText, title);
    // The finish write restores the original screen and cursor; bind is at the nonce row. Hidden toast text and window focus are not restored state.
    delete metadata.buffer.cursorRow; delete metadata.buffer.cursorCol;
    for (const key of ['windowActive', 'toastTitle', 'toastMessage']) delete metadata.presentation[key];
    return { metadata, status: snapshot.status };
}

function toastObservation(snapshot, expected) {
    const p = snapshot.metadata.presentation;
    const matches = expected === null ? !p.toastVisible : p.toastVisible && p.toastTitle === expected.title && p.toastMessage === expected.message;
    return { toast: { visible: p.toastVisible, title: p.toastVisible ? p.toastTitle : null, message: p.toastVisible ? p.toastMessage : null }, focus: exposeNotifyFacts(snapshot).focus, passed: matches };
}

const deliverable = focus => focus.notificationsEnabled && focus.windowVisible && focus.paneActive && !focus.focusMode && !focus.windowActive;
const suppressible = focus => focus.notificationsEnabled && focus.windowVisible && focus.paneActive && !focus.focusMode && focus.windowActive;

/** Only the effect read expects the contracted toast; setup, held and hidden reads expect none. */
const expectedFor = (contract, step) => (step.kind === 'effect' ? contract.expected : null);

function checkActions(contract, recorded) {
    const names = contract.plan.filter(step => step.action !== undefined).map(step => step.action), actions = recorded.actions ?? [];
    check(actions.length === names.length && actions.every((item, index) => item.action === names[index] && item.response?.action === names[index]), 'Notification action record changed.');
    return actions;
}

function checkRead(frame, entry, step, recorded, feeds) {
    const snapshot = frame.before;
    check(entry.kind === step.read && frame.barrier === recorded.barriers[feeds - 1] && same(entry.cursor, [snapshot.screen.cursorRow, snapshot.screen.cursorCol])
        && same(entry.facts, exposeNotifyFacts(snapshot)), 'Notification read identity changed.');
}

/** The cursor the experiment leaves: the nonce row's end for every experiment but the pinned OSC 777 fixture, which parks it at the origin before and after. */
const contractCursor = contract => (contract.name === 'pinned-fixture' ? [0, 0] : null);

/** Evaluate contracted observations in journal order. */
function evaluateExperiments(frames, run, runId, contracts) {
    const bound = frames[1].before, evaluations = [], title = notifyFixture(runId).title;
    let frameIndex = 2;
    for (const [index, contract] of contracts.entries()) {
        const recorded = run.experiments[index];
        check(recorded?.caseId === contract.caseId, 'Notification experiment identity changed.');
        validateExactFeeds(contract, recorded.feeds, 'notification contract');
        check(Array.isArray(recorded.barriers) && recorded.barriers.length === planFeeds(contract).length
            && recorded.barriers.every(value => /^\x1b\[\?[\d;]+c$/.test(value ?? '')) && recorded.reads?.length === planReads(contract), 'Invalid notification capture.');
        const actions = checkActions(contract, recorded), cursor = contractCursor(contract);
        let feeds = 0, read = 0; const observations = [];
        for (const step of contract.plan) {
            if (step.feed !== undefined) { feeds++; continue; }
            if (step.read === undefined) continue;
            const frame = frames[frameIndex++], entry = recorded.reads[read++];
            checkRead(frame, entry, step, recorded, feeds);
            const preserved = same(invariant(frame.before, title, cursor), invariant(bound, title, cursor));
            check(preserved, `Unrelated notification state drifted (${contract.caseId} ${step.read}).`);
            observations.push({ label: step.read, kind: step.kind, snapshot: frame.index, ...toastObservation(frame.before, expectedFor(contract, step)), preserved });
        }
        evaluations.push({ caseId: contract.caseId, owner: contract.owner, name: contract.name, role: contract.role, expected: contract.expected, observations, ...(actions.length ? { actions } : {}) });
    }
    return evaluations;
}

const settled = item => item.observations.every(entry => entry.preserved) && item.observations[0].passed;
const effectOf = item => item.observations.find(entry => entry.kind === 'effect');

/** A case's effect verdict: supported only when every protocol experiment matched with every validity control and precondition held; unsupported only then too. */
function effectVerdict(id, experiments) {
    const protocol = experiments.filter(item => item.role !== 'policy'), gated = protocol.filter(item => item.observations[0].focus !== undefined);
    const valid = experiments.every(settled);
    // The delivery control: every protocol observation must have started from a hidden toast with the window inactive, both at control time and at delivery.
    const preconditions = gated.map(item => ({ name: item.name, control: deliverable(item.observations[0].focus), delivery: deliverable(effectOf(item).focus) }));
    const preconditionsMet = preconditions.every(item => item.control && item.delivery);
    const hidden = experiments.find(item => item.name === (id === osc777CaseId ? 'delivered' : 'delivered-title'))?.observations.find(entry => entry.kind === 'hidden');
    // A held notification (a chunk with d=0) must show nothing before its last chunk.
    const held = experiments.flatMap(item => item.observations.filter(entry => entry.kind === 'held'));
    const controlsHeld = valid && experiments.filter(item => item.role === 'control').every(item => effectOf(item).passed) && hidden?.passed === true && held.every(entry => entry.passed);
    let outcome = 'inconclusive', reason = null;
    if (!preconditionsMet) reason = 'inactive-window-precondition-unmet';
    else if (!controlsHeld) reason = 'validity-control-failed';
    else outcome = protocol.every(item => effectOf(item).passed) ? 'supported' : 'unsupported';
    return { id: contractIds[id], outcome, controlsHeld, preconditionsMet, reason, preconditions,
        protocolExperiments: protocol.map(item => ({ name: item.name, role: item.role, passed: effectOf(item).passed })) };
}

function policyRecords(experiments) {
    return experiments.filter(item => item.role === 'policy').map(item => {
        const before = item.observations[0], after = effectOf(item), focus = after.focus;
        const applicable = item.name !== 'focused-suppressed' || (suppressible(before.focus) && suppressible(focus));
        return { name: item.name, graded: false, applicable, held: before.passed, matchesDocumentedPolicy: after.passed, observed: after.toast, expected: item.expected };
    });
}

/** The pinned record of one case: the replayed OSC 777 terminal path, or OSC 99's identification (its verdict belongs to the stock run and the exact-query witness). */
function pinnedRecord(id, run, experiments) {
    const recorded = run.pinned.find(item => item.caseId === id);
    if (id === osc99CaseId) {
        check(same(recorded, { caseId: id, termlessFunction: false, termless: null, terminal: null, replay: notifyPinned[id].replay }), 'Pinned OSC 99 identification changed.');
        return { ...recorded, qualifiedOutcome: null };
    }
    const cursor = experiments.find(item => item.name === 'pinned-fixture').observations.find(entry => entry.kind === 'effect');
    const journaled = run.experiments.find(item => item.caseId === `${id}/pinned-fixture`).reads.find(entry => entry.kind === 'after').cursor;
    check(cursor !== undefined && same(recorded, replayOsc777PinnedStatic(journaled)), 'Pinned OSC 777 result differs from its replay.');
    return { ...recorded, qualifiedOutcome: 'inconclusive' };
}

function divergences(pinned, effectContract) {
    const base = pinned.caseId === osc777CaseId ? [{ path: 'terminal', outcome: 'inconclusive' }, { path: 'termless', outcome: 'absent' }]
        : [{ path: 'termless', outcome: 'absent' }, { path: 'terminal', outcome: 'stock-collector-and-exact-query-witness' }];
    return [...base, ...(pinned.qualifiedOutcome !== null && effectContract.outcome !== pinned.qualifiedOutcome
        ? [{ path: 'effect', pinnedOutcome: pinned.qualifiedOutcome, effectOutcome: effectContract.outcome }] : [])];
}

/** Re-derive all reads, refusing changed identities, incomplete frames, feeds, ordering and restoration. */
export function buildNotifyReport(frames, run, runId, adapter) {
    const contracts = notifyExperiments(runId), count = 3 + contracts.reduce((n, item) => n + planReads(item), 0);
    check(Array.isArray(frames) && frames.length === count && count <= 64, 'Incomplete notification journal.');
    const first = validateMetadataSnapshot(frames[0]?.before, undefined, true);
    check(pristineEditing(first.screen) && first.screen.rows >= notifyMinimum.rows && first.screen.cols >= notifyMinimum.cols, 'Invalid notification initial screen.');
    frames.forEach((frame, index) => validateFrame(frame, index, count - 1, first, null, notifyPrefix, true));
    check(matchesNotifyBaseline(frames[1].before, first, runId, false) && matchesNotifyBaseline(frames.at(-1).before, first, runId, true), 'Notification baseline or restoration failed.');
    check(same(restoredContext(frames[1].before, notifyFixture(runId).title), restoredContext(frames.at(-1).before, notifyFixture(runId).title)), 'Notification session or metadata restoration failed.');
    check(adapter?.id === notifyAdapterId && adapter.derivedFromAdapterId === 'ntilde-owned-left-right-margins-grading-v1'
        && /^[a-f\d]{64}$/.test(adapter.bundleSha256 ?? '') && /^[a-f\d]{64}$/.test(adapter.upstreamBundleSha256 ?? '') && run?.adapterId === adapter.id
        && same(run.probeIds, notifyCaseIds) && Array.isArray(run.experiments) && run.experiments.length === contracts.length
        && same(run.pinned?.map(item => item.caseId), notifyCaseIds), 'Invalid notification run identity.');
    const evaluations = evaluateExperiments(frames, run, runId, contracts);
    const cases = notifyCaseIds.map(id => {
        const experiments = evaluations.filter(item => item.owner === id), pinned = pinnedRecord(id, run, experiments), effectContract = effectVerdict(id, experiments);
        return { caseId: id, effectContract, experiments, policy: policyRecords(experiments), pinned, divergences: divergences(pinned, effectContract) };
    });
    return { schemaVersion: 1, suiteId: notifySuiteId, contractsVersion: notifyContractsVersion, runId, adapter, cases, snapshots: frames };
}

/** The receipt counts: the pinned verdict this layer can state (OSC 99's belongs to the stock run and the exact-query witness) over the effect verdict. */
export function notifyCounts(report) {
    const counts = {};
    for (const item of report.cases) {
        const key = `${item.pinned.qualifiedOutcome ?? 'stock-and-query-witness'}/${item.effectContract.outcome}`;
        counts[key] = (counts[key] ?? 0) + 1;
    }
    return counts;
}
