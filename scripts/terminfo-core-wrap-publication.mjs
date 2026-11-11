import { createCorePublication } from './terminfo-core-publication.mjs';
import { deriveWrapCoreReport, readableWrapReport } from './terminfo-core-wrap-results.mjs';

export const publication = 'docs/qa/terminfo/2026-11-11-core-wrap-results';
export const manifestSha256 = '578b12a1014c0acf29bc42525c683a4d3ad69e6f534034a539131bcca40314d1';
const shared = createCorePublication({ publication, manifestSha256, label: 'wrap', derive: deriveWrapCoreReport, readable: readableWrapReport });
export const { loadMeasurement, buildPublication, verifyOutput } = shared;
shared.runIfEntry(import.meta.url);
