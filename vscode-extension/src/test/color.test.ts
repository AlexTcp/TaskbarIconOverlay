import { test } from 'node:test';
import * as assert from 'node:assert/strict';
import { adjustLightness, normalizeColor } from '../color';

test('normalizes hex formats', () => {
  assert.equal(normalizeColor('#FF0000'), '#ff0000');
  assert.equal(normalizeColor('#f00'), '#ff0000');
  assert.equal(normalizeColor('f00'), '#ff0000');
  assert.equal(normalizeColor('ff0000'), '#ff0000');
  assert.equal(normalizeColor('#f00f'), '#ff0000');
  assert.equal(normalizeColor('#42b883ff'), '#42b883');
  assert.equal(normalizeColor('  #42B883  '), '#42b883');
});

test('normalizes named colours, case-insensitive', () => {
  assert.equal(normalizeColor('red'), '#ff0000');
  assert.equal(normalizeColor('RebeccaPurple'), '#663399');
  assert.equal(normalizeColor('green'), '#008000');
});

test('normalizes rgb/hsl/hsv functions', () => {
  assert.equal(normalizeColor('rgb(255, 0, 0)'), '#ff0000');
  assert.equal(normalizeColor('rgba(0,128,0,0.5)'), '#008000');
  assert.equal(normalizeColor('rgb(100%, 0%, 0%)'), '#ff0000');
  assert.equal(normalizeColor('hsl(120, 100%, 25%)'), '#008000');
  assert.equal(normalizeColor('hsl(0, 0%, 100%)'), '#ffffff');
  assert.equal(normalizeColor('hsv(0, 100%, 100%)'), '#ff0000');
});

test('rejects invalid and transparent colours', () => {
  for (const bad of ['', '   ', 'notacolor', '#12', '#12345', 'rgb(1,2)', 'transparent', '#00000000', 'rgba(1,2,3,0)', undefined, null, 42, {}]) {
    assert.equal(normalizeColor(bad), null, `expected null for ${JSON.stringify(bad)}`);
  }
});

test('adjustLightness matches tinycolor lighten/darken(10)', () => {
  // tinycolor('#007acc').lighten(10) === '#0098ff'; darken back restores the input.
  assert.equal(adjustLightness('#007acc', 10), '#0098ff');
  assert.equal(adjustLightness('#0098ff', -10), '#007acc');
  assert.equal(adjustLightness('#ffffff', 10), '#ffffff');
  assert.equal(adjustLightness('#000000', -10), '#000000');
});
