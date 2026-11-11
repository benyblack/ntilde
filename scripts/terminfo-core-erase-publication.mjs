import { createCorePublication } from './terminfo-core-publication.mjs';
import { deriveEraseCoreReport, readableEraseReport } from './terminfo-core-erase-results.mjs';

export const publication = 'docs/qa/terminfo/2026-11-11-core-erase-results';
// Pinned after the owned run is retained; replacement evidence needs review.
export const manifestSha256 = '71cbfb45703c85bf0b33a0e4b78ebd76b9c7cd19d342119c755e77f417e322a9';
const shared = createCorePublication({ publication, manifestSha256, label: 'erase', derive: deriveEraseCoreReport, readable: readableEraseReport });
export const { loadMeasurement, buildPublication, verifyOutput } = shared;
shared.runIfEntry(import.meta.url);
