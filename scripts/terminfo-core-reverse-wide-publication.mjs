import { createCorePublication } from './terminfo-core-publication.mjs';
import { deriveReverseWideCoreReport, readableReverseWideReport } from './terminfo-core-reverse-wide-results.mjs';

export const publication = 'docs/qa/terminfo/2026-11-11-core-reverse-wide-results';
// Pinned after the owned run is retained; replacement evidence needs review.
export const manifestSha256 = '62b4ee7920acf7fe555fa2e42c7bbf8bcd3e455b4b50244502a36ac2d34b7407';
const shared = createCorePublication({ publication, manifestSha256, label: 'reverse-wide',
    derive: (directory, mapping, identity, measurement) => deriveReverseWideCoreReport(directory, mapping, identity, measurement), readable: readableReverseWideReport });
export const { loadMeasurement, buildPublication, verifyOutput } = shared;
shared.runIfEntry(import.meta.url);
