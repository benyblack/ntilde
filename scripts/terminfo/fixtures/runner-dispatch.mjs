// The owned runner is a trusted launcher script with side effects at import, so tests read its source. This helper extracts the
// request-kind prefix and step literals that serve untranslated cell observations, pins the runner's dispatch predicate to its
// exact expected text, and exposes a local reimplementation of that predicate. Nothing from the runner is evaluated.
import { readFileSync } from 'node:fs';

// A Windows checkout with core.autocrlf rewrites the runner to CRLF; the patterns below are written against LF.
const source = readFileSync(new URL('../../terminfo-owned-runner.mjs', import.meta.url), 'utf8').replaceAll('\r\n', '\n');

/** The exact predicate text the runner must declare; a changed predicate fails here and must be reviewed with the tests. */
export const expectedPredicateText = `function untranslatedCellKind(kind) {
    const parts = typeof kind === 'string' ? kind.split('-') : [];
    return parts.length === 2 && untranslatedCellPrefixes.has(parts[0]) && untranslatedCellSteps.has(parts[1]);
}
`;

/** The string literals of one \`new Set([...])\` declaration, refusing anything that is not a plain list of lowercase words. */
function setLiterals(name) {
    const match = new RegExp(`const ${name} = new Set\\(\\[([^\\]]*)\\]\\);\n`).exec(source);
    if (match === null) throw new Error(`The runner no longer declares ${name} as a literal set.`);
    const words = [...match[1].matchAll(/'([a-z]+)'/g)].map(item => item[1]);
    if (match[1].replaceAll(/'[a-z]+'/g, '').replaceAll(/[\s,]/g, '') !== '') throw new Error(`${name} holds something other than lowercase word literals.`);
    return new Set(words);
}

const predicate = /function untranslatedCellKind\(kind\) \{[\s\S]*?\n\}\n/.exec(source);
if (predicate === null || predicate[0] !== expectedPredicateText) throw new Error('The runner dispatch predicate differs from the text these tests pin.');

/** The cell observation prefixes and request steps, as declared in the runner. */
export const untranslatedCellPrefixes = setLiterals('untranslatedCellPrefixes');
export const untranslatedCellSteps = setLiterals('untranslatedCellSteps');

/** A local reimplementation of the runner's predicate over the declared sets. */
export const untranslatedCellKind = kind => {
    const parts = typeof kind === 'string' ? kind.split('-') : [];
    return parts.length === 2 && untranslatedCellPrefixes.has(parts[0]) && untranslatedCellSteps.has(parts[1]);
};
