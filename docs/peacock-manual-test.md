# Peacock integration – manual test procedure

These steps cover Windows behaviour that automated tests can't: the real taskbar, Explorer,
VS Code window lifecycles and Remote SSH. Use Windows 11 with ungrouped taskbar buttons
(e.g. Windhawk "Disable grouping on the taskbar"), VS Code with Remote SSH and Peacock, and
at least two SSH hosts. Below they're called `hostA` and `hostB`.

Keep **Peacock Status...** (tray menu) open, or watch
`%LOCALAPPDATA%\TaskbarIconOverlay\peacock-status.txt`, to see what the app decided at each step.

## 0. Setup

1. Build and install the extension: `cd vscode-extension && npm install && npm run package`,
   then `code --install-extension taskbar-icon-overlay-peacock-1.0.0.vsix`.
2. Build the app: `dotnet build`, then run `bin\Debug\net8.0-windows\win-x64\TaskbarIconOverlay.exe`.
   Peacock integration is on by default (tray → **Peacock Colours (VS Code)** is checked).

## 1. Three Remote SSH workspaces with different colours

1. Open three Remote SSH windows at the same time:
   * W1: `hostA:/srv/api`
   * W2: `hostB:/srv/api` (same folder name, different host)
   * W3: `hostA:/srv/web`
2. In each, run **Peacock: Change to a Favorite Color**: W1 red, W2 green, W3 purple.

**Expected:** within about a second of each change, that window's taskbar button shows the
VS Code logo in red, green or purple. The other buttons don't change. Peacock Status lists
`api [SSH: hostA]`, `api [SSH: hostB]` and `web [SSH: hostA]`, each "matched by title"
with its own window handle.

## 2. Changing one colour leaves the others alone

1. In W2 choose a different colour (e.g. **Peacock: Change to a Random Color**).
2. In W1 edit `.vscode/settings.json` directly and change `peacock.remoteColor`
   (or `peacock.color`) to `"#1e90ff"`, then save.

**Expected:** only W2's icon changes after step 1, and only W1's after step 2. W3 stays purple.

## 3. Remote colour precedence

1. In W3's workspace settings set `"peacock.color": "orange"` and `"peacock.remoteColor": "purple"`.

**Expected:** W3 stays purple (`remoteColor` wins in remote windows). Remove `peacock.remoteColor`
and reload the window (*Developer: Reload Window*). W3 turns orange.

## 4. Editing changes the title

1. In W1 open several files, type to make one dirty (title gets `●`), and switch tabs.

**Expected:** W1 stays red and nothing flickers. The status keeps "matched by title".

## 5. Identical titles

1. Open a fourth window W4: `hostA:/other/api` (title `api [SSH: hostA]`, same as W1). Give it a
   yellow colour.

**Expected:** W1 and W4 both show "AMBIGUOUS" in the status. Neither gets a new colour. W1 may
keep red briefly, then reverts to the blue logo after a few seconds. Nothing gets a wrong colour.

2. Alt+Tab to W4, wait a second, then Alt+Tab to W1.

**Expected:** W4 turns yellow ("identified by focus") and W1 turns red again.

## 6. Reload, reconnect, close

1. In W2 run *Developer: Reload Window*. **Expected:** the icon keeps its colour (at most a brief
   flicker).
2. Disconnect W2's network or kill its SSH session, then let Remote SSH reconnect.
   **Expected:** the colour stays. The extension runs locally, so nothing is lost.
3. Close W3. **Expected:** its session file disappears from
   `%LOCALAPPDATA%\TaskbarIconOverlay\sessions` and the status no longer lists it.

## 7. Restart VS Code, Explorer and TaskbarIconOverlay

1. Quit VS Code completely, then reopen the same windows. **Expected:** colours return as each
   window's extension activates.
2. Restart Explorer (Task Manager → Windows Explorer → Restart). **Expected:** after the taskbar
   reappears, coloured icons (and badges in overlay mode) are restored within a few seconds.
3. Exit TaskbarIconOverlay from the tray. **Expected:** every VS Code button returns to the blue
   logo straight away. Start it again: colours return within a second.
4. Kill TaskbarIconOverlay from Task Manager (abnormal exit), then start it again.
   **Expected:** after the restart all colours are correct. Between kill and restart, coloured
   buttons may show a blank or default icon. This is a documented limitation.

## 8. Toggles and styles

1. Tray → uncheck **Peacock Colours (VS Code)**. **Expected:** all VS Code icons go back to blue.
   Check it again: colours return.
2. Put `{ "PeacockStyle": "Overlay" }` in `config.json` next to the exe and restart the app.
   **Expected:** the VS Code logos stay blue and each window gets a round badge in its colour
   with a contrasting ring.
3. In one workspace set `"taskbarIconOverlay.enabled": false`. **Expected:** that window loses
   its colour, and the others keep theirs.

## 9. Non-Peacock users

1. Remove all `peacock.*` settings from a workspace (Peacock: *Reset Workspace Colors*).
   **Expected:** that window returns to the normal logo. Status says "no colour".
2. Uninstall the extension and keep the app running. **Expected:** the status says no windows are
   reporting. Existing `icons/` overlays (if any) still work as before.
