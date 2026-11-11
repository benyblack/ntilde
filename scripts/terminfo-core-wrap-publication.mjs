import { createCorePublication } from './terminfo-core-publication.mjs';
import { deriveWrapCoreReport, readableWrapReport } from './terminfo-core-wrap-results.mjs';

export const publication = 'docs/qa/terminfo/2026-11-11-core-wrap-results';
export const manifestSha256 = 'b18fdd799ae4d7b8b502f59cd66bc358fb02b1a13f0f553447375ccff6f91de1';
const shared = createCorePublication({ publication, manifestSha256, label: 'wrap', derive: deriveWrapCoreReport, readable: readableWrapReport });
export const { loadMeasurement, buildPublication, verifyOutput } = shared;
shared.runIfEntry(import.meta.url);
