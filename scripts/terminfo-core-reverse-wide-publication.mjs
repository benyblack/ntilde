import { createCorePublication } from './terminfo-core-publication.mjs';
import { deriveReverseWideCoreReport, readableReverseWideReport } from './terminfo-core-reverse-wide-results.mjs';

export const publication = 'docs/qa/terminfo/2026-11-11-core-reverse-wide-results';
// Pinned after the owned run is retained; replacement evidence needs review.
export const manifestSha256 = '21b1c05bc835c46a36750b947c0c8791ad0b86f6520d44822be726a39a320cdd';
const shared = createCorePublication({ publication, manifestSha256, label: 'reverse-wide',
    derive: (directory, mapping, identity, measurement) => deriveReverseWideCoreReport(directory, mapping, identity, measurement), readable: readableReverseWideReport });
export const { loadMeasurement, buildPublication, verifyOutput } = shared;
shared.runIfEntry(import.meta.url);
