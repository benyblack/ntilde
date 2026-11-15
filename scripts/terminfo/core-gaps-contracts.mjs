// Inventory contract of the Core-gaps reconciliation: which benchmark ids the suite `ntilde-owned-core-gaps-v1` is admitted for.
//
// The suite itself (collector, journal, grading, receipt binding) is core-gaps.mjs and has been part of every owned collection since the
// Core requirement run. It also measures `device.env-term` (not in this reconciliation's scope) and `extensions.osc1-icon` (already claimed,
// with its own stronger fixtures, by the osc-title suite). Only the two ids below are admitted here.
import { coreGapsSuiteId } from './core-gaps.mjs';

export const coreGapsQualSuiteId = coreGapsSuiteId;
export const coreGapsQualContractsVersion = 1;
export const coreGapsQualCaseIds = Object.freeze(['text.reverse-index-scroll', 'reset.method']);

/** The coarse evidence tag the inventory classifies into measured layers (see measuredLayersOf). */
export const coreGapsQualEvidence = Object.freeze({
    'text.reverse-index-scroll': 'owned-stored-cells-cursor-margins-and-actual-scrollback',
    'reset.method': 'owned-desktop-backend-reset-api-stored-cells-and-actual-scrollback',
});

/** What the retained effect contract (`ntilde-core-gaps-effect-v1`) was graded on, per case, in the words the publication states. */
export const coreGapsQualGrading = 'pinned-replay-plus-core-gaps-effect-v1';
