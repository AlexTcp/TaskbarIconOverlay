import { adjustLightness, normalizeColor } from './color';

// Mirrors how Peacock (johnpapa/vscode-peacock) decides which colour a window shows.
//
// Peacock writes its colours into workspace-scoped `workbench.colorCustomizations`
// (activityBar.background, statusBar.background, titleBar.activeBackground, ...). Those
// customizations are what the user actually sees, so they win when present. The configured
// setting is used when nothing has been applied (for example when every affected element
// was excluded, or Peacock has not yet run in this window).
//
// Setting precedence, from Peacock's src/remote/integration.ts and read-configuration.ts:
//   remote window:  peacock.remoteColor || peacock.color
//   local window:   peacock.color

export interface PeacockInputs {
  /** vscode.env.remoteName, e.g. "ssh-remote"; undefined for local windows. */
  remoteName: string | undefined;
  /** Effective `peacock.color` (all scopes, as Peacock reads it). */
  color: unknown;
  /** Effective `peacock.remoteColor`. */
  remoteColor: unknown;
  /** Workspace-scoped `workbench.colorCustomizations` (inspect().workspaceValue). */
  workspaceColorCustomizations: unknown;
  /** Effective `peacock.elementAdjustments`, e.g. { activityBar: "lighten" }. */
  elementAdjustments: unknown;
  /** True when the Peacock extension is installed in this window. */
  peacockInstalled: boolean;
}

export type ColorSource =
  | 'workbench.colorCustomizations'
  | 'peacock.remoteColor'
  | 'peacock.color';

export interface ResolvedColor {
  /** Normalized "#rrggbb", or null when Peacock shows no colour in this window. */
  color: string | null;
  source: ColorSource | null;
  /** Human-readable reason when color is null, for diagnostics. */
  reason?: string;
}

// Order matters: Peacock's getColorAndAdjustment() checks activity bar, then status bar,
// then title bar.
const ELEMENT_KEYS: Array<[element: string, key: string]> = [
  ['activityBar', 'activityBar.background'],
  ['statusBar', 'statusBar.background'],
  ['titleBar', 'titleBar.activeBackground'],
];

export function resolvePeacockColor(input: PeacockInputs): ResolvedColor {
  const setting = resolveSetting(input);
  const applied = resolveApplied(input);

  // Only trust colour customizations when Peacock manages this window, so that a user's own
  // hand-written status bar colour is not mistaken for a Peacock colour.
  if (applied && (setting.color || input.peacockInstalled)) {
    return { color: applied, source: 'workbench.colorCustomizations' };
  }
  if (setting.color) {
    return setting;
  }
  return {
    color: null,
    source: null,
    reason: setting.reason ?? 'No Peacock colour is set for this workspace.',
  };
}

export function resolveSetting(input: PeacockInputs): ResolvedColor {
  if (input.remoteName) {
    const remote = normalizeColor(input.remoteColor);
    if (remote) return { color: remote, source: 'peacock.remoteColor' };
  }
  const local = normalizeColor(input.color);
  if (local) return { color: local, source: 'peacock.color' };

  const raw = input.remoteName && isNonEmpty(input.remoteColor) ? input.remoteColor : input.color;
  if (isNonEmpty(raw)) {
    return { color: null, source: null, reason: `Peacock colour "${String(raw)}" is not a valid colour.` };
  }
  return { color: null, source: null };
}

/** Recovers Peacock's base colour from the colours it applied, undoing element adjustments. */
export function resolveApplied(input: PeacockInputs): string | null {
  const customizations = input.workspaceColorCustomizations;
  if (!customizations || typeof customizations !== 'object') return null;
  const values = customizations as Record<string, unknown>;

  const adjustments =
    input.elementAdjustments && typeof input.elementAdjustments === 'object'
      ? (input.elementAdjustments as Record<string, unknown>)
      : {};
  // Peacock's getAdjustedColorHex() always uses its fixed default of 10%.
  const amount = 10;

  for (const [element, key] of ELEMENT_KEYS) {
    const color = normalizeColor(values[key]);
    if (!color) continue;
    switch (adjustments[element]) {
      case 'lighten':
        return adjustLightness(color, -amount);
      case 'darken':
        return adjustLightness(color, amount);
      default:
        return color;
    }
  }
  return null;
}

function isNonEmpty(value: unknown): boolean {
  return typeof value === 'string' && value.trim().length > 0;
}
