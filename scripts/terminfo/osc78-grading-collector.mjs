// Explicitly versioned collector extension for the OSC 7 / OSC 8 qualification (extensions.osc7-cwd and extensions.osc8). This is a digest-checked
// derivation layered on top of the left/right-margin grading extension, a sibling of the OSC 133, routed-input and runtime-color reset layers: the pinned
// bundle, the owned adapter and every earlier grading extension stay byte-for-byte unchanged, and each graded result carries this extension's identity so
// it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED probe definitions so their paths can be replayed over the parent-observed journal:
// extensions.osc7-cwd (termless: declared-capability report; terminal: no readback; neither reads or writes a byte) and extensions.osc8 (termless: feeds the
// pinned bytes and reads stored cells and their hyperlink metadata; terminal: writes the same bytes and asks for the cursor position). The derivation refuses
// unless the retained left/right-margin extension still contains both exact registrations and the readHyperlinkMetadata helper, each verbatim and once.
import { osc78CaseIds } from './osc78-contracts.mjs';

/** Identity of this layered extension. */
export const osc78AdapterId = 'ntilde-owned-osc78-grading-v1';

/** Exact pinned seams from terminfo.dev@4.0.0 (as retained by the left/right-margin extension). Each must appear exactly once: the extensions.osc7-cwd
 * registration, the extensions.osc8 registration, the readHyperlinkMetadata helper, then the retained previous layer's exports. */
const pinnedOsc78Seams = Object.freeze([
    // extensions.osc7-cwd registration
    "    probe(\"extensions.osc7-cwd\", (ctx) => {\n      const note = \"Declared OSC 7 capability does not establish working-directory metadata delivery\";\n      return {\n        pass: false,\n        response: JSON.stringify({ declared: ctx.capabilities.extensions.has(\"osc7\") }),\n        note,\n        observation: { outcome: \"inconclusive\", reason: \"insufficient-evidence\", evidence: \"legacy\", note }\n      };\n    }, () => {\n      const note = \"No working-directory metadata readback for OSC 7\";\n      return Promise.resolve({\n        pass: false,\n        note,\n        observation: { outcome: \"inconclusive\", reason: \"insufficient-evidence\", evidence: \"none\", note }\n      });\n    }),\n",
    // extensions.osc8 registration
    "    {\n      ...probe(\"extensions.osc8\", (ctx) => {\n        const uri = \"https://example.com/osc8-proof\";\n        ctx.feed(`\\x1B[1;1H\\x1B[2KA\\x1B]8;;${uri}\\x07LINK\\x1B]8;;\\x07Z`);\n        const cells = Array.from({ length: 6 }, (_, col) => {\n          const { hyperlink: _unreported, ...cell } = ctx.getCell(0, col);\n          const hyperlink = readHyperlinkMetadata(ctx.capabilities.extensions.has(\"hyperlinks\"), ctx.getHyperlinkAt, 0, col, \"OSC 8 context\");\n          return { ...cell, ...hyperlink !== undefined && { hyperlink } };\n        });\n        const expectedChars = \"ALINKZ\";\n        const links = cells.map((cell) => cell.hyperlink !== undefined ? { reported: true, uri: cell.hyperlink } : { reported: false });\n        const response = JSON.stringify({ chars: cells.map((cell) => cell.char), links });\n        const reported = links.every((link) => link.reported);\n        const charsMatch = cells.every((cell, index) => cell.char === expectedChars[index]);\n        if (!reported || !charsMatch || cells.slice(1, 5).some((cell) => cell.hyperlink === null)) {\n          return {\n            pass: false,\n            response,\n            observation: {\n              outcome: \"inconclusive\",\n              reason: \"insufficient-evidence\",\n              evidence: \"parser-state\",\n              note: !reported ? \"Backend did not report OSC 8 link metadata\" : \"Linked text was not observable\"\n            }\n          };\n        }\n        const expected = JSON.stringify({\n          chars: [...expectedChars],\n          links: [null, uri, uri, uri, uri, null].map((link) => ({ reported: true, uri: link }))\n        });\n        const observed = response;\n        const pass = observed === expected;\n        return {\n          pass,\n          response,\n          observation: {\n            outcome: pass ? \"supported\" : \"unsupported\",\n            evidence: \"parser-state\",\n            ...!pass && { note: \"OSC 8 cell URI differs from the exact requested link or leaked past close\" }\n          },\n          assertions: [{ kind: pass ? \"positive\" : \"negative\", expected, observed }]\n        };\n      }, async (ctx) => {\n        if (!Number.isSafeInteger(ctx.rows) || !Number.isSafeInteger(ctx.cols) || ctx.rows < 1 || ctx.cols < 7) {\n          return {\n            pass: false,\n            observation: {\n              outcome: \"inconclusive\",\n              reason: \"insufficient-evidence\",\n              evidence: \"none\",\n              note: `OSC 8 fixture needs at least 1x7, measured ${ctx.rows}x${ctx.cols}`\n            }\n          };\n        }\n        ctx.write(\"\\x1B[1;1H\\x1B[2K\");\n        ctx.write(\"A\\x1B]8;;https://example.com/osc8-proof\\x07LINK\\x1B]8;;\\x07Z\");\n        const pos = await ctx.queryCursorPosition();\n        if (!pos) {\n          return {\n            pass: false,\n            note: \"No cursor response\",\n            observation: { outcome: \"inconclusive\", reason: \"no-response\", evidence: \"consumed\" }\n          };\n        }\n        return {\n          pass: false,\n          response: `${pos.row};${pos.col}`,\n          observation: {\n            outcome: \"inconclusive\",\n            reason: \"insufficient-evidence\",\n            evidence: \"consumed\",\n            note: \"Cursor movement cannot verify OSC 8 link metadata or click behavior\"\n          }\n        };\n      }, \"consumed\"),\n      termNeedsGeometry: true,\n      termlessObservationEvidence: \"parser-state\"\n    },\n",
    // readHyperlinkMetadata helper
    "function readHyperlinkMetadata(available, readLink, row, col, backendName) {\n  if (!available)\n    return;\n  if (typeof readLink !== \"function\") {\n    throw new Error(`${backendName} declares OSC 8 metadata but has no getHyperlinkAt method at ${row},${col}`);\n  }\n  const hyperlink = readLink(row, col);\n  if (hyperlink !== null && typeof hyperlink !== \"string\") {\n    throw new Error(`${backendName} declares OSC 8 metadata but returned an invalid value at ${row},${col}`);\n  }\n  return hyperlink;\n}\n",
    // the retained previous layer's exports
    'export const ntildeLrMarginsAdapter = Object.freeze({',
    'export function ntildeRunLrMarginsTermless(probeId, ctx) {',
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const osc78Seams = pinnedOsc78Seams;

/** Derive the OSC 7 / OSC 8 grading extension from the left/right-margin grading extension. */
export function adaptOsc78Collector(lrMarginsSource) {
    const source = String(lrMarginsSource);
    for (const seam of pinnedOsc78Seams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned OSC 7/8 probe seam changed; refusing derivation.');
    }
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local OSC 7 / OSC 8 grading extension ${JSON.stringify(osc78AdapterId)}.
// Derived from the left/right-margin grading extension; probe definitions, policy
// gates and stock grading remain intact.
export const ntildeOsc78Adapter = Object.freeze({
  id: ${JSON.stringify(osc78AdapterId)},
  probeIds: ${JSON.stringify([...osc78CaseIds])}
});
/** Execute one pinned termless probe over a caller-served context. */
export function ntildeRunOsc78Termless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
/** Execute one pinned terminal-path probe over a caller-served write/cursor-position context. */
export async function ntildeRunOsc78Terminal(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.term !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = await probe.term(ctx);
  return { probeId, ...result };
}
`;
}
