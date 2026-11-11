import { createCorePublication } from './terminfo-core-publication.mjs';
import { deriveTabStopsCoreReport, readableTabStopsReport } from './terminfo-core-tab-stops-results.mjs';

export const publication = 'docs/qa/terminfo/2026-11-11-core-tab-stops-results';
// Pinned after the owned run is retained; replacement evidence needs review.
export const manifestSha256 = 'b3db271ea17bfeff75d1a5fe752a435866ffab4d8e506941b52c245a840748d2';
const shared = createCorePublication({ publication, manifestSha256, label: 'tab-stops',
    derive: (directory, mapping, identity, measurement) => deriveTabStopsCoreReport(directory, mapping, identity, measurement), readable: readableTabStopsReport });
export const { loadMeasurement, buildPublication, verifyOutput } = shared;
shared.runIfEntry(import.meta.url);
