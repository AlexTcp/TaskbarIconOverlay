using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Win32;
using TaskbarIconOverlay.Peacock;

class Program
{
    static string ConfigFilePath => Path.Combine(AppContext.BaseDirectory, "config.json");

    static System.Windows.Forms.Timer? _pollTimer;

    // Process names to ignore (system/shell windows that don't need overlays)
    static readonly string[] IgnoredProcessNames = {
        "explorer",
        "SystemSettings",
        "ApplicationFrameHost",
        "TextInputHost",
        "ShellExperienceHost",
        "SearchHost",
        "StartMenuExperienceHost",
        "LockApp"
    };

    static NotifyIcon? _trayIcon;

    static PeacockManager? _peacock;

    static bool IsIgnoredProcess(string processName)
    {
        foreach (var ignored in IgnoredProcessNames)
        {
            if (processName.Equals(ignored, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    [STAThread]
    static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Create system tray icon
        _trayIcon = new NotifyIcon
        {
            Text = "TaskbarIconOverlay",
            Visible = true
        };

        // Try to load the app icon, fall back to a default
        string appIconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(appIconPath))
        {
            _trayIcon.Icon = new Icon(appIconPath);
        }
        else
        {
            _trayIcon.Icon = SystemIcons.Application;
        }

        // Create context menu
        var contextMenu = new ContextMenuStrip();
        
        var applyItem = new ToolStripMenuItem("Apply Overlays");
        applyItem.Click += (s, e) => ApplyOverlaysWithNotification();
        contextMenu.Items.Add(applyItem);

        var removeItem = new ToolStripMenuItem("Remove Overlays");
        removeItem.Click += (s, e) => RemoveOverlays();
        contextMenu.Items.Add(removeItem);

        var autoReapplyItem = new ToolStripMenuItem("Auto-Reapply");
        autoReapplyItem.Checked = true;
        autoReapplyItem.Click += (s, e) =>
        {
            var item = (ToolStripMenuItem)s!;
            item.Checked = !item.Checked;
            if (item.Checked)
                StartPolling();
            else
                StopPolling();
        };
        contextMenu.Items.Add(autoReapplyItem);

        var startupItem = new ToolStripMenuItem("Start with Windows");
        startupItem.Checked = IsStartupEnabled();
        startupItem.Click += (s, e) =>
        {
            var item = (ToolStripMenuItem)s!;
            item.Checked = !item.Checked;
            SetStartupEnabled(item.Checked);
        };
        contextMenu.Items.Add(startupItem);

        contextMenu.Items.Add(new ToolStripSeparator());

        var config = LoadConfig();

        var peacockItem = new ToolStripMenuItem("Peacock Colours (VS Code)");
        peacockItem.Checked = config.PeacockEnabled;
        peacockItem.Click += (s, e) =>
        {
            var item = (ToolStripMenuItem)s!;
            item.Checked = !item.Checked;
            if (item.Checked)
                StartPeacock(config.PeacockStyle);
            else
                StopPeacock();
        };
        contextMenu.Items.Add(peacockItem);

        var peacockStatusItem = new ToolStripMenuItem("Peacock Status...");
        peacockStatusItem.Click += (s, e) => ShowPeacockStatus();
        contextMenu.Items.Add(peacockStatusItem);

        contextMenu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (s, e) =>
        {
            StopPolling();
            StopPeacock();
            _trayIcon.Visible = false;
            Application.Exit();
        };
        contextMenu.Items.Add(exitItem);

        _trayIcon.ContextMenuStrip = contextMenu;

        // Double-click to apply overlays
        _trayIcon.DoubleClick += (s, e) => ApplyOverlaysWithNotification();

        // Colour VS Code windows from their Peacock colours (needs the companion extension)
        if (config.PeacockEnabled)
            StartPeacock(config.PeacockStyle);

        // Restore VS Code icons however the message loop ends; our icon handles die with us
        Application.ApplicationExit += (s, e) => StopPeacock();

        // Apply overlays on startup
        ApplyOverlaysWithNotification();

        // Start polling to keep overlays applied
        StartPolling();

        // Run the application
        Application.Run();
    }

    static void ApplyOverlaysWithNotification()
    {
        var appliedIcons = ApplyOverlays();
        if (appliedIcons.Count > 0)
        {
            _trayIcon?.ShowBalloonTip(
                2000,
                "TaskbarIconOverlay",
                $"Applied {appliedIcons.Count} overlay(s): {string.Join(", ", appliedIcons)}",
                ToolTipIcon.Info);
        }
        else
        {
            _trayIcon?.ShowBalloonTip(
                2000,
                "TaskbarIconOverlay",
                "No new overlays to apply.",
                ToolTipIcon.None);
        }
    }

    static List<string> ApplyOverlays()
    {
        // Load configuration or use defaults
        var config = LoadConfig();
        string iconsDir = config.IconsPath;

        var appliedIcons = new List<string>();

        var taskbarObj = new CTaskbarList();
        var taskbar = (ITaskbarList3)taskbarObj;
        taskbar.HrInit();

        try
        {
        EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd)) return true;

            // Grab window title
            string title = GetTitle(hWnd);
            if (string.IsNullOrWhiteSpace(title))
                return true;

            string processName = GetProcessName(hWnd);

            // Skip ignored system processes for efficiency
            if (IsIgnoredProcess(processName))
                return true;

            // The Peacock integration owns this window's overlay
            if (_peacock?.OwnsOverlay(hWnd) == true)
                return true;

            // Determine the icon name to use
            string? iconName = null;
            string? subFolder = null;

            // Special handling for VS Code / VSCodium: extract workspace name
            // and use a process-name-based subfolder (e.g., icons/Code/, icons/VSCodium/)
            if (IsCodeFamilyWindow(hWnd))
            {
                iconName = ExtractWorkspaceName(title);
                subFolder = processName;
            }

            // Fallback: use the process name as icon name (in root icons folder)
            if (string.IsNullOrWhiteSpace(iconName))
            {
                iconName = SanitizeFileStem(processName);
                subFolder = null;
            }

            // Try to find an icon file
            string icoPath = subFolder != null
                ? Path.Combine(iconsDir, subFolder, iconName + ".ico")
                : Path.Combine(iconsDir, iconName + ".ico");

            if (!File.Exists(icoPath))
            {
                // No icon found for this window, skip it
                return true;
            }

            IntPtr hIcon = LoadImage(IntPtr.Zero, icoPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE);
            if (hIcon == IntPtr.Zero)
            {
                // Can't load the icon (bad .ico, permissions, etc.)
                return true;
            }

            taskbar.SetOverlayIcon(hWnd, hIcon, iconName);
            DestroyIcon(hIcon);
            appliedIcons.Add(iconName);

            return true;
        }, IntPtr.Zero);
        }
        finally
        {
            Marshal.ReleaseComObject(taskbarObj);
        }

        return appliedIcons;
    }

    static void RemoveOverlays()
    {
        var taskbarObj = new CTaskbarList();
        var taskbar = (ITaskbarList3)taskbarObj;
        taskbar.HrInit();

        try
        {
        EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd)) return true;

            string title = GetTitle(hWnd);
            if (string.IsNullOrWhiteSpace(title)) return true;

            string processName = GetProcessName(hWnd);
            if (IsIgnoredProcess(processName)) return true;

            taskbar.SetOverlayIcon(hWnd, IntPtr.Zero, null);
            return true;
        }, IntPtr.Zero);
        }
        finally
        {
            Marshal.ReleaseComObject(taskbarObj);
        }

        _peacock?.InvalidateOverlays();

        _trayIcon?.ShowBalloonTip(
            2000,
            "TaskbarIconOverlay",
            "All overlays removed.",
            ToolTipIcon.Info);
    }

    static void StartPeacock(PeacockStyle style)
    {
        if (_peacock != null) return;
        try
        {
            _peacock = new PeacockManager(style);
            _peacock.StatusChanged += summary => SetTrayText(summary);
            _peacock.TaskbarRestarted += () => ApplyOverlays();
            _peacock.Start();
        }
        catch (Exception ex)
        {
            _peacock?.Dispose();
            _peacock = null;
            _trayIcon?.ShowBalloonTip(3000, "TaskbarIconOverlay", "Peacock integration failed to start: " + ex.Message, ToolTipIcon.Warning);
        }
    }

    static void StopPeacock()
    {
        _peacock?.Dispose();
        _peacock = null;
        SetTrayText(null);
    }

    static void ShowPeacockStatus()
    {
        string text = _peacock?.BuildStatusReport()
            ?? "Peacock integration is turned off. Enable \"Peacock Colours (VS Code)\" in this menu.";
        MessageBox.Show(text, "TaskbarIconOverlay – Peacock Status", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    static void SetTrayText(string? detail)
    {
        if (_trayIcon == null) return;
        string text = detail == null ? "TaskbarIconOverlay" : "TaskbarIconOverlay\n" + detail;
        _trayIcon.Text = text.Length > 127 ? text[..127] : text;
    }

    const string StartupRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string StartupValueName = "TaskbarIconOverlay";

    static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, false);
        return key?.GetValue(StartupValueName) != null;
    }

    static void SetStartupEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, true);
        if (key == null) return;

        if (enabled)
        {
            // Use the current executable path
            string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;
            key.SetValue(StartupValueName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(StartupValueName, false);
        }
    }

    static void StartPolling(int intervalMs = 5000)
    {
        if (_pollTimer != null) return;
        _pollTimer = new System.Windows.Forms.Timer();
        _pollTimer.Interval = intervalMs;
        _pollTimer.Tick += (s, e) => ApplyOverlays();
        _pollTimer.Start();
    }

    static void StopPolling()
    {
        _pollTimer?.Stop();
        _pollTimer?.Dispose();
        _pollTimer = null;
    }

    static Config LoadConfig()
    {
        string defaultIconsDir = Path.Combine(AppContext.BaseDirectory, "icons");

        if (!File.Exists(ConfigFilePath))
        {
            return new Config { IconsPath = defaultIconsDir };
        }

        try
        {
            string json = File.ReadAllText(ConfigFilePath);
            var config = JsonSerializer.Deserialize<Config>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            });

            if (config == null)
            {
                return new Config { IconsPath = defaultIconsDir };
            }

            if (string.IsNullOrWhiteSpace(config.IconsPath))
            {
                config.IconsPath = defaultIconsDir;
            }

            // If the path is relative, make it relative to the exe directory
            if (!Path.IsPathRooted(config.IconsPath))
            {
                config.IconsPath = Path.Combine(AppContext.BaseDirectory, config.IconsPath);
            }

            return config;
        }
        catch
        {
            return new Config { IconsPath = defaultIconsDir };
        }
    }

    static bool IsCodeFamilyWindow(IntPtr hWnd)
    {
        GetWindowThreadProcessId(hWnd, out uint pid);
        if (pid == 0) return false;

        try
        {
            using var p = Process.GetProcessById((int)pid);
            string name = p.ProcessName;

            // VS Code: Code.exe
            // VSCodium: VSCodium.exe
            // (ProcessName doesn't include .exe)
            if (!name.Equals("Code", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("VSCodium", StringComparison.OrdinalIgnoreCase))
                return false;

            // Electron top-level window class is typically Chrome_WidgetWin_1
            // This helps exclude odd helper windows
            string cls = GetClass(hWnd);
            if (!cls.Equals("Chrome_WidgetWin_1", StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    static string GetProcessName(IntPtr hWnd)
    {
        GetWindowThreadProcessId(hWnd, out uint pid);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return "UnknownProcess";
        }
    }

    static string GetTitle(IntPtr hWnd)
    {
        var sb = new StringBuilder(2048);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    static string GetClass(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    static string? ExtractWorkspaceName(string title)
    {
        const string marker = "(Workspace)";

        int markerIndex = title.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return null;

        // We want the chunk immediately before "(Workspace)" in a pattern like:
        // "... - something (Workspace) - ..."
        // So take substring up to marker, then split by " - " and take last chunk.
        string beforeMarker = title.Substring(0, markerIndex).Trim();

        // Remove any trailing "-" or extra spaces
        beforeMarker = beforeMarker.TrimEnd('-', ' ');

        // Split on " - " and take last segment
        string[] parts = beforeMarker.Split(" - ", StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;

        string instance = parts[^1].Trim();

        // Optional: sanitize filename-unfriendly characters
        instance = SanitizeFileStem(instance);

        return string.IsNullOrWhiteSpace(instance) ? null : instance;
    }

    static string SanitizeFileStem(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s;
    }

    // --- Win32 ---
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    const uint IMAGE_ICON = 1;
    const uint LR_LOADFROMFILE = 0x0010;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadImage(IntPtr hInst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool DestroyIcon(IntPtr hIcon);

    // --- Taskbar COM ---
    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    [ClassInterface(ClassInterfaceType.None)]
    internal class CTaskbarList { }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")] // <-- correct IID
    internal interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);

        // ITaskbarList2
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);

        // ITaskbarList3
        void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
        void SetProgressState(IntPtr hwnd, TBPFLAG tbpFlags);
        void RegisterTab(IntPtr hwndTab, IntPtr hwndMDI);
        void UnregisterTab(IntPtr hwndTab);
        void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
        void SetTabActive(IntPtr hwndTab, IntPtr hwndMDI, uint dwReserved);
        void ThumbBarAddButtons(IntPtr hwnd, uint cButtons, IntPtr pButtons);
        void ThumbBarUpdateButtons(IntPtr hwnd, uint cButtons, IntPtr pButtons);
        void ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
        void SetOverlayIcon(IntPtr hwnd, IntPtr hIcon, [MarshalAs(UnmanagedType.LPWStr)] string? pszDescription);
        void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string pszTip);
        void SetThumbnailClip(IntPtr hwnd, IntPtr prcClip);
    }

    internal enum TBPFLAG
    {
        TBPF_NOPROGRESS = 0,
        TBPF_INDETERMINATE = 0x1,
        TBPF_NORMAL = 0x2,
        TBPF_ERROR = 0x4,
        TBPF_PAUSED = 0x8
    }
}

class Config
{
    public string IconsPath { get; set; } = "icons";

    /// <summary>Colour VS Code taskbar icons from Peacock (requires the companion extension).</summary>
    public bool PeacockEnabled { get; set; } = true;

    /// <summary>Icon (recolour the VS Code logo), Overlay (badge) or IconAndOverlay.</summary>
    public PeacockStyle PeacockStyle { get; set; } = PeacockStyle.Icon;
}
