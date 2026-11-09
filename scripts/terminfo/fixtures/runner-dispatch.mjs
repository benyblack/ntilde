// The owned runner is a trusted launcher script with side effects at import, so tests read its source. This helper extracts the
// request-kind prefix set and the dispatch predicate that serves untranslated cell observations, and evaluates only those two
// declarations in isolation.
import { readFileSync } from 'node:fs';

// A Windows checkout with core.autocrlf rewrites the runner to CRLF; the declaration pattern below is written against LF.
const source = readFileSync(new URL('../../terminfo-owned-runner.mjs', import.meta.url), 'utf8').replaceAll('\r\n', '\n');
const declarations = /const untranslatedCellPrefixes = new Set\(\[[^\]]*\]\);\nconst untranslatedCellSteps = new Set\(\[[^\]]*\]\);\n(?:\/\*\*[^\n]*\*\/\n)?function untranslatedCellKind\(kind\) \{[\s\S]*?\n\}\n/.exec(source);
if (declarations === null) throw new Error('The runner no longer declares the untranslated cell dispatch the tests read.');

/** The cell observation prefixes and the dispatch predicate, as declared in the runner. */
export const { prefixes: untranslatedCellPrefixes, steps: untranslatedCellSteps, kind: untranslatedCellKind } =
    new Function(`${declarations[0]}\nreturn { prefixes: untranslatedCellPrefixes, steps: untranslatedCellSteps, kind: untranslatedCellKind };`)();
