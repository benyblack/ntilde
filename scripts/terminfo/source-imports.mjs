// Pure conservative all-candidate audit; spurious candidates may refuse source.
// Parsed trivia or strings never move the independent keyword-search cursor.
// This is not a computed-import or JavaScript sandbox.
// Comments are lexical trivia, including inside literal dynamic-import parentheses.
function importTriviaEnd(source, offset) {
    while (offset < source.length) {
        if (/\s/u.test(source[offset])) { offset++; continue; }
        if (source.startsWith('/*', offset)) {
            const end = source.indexOf('*/', offset + 2);
            if (end < 0) throw new Error('Unterminated comment in captured import.');
            offset = end + 2; continue;
        }
        if (source.startsWith('//', offset)) {
            const end = source.slice(offset + 2).search(/[\r\n\u2028\u2029]/u);
            if (end < 0) return source.length;
            offset += end + 2; continue;
        }
        return offset;
    }
    return offset;
}
export function literalImports(source) {
    const keywords = /\b(?:(?<from>from)|(?<imported>import))\b/g, targets = [];
    let work = 0;
    const charge = amount => {
        work += amount;
        if (work > source.length * 4) throw new Error('Source import traversal budget exceeded.');
    };
    let match = keywords.exec(source);
    while (match) {
        let offset = importTriviaEnd(source, keywords.lastIndex);
        charge(offset - keywords.lastIndex);
        if (Boolean(match.groups.imported) && source[offset] === '(') {
            const next = importTriviaEnd(source, offset + 1); charge(next - offset); offset = next;
        }
        const quote = source[offset];
        if (quote === '`') throw new Error('Unsupported template import syntax.');
        if (quote === '"' || quote === "'") {
            const end = source.indexOf(quote, offset + 1), target = source.slice(offset + 1, end);
            if (end < 0 || target.includes('\\')) throw new Error('Unsupported captured import specifier.');
            charge(end - offset); targets.push(target);
        }
        match = keywords.exec(source);
    }
    return targets;
}
