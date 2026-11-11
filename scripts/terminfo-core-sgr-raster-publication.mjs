import { createCorePublication } from './terminfo-core-publication.mjs';
import { deriveSgrRasterCoreReport, readableSgrRasterReport } from './terminfo-core-sgr-raster-results.mjs';

export const publication = 'docs/qa/terminfo/2026-11-11-core-sgr-raster-results';
// Pinned after the owned run is retained; replacement evidence needs review.
export const manifestSha256 = 'c885fba295c08688e8e2a6b97a1841d6e183be858749bd7c5722ddabe507d767';
const shared = createCorePublication({ publication, manifestSha256, label: 'sgr-raster',
    derive: (directory, mapping, identity, measurement) => deriveSgrRasterCoreReport(directory, mapping, identity, measurement), readable: readableSgrRasterReport });
export const { loadMeasurement, buildPublication, verifyOutput } = shared;
shared.runIfEntry(import.meta.url);
