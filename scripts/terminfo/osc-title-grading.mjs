import { snapshotHash } from './agent-observer.mjs';
import { pristineEditing } from './editing-capture.mjs';
import { validateFrame, validateMetadataSnapshot } from './metadata.mjs';
import { colorBindingHash } from './resolved-colors.mjs';
import { planFeeds, planReads, validateExactFeeds } from './graded-plan.mjs';
import { oscTitleCaseIds, oscTitleContractsVersion, oscTitleExperiments, oscTitleFixture, oscTitlePrefix, oscTitleSuiteId } from './osc-title-contracts.mjs';
import { oscTitleAdapterId } from './osc-title-grading-collector.mjs';

const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const check = (v, message) => { if (!v) throw new Error(message); };

export function replayOscTitlePinned(caseId, before, after) {
    if (caseId !== 'extensions.osc2-title') {
        const note = caseId === 'extensions.osc1-icon' ? 'No icon-name readback for OSC 1' : 'No icon-name and window-title readback for OSC 0';
        return { termless: { pass: false, response: JSON.stringify({ before, after }), note: 'icon-name state',
            notTested: { reason: 'no-semantic-observable', noObservable: 'icon-name state' } },
        terminal: { pass: false, note, observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'none', note } } };
    }
    const pass = after === 'Test Title', response = JSON.stringify({ title: after });
    const note = 'OSC 2 window title was not attempted; this probe has no effect readback or prior-state restoration';
    return { termless: { pass, response, observation: { outcome: pass ? 'supported' : 'unsupported', evidence: 'parser-state' },
        assertions: [{ kind: pass ? 'positive' : 'negative', expected: 'OSC 2 sets the exact requested window title', observed: response }] },
    terminal: { pass: false, note, observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'none', note } } };
}

export function oscTitleObserverPlan(snapshot, runId) {
    check(snapshot.screen.rows >= 12 && snapshot.screen.cols >= 48, 'OSC title fixture needs a larger grid.');
    return oscTitleExperiments(runId).flatMap(item => item.plan.filter(step => step.read).map(() => ({})));
}

export function matchesOscTitleBaseline(snapshot, first, runId, finish) {
    const fixture = oscTitleFixture(runId), screen = structuredClone(first.screen), metadata = snapshot.metadata, buffer = metadata.buffer;
    if (!finish) { screen.lines[9] = fixture.nonce; screen.cursorRow = 9; screen.cursorCol = fixture.nonce.length; }
    const indices = first.screen.colorBinding.paletteOverrideIndices;
    const colors = finish ? colorBindingHash(snapshot.screen.colorBinding) === colorBindingHash(first.screen.colorBinding)
        : same(snapshot.screen.colorBinding.paletteOverrideIndices, [...indices, 7].sort((a, b) => a - b));
    const title = titleFacts(snapshot, { title: fixture.title, icon: fixture.title }, runId);
    return snapshotHash(snapshot.screen.lines) === snapshotHash(screen.lines) && snapshot.screen.cursorRow === screen.cursorRow
        && snapshot.screen.cursorCol === screen.cursorCol && colors && metadata.oscTitle === fixture.title && buffer.requestedIconName === fixture.title
        && title.displayMatches && title.tabMatches
        && metadata.workingDirectory === fixture.cwd && !buffer.acceptingCommandInput && buffer.commandStart === null && buffer.outputStart === null
        && buffer.links.length === 0 && !buffer.alternateScreen && buffer.scrollbackLines === 0
        && (!finish || snapshotHash(snapshot.screen) === snapshotHash(first.screen));
}

/** Ignore only the title channels being qualified; compare every other captured metadata/status field. */
function invariant(snapshot) {
    const metadata = structuredClone(snapshot.metadata);
    delete metadata.oscTitle; delete metadata.displayTitle; delete metadata.buffer.requestedIconName; delete metadata.presentation.tabText;
    return { screen: snapshot.screen, metadata, status: snapshot.status };
}

function restoredContext(snapshot) {
    const metadata = structuredClone(snapshot.metadata);
    // The finish write restores the original screen and cursor; bind is at the nonce row.
    delete metadata.buffer.cursorRow; delete metadata.buffer.cursorCol;
    return { metadata, status: snapshot.status };
}

function titleFacts(snapshot, expected, runId) {
    const metadata = snapshot.metadata, display = metadata.displayTitle, tab = metadata.presentation.tabText;
    const fallback = expected.title.trim().length === 0;
    // The tab can truncate its long profile/cwd fallback while retaining a run-specific prefix.
    // Its actual text stays in the report; it must contain the full display title or a long exact prefix followed by an ellipsis.
    const ellipsis = typeof tab === 'string' ? tab.indexOf('…') : -1;
    const truncatedFallback = fallback && ellipsis >= 32 && typeof display === 'string'
        && ellipsis < display.length && display.startsWith(tab.slice(0, ellipsis));
    return { title: metadata.oscTitle, icon: metadata.buffer.requestedIconName, displayTitle: display, tabText: tab,
        titleMatches: metadata.oscTitle === expected.title, iconMatches: metadata.buffer.requestedIconName === expected.icon,
        displayMatches: fallback ? display === `terminfo disposable worker ${runId} · osc-title-base` : display === expected.title,
        tabMatches: typeof tab === 'string' && typeof display === 'string' && display.length > 0 && (tab.includes(display) || truncatedFallback) };
}

/** Re-derive all reads, refusing changed identities, incomplete frames, feeds, ordering and restoration. */
export function buildOscTitleReport(frames, run, runId, adapter) {
    const contracts = oscTitleExperiments(runId), count = 3 + contracts.reduce((n, item) => n + planReads(item), 0);
    check(Array.isArray(frames) && frames.length === count && count <= 64, 'Incomplete OSC title journal.');
    const first = validateMetadataSnapshot(frames[0]?.before, undefined, true);
    check(pristineEditing(first.screen) && first.screen.rows >= 12 && first.screen.cols >= 48, 'Invalid OSC title initial screen.');
    frames.forEach((frame, index) => validateFrame(frame, index, count - 1, first, null, oscTitlePrefix, true));
    check(matchesOscTitleBaseline(frames[1].before, first, runId, false) && matchesOscTitleBaseline(frames.at(-1).before, first, runId, true),
        'OSC title baseline or restoration failed.');
    check(same(restoredContext(frames[1].before), restoredContext(frames.at(-1).before)), 'OSC title session or metadata restoration failed.');
    check(adapter?.id === oscTitleAdapterId && adapter.derivedFromAdapterId === 'ntilde-owned-title-stack-grading-v1'
        && /^[a-f\d]{64}$/.test(adapter.bundleSha256 ?? '') && /^[a-f\d]{64}$/.test(adapter.upstreamBundleSha256 ?? '') && run?.adapterId === adapter.id
        && same(run.probeIds, oscTitleCaseIds) && Array.isArray(run.experiments) && run.experiments.length === contracts.length
        && same(run.pinned?.map(item => item.caseId), oscTitleCaseIds), 'Invalid OSC title run identity.');
    const bound = frames[1].before, evaluations = [];
    let frameIndex = 2;
    for (const [index, contract] of contracts.entries()) {
        const recorded = run.experiments[index];
        check(recorded?.caseId === contract.caseId, 'OSC title experiment identity changed.');
        validateExactFeeds(contract, recorded.feeds, 'OSC title contract');
        check(Array.isArray(recorded.barriers) && recorded.barriers.length === planFeeds(contract).length
            && recorded.barriers.every(value => /^\x1b\[\?[\d;]+c$/.test(value ?? '')) && recorded.reads?.length === 2, 'Invalid OSC title capture.');
        let feeds = 0, read = 0; const observations = [];
        for (const step of contract.plan) {
            if (step.feed !== undefined) { feeds++; continue; }
            const frame = frames[frameIndex++], entry = recorded.reads[read++], snapshot = frame.before;
            check(entry.kind === step.read && frame.barrier === recorded.barriers[feeds - 1]
                && same(entry.cursor, [snapshot.screen.cursorRow, snapshot.screen.cursorCol])
                && same(entry.facts, { title: snapshot.metadata.oscTitle, icon: snapshot.metadata.buffer.requestedIconName }), 'OSC title read identity changed.');
            check(typeof snapshot.metadata.oscTitle === 'string' && typeof snapshot.metadata.buffer.requestedIconName === 'string', 'Missing OSC title facts.');
            // A different experiment's run-bound setup is not a negative result for this case.
            check(!contracts.some(other => other.caseId !== contract.caseId
                && (snapshot.metadata.oscTitle === other.baseline.title || snapshot.metadata.buffer.requestedIconName === other.baseline.icon)),
            'Cross-case OSC title observation.');
            const facts = titleFacts(snapshot, step.kind === 'setup' ? contract.baseline : contract.expected, runId);
            const preserved = same(invariant(snapshot), invariant(bound));
            check(preserved, 'Unrelated OSC title state drifted.');
            observations.push({ label: step.read, kind: step.kind, snapshot: frame.index, ...facts, preserved,
                passed: facts.titleMatches && facts.iconMatches && facts.displayMatches && facts.tabMatches && preserved });
        }
        evaluations.push({ caseId: contract.caseId, owner: contract.owner, role: contract.role, observations });
    }
    const cases = oscTitleCaseIds.map(id => {
        const experiments = evaluations.filter(item => item.owner === id), controlsHeld = experiments.every(item => item.observations[0].passed);
        // An unchanged target can be an old read as well as a failed setter. It is not a negative verdict.
        const stale = experiments.some(item => {
            const [before, after] = item.observations, contract = contracts.find(value => value.caseId === item.caseId);
            return (contract.expected.title !== contract.baseline.title && after.title === before.title)
                || (contract.expected.icon !== contract.baseline.icon && after.icon === before.icon);
        });
        const controlEffectsHeld = experiments.filter(item => item.role === 'control').every(item => item.observations[1].passed);
        const outcome = !controlsHeld || !controlEffectsHeld || stale ? 'inconclusive'
            : experiments[0].observations[1].passed ? 'supported' : 'unsupported';
        const pinned = run.pinned.find(item => item.caseId === id), stock = experiments[0].observations;
        const replay = replayOscTitlePinned(id, stock[0].title, stock[1].title);
        check(same(pinned?.termless, replay.termless) && same(pinned?.terminal, replay.terminal), 'Pinned OSC title result differs from observations.');
        const pinnedStale = stock[1].title === stock[0].title;
        const pinnedOutcome = id !== 'extensions.osc2-title' ? 'inconclusive'
            : !stock[0].passed || pinnedStale ? 'inconclusive' : pinned.termless.observation.outcome;
        return { caseId: id, effectContract: { id: `ntilde-${id}-effect-v1`, outcome, controlsHeld, controlEffectsHeld, stale }, experiments,
            pinned: { ...pinned, qualifiedOutcome: pinnedOutcome },
            divergences: [{ path: 'terminal', outcome: 'inconclusive' }, ...(pinnedOutcome !== outcome ? [{ path: 'termless', pinnedOutcome, effectOutcome: outcome }] : [])] };
    });
    return { schemaVersion: 1, suiteId: oscTitleSuiteId, contractsVersion: oscTitleContractsVersion, runId, adapter, cases, snapshots: frames };
}

export function oscTitleCounts(report) {
    const counts = {};
    for (const item of report.cases) {
        const key = `${item.pinned.qualifiedOutcome}/${item.effectContract.outcome}`;
        counts[key] = (counts[key] ?? 0) + 1;
    }
    return counts;
}
