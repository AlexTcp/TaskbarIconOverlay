import { createHash } from 'crypto';
import { ResolvedColor } from './resolve';

/** Version of the JSON contract shared with TaskbarIconOverlay (PeacockSession.cs). */
export const SCHEMA_VERSION = 1;

/** How the window title names the workspace, derived from the `window.title` template. */
export type TitleNameMode = 'rootName' | 'rootNameShort' | 'none';

/**
 * One file per VS Code window. Contains only what is needed to colour the taskbar button:
 * no file contents, no paths in clear text, no credentials. The workspace name and host
 * label are already visible in the window title.
 */
export interface SessionReport {
  schemaVersion: number;
  sessionId: string;
  /** PID of this window's local extension host. Its parent is the Electron main process. */
  pid: number;
  ppid: number;
  /** Extension host start time, used by the Windows side to detect PID reuse. */
  processStartMs: number;
  appName: string;
  /** Exactly VS Code's ${rootName} title variable (includes " [SSH: host]" for remotes). */
  workspaceName: string | null;
  workspaceKind: 'folder' | 'multi-root' | 'none';
  /** Opaque hash of the workspace URIs: distinguishes same-named folders on different hosts. */
  workspaceId: string | null;
  remoteName: string | null;
  remoteHost: string | null;
  titleNameMode: TitleNameMode;
  titleSeparator: string;
  color: string | null;
  colorSource: string | null;
  colorReason: string | null;
  focused: boolean;
  focusedAtMs: number;
  updatedAtMs: number;
}

export interface WorkspaceInfo {
  name: string | undefined;
  /** URIs as strings: the .code-workspace file, or the folder(s). */
  workspaceFile: string | undefined;
  folders: string[];
  remoteName: string | undefined;
  /** URI authority of the first remote folder/workspace file, e.g. "ssh-remote+myhost". */
  remoteAuthority: string | undefined;
}

export function workspaceKind(info: WorkspaceInfo): SessionReport['workspaceKind'] {
  if (info.workspaceFile) return 'multi-root';
  if (info.folders.length > 0) return 'folder';
  return 'none';
}

/** Stable, opaque identity for a workspace. Includes the remote authority via the URIs. */
export function workspaceId(info: WorkspaceInfo): string | null {
  const parts = info.workspaceFile ? [info.workspaceFile] : [...info.folders].sort();
  if (parts.length === 0) return null;
  return createHash('sha256').update(parts.join('\n')).digest('hex').slice(0, 16);
}

/**
 * Extracts the SSH host from a remote authority. Remote SSH uses either
 * "ssh-remote+myhost" or "ssh-remote+<hex of {"hostName":"myhost",...}>".
 */
export function remoteHostFromAuthority(authority: string | undefined): string | null {
  if (!authority) return null;
  const plus = authority.indexOf('+');
  if (plus < 0) return null;
  const encoded = authority.slice(plus + 1);
  if (!encoded) return null;
  if (/^[0-9a-f]+$/i.test(encoded) && encoded.length % 2 === 0) {
    try {
      const json = JSON.parse(Buffer.from(encoded, 'hex').toString('utf8'));
      if (json && typeof json.hostName === 'string') return json.hostName;
    } catch {
      // Not hex-encoded JSON; a plain host name that happens to be hex (e.g. "cafe").
    }
  }
  try {
    return decodeURIComponent(encoded);
  } catch {
    return encoded;
  }
}

export function titleNameMode(titleTemplate: unknown): TitleNameMode {
  if (typeof titleTemplate !== 'string') return 'rootName';
  if (titleTemplate.includes('${rootName}')) return 'rootName';
  if (titleTemplate.includes('${rootNameShort}')) return 'rootNameShort';
  return 'none';
}

export interface ReportInputs {
  sessionId: string;
  pid: number;
  ppid: number;
  processStartMs: number;
  appName: string;
  workspace: WorkspaceInfo;
  titleTemplate: unknown;
  titleSeparator: unknown;
  resolved: ResolvedColor;
  focused: boolean;
  focusedAtMs: number;
  nowMs: number;
}

export function buildReport(i: ReportInputs): SessionReport {
  const kind = workspaceKind(i.workspace);
  return {
    schemaVersion: SCHEMA_VERSION,
    sessionId: i.sessionId,
    pid: i.pid,
    ppid: i.ppid,
    processStartMs: i.processStartMs,
    appName: i.appName,
    workspaceName: kind === 'none' ? null : i.workspace.name ?? null,
    workspaceKind: kind,
    workspaceId: workspaceId(i.workspace),
    remoteName: i.workspace.remoteName ?? null,
    remoteHost: remoteHostFromAuthority(i.workspace.remoteAuthority),
    titleNameMode: titleNameMode(i.titleTemplate),
    titleSeparator: typeof i.titleSeparator === 'string' && i.titleSeparator ? i.titleSeparator : ' - ',
    color: kind === 'none' ? null : i.resolved.color,
    colorSource: kind === 'none' ? null : i.resolved.source,
    colorReason: kind === 'none' ? 'No folder or workspace is open.' : i.resolved.reason ?? null,
    focused: i.focused,
    focusedAtMs: i.focusedAtMs,
    updatedAtMs: i.nowMs,
  };
}

/** True when two reports differ in anything other than their write timestamp. */
export function reportChanged(a: SessionReport | undefined, b: SessionReport): boolean {
  if (!a) return true;
  const strip = (r: SessionReport) => JSON.stringify({ ...r, updatedAtMs: 0 });
  return strip(a) !== strip(b);
}
