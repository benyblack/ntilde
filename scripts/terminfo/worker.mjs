import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { collectCoreGaps } from './core-gaps.mjs';
import { adapterId } from './collector.mjs';
import { assertPrivateFile, processRecord, requestOwner, verifyNtildeOutput } from './ownership.mjs';
import { collectCells } from './cells.mjs';
import { terminalFence } from './terminal-fence.mjs';
import { ownedTerminalRestorer, restoreOwnedTerminalState, prepareOwnedOutput } from './terminal-output.mjs';
import { collectAsciiSgrGrading } from './ascii-sgr-collector.mjs';
import { collectSgrColorsGrading } from './sgr-colors-collector.mjs';
import { collectSgrRgbGrading } from './sgr-rgb-collector.mjs';
import { collectSgrAttrsGrading } from './sgr-attrs-collector.mjs';
import { collectSgrPaletteGrading } from './sgr-palette-collector.mjs';
import { collectSgrTruecolorGrading } from './sgr-truecolor-collector.mjs';
import { collectTruecolorFindingGrading } from './truecolor-literal-finding-collector.mjs';
import { collectSgrUnderlineGrading } from './sgr-underline-collector.mjs';
import { collectSgrUnderlineColorGrading } from './sgr-underline-color-collector.mjs';
import { collectUnderlineResolvedGrading } from './sgr-underline-color-resolved-collector.mjs';
import { collectSgrResetGrading } from './sgr-reset-collector.mjs';
import { collectSgrClosureGrading } from './sgr-closure-collector.mjs';
import { collectEraseLineGrading } from './erase-line-collector.mjs';
import { collectCharEditGrading } from './char-edit-collector.mjs';
import { collectEraseDisplayGrading } from './erase-display-collector.mjs';
import { collectLineEditGrading } from './line-edit-collector.mjs';
import { collectCharsetsGrading } from './charsets-collector.mjs';
import { collectScrollbackGrading } from './scrollback-collector.mjs';
import { collectSavedResetGrading } from './saved-reset-collector.mjs';
import { collectEraseAttrsRegionGrading } from './erase-attrs-region-collector.mjs';
import { collectAttrRectGrading } from './attr-rect-collector.mjs';
import { collectCursorQualGrading } from './cursor-qual-collector.mjs';
import { collectEraseCharsHistoryGrading } from './erase-chars-history-collector.mjs';
import { collectSelectiveEraseGrading } from './selective-erase-collector.mjs';
import { collectRectEditGrading } from './rect-edit-collector.mjs';
import { collectColEditGrading } from './col-edit-collector.mjs';
import { collectRectChecksumGrading } from './rect-checksum-collector.mjs';
import { collectLrMarginsGrading } from './lr-margins-collector.mjs';
import { collectColorResetGrading } from './color-reset-collector.mjs';
import { collectSgr } from './sgr.mjs';
import { collectRgb } from './rgb.mjs';
import { collectRendition } from './rendition.mjs';
import { collectCursor } from './cursor.mjs';
import { collectErase, collectEditing } from './editing-cases.mjs';
import { collectInput } from './input-collector.mjs';
import { collectQueries } from './query-collector.mjs';
import { collectStaticImages } from './static-images-collector.mjs';
import { collectMetadata } from './metadata-cases.mjs';
import { collectSaved } from './saved-cases.mjs';
import { collectResize } from './resize-cases.mjs';
import { collectHistory } from './history-cases.mjs';
import { collectColors } from './color-cases.mjs';
import { collectPixels } from './pixels-cases.mjs';

/**
 * Format worker-owned failures, including falsy reasons and up to eight entries per
 * AggregateError at each of eight nesting levels. Each reason contributes at most
 * 8,192 characters; the caller separately truncates the final retained text to 65,536.
 */
function retainedFailure(error, depth = 0) {
    if (depth >= 8) return '[nested failure limit]';
    const text = String(error?.stack ?? error).slice(0, 8192);
    return error instanceof AggregateError ? text + '\n' + error.errors.slice(0, 8)
        .map((value, index) => '[' + index + '] ' + retainedFailure(value, depth + 1)).join('\n') : text;
}
let config;
try {
    assertPrivateFile(process.argv[2]);
    config = JSON.parse(readFileSync(process.argv[2], 'utf8'));
    // Establish the real process/PTY relation before loading the probe engine.
    const binding = await verifyNtildeOutput(config, process.stdout);
    const fence = terminalFence(process.stdin, process.stdout);
    let restoreOutput;
    const restore = ownedTerminalRestorer([() => restoreOutput?.(), () => fence.dispose(),
        /** Require the pre-fixture kernel-mode token in private mode; other runs use the legacy restorers. */
        () => { if (config.privateClipboard === true) restoreOwnedTerminalState(binding.tabTty, binding.originalTerminalState); }]);
    let run, primary, failed = false, restoreFailure, restoreFailed = false;
    try {
        restoreOutput = prepareOwnedOutput(binding.tabTty);
        await requestOwner(config, 'raw-output', { tty: binding.tabTty });
        const owner = (kind, barrier = null, extra = {}) => requestOwner(config, kind, { ...extra, tty: binding.tabTty, barrier });
        await collectCells({ config, fence, owner });
        await collectSgr({ config, fence, owner });
        await collectRgb({ config, fence, owner });
        await collectRendition({ config, fence, owner });
        await collectCursor({ config, fence, owner });
        await collectPixels({ config, fence, owner });
        await collectColors({ config, fence, owner });
        await collectErase({ config, fence, owner });
        await collectEditing({ config, fence, owner });
        await collectHistory({ config, fence, owner });
        await collectResize({ config, fence, owner });
        await collectSaved({ config, fence, owner });
        await collectMetadata({ config, fence, owner });
        // Graded ASCII/SGR qualification replays the pinned fixtures through the
        // fence while it is still exclusively owned; the derived extension
        // executes the pinned termless probes over parent-observed screens.
        const gradingSource = readFileSync(config.gradingBundle);
        if (createHash('sha256').update(gradingSource).digest('hex') !== config.gradingBundleSha256) {
            throw new Error('Local grading adapter bundle changed.');
        }
        const gradingModule = await import(pathToFileURL(config.gradingBundle).href);
        const gradingRun = await collectAsciiSgrGrading({ config, fence, owner, gradingModule });
        writeFileSync(join(config.output, 'grading-run.json'), JSON.stringify(gradingRun) + '\n', { flag: 'wx', mode: 0o600 });
        // The layered SGR reset/standard-color qualification follows the first
        // graded suite on the same exclusively owned fence.
        const sgrColorsSource = readFileSync(config.sgrColorsBundle);
        if (createHash('sha256').update(sgrColorsSource).digest('hex') !== config.sgrColorsBundleSha256) {
            throw new Error('Local SGR-colors adapter bundle changed.');
        }
        const sgrColorsModule = await import(pathToFileURL(config.sgrColorsBundle).href);
        const sgrColorsRun = await collectSgrColorsGrading({ config, fence, owner, gradingModule: sgrColorsModule });
        writeFileSync(join(config.output, 'sgcolors-run.json'), JSON.stringify(sgrColorsRun) + '\n', { flag: 'wx', mode: 0o600 });
        // The layered SGR-RGB qualification follows the SGR-colors suite on the
        // same exclusively owned fence; its cells carry the App-resolved RGB.
        const sgrRgbSource = readFileSync(config.sgrRgbBundle);
        if (createHash('sha256').update(sgrRgbSource).digest('hex') !== config.sgrRgbBundleSha256) {
            throw new Error('Local SGR-RGB adapter bundle changed.');
        }
        const sgrRgbModule = await import(pathToFileURL(config.sgrRgbBundle).href);
        const sgrRgbRun = await collectSgrRgbGrading({ config, fence, owner, gradingModule: sgrRgbModule });
        writeFileSync(join(config.output, 'sgrgb-run.json'), JSON.stringify(sgrRgbRun) + '\n', { flag: 'wx', mode: 0o600 });
        // The layered SGR-attributes qualification follows the SGR-RGB suite on the
        // same exclusively owned fence.
        const sgrAttrsSource = readFileSync(config.sgrAttrsBundle);
        if (createHash('sha256').update(sgrAttrsSource).digest('hex') !== config.sgrAttrsBundleSha256) {
            throw new Error('Local SGR-attributes adapter bundle changed.');
        }
        const sgrAttrsModule = await import(pathToFileURL(config.sgrAttrsBundle).href);
        const sgrAttrsRun = await collectSgrAttrsGrading({ config, fence, owner, gradingModule: sgrAttrsModule });
        writeFileSync(join(config.output, 'sgattr-run.json'), JSON.stringify(sgrAttrsRun) + '\n', { flag: 'wx', mode: 0o600 });
        // The layered SGR-palette qualification follows the SGR-attributes suite on the
        // same exclusively owned fence.
        const sgrPaletteSource = readFileSync(config.sgrPaletteBundle);
        if (createHash('sha256').update(sgrPaletteSource).digest('hex') !== config.sgrPaletteBundleSha256) {
            throw new Error('Local SGR-palette adapter bundle changed.');
        }
        const sgrPaletteModule = await import(pathToFileURL(config.sgrPaletteBundle).href);
        const sgrPaletteRun = await collectSgrPaletteGrading({ config, fence, owner, gradingModule: sgrPaletteModule });
        writeFileSync(join(config.output, 'sgpal-run.json'), JSON.stringify(sgrPaletteRun) + '\n', { flag: 'wx', mode: 0o600 });
        // The layered SGR-truecolor qualification follows the SGR-palette suite on the same fence.
        const sgrTruecolorSource = readFileSync(config.sgrTruecolorBundle);
        if (createHash('sha256').update(sgrTruecolorSource).digest('hex') !== config.sgrTruecolorBundleSha256) {
            throw new Error('Local SGR-truecolor adapter bundle changed.');
        }
        const sgrTruecolorModule = await import(pathToFileURL(config.sgrTruecolorBundle).href);
        const sgrTruecolorRun = await collectSgrTruecolorGrading({ config, fence, owner, gradingModule: sgrTruecolorModule });
        writeFileSync(join(config.output, 'sgtc-run.json'), JSON.stringify(sgrTruecolorRun) + '\n', { flag: 'wx', mode: 0o600 });
        // The truecolor-literal finding (no pinned probe, no collector extension) follows on the same fence.
        const truecolorFindingRun = await collectTruecolorFindingGrading({ config, fence, owner });
        writeFileSync(join(config.output, 'sgtf-run.json'), JSON.stringify(truecolorFindingRun) + '\n', { flag: 'wx', mode: 0o600 });
        // The layered SGR-underline-style qualification follows the truecolor finding on the same fence.
        const sgrUnderlineSource = readFileSync(config.sgrUnderlineBundle);
        if (createHash('sha256').update(sgrUnderlineSource).digest('hex') !== config.sgrUnderlineBundleSha256) {
            throw new Error('Local SGR-underline adapter bundle changed.');
        }
        const sgrUnderlineModule = await import(pathToFileURL(config.sgrUnderlineBundle).href);
        const sgrUnderlineRun = await collectSgrUnderlineGrading({ config, fence, owner, gradingModule: sgrUnderlineModule });
        writeFileSync(join(config.output, 'sgus-run.json'), JSON.stringify(sgrUnderlineRun) + '\n', { flag: 'wx', mode: 0o600 });
        // The layered SGR-underline-color qualification follows the underline-style suite on the same fence.
        const sgrUnderlineColorSource = readFileSync(config.sgrUnderlineColorBundle);
        if (createHash('sha256').update(sgrUnderlineColorSource).digest('hex') !== config.sgrUnderlineColorBundleSha256) {
            throw new Error('Local SGR-underline-color adapter bundle changed.');
        }
        const sgrUnderlineColorModule = await import(pathToFileURL(config.sgrUnderlineColorBundle).href);
        const sgrUnderlineColorRun = await collectSgrUnderlineColorGrading({ config, fence, owner, gradingModule: sgrUnderlineColorModule });
        writeFileSync(join(config.output, 'sguc-run.json'), JSON.stringify(sgrUnderlineColorRun) + '\n', { flag: 'wx', mode: 0o600 });
        // The App-observed underline-color resolution suite follows it on the same fence; it has no collector extension.
        const underlineResolvedRun = await collectUnderlineResolvedGrading({ config, fence, owner });
        writeFileSync(join(config.output, 'sgur-run.json'), JSON.stringify(underlineResolvedRun) + '\n', { flag: 'wx', mode: 0o600 });
        // The layered SGR selective-reset qualification follows the underline-resolution suite on the same fence.
        const sgrResetSource = readFileSync(config.sgrResetBundle);
        if (createHash('sha256').update(sgrResetSource).digest('hex') !== config.sgrResetBundleSha256) {
            throw new Error('Local SGR selective-reset adapter bundle changed.');
        }
        const sgrResetModule = await import(pathToFileURL(config.sgrResetBundle).href);
        const sgrResetRun = await collectSgrResetGrading({ config, fence, owner, gradingModule: sgrResetModule });
        writeFileSync(join(config.output, 'sgsr-run.json'), JSON.stringify(sgrResetRun) + '\n', { flag: 'wx', mode: 0o600 });
        // The layered SGR closure qualification follows the selective-reset suite on the same fence.
        const sgrClosureSource = readFileSync(config.sgrClosureBundle);
        if (createHash('sha256').update(sgrClosureSource).digest('hex') !== config.sgrClosureBundleSha256) {
            throw new Error('Local SGR closure adapter bundle changed.');
        }
        const sgrClosureModule = await import(pathToFileURL(config.sgrClosureBundle).href);
        const sgrClosureRun = await collectSgrClosureGrading({ config, fence, owner, gradingModule: sgrClosureModule });
        writeFileSync(join(config.output, 'sgcl-run.json'), JSON.stringify(sgrClosureRun) + '\n', { flag: 'wx', mode: 0o600 });
        // The layered erase-in-line qualification follows the SGR closure suite on the same fence.
        const eraseLineSource = readFileSync(config.eraseLineBundle);
        if (createHash('sha256').update(eraseLineSource).digest('hex') !== config.eraseLineBundleSha256) {
            throw new Error('Local erase-in-line adapter bundle changed.');
        }
        const eraseLineModule = await import(pathToFileURL(config.eraseLineBundle).href);
        const eraseLineRun = await collectEraseLineGrading({ config, fence, owner, gradingModule: eraseLineModule });
        writeFileSync(join(config.output, 'erln-run.json'), JSON.stringify(eraseLineRun) + '\n', { flag: 'wx', mode: 0o600 });
        // The layered character-editing qualification follows the erase-in-line suite on the same fence.
        const charEditSource = readFileSync(config.charEditBundle);
        if (createHash('sha256').update(charEditSource).digest('hex') !== config.charEditBundleSha256) {
            throw new Error('Local character-editing adapter bundle changed.');
        }
        const charEditModule = await import(pathToFileURL(config.charEditBundle).href);
        const charEditRun = await collectCharEditGrading({ config, fence, owner, gradingModule: charEditModule });
        writeFileSync(join(config.output, 'ched-run.json'), JSON.stringify(charEditRun) + '\n', { flag: 'wx', mode: 0o600 });
        // The layered erase-in-display qualification follows the character-editing suite on the same fence.
        const eraseDisplaySource = readFileSync(config.eraseDisplayBundle);
        if (createHash('sha256').update(eraseDisplaySource).digest('hex') !== config.eraseDisplayBundleSha256) {
            throw new Error('Local erase-in-display adapter bundle changed.');
        }
        const eraseDisplayModule = await import(pathToFileURL(config.eraseDisplayBundle).href);
        const eraseDisplayRun = await collectEraseDisplayGrading({ config, fence, owner, gradingModule: eraseDisplayModule });
        writeFileSync(join(config.output, 'erdi-run.json'), JSON.stringify(eraseDisplayRun) + '\n', { flag: 'wx', mode: 0o600 });
        // The layered line-editing qualification follows the erase-in-display suite on the same fence.
        const lineEditSource = readFileSync(config.lineEditBundle);
        if (createHash('sha256').update(lineEditSource).digest('hex') !== config.lineEditBundleSha256) {
            throw new Error('Local line-editing adapter bundle changed.');
        }
        const lineEditModule = await import(pathToFileURL(config.lineEditBundle).href);
        const lineEditRun = await collectLineEditGrading({ config, fence, owner, gradingModule: lineEditModule });
        writeFileSync(join(config.output, 'ledt-run.json'), JSON.stringify(lineEditRun) + '\n', { flag: 'wx', mode: 0o600 });
        // The layered charset qualification follows the line-editing suite on the same fence.
        const charsetsSource = readFileSync(config.charsetsBundle);
        if (createHash('sha256').update(charsetsSource).digest('hex') !== config.charsetsBundleSha256) {
            throw new Error('Local charsets adapter bundle changed.');
        }
        const charsetsModule = await import(pathToFileURL(config.charsetsBundle).href);
        const charsetsRun = await collectCharsetsGrading({ config, fence, owner, gradingModule: charsetsModule });
        writeFileSync(join(config.output, 'chrs-run.json'), JSON.stringify(charsetsRun) + '\n', { flag: 'wx', mode: 0o600 });
        // The layered scrolling/history qualification follows the charsets suite on the same fence, on the main screen.
        const scrollbackSource = readFileSync(config.scrollbackBundle);
        if (createHash('sha256').update(scrollbackSource).digest('hex') !== config.scrollbackBundleSha256) {
            throw new Error('Local scrolling/history adapter bundle changed.');
        }
        const scrollbackModule = await import(pathToFileURL(config.scrollbackBundle).href);
        const scrollbackRun = await collectScrollbackGrading({ config, fence, owner, gradingModule: scrollbackModule });
        writeFileSync(join(config.output, 'scrl-run.json'), JSON.stringify(scrollbackRun) + '\n', { flag: 'wx', mode: 0o600 });
        // The layered saved-state/reset qualification follows the scrolling/history suite on the same fence.
        const savedResetSource = readFileSync(config.savedResetBundle);
        if (createHash('sha256').update(savedResetSource).digest('hex') !== config.savedResetBundleSha256) {
            throw new Error('Local saved-state/reset adapter bundle changed.');
        }
        const savedResetModule = await import(pathToFileURL(config.savedResetBundle).href);
        const savedResetRun = await collectSavedResetGrading({ config, fence, owner, gradingModule: savedResetModule });
        writeFileSync(join(config.output, 'svrs-run.json'), JSON.stringify(savedResetRun) + '\n', { flag: 'wx', mode: 0o600 });
        // The layered erase attributes / erase margins qualification follows the saved-state/reset suite on the same fence.
        const eraseAttrsRegionSource = readFileSync(config.eraseAttrsRegionBundle);
        if (createHash('sha256').update(eraseAttrsRegionSource).digest('hex') !== config.eraseAttrsRegionBundleSha256) {
            throw new Error('Local erase-attributes adapter bundle changed.');
        }
        const eraseAttrsRegionModule = await import(pathToFileURL(config.eraseAttrsRegionBundle).href);
        const eraseAttrsRegionRun = await collectEraseAttrsRegionGrading({ config, fence, owner, gradingModule: eraseAttrsRegionModule });
        writeFileSync(join(config.output, 'erar-run.json'), JSON.stringify(eraseAttrsRegionRun) + '\n', { flag: 'wx', mode: 0o600 });
        // The layered attribute-rectangle qualification follows the erase attributes / erase margins suite on the same fence.
        const attrRectSource = readFileSync(config.attrRectBundle);
        if (createHash('sha256').update(attrRectSource).digest('hex') !== config.attrRectBundleSha256) {
            throw new Error('Local attribute-rectangle adapter bundle changed.');
        }
        const attrRectModule = await import(pathToFileURL(config.attrRectBundle).href);
        const attrRectRun = await collectAttrRectGrading({ config, fence, owner, gradingModule: attrRectModule });
        writeFileSync(join(config.output, 'atrc-run.json'), JSON.stringify(attrRectRun) + '\n', { flag: 'wx', mode: 0o600 });
        // The layered cursor qualification follows the attribute-rectangle suite on the same fence, on the alternate screen.
        const cursorQualSource = readFileSync(config.cursorQualBundle);
        if (createHash('sha256').update(cursorQualSource).digest('hex') !== config.cursorQualBundleSha256) {
            throw new Error('Local cursor adapter bundle changed.');
        }
        const cursorQualModule = await import(pathToFileURL(config.cursorQualBundle).href);
        const cursorQualRun = await collectCursorQualGrading({ config, fence, owner, gradingModule: cursorQualModule });
        writeFileSync(join(config.output, 'curq-run.json'), JSON.stringify(cursorQualRun) + '\n', { flag: 'wx', mode: 0o600 });
        // The layered erase-character/saved-lines qualification follows the cursor-qualification suite on the same fence.
        const eraseCharsHistorySource = readFileSync(config.eraseCharsHistoryBundle);
        if (createHash('sha256').update(eraseCharsHistorySource).digest('hex') !== config.eraseCharsHistoryBundleSha256) {
            throw new Error('Local erase-character/saved-lines adapter bundle changed.');
        }
        const eraseCharsHistoryModule = await import(pathToFileURL(config.eraseCharsHistoryBundle).href);
        const eraseCharsHistoryRun = await collectEraseCharsHistoryGrading({ config, fence, owner, gradingModule: eraseCharsHistoryModule });
        writeFileSync(join(config.output, 'echh-run.json'), JSON.stringify(eraseCharsHistoryRun) + '\n', { flag: 'wx', mode: 0o600 });
        // The layered selective-erasure qualification follows the erase-character/saved-lines suite on the same fence.
        const selectiveEraseSource = readFileSync(config.selectiveEraseBundle);
        if (createHash('sha256').update(selectiveEraseSource).digest('hex') !== config.selectiveEraseBundleSha256) {
            throw new Error('Local selective-erasure adapter bundle changed.');
        }
        const selectiveEraseModule = await import(pathToFileURL(config.selectiveEraseBundle).href);
        const selectiveEraseRun = await collectSelectiveEraseGrading({ config, fence, owner, gradingModule: selectiveEraseModule });
        writeFileSync(join(config.output, 'sels-run.json'), JSON.stringify(selectiveEraseRun) + '\n', { flag: 'wx', mode: 0o600 });
        // The layered rectangle-editing qualification follows the selective-erasure suite on the same fence.
        const rectEditSource = readFileSync(config.rectEditBundle);
        if (createHash('sha256').update(rectEditSource).digest('hex') !== config.rectEditBundleSha256) {
            throw new Error('Local rectangle-editing adapter bundle changed.');
        }
        const rectEditModule = await import(pathToFileURL(config.rectEditBundle).href);
        const rectEditRun = await collectRectEditGrading({ config, fence, owner, gradingModule: rectEditModule });
        writeFileSync(join(config.output, 'rect-run.json'), JSON.stringify(rectEditRun) + '\n', { flag: 'wx', mode: 0o600 });
        // The layered column-editing qualification follows the rectangle-editing suite on the same fence.
        const colEditSource = readFileSync(config.colEditBundle);
        if (createHash('sha256').update(colEditSource).digest('hex') !== config.colEditBundleSha256) {
            throw new Error('Local column-editing adapter bundle changed.');
        }
        const colEditModule = await import(pathToFileURL(config.colEditBundle).href);
        const colEditRun = await collectColEditGrading({ config, fence, owner, gradingModule: colEditModule });
        writeFileSync(join(config.output, 'cole-run.json'), JSON.stringify(colEditRun) + '\n', { flag: 'wx', mode: 0o600 });
        // The layered rectangle-checksum qualification follows the column-editing suite on the same fence.
        const rectChecksumSource = readFileSync(config.rectChecksumBundle);
        if (createHash('sha256').update(rectChecksumSource).digest('hex') !== config.rectChecksumBundleSha256) {
            throw new Error('Local rectangle-checksum adapter bundle changed.');
        }
        const rectChecksumModule = await import(pathToFileURL(config.rectChecksumBundle).href);
        const rectChecksumRun = await collectRectChecksumGrading({ config, fence, owner, gradingModule: rectChecksumModule });
        writeFileSync(join(config.output, 'csum-run.json'), JSON.stringify(rectChecksumRun) + '\n', { flag: 'wx', mode: 0o600 });
        // The layered left/right-margin qualification follows the rectangle-checksum suite on the same fence.
        const lrMarginsSource = readFileSync(config.lrMarginsBundle);
        if (createHash('sha256').update(lrMarginsSource).digest('hex') !== config.lrMarginsBundleSha256) {
            throw new Error('Local left/right-margin adapter bundle changed.');
        }
        const lrMarginsModule = await import(pathToFileURL(config.lrMarginsBundle).href);
        const lrMarginsRun = await collectLrMarginsGrading({ config, fence, owner, gradingModule: lrMarginsModule });
        writeFileSync(join(config.output, 'lrmm-run.json'), JSON.stringify(lrMarginsRun) + '\n', { flag: 'wx', mode: 0o600 });
        if (createHash('sha256').update(readFileSync(config.coreGapsBundle)).digest('hex') !== config.coreGapsBundleSha256) throw new Error('Core-gap bundle changed.');
        const coreRun = await collectCoreGaps({ config, fence, owner, gradingModule: await import(pathToFileURL(config.coreGapsBundle).href) });
        writeFileSync(join(config.output, 'cg-run.json'), JSON.stringify(coreRun) + '\n', { flag: 'wx', mode: 0o600 });
        // The layered runtime-color reset qualification follows the left/right-margin suite on the same fence.
        const colorResetSource = readFileSync(config.colorResetBundle);
        if (createHash('sha256').update(colorResetSource).digest('hex') !== config.colorResetBundleSha256) {
            throw new Error('Local runtime-color reset adapter bundle changed.');
        }
        const colorResetModule = await import(pathToFileURL(config.colorResetBundle).href);
        const colorResetRun = await collectColorResetGrading({ config, fence, owner, gradingModule: colorResetModule });
        writeFileSync(join(config.output, 'crst-run.json'), JSON.stringify(colorResetRun) + '\n', { flag: 'wx', mode: 0o600 });
        const inputResult = await collectInput({ config, fence, owner });
        await collectStaticImages({ config, fence, owner, detachFence: inputResult?.status === 'unavailable' });
        await collectQueries({ runId: config.runId, owner, expectedOwnerPid: binding.ownerPid, outputDirectory: config.output });
        if (config.privateClipboard === true) fence.retireInput();
        else restore();
        const source = readFileSync(config.bundle);
        const digest = createHash('sha256').update(source).digest('hex');
        if (digest !== config.adapterBundleSha256) throw new Error('Local adapter bundle changed.');
        const { collectNtildeRun } = await import(pathToFileURL(config.bundle).href);
        run = await collectNtildeRun(config);
    } catch (error) { primary = error; failed = true; }
    finally {
        try { restore(); }
        catch (error) { restoreFailure = error; restoreFailed = true; }
    }
    if (failed && restoreFailed) throw new AggregateError([primary, restoreFailure], 'Owned collection and terminal restoration failed.');
    if (restoreFailed) throw restoreFailure;
    if (failed) throw primary;
    run.collectorAdapter = { id: adapterId, bundleSha256: config.adapterBundleSha256, upstreamBundleSha256: config.upstreamBundleSha256 };
    writeFileSync(join(config.output, 'raw.json'), JSON.stringify(run) + '\n', { flag: 'wx', mode: 0o600 });
    await requestOwner(config, 'done', { tty: `/dev/${processRecord(process.pid).tty}` });
} catch (error) {
    if (config) {
        writeFileSync(join(config.output, 'worker-error.txt'), retainedFailure(error).slice(0, 65536) + '\n', { flag: 'wx', mode: 0o600 });
        try { await requestOwner(config, 'failed', { error: String(error?.message ?? error).slice(0, 1024), tty: `/dev/${processRecord(process.pid).tty}` }); } catch {}
    }
    console.error(error);
    process.exitCode = 1;
}
