import { createCorePublication } from './terminfo-core-publication.mjs';
import { deriveTabStopsCoreReport, readableTabStopsReport } from './terminfo-core-tab-stops-results.mjs';

export const publication = 'docs/qa/terminfo/2026-11-11-core-tab-stops-results';
// Pinned after the owned run is retained; replacement evidence needs review.
export const manifestSha256 = '3d5fac2ba2d6c07a0434b70292df1b2f700466f458033bde91f373c039a3c3ab';
const shared = createCorePublication({ publication, manifestSha256, label: 'tab-stops',
    derive: (directory, mapping, identity, measurement) => deriveTabStopsCoreReport(directory, mapping, identity, measurement), readable: readableTabStopsReport });
export const { loadMeasurement, buildPublication, verifyOutput } = shared;
shared.runIfEntry(import.meta.url);
