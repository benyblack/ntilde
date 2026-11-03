// Lossless run-length projection of App readScreen.attributeLines. This contains
// flags and palette indices only: RGB values and underline metadata are absent.
function validAttribute({ flags, fg, bg }) {
    return Number.isSafeInteger(flags) && flags >= 0 && flags <= 65535
        && [fg, bg].every(index => Number.isSafeInteger(index) && index >= -1 && index <= 255);
}

const same = (a, b) => a.flags === b.flags && a.fg === b.fg && a.bg === b.bg;

function parseToken(token) {
    const match = /^([0-9A-F]{4}):(-1|0|[1-9]\d{0,2})\/(-1|0|[1-9]\d{0,2})$/.exec(token);
    if (!match) throw new Error('Invalid attribute token.');
    const attribute = { flags: Number.parseInt(match[1], 16), fg: Number(match[2]), bg: Number(match[3]) };
    if (!validAttribute(attribute)) throw new Error('Invalid palette index.');
    return attribute;
}

function parseRow(line, cols) {
    if (typeof line !== 'string' || line.length > cols * 14 || !line.endsWith('|')) throw new Error('Invalid attribute row.');
    const tokens = line.slice(0, -1).split('|');
    if (tokens.length !== cols) throw new Error('Attribute columns do not match the ASCII viewport.');
    return tokens.map(parseToken);
}

export function readAttributeRuns(lines, rows, cols) {
    if (!Array.isArray(lines) || lines.length !== rows) throw new Error('Missing complete attribute rows.');
    const runs = [];
    for (const line of lines) {
        for (const attribute of parseRow(line, cols)) {
            if (runs.length && same(runs.at(-1), attribute)) runs.at(-1).count++;
            else runs.push({ count: 1, ...attribute });
        }
    }
    return runs;
}

export function copyAttributeRuns(runs, cells) {
    if (!Array.isArray(runs) || !runs.length || runs.length > cells) throw new Error('Invalid attribute runs.');
    let total = 0, previous;
    const result = runs.map(run => {
        if (!run || !validAttribute(run) || !Number.isSafeInteger(run.count) || run.count < 1
            || (previous && same(previous, run))) throw new Error('Invalid or noncanonical attribute run.');
        total += run.count;
        previous = run;
        if (total > cells) throw new Error('Attribute run exceeds viewport.');
        return { count: run.count, flags: run.flags, fg: run.fg, bg: run.bg };
    });
    if (total !== cells) throw new Error('Incomplete attribute viewport.');
    return result;
}

export function expandAttributeRuns(runs, cells) {
    return copyAttributeRuns(runs, cells).flatMap(({ count, ...attribute }) => Array.from({ length: count }, () => ({ ...attribute })));
}
