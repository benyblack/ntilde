// Require requested facts; never substitute defaults from an older endpoint.
export function copyBufferState(value, rows, local = true) {
    const flags = ['originMode', 'autoWrap', 'insertMode', 'pendingWrap', 'alternateScreen'];
    if (!value || !flags.every(key => typeof value[key] === 'boolean')
        || !Number.isSafeInteger(value.scrollTop) || !Number.isSafeInteger(value.scrollBottom)
        || value.scrollTop < 0 || value.scrollBottom < value.scrollTop || value.scrollBottom >= rows
        || (local ? !Number.isSafeInteger(value.scrollbackLines) || value.scrollbackLines < 0 || value.scrollbackLines > 64 : value.scrollbackLines !== null)) {
        throw new Error('Missing or invalid observed editing state.');
    }
    return { scrollTop: value.scrollTop, scrollBottom: value.scrollBottom, ...Object.fromEntries(flags.map(key => [key, value[key]])), scrollbackLines: value.scrollbackLines };
}
export function copyHistory(value, expectedCount, cols) {
    if (!Number.isSafeInteger(expectedCount) || expectedCount < 0 || expectedCount > 64
        || value?.startLine !== 0 || value.totalLines !== expectedCount || !Array.isArray(value.lines)
        || value.lines.length !== expectedCount || value.lines.some(line => typeof line !== 'string' || line.length > cols || /[^ -~]/.test(line))) {
        throw new Error('Incomplete owned editing history.');
    }
    return { startLine: 0, totalLines: expectedCount, lines: [...value.lines] };
}
