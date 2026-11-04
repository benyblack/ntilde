// Immutable independent 16x16 protocol inputs, not decoded/rendered runtime evidence.
import { createHash } from 'node:crypto';
export const staticImageSize = Object.freeze({ width: 16, height: 16 });
export const staticImageBackground = Object.freeze([16, 16, 16, 255]);
const palette = [[255, 0, 0, 255], [0, 255, 0, 255], [0, 0, 255, 255], [255, 255, 0, 255]];
const assets = {
    "opaque": {
        "rgbaBase64": "/wAA//8AAP//AAD//wAA//8AAP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A//8AAP//AAD//wAA//8AAP//AAD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP//AAD//wAA//8AAP//AAD//wAA/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD//wAA//8AAP//AAD//wAA//8AAP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A//8AAP//AAD//wAA//8AAP//AAD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP//AAD//wAA//8AAP//AAD//wAA/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD//wAA//8AAP//AAD//wAA//8AAP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wAA//8AAP//AAD//wAA//8AAP////8A////AP///wD///8A////AP///wD///8A////AP///wD///8A////AP8AAP//AAD//wAA//8AAP//AAD/////AP///wD///8A////AP///wD///8A////AP///wD///8A////AP///wD/AAD//wAA//8AAP//AAD//wAA/////wD///8A////AP///wD///8A////AP///wD///8A////AP///wD///8A/wAA//8AAP//AAD//wAA//8AAP////8A////AP///wD///8A////AP///wD///8A////AP///wD///8A////AP8AAP//AAD//wAA//8AAP//AAD/////AP///wD///8A////AP///wD///8A////AP///wD///8A////AP///wD/AAD//wAA//8AAP//AAD//wAA/////wD///8A////AP///wD///8A////AP///wD///8A////AP///wD///8A/wAA//8AAP//AAD//wAA//8AAP////8A////AP///wD///8A////AP///wD///8A////AP///wD///8A////AP8AAP//AAD//wAA//8AAP//AAD/////AP///wD///8A////AP///wD///8A////AP///wD///8A////AP///wD/AAD//wAA//8AAP//AAD//wAA/////wD///8A////AP///wD///8A////AP///wD///8A////AP///wD///8A/w==",
        "rgbaSha256": "8ffa4bab91839c5f470fd5ffed325c719d44463ff1b05b730dfa77411d611c4a",
        "pngBase64": "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAAIklEQVR42mP4z8DwHx2TCEcNGHgDsOgHAgai8agBowaAMACUTmC8JGjOJwAAAABJRU5ErkJggg==",
        "pngSha256": "1f3bae32222264eef0d1549fed4a1120eddbb064a7ae9f3c5bd1fd725ad5b495"
    },
    "alpha": {
        "rgbaBase64": "/wAA//8AAP//AAD//wAA//8AAP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A//8AAP//AAD//wAA//8AAP//AAD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP//AAD//wAA//8AAP//AAD//wAA/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD//wAA//8AAP//AAD//wAA//8AAP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A//8AAP//AAD//wAA//8AAP//AAD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP//AAD//wAA//8AAP//AAD//wAA/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD//wAA//8AAP//AAD//wAA//8AAP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wAA//8AAP//AAD//wAA//8AAP////8A////AP///wD///8A////AP///wD///8A////AP///wD///8A////AP8AAP//AAD//wAA//8AAP//AAD/////AP///wD///8A////AP///wD///8A////AP///wD///8A////AP///wD/AAD//wAA//8AAAAAAAAAAAAA/////wD///8A////AP///wD///8A////AP///wD///8A////AP///wD///8A/wAA//8AAP//AAAAAAAAAAAAAP////8A////AP///wD///8A////AP///wD///8A////AP///wD///8A////AP8AAP//AAD//wAA//8AAP//AAD/////AP///wD///8A////AP///wD///8A////AP///wD///8A////AP///wD/AAD//wAA//8AAP//AAD//wAA/////wD///8A////AP///wD///8A////AP///wD///8A////AP///wD///8A/wAA//8AAP//AAD//wAA//8AAP////8A////AP///wD///8A////AP///wD///8A////AP///wD///8A////AP8AAP//AAD//wAA//8AAP//AAD/////AP///wD///8A////AP///wD///8A////AP///wD///8A////AP///wD/AAD//wAA//8AAP//AAD//wAA/////wD///8A////AP///wD///8A////AP///wD///8A////AP///wD///8A/w==",
        "rgbaSha256": "dd2347302c818143357848f63f654ccb3e027d8427122bd7c77f914fafca94b6",
        "pngBase64": "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAAKUlEQVR42mP4z8DwHx2TCEcNGHgDsOgHAgaiMTUNgIGBM2Dgw2CoGgAA2FNYxO0s1HMAAAAASUVORK5CYII=",
        "pngSha256": "8efc1e787055366b8bda3361703e980f7b4ed402b750781b6d15d0b69f400c2f"
    },
    "reversed": {
        "rgbaBase64": "//8A////AP///wD///8A////AP8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD/////AP///wD///8A////AP///wD/AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA/////wD///8A////AP///wD///8A/wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP////8A////AP///wD///8A////AP8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD/////AP///wD///8A////AP///wD/AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA/////wD///8A////AP///wD///8A/wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP////8A////AP///wD///8A////AP8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wD/AP8A/wD/AP8A/wD/AP8A/wD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP8A/wD/AP8A/wD/AP8A/wD/AP8A//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD/AP8A/wD/AP8AAAAAAAAAAAD/AP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA/wD/AP8A/wD/AAAAAAAAAAAA/wD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP8A/wD/AP8A/wD/AP8A/wD/AP8A//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD/AP8A/wD/AP8A/wD/AP8A/wD/AP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA/wD/AP8A/wD/AP8A/wD/AP8A/wD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP8A/wD/AP8A/wD/AP8A/wD/AP8A//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD/AP8A/wD/AP8A/wD/AP8A/wD/AP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA/w==",
        "rgbaSha256": "98fd63b9734f18faaa0309fcea81ff66d164a30abe45a4e4eb15f435b3ea4177",
        "pngBase64": "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAAK0lEQVR42mP4/5/hPzpmAFJE41EDBoMBDP8xIUm6qWcADAycAQMfBkPWAABoChkEC+ryRAAAAABJRU5ErkJggg==",
        "pngSha256": "70cf27da9e2b061f3ab067bf61462834b6c11f11da23fcec4c9788a17d564180"
    }
};
export const staticSixel = "\u001bP0;1q\"1;1;16;16#0;2;100;0;0#1;2;0;100;0#2;2;0;0;100#3;2;100;100;0#0~~~~~???????????$#1?????~~~~~~~~~~~$#2????????????????$#3????????????????-#0@@@@@???????????$#1?????@@@@@@@@@@@$#2}}}}}???????????$#3?????}}}}}}}}}}}-#0????????????????$#1????????????????$#2NNNNN???????????$#3?????NNNNNNNNNNN\u001b\\";
export const staticSixelSha256 = "12625065714710ecd364e1cb9ad7e2afe2cf73974dffa132cb6a86c9f14f64a4";
const hash = bytes => createHash('sha256').update(bytes).digest('hex');

/** Fresh bytes, validated against fixed SHA-256 pins; callers cannot mutate a later input. */
export function staticImageInput(sourceId = 'alpha', format = 'png') {
    const asset = Object.hasOwn(assets, sourceId) ? assets[sourceId] : null;
    if (!asset || !['png', 'rgba'].includes(format)) throw new Error('Unknown static image input.');
    const bytes = Buffer.from(asset[format + 'Base64'], 'base64');
    const sha256 = asset[format + 'Sha256'];
    if (hash(bytes) !== sha256) throw new Error('Static image input pin mismatch.');
    return { sourceId, format, ...staticImageSize, bytes, sha256 };
}

/** Independent spatial definition: unequal quadrant split and an alpha-zero interior hole. */
export function expectedStaticSourcePixel(sourceId, x, y) {
    if (!Object.hasOwn(assets, sourceId) || !Number.isInteger(x) || !Number.isInteger(y)
        || x < 0 || y < 0 || x >= 16 || y >= 16) throw new Error('Invalid static source coordinate.');
    if (sourceId !== 'opaque' && x >= 2 && x < 4 && y >= 9 && y < 11) return [0, 0, 0, 0];
    let index = (x < 5 ? 0 : 1) + (y < 7 ? 0 : 2);
    if (sourceId === 'reversed') index = 3 - index;
    return [...palette[index]];
}

/** Independent fixed-packet walk, not the terminal decoder or observed bitmap dimensions. */
export function staticSixelCanvas() {
    const header = '\x1bP0;1q"1;1;16;16#0;2;100;0;0#1;2;0;100;0#2;2;0;0;100#3;2;100;100;0';
    if (hash(Buffer.from(staticSixel)) !== staticSixelSha256 || !staticSixel.startsWith(header)) throw new Error('Sixel source pin mismatch.');
    const { pixels, width, height } = walkFixedSixel(staticSixel.slice(header.length, -2));
    if (width !== 16 || height !== 18 || pixels.size !== 256) throw new Error('Unexpected fixed Sixel canvas.');
    verifyFixedSixelPaint(pixels);
    return { declaredWidth: 16, declaredHeight: 16, width, height, paintedWidth: 16, paintedHeight: 16 };
}
function walkFixedSixel(data) {
    const pixels = new Map(); let x = 0, y = 0, width = 0, height = 0, color = 0;
    for (let at = 0; at < data.length; at++) {
        const code = data.codePointAt(at);
        if (code === 35) { color = Number(data[++at]); continue; }
        if (code === 36) { x = 0; continue; }
        if (code === 45) { x = 0; y += 6; continue; }
        if (code < 63 || code > 126 || !Number.isInteger(color) || color < 0 || color > 3) throw new Error('Unknown fixed Sixel data.');
        width = Math.max(width, x + 1); height = Math.max(height, y + 6);
        paintFixedSixel(pixels, code - 63, x++, y, color);
    }
    return { pixels, width, height };
}
function paintFixedSixel(pixels, bits, x, y, color) {
    for (let bit = 0; bit < 6; bit++) if (bits & (1 << bit)) pixels.set(x + ',' + (y + bit), palette[color]);
}
function verifyFixedSixelPaint(pixels) {
    for (let row = 0; row < 16; row++) for (let col = 0; col < 16; col++)
        if (!pixels.get(col + ',' + row)?.every((v, i) => v === expectedStaticSourcePixel('opaque', col, row)[i]))
            throw new Error('Sixel painted source mismatch.');
}
