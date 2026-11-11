// Strict bounded wire projection; absent evidence is never replaced with a success default.
const check = value => { if (!value) throw new Error('Invalid or incomplete owned pane metadata.'); };
const integer = (value, min, max = Number.MAX_SAFE_INTEGER) => Number.isSafeInteger(value) && value >= min && value <= max;
const guid = value => typeof value === 'string' && /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i.test(value);
function shape(value, required, optional = []) {
    check(value && typeof value === 'object' && !Array.isArray(value));
    const allowed = new Set([...required, ...optional]);
    check(required.every(key => Object.hasOwn(value, key)) && Object.keys(value).every(key => allowed.has(key)));
}
function flag(value) { check(typeof value === 'boolean'); return value; }
function text(value, max = 4096) { check(typeof value === 'string' && value.length <= max); return value; }
function nullableText(value, max) { return value == null ? null : text(value, max); }
function mark(value) {
    if (value == null) return null;
    shape(value, ['row', 'col', 'absoluteRow', 'generation', 'alternateScreen']);
    check(integer(value.row, -2147483648, 2147483647) && integer(value.col, 0, 255)
        && integer(value.absoluteRow, 0) && integer(value.generation, 0));
    return { row: value.row, col: value.col, absoluteRow: value.absoluteRow,
        generation: value.generation, alternateScreen: flag(value.alternateScreen) };
}
function links(values, rows, cols) {
    check(Array.isArray(values) && values.length <= 128);
    const identities = new Map(); let end = -1;
    return values.map(value => {
        shape(value, ['row', 'col', 'length', 'identity', 'uri'], ['id']);
        check(integer(value.row, 0, rows - 1) && integer(value.col, 0, cols - 1)
            && integer(value.length, 1, cols - value.col) && integer(value.identity, 0, 127));
        const start = value.row * cols + value.col;
        check(start >= end); end = start + value.length;
        const uri = text(value.uri, 2083), id = nullableText(value.id, 250);
        check(uri.length > 0);
        const identityValue = JSON.stringify([uri, id]);
        if (identities.has(value.identity)) check(identities.get(value.identity) === identityValue);
        else { check(value.identity === identities.size); identities.set(value.identity, identityValue); }
        return { row: value.row, col: value.col, length: value.length, identity: value.identity, uri, id };
    });
}
function buffer(value) {
    shape(value, ['rows', 'cols', 'cursorRow', 'cursorCol', 'alternateScreen', 'scrollbackLines',
        'historyGeneration', 'rowsEvicted', 'acceptingCommandInput', 'links'], ['commandStart', 'outputStart', 'requestedIconName']);
    check(integer(value.rows, 3, 128) && integer(value.cols, 48, 256) && value.rows * value.cols <= 8192
        && integer(value.cursorRow, 0, value.rows - 1) && integer(value.cursorCol, 0, value.cols - 1)
        && integer(value.scrollbackLines, 0) && integer(value.historyGeneration, 0) && integer(value.rowsEvicted, 0));
    return { rows: value.rows, cols: value.cols, cursorRow: value.cursorRow, cursorCol: value.cursorCol,
        alternateScreen: flag(value.alternateScreen), scrollbackLines: value.scrollbackLines,
        historyGeneration: value.historyGeneration, rowsEvicted: value.rowsEvicted,
        acceptingCommandInput: flag(value.acceptingCommandInput), commandStart: mark(value.commandStart),
        outputStart: mark(value.outputStart), links: links(value.links, value.rows, value.cols) };
}
function progressBar(value) {
    if (value == null) return null;
    shape(value, ['visible', 'indeterminate', 'value']);
    check(typeof value.value === 'number' && Number.isFinite(value.value) && value.value >= 0 && value.value <= 100);
    return { visible: flag(value.visible), indeterminate: flag(value.indeterminate), value: value.value };
}
function presentation(value) {
    shape(value, ['windowActive', 'windowVisible', 'paneActive', 'focusMode', 'notificationsEnabled', 'progressEnabled',
        'tabText', 'tabTextVisible', 'toastVisible', 'toastTitle', 'toastMessage'],
    ['progressOwner', 'progressKind', 'progressPercent', 'progressBar']);
    check(value.progressOwner == null || guid(value.progressOwner));
    check(value.progressKind == null || ['Normal', 'Error', 'Indeterminate', 'Paused'].includes(value.progressKind));
    check(value.progressPercent == null || integer(value.progressPercent, 0, 100));
    return { windowActive: flag(value.windowActive), windowVisible: flag(value.windowVisible), paneActive: flag(value.paneActive),
        focusMode: flag(value.focusMode), notificationsEnabled: flag(value.notificationsEnabled), progressEnabled: flag(value.progressEnabled),
        tabText: text(value.tabText), tabTextVisible: flag(value.tabTextVisible), progressOwner: value.progressOwner ?? null,
        progressKind: value.progressKind ?? null, progressPercent: value.progressPercent ?? null, progressBar: progressBar(value.progressBar),
        toastVisible: flag(value.toastVisible), toastTitle: text(value.toastTitle), toastMessage: text(value.toastMessage) };
}
/** Normalize omitted nullable fields, preserving actual hidden-control properties and raw anchors. */
export function copyPaneMetadata(value) {
    shape(value, ['paneId', 'displayTitle', 'buffer', 'presentation'], ['oscTitle', 'workingDirectory']);
    check(guid(value.paneId));
    const result = { paneId: value.paneId, oscTitle: nullableText(value.oscTitle), workingDirectory: nullableText(value.workingDirectory),
        displayTitle: text(value.displayTitle), buffer: buffer(value.buffer), presentation: presentation(value.presentation) };
    const p = result.presentation;
    const fields = [result.oscTitle, result.workingDirectory, result.displayTitle, p.tabText, p.toastTitle, p.toastMessage,
        ...result.buffer.links.flatMap(link => [link.uri, link.id])];
    check(fields.reduce((sum, field) => sum + (field?.length ?? 0), 0) <= 32768);
    return result;
}
/** Stable status facts only: elapsed timestamps are retained separately, never asserted equal across reads. */
export function copyMetadataStatus(value, paneId) {
    shape(value, ['paneId', 'status', 'confidence', 'statusSinceMs', 'lastOutputAtMs', 'isStalled',
        'stallThresholdSeconds', 'idleThresholdSeconds'], ['exitCode', 'needsUser', 'currentCommand']);
    check(value.paneId === paneId && ['idle', 'running', 'awaitingInput', 'exited'].includes(value.status)
        && ['precise', 'heuristic'].includes(value.confidence)
        && integer(value.statusSinceMs, 0) && integer(value.lastOutputAtMs, 0)
        && integer(value.stallThresholdSeconds, 1) && integer(value.idleThresholdSeconds, 1)
        && (value.exitCode == null || integer(value.exitCode, -2147483648, 2147483647)));
    return { paneId, status: value.status, confidence: value.confidence, exitCode: value.exitCode ?? null,
        needsUser: nullableText(value.needsUser), currentCommand: nullableText(value.currentCommand),
        isStalled: flag(value.isStalled), stallThresholdSeconds: value.stallThresholdSeconds, idleThresholdSeconds: value.idleThresholdSeconds };
}
