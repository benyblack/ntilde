import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { deriveSixelNativePublication } from './sixel-native-publication.mjs';

test('the fresh SIXEL raster claim rederives from a complete owned receipt and the unchanged calibrated oracle', async () => {
    const result = await deriveSixelNativePublication();
    for (const [name, value] of Object.entries(result)) {
        const file = new URL(`../../docs/qa/terminfo/2026-11-26-sixel-native-fidelity/${name}.json`, import.meta.url);
        assert.deepEqual(JSON.parse(readFileSync(file)), value);
    }
});
