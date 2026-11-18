// Worker-side driver for the iTerm2 feature-reporting qualification (extensions.osc1337-capabilities), via the
// shared query-probe collector (probe-suite-shared.mjs). Replays the case's exact plan through the owned fence:
// the PINNED request on a measured seeded alternate screen, then the graded framing, correlation and silence
// groups. A request step is one fenced exchange that retains every raw reply (or their explicit absence) before
// the DA barrier; every read is journaled by the parent observer together with those replies. Both pinned paths
// then run over the journaled bytes of the pinned request's own exchange.
import { capsContracts, capsFinish, capsPlanSeed, capsPrefix, capsPinnedPattern } from './caps-contracts.mjs';
import { devqRecordingContext } from './devq-collector.mjs';
import { planFeeds, planReads, validateExactFeeds } from './graded-plan.mjs';
import { parseCapsFrame } from './caps-report.mjs';
import { makeProbeSuiteCollector } from './probe-suite-shared.mjs';

const { collectGrading, recordingContext: capsRecordingContext, bindOf: capsBind } = makeProbeSuiteCollector({
    label: 'feature-reporting', prefix: capsPrefix, contracts: capsContracts,
    planSeed: capsPlanSeed, finish: capsFinish, pinnedPattern: capsPinnedPattern, parseFrame: parseCapsFrame,
    deps: { devqRecordingContext, planFeeds, planReads, validateExactFeeds },
});

/** Translate the layered bundle's exported names (ntildeCapsAdapter, ntildeRunCapsTermless, ntildeRunCapsTerminal)
 *  into the shared driver's grading-module shape, then run the contracted case with parent-journaled reads. */
export function collectCapsGrading({ config, fence, owner, gradingModule }) {
    return collectGrading({ config, fence, owner, gradingModule: {
        adapter: gradingModule.ntildeCapsAdapter,
        runTermless: gradingModule.ntildeRunCapsTermless,
        runTerminal: gradingModule.ntildeRunCapsTerminal,
    } });
}

export { capsRecordingContext, capsBind };
