import { createCorePublication } from './terminfo-core-publication.mjs';
import { deriveSgrRasterCoreReport, readableSgrRasterReport } from './terminfo-core-sgr-raster-results.mjs';

export const publication = 'docs/qa/terminfo/2026-11-11-core-sgr-raster-results';
// Pinned after the owned run is retained; replacement evidence needs review.
export const manifestSha256 = 'd7e0bf8975f93205c02cdc60d8ccfa1826dd6c7527c3b12448904489666f8ed1';
const shared = createCorePublication({ publication, manifestSha256, label: 'sgr-raster',
    derive: (directory, mapping, identity, measurement) => deriveSgrRasterCoreReport(directory, mapping, identity, measurement), readable: readableSgrRasterReport });
export const { loadMeasurement, buildPublication, verifyOutput } = shared;
shared.runIfEntry(import.meta.url);
