import * as vscode from 'vscode';
import { randomBytes } from 'crypto';
import { resolvePeacockColor } from './resolve';
import { buildReport, reportChanged, SessionReport, WorkspaceInfo } from './report';
import {
  defaultStateDirectory,
  removeStaleSessions,
  sessionFilePath,
  tryDelete,
  writeAtomic,
} from './stateFile';
import * as fs from 'fs';

const PEACOCK_ID = 'johnpapa.vscode-peacock';
const WATCHED_SETTINGS = [
  'peacock',
  'workbench.colorCustomizations',
  'window.title',
  'window.titleSeparator',
  'taskbarIconOverlay',
];

let state: Bridge | undefined;

export function activate(context: vscode.ExtensionContext): void {
  const output = vscode.window.createOutputChannel('Taskbar Icon Overlay');
  context.subscriptions.push(output);

  const directory = defaultStateDirectory();
  if (process.platform !== 'win32' || !directory) {
    output.appendLine('Taskbar Icon Overlay bridge only runs on Windows; nothing to do.');
    return;
  }

  state = new Bridge(directory, output);
  context.subscriptions.push(state);
  context.subscriptions.push(
    vscode.commands.registerCommand('taskbarIconOverlay.showStatus', () => state?.showStatus()),
  );
  state.start(context);
}

export function deactivate(): void {
  state?.dispose();
  state = undefined;
}

class Bridge implements vscode.Disposable {
  private readonly sessionId = `${process.pid}-${randomBytes(4).toString('hex')}`;
  private readonly file: string;
  private readonly processStartMs = Math.round(Date.now() - process.uptime() * 1000);
  private last: SessionReport | undefined;
  private focusedAtMs = 0;
  private debounce: NodeJS.Timeout | undefined;
  private keepAlive: NodeJS.Timeout | undefined;
  private retry: NodeJS.Timeout | undefined;
  private disposed = false;
  private readonly onExit = () => tryDelete(this.file);

  constructor(private readonly directory: string, private readonly output: vscode.OutputChannel) {
    this.file = sessionFilePath(directory, this.sessionId);
  }

  start(context: vscode.ExtensionContext): void {
    const removed = removeStaleSessions(this.directory);
    if (removed.length) this.log(`Removed ${removed.length} stale session file(s).`);

    if (vscode.window.state.focused) this.focusedAtMs = Date.now();

    context.subscriptions.push(
      vscode.workspace.onDidChangeConfiguration(e => {
        if (WATCHED_SETTINGS.some(s => e.affectsConfiguration(s))) this.scheduleUpdate();
      }),
      vscode.workspace.onDidChangeWorkspaceFolders(() => this.scheduleUpdate()),
      vscode.extensions.onDidChange(() => this.scheduleUpdate()),
      vscode.window.onDidChangeWindowState(e => {
        if (e.focused && !this.last?.focused) this.focusedAtMs = Date.now();
        // Focus changes are written immediately: TaskbarIconOverlay correlates their
        // timestamps with the Windows foreground window to identify this window.
        this.update();
      }),
    );

    // Recreate the file if something removed it (e.g. a mistaken stale-file cleanup).
    this.keepAlive = setInterval(() => {
      if (!fs.existsSync(this.file)) {
        this.last = undefined;
        this.update();
      }
    }, 60_000);

    process.once('exit', this.onExit);
    this.update();
  }

  private scheduleUpdate(): void {
    // Peacock rewrites several settings in quick succession when the colour changes.
    if (this.debounce) clearTimeout(this.debounce);
    this.debounce = setTimeout(() => this.update(), 200);
  }

  private update(): void {
    if (this.disposed) return;
    if (this.debounce) {
      clearTimeout(this.debounce);
      this.debounce = undefined;
    }

    if (!vscode.workspace.getConfiguration('taskbarIconOverlay').get<boolean>('enabled', true)) {
      if (this.last) this.log('Disabled by taskbarIconOverlay.enabled; removing session file.');
      tryDelete(this.file);
      this.last = undefined;
      return;
    }

    const report = this.buildReport();
    if (!reportChanged(this.last, report)) return;
    try {
      writeAtomic(this.file, JSON.stringify(report, null, 2));
      if (this.last?.color !== report.color || this.last?.workspaceName !== report.workspaceName) {
        this.log(
          `Reported "${report.workspaceName ?? '(no workspace)'}" colour ${report.color ?? 'none'}` +
            (report.colorSource ? ` from ${report.colorSource}` : '') +
            (report.colorReason ? ` (${report.colorReason})` : ''),
        );
      }
      this.last = report;
    } catch (err) {
      this.log(`Failed to write ${this.file}: ${err}; retrying.`);
      if (this.retry) clearTimeout(this.retry);
      this.retry = setTimeout(() => this.update(), 1000);
    }
  }

  private buildReport(): SessionReport {
    const peacock = vscode.workspace.getConfiguration('peacock');
    const customizations = vscode.workspace.getConfiguration().inspect('workbench.colorCustomizations');
    const windowConfig = vscode.workspace.getConfiguration('window');

    const resolved = resolvePeacockColor({
      remoteName: vscode.env.remoteName,
      color: peacock.get('color'),
      remoteColor: peacock.get('remoteColor'),
      workspaceColorCustomizations: customizations?.workspaceValue ?? customizations?.workspaceFolderValue,
      elementAdjustments: peacock.get('elementAdjustments'),
      peacockInstalled: vscode.extensions.getExtension(PEACOCK_ID) !== undefined,
    });

    return buildReport({
      sessionId: this.sessionId,
      pid: process.pid,
      ppid: process.ppid,
      processStartMs: this.processStartMs,
      appName: vscode.env.appName,
      workspace: currentWorkspace(),
      titleTemplate: windowConfig.get('title'),
      titleSeparator: windowConfig.get('titleSeparator'),
      resolved,
      focused: vscode.window.state.focused,
      focusedAtMs: this.focusedAtMs,
      nowMs: Date.now(),
    });
  }

  showStatus(): void {
    const report = this.last ?? this.buildReport();
    this.output.appendLine('');
    this.output.appendLine(`Session file: ${this.file}`);
    this.output.appendLine(JSON.stringify(report, null, 2));
    this.output.appendLine(
      'TaskbarIconOverlay matches this window by "workspaceName" in the window title, ' +
        'or, when several windows share a title, by the window you focus next.',
    );
    this.output.show(true);
  }

  private log(message: string): void {
    this.output.appendLine(`[${new Date().toLocaleTimeString()}] ${message}`);
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    if (this.debounce) clearTimeout(this.debounce);
    if (this.keepAlive) clearInterval(this.keepAlive);
    if (this.retry) clearTimeout(this.retry);
    process.removeListener('exit', this.onExit);
    tryDelete(this.file);
  }
}

function currentWorkspace(): WorkspaceInfo {
  const workspaceFile = vscode.workspace.workspaceFile;
  const folders = vscode.workspace.workspaceFolders ?? [];
  const primary = workspaceFile ?? folders[0]?.uri;
  return {
    name: vscode.workspace.name,
    workspaceFile: workspaceFile?.toString(),
    folders: folders.map(f => f.uri.toString()),
    remoteName: vscode.env.remoteName,
    remoteAuthority: primary?.scheme === 'vscode-remote' ? primary.authority : undefined,
  };
}
