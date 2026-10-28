import { test } from 'node:test';
import * as assert from 'node:assert/strict';
import { PeacockInputs, resolvePeacockColor } from '../resolve';

function inputs(overrides: Partial<PeacockInputs>): PeacockInputs {
  return {
    remoteName: undefined,
    color: undefined,
    remoteColor: undefined,
    workspaceColorCustomizations: undefined,
    elementAdjustments: undefined,
    peacockInstalled: true,
    ...overrides,
  };
}

test('local window uses peacock.color and ignores remoteColor', () => {
  const r = resolvePeacockColor(inputs({ color: '#ff0000', remoteColor: '#00ff00' }));
  assert.deepEqual(r, { color: '#ff0000', source: 'peacock.color' });
});

test('remote window prefers peacock.remoteColor', () => {
  const r = resolvePeacockColor(inputs({ remoteName: 'ssh-remote', color: '#ff0000', remoteColor: 'green' }));
  assert.deepEqual(r, { color: '#008000', source: 'peacock.remoteColor' });
});

test('remote window falls back to peacock.color when remoteColor is unset or empty', () => {
  for (const remoteColor of [undefined, '']) {
    const r = resolvePeacockColor(inputs({ remoteName: 'ssh-remote', color: '#ff0000', remoteColor }));
    assert.deepEqual(r, { color: '#ff0000', source: 'peacock.color' });
  }
});

test('applied colour customizations win because that is what is displayed', () => {
  const r = resolvePeacockColor(
    inputs({ color: '#ff0000', workspaceColorCustomizations: { 'activityBar.background': '#00ff00' } }),
  );
  assert.deepEqual(r, { color: '#00ff00', source: 'workbench.colorCustomizations' });
});

test('applied colour follows Peacock element order: activity bar, status bar, title bar', () => {
  const both = { 'titleBar.activeBackground': '#0000ff', 'statusBar.background': '#00ff00' };
  assert.equal(resolvePeacockColor(inputs({ color: '#f00', workspaceColorCustomizations: both })).color, '#00ff00');
  const title = { 'titleBar.activeBackground': '#0000ff' };
  assert.equal(resolvePeacockColor(inputs({ color: '#f00', workspaceColorCustomizations: title })).color, '#0000ff');
});

test('element adjustments are reversed to recover the base colour', () => {
  const r = resolvePeacockColor(
    inputs({
      color: '#007acc',
      workspaceColorCustomizations: { 'activityBar.background': '#0098ff' },
      elementAdjustments: { activityBar: 'lighten' },
    }),
  );
  assert.equal(r.color, '#007acc');
});

test('customizations without Peacock installed or configured are not treated as Peacock colours', () => {
  const r = resolvePeacockColor(
    inputs({ peacockInstalled: false, workspaceColorCustomizations: { 'statusBar.background': '#123456' } }),
  );
  assert.equal(r.color, null);
});

test('customizations are used when Peacock is installed even without a colour setting', () => {
  const r = resolvePeacockColor(inputs({ workspaceColorCustomizations: { 'statusBar.background': '#123456' } }));
  assert.equal(r.color, '#123456');
});

test('no colour yields null with a reason; invalid colour explains itself', () => {
  assert.equal(resolvePeacockColor(inputs({})).color, null);
  const bad = resolvePeacockColor(inputs({ color: 'not-a-colour' }));
  assert.equal(bad.color, null);
  assert.match(bad.reason ?? '', /not a valid colour/);
});

test('ignores non-colour customization values and theme-scoped blocks', () => {
  const r = resolvePeacockColor(
    inputs({
      color: '#ff0000',
      workspaceColorCustomizations: { '[Default Dark+]': { 'activityBar.background': '#00ff00' }, 'activityBar.background': 42 },
    }),
  );
  assert.deepEqual(r, { color: '#ff0000', source: 'peacock.color' });
});
