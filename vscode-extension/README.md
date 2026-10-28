# Taskbar Icon Overlay – Peacock Bridge

Companion extension for [TaskbarIconOverlay](https://github.com/AlexTcp/TaskbarIconOverlay).
It reports each VS Code window's effective [Peacock](https://github.com/johnpapa/vscode-peacock)
colour to TaskbarIconOverlay, which recolours that window's Windows taskbar icon.

- Runs only in the local (UI) extension host, so it works the same for local folders,
  Remote SSH, WSL, Dev Containers and multi-root workspaces. Nothing is installed remotely.
- Reads colours through the VS Code configuration API, using Peacock's own precedence
  (`peacock.remoteColor` → `peacock.color` in remote windows, and the colours Peacock
  actually applied in `workbench.colorCustomizations`).
- Writes one small JSON file per window to `%LOCALAPPDATA%\TaskbarIconOverlay\sessions`.
  The file holds the workspace name shown in the title bar, an opaque hash of the workspace
  URI, the colour, and process IDs. No paths, file contents or credentials.

Settings: `taskbarIconOverlay.enabled` (default `true`).

Command: **Taskbar Icon Overlay: Show Peacock Taskbar Status** prints what this window reports.

See the main README for installation and troubleshooting.
