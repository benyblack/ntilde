// Projection only: the receipt and journal remain the authority for every value.
import { osc633Publication } from './osc633-publication.mjs';

export const measuredScriptsRevision = '9c814b13215b6abf9186b0c4c784e7702b3e1828';
export const measuredAppRevision = '9c814b13215b6abf9186b0c4c784e7702b3e1828';
const scope = 'Pinned terminfo.dev@4.0.0 OSC 633 A–D termless and terminal marker-consumption probes replayed over parent-journaled stored cells and cursor; '
    + 'the unchanged pinned verdicts are always inconclusive. The unchanged lifecycle effect contract ntilde-osc633-shell-lifecycle-effect-v1 is remeasured on an App that '
    + 'implements OSC 633 A–D, with paired pane metadata and session status, prior-state, unmarked, positive OSC 133 and wrong-marker controls. ST and optional exit-code '
    + 'variants are observations only. Direct-local disposable App; no command exit-event payload, real shell, remote or mux, E/P record, other platforms, common-harness or '
    + 'stock-score claim. OSC 9 and OSC 777/99 evidence is from the separately identified, independently revalidated predecessor run.';

export const osc633ImplementationPublication = dir => osc633Publication(dir, { scope, appRevision: measuredAppRevision, scriptsRevision: measuredScriptsRevision });
