using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace TaskbarIconOverlay.Peacock;

internal enum PeacockStyle
{
    /// <summary>Recolour the application icon itself (WM_SETICON). Default.</summary>
    Icon,
    /// <summary>Leave the icon alone and add a coloured overlay badge (ITaskbarList3).</summary>
    Overlay,
    /// <summary>Both.</summary>
    IconAndOverlay,
}

/// <summary>
/// Colours VS Code taskbar buttons from the Peacock colours reported by the companion
/// extension. Event driven: session files (FileSystemWatcher), window titles and foreground
/// changes (WinEvent hooks) and Explorer restarts (TaskbarCreated) trigger a reconcile; a slow
/// timer is only a safety net. Every change is a diff, so unchanged windows are not touched.
/// </summary>
internal sealed class PeacockManager : IDisposable
{
    const string ElectronWindowClass = "Chrome_WidgetWin_1";
    const int SafetyIntervalMs = 5000;
    /// <summary>Keep a colour briefly when a window's session disappears (window reload), to avoid flicker.</summary>
    const long OrphanGraceMs = 4000;
    /// <summary>Tolerance between the extension host's self-reported start time and Windows' creation time.</summary>
    const long StartTimeToleranceMs = 15000;

    public static string DefaultStateDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskbarIconOverlay", "sessions");

    sealed class TrackedSession
    {
        public required PeacockSession Report;
        public HashSet<int>? Ancestors;
        public IntPtr BoundWindow;
        public bool FocusPending;
        public string? FocusNote;
        public MatchResult? Match;
    }

    sealed class AppliedWindow
    {
        public PeacockColor Color;
        public string Label = "";
        public IconKey? BigKey, SmallKey;
        public IntPtr Big, Small;
        public IntPtr OriginalBig, OriginalSmall;
        public bool OverlaySet;
        public long OrphanedSinceMs;
    }

    readonly string _directory;
    readonly PeacockStyle _style;
    readonly Dictionary<string, TrackedSession> _sessions = new();
    readonly Dictionary<IntPtr, AppliedWindow> _applied = new();
    readonly Dictionary<string, string> _fileErrors = new();
    readonly Dictionary<int, string?> _imagePaths = new();
    readonly Dictionary<(string, int), uint[]?> _sourcePixels = new();
    readonly ForegroundHistory _history = new();
    readonly IconCache _icons;
    readonly List<IntPtr> _hooks = new();
    readonly Native.WinEventProc _hookProc;
    readonly System.Windows.Forms.Timer _debounce = new();
    readonly System.Windows.Forms.Timer _safety = new();
    readonly SynchronizationContext _ui;

    FileSystemWatcher? _watcher;
    TaskbarCreatedListener? _taskbarListener;
    object? _taskbarObj;
    Program.ITaskbarList3? _taskbar;
    bool _sessionsDirty = true;
    long _debounceDueMs;
    bool _disposed;
    string _lastStatus = "";

    /// <summary>Raised after Explorer restarts, so other overlays can be re-applied too.</summary>
    public event Action? TaskbarRestarted;
    /// <summary>Raised when the status summary changes.</summary>
    public event Action<string>? StatusChanged;

    public string Summary { get; private set; } = "Peacock: starting";

    public PeacockManager(PeacockStyle style, string? directory = null)
    {
        _style = style;
        _directory = directory ?? DefaultStateDirectory;
        _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Create on the UI thread.");
        _hookProc = OnWinEvent;
        _icons = new IconCache(CreateIcon, h => Native.DestroyIcon(h));
        _debounce.Tick += (s, e) =>
        {
            _debounce.Stop();
            _debounceDueMs = 0;
            Reconcile();
        };
        _safety.Interval = SafetyIntervalMs;
        _safety.Tick += (s, e) =>
        {
            _sessionsDirty = true;
            Reconcile();
        };
    }

    bool UseIcon => _style is PeacockStyle.Icon or PeacockStyle.IconAndOverlay;
    bool UseOverlay => _style is PeacockStyle.Overlay or PeacockStyle.IconAndOverlay;

    public void Start()
    {
        Directory.CreateDirectory(_directory);

        _watcher = new FileSystemWatcher(_directory, "*" + PeacockSession.FileSuffix)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            IncludeSubdirectories = false,
        };
        FileSystemEventHandler changed = (s, e) => _ui.Post(_ => OnSessionsChanged(), null);
        _watcher.Created += changed;
        _watcher.Changed += changed;
        _watcher.Deleted += changed;
        _watcher.Renamed += (s, e) => _ui.Post(_ => OnSessionsChanged(), null);
        _watcher.Error += (s, e) => _ui.Post(_ => OnSessionsChanged(), null);
        _watcher.EnableRaisingEvents = true;

        uint flags = Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS;
        _hooks.Add(Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _hookProc, 0, 0, flags));
        _hooks.Add(Native.SetWinEventHook(Native.EVENT_OBJECT_DESTROY, Native.EVENT_OBJECT_SHOW, IntPtr.Zero, _hookProc, 0, 0, flags));
        _hooks.Add(Native.SetWinEventHook(Native.EVENT_OBJECT_NAMECHANGE, Native.EVENT_OBJECT_NAMECHANGE, IntPtr.Zero, _hookProc, 0, 0, flags));

        IntPtr foreground = Native.GetForegroundWindow();
        _history.Start(NowMs(), foreground, IsElectronWindow(foreground));

        _taskbarListener = new TaskbarCreatedListener(OnTaskbarCreated);
        CreateTaskbar();

        _safety.Start();
        Reconcile();
    }

    static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    void OnSessionsChanged()
    {
        if (_disposed) return;
        _sessionsDirty = true;
        ScheduleReconcile(100);
    }

    void ScheduleReconcile(long delayMs)
    {
        if (_disposed) return;
        long due = NowMs() + Math.Max(1, delayMs);
        if (_debounce.Enabled && _debounceDueMs <= due) return;
        _debounce.Stop();
        _debounce.Interval = (int)Math.Max(1, delayMs);
        _debounceDueMs = due;
        _debounce.Start();
    }

    void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint timeMs)
    {
        if (_disposed || hwnd == IntPtr.Zero || idObject != Native.OBJID_WINDOW || idChild != 0) return;
        switch (eventType)
        {
            case Native.EVENT_SYSTEM_FOREGROUND:
                _history.Record(NowMs(), hwnd, IsElectronWindow(hwnd));
                if (_sessions.Values.Any(s => s.FocusPending))
                    ScheduleReconcile(ForegroundHistory.SettleMs + 50);
                break;
            case Native.EVENT_OBJECT_DESTROY:
                if (_applied.ContainsKey(hwnd)) ScheduleReconcile(100);
                break;
            default: // shown or title changed
                if (_sessions.Count > 0 && IsElectronWindow(hwnd)) ScheduleReconcile(250);
                break;
        }
    }

    static bool IsElectronWindow(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && Native.GetClass(hwnd) == ElectronWindowClass;

    void OnTaskbarCreated()
    {
        // Explorer restarted: overlays are gone and the taskbar must re-read window icons.
        CreateTaskbar();
        foreach (var (hwnd, applied) in _applied)
        {
            applied.OverlaySet = false;
            if (applied.Big != IntPtr.Zero)
            {
                Native.SetWindowIcon(hwnd, Native.ICON_BIG, applied.Big);
                Native.SetWindowIcon(hwnd, Native.ICON_SMALL, applied.Small);
            }
        }
        Reconcile();
        TaskbarRestarted?.Invoke();
    }

    void CreateTaskbar()
    {
        if (_taskbarObj != null) Marshal.ReleaseComObject(_taskbarObj);
        _taskbarObj = null;
        _taskbar = null;
        if (!UseOverlay) return;
        try
        {
            _taskbarObj = new Program.CTaskbarList();
            _taskbar = (Program.ITaskbarList3)_taskbarObj;
            _taskbar.HrInit();
        }
        catch (COMException)
        {
            _taskbar = null; // Explorer not ready yet; TaskbarCreated will retry.
        }
    }

    /// <summary>True when this manager controls the overlay of that window.</summary>
    public bool OwnsOverlay(IntPtr hwnd) => _applied.TryGetValue(hwnd, out var a) && a.OverlaySet;

    /// <summary>Forget overlay state after something else cleared all overlays.</summary>
    public void InvalidateOverlays()
    {
        foreach (var applied in _applied.Values) applied.OverlaySet = false;
        ScheduleReconcile(50);
    }

    // ---------------------------------------------------------------------------------------
    // Reconcile
    // ---------------------------------------------------------------------------------------

    public void Reconcile()
    {
        if (_disposed) return;
        long now = NowMs();
        if (_sessionsDirty) ReloadSessions();
        RemoveDeadSessions();

        var windows = EnumerateWindows();
        var byHandle = windows.ToDictionary(w => w.Handle);
        foreach (var s in _sessions.Values)
            if (s.BoundWindow != IntPtr.Zero && !byHandle.ContainsKey(s.BoundWindow))
                s.BoundWindow = IntPtr.Zero;

        ProcessFocusReports(now, byHandle);

        var sessions = _sessions.Values.ToList();
        var results = WindowMatcher.Match(sessions.Select(ToRequest), windows);
        var desired = new Dictionary<IntPtr, (PeacockColor Color, string Label)>();
        for (int i = 0; i < sessions.Count; i++)
        {
            sessions[i].Match = results[i];
            if (results[i].IsMatched && sessions[i].Report.Color is PeacockColor color)
                desired[results[i].Window] = (color, sessions[i].Report.DisplayName);
        }

        Apply(desired, byHandle, now);
        UpdateStatus();
    }

    MatchRequest ToRequest(TrackedSession s) => new(
        s.Report.SessionId,
        s.Report.WorkspaceName,
        s.Report.TitleNameMode,
        s.Report.TitleSeparator,
        s.Ancestors ?? new HashSet<int>(),
        s.BoundWindow);

    void ReloadSessions()
    {
        _sessionsDirty = false;
        _fileErrors.Clear();
        var seen = new HashSet<string>();
        string[] files;
        try
        {
            files = Directory.GetFiles(_directory, "*" + PeacockSession.FileSuffix);
        }
        catch (IOException)
        {
            files = Array.Empty<string>();
        }

        foreach (string file in files)
        {
            string? json = ReadShared(file);
            if (json == null) continue;
            var report = PeacockSession.Parse(json, out string? error);
            if (report == null)
            {
                _fileErrors[Path.GetFileName(file)] = error ?? "unreadable";
                continue;
            }
            seen.Add(report.SessionId);
            if (_sessions.TryGetValue(report.SessionId, out var tracked))
            {
                if (report.Focused && report.FocusedAtMs != tracked.Report.FocusedAtMs)
                    tracked.FocusPending = true;
                tracked.Report = report;
            }
            else
            {
                _sessions[report.SessionId] = new TrackedSession { Report = report, FocusPending = report.Focused && report.FocusedAtMs > 0 };
            }
        }

        foreach (string id in _sessions.Keys.Where(id => !seen.Contains(id)).ToList())
            _sessions.Remove(id);
    }

    /// <summary>Reads a file the extension may be replacing; held open only momentarily.</summary>
    static string? ReadShared(string path)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (IOException)
            {
                Thread.Sleep(15);
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
        return null;
    }

    void RemoveDeadSessions()
    {
        Dictionary<int, int>? parents = null;
        foreach (var s in _sessions.Values.ToList())
        {
            long? start = Native.GetProcessStartMs(s.Report.Pid);
            bool alive = start != null
                         && (s.Report.ProcessStartMs == 0 || Math.Abs(start.Value - s.Report.ProcessStartMs) <= StartTimeToleranceMs);
            if (!alive)
            {
                // The window's extension host crashed or was killed without cleaning up.
                _sessions.Remove(s.Report.SessionId);
                try
                {
                    File.Delete(Path.Combine(_directory, s.Report.SessionId + PeacockSession.FileSuffix));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                continue;
            }

            if (s.Ancestors == null)
            {
                parents ??= Native.GetParentMap();
                s.Ancestors = Ancestors(s.Report.Pid, s.Report.ParentPid, parents);
            }
        }
    }

    /// <summary>The window belongs to the Electron main process, an ancestor of the extension host.</summary>
    internal static HashSet<int> Ancestors(int pid, int reportedParent, IReadOnlyDictionary<int, int> parents)
    {
        var result = new HashSet<int>();
        if (reportedParent > 0) result.Add(reportedParent);
        int current = pid;
        for (int depth = 0; depth < 8 && parents.TryGetValue(current, out int parent) && parent > 0; depth++)
        {
            if (!result.Add(parent) && parent != reportedParent) break; // cycle (PID reuse)
            current = parent;
        }
        return result;
    }

    List<WindowInfo> EnumerateWindows()
    {
        var allowed = new HashSet<int>(_sessions.Values.Where(s => s.Ancestors != null).SelectMany(s => s.Ancestors!));
        var windows = new List<WindowInfo>();
        if (allowed.Count == 0) return windows;
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsTaskbarWindow(hwnd)) return true;
            int pid = Native.GetProcessId(hwnd);
            if (!allowed.Contains(pid) || Native.GetClass(hwnd) != ElectronWindowClass) return true;
            windows.Add(new WindowInfo(hwnd, pid, Native.GetTitle(hwnd)));
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    void ProcessFocusReports(long now, Dictionary<IntPtr, WindowInfo> windows)
    {
        foreach (var s in _sessions.Values.Where(s => s.FocusPending))
        {
            var resolution = _history.Resolve(s.Report.FocusedAtMs, now);
            switch (resolution.Kind)
            {
                case FocusResolutionKind.Pending:
                    ScheduleReconcile(s.Report.FocusedAtMs + ForegroundHistory.SettleMs + 50 - now);
                    continue;
                case FocusResolutionKind.Unknown:
                    s.FocusNote = null; // predates this process; title matching still applies
                    break;
                case FocusResolutionKind.Ambiguous:
                    s.FocusNote = "focus changed too quickly to identify the window";
                    break;
                case FocusResolutionKind.Resolved:
                    s.FocusNote = TryBind(s, resolution.Window, windows);
                    break;
            }
            s.FocusPending = false;
        }
    }

    string? TryBind(TrackedSession s, IntPtr hwnd, Dictionary<IntPtr, WindowInfo> windows)
    {
        if (!windows.TryGetValue(hwnd, out var window) || s.Ancestors == null || !s.Ancestors.Contains(window.ProcessId))
            return "focused window does not belong to this VS Code instance";
        var request = ToRequest(s);
        if (request.TitleNameMode != TitleNameMode.None && !WindowMatcher.TitleMatches(request, window.Title, loose: true))
            return $"focused window title \"{window.Title}\" does not show \"{s.Report.WorkspaceName}\"";

        foreach (var other in _sessions.Values.Where(o => o != s && o.BoundWindow == hwnd))
            other.BoundWindow = IntPtr.Zero;
        s.BoundWindow = hwnd;
        return null;
    }

    // ---------------------------------------------------------------------------------------
    // Applying icons
    // ---------------------------------------------------------------------------------------

    void Apply(Dictionary<IntPtr, (PeacockColor Color, string Label)> desired, Dictionary<IntPtr, WindowInfo> windows, long now)
    {
        foreach (var (hwnd, applied) in _applied.ToList())
        {
            if (desired.ContainsKey(hwnd)) continue;
            bool gone = !Native.IsWindow(hwnd);
            if (!gone && applied.OrphanedSinceMs == 0)
            {
                applied.OrphanedSinceMs = now;
                ScheduleReconcile(OrphanGraceMs + 50);
                continue;
            }
            if (gone || now - applied.OrphanedSinceMs >= OrphanGraceMs)
            {
                Restore(hwnd, applied, gone);
                _applied.Remove(hwnd);
            }
            else
            {
                ScheduleReconcile(applied.OrphanedSinceMs + OrphanGraceMs + 50 - now);
            }
        }

        foreach (var (hwnd, (color, label)) in desired)
        {
            bool isNew = !_applied.TryGetValue(hwnd, out var applied);
            if (applied == null)
            {
                applied = new AppliedWindow
                {
                    Color = color,
                    OriginalBig = ValidIconOrZero(Native.GetWindowIcon(hwnd, Native.ICON_BIG)),
                    OriginalSmall = ValidIconOrZero(Native.GetWindowIcon(hwnd, Native.ICON_SMALL)),
                };
                _applied[hwnd] = applied;
            }
            applied.OrphanedSinceMs = 0;
            applied.Label = label;
            bool colorChanged = isNew || applied.Color != color;
            applied.Color = color;

            if (UseIcon) ApplyIcon(hwnd, windows[hwnd].ProcessId, applied, colorChanged);
            if (UseOverlay && (colorChanged || !applied.OverlaySet)) ApplyOverlay(hwnd, applied);
        }
    }

    void ApplyIcon(IntPtr hwnd, int pid, AppliedWindow applied, bool colorChanged)
    {
        if (!colorChanged && applied.Big != IntPtr.Zero)
        {
            // Re-assert only if something (VS Code, Electron) replaced our icon.
            if (Native.GetWindowIcon(hwnd, Native.ICON_BIG) == applied.Big) return;
            Native.SetWindowIcon(hwnd, Native.ICON_BIG, applied.Big);
            Native.SetWindowIcon(hwnd, Native.ICON_SMALL, applied.Small);
            return;
        }

        string? exe = ImagePath(pid);
        if (exe == null) return;
        int rgb = applied.Color.ToRgbInt();
        var bigKey = new IconKey(exe, Native.GetSystemMetrics(Native.SM_CXICON), rgb, IconKind.Recolored);
        var smallKey = new IconKey(exe, Native.GetSystemMetrics(Native.SM_CXSMICON), rgb, IconKind.Recolored);
        IntPtr big = _icons.Acquire(bigKey);
        IntPtr small = _icons.Acquire(smallKey);
        if (big == IntPtr.Zero || small == IntPtr.Zero)
        {
            if (big != IntPtr.Zero) _icons.Release(bigKey);
            if (small != IntPtr.Zero) _icons.Release(smallKey);
            return;
        }

        Native.SetWindowIcon(hwnd, Native.ICON_BIG, big);
        Native.SetWindowIcon(hwnd, Native.ICON_SMALL, small);

        if (applied.BigKey is IconKey oldBig) _icons.Release(oldBig);
        if (applied.SmallKey is IconKey oldSmall) _icons.Release(oldSmall);
        applied.Big = big;
        applied.Small = small;
        applied.BigKey = bigKey;
        applied.SmallKey = smallKey;
    }

    void ApplyOverlay(IntPtr hwnd, AppliedWindow applied)
    {
        if (_taskbar == null) return;
        var key = new IconKey("badge", Native.GetSystemMetrics(Native.SM_CXSMICON), applied.Color.ToRgbInt(), IconKind.Badge);
        IntPtr badge = _icons.Acquire(key);
        if (badge == IntPtr.Zero) return;
        try
        {
            // The taskbar copies overlay icons, so the cached handle can be released right away.
            _taskbar.SetOverlayIcon(hwnd, badge, $"Peacock {applied.Color}");
            applied.OverlaySet = true;
        }
        catch (COMException)
        {
            applied.OverlaySet = false;
        }
        finally
        {
            _icons.Release(key);
        }
    }

    void Restore(IntPtr hwnd, AppliedWindow applied, bool windowGone)
    {
        if (!windowGone)
        {
            if (applied.Big != IntPtr.Zero)
            {
                // Setting the icon back to NULL leaves Windows 11's taskbar showing our icon,
                // so restore an explicit icon: the window's own one, else its class icon.
                IntPtr big = applied.OriginalBig != IntPtr.Zero ? applied.OriginalBig : Native.GetClassLongPtr(hwnd, Native.GCLP_HICON);
                IntPtr small = applied.OriginalSmall != IntPtr.Zero ? applied.OriginalSmall : Native.GetClassLongPtr(hwnd, Native.GCLP_HICONSM);
                if (small == IntPtr.Zero) small = big;
                Native.SetWindowIcon(hwnd, Native.ICON_BIG, big);
                Native.SetWindowIcon(hwnd, Native.ICON_SMALL, small);
            }
            if (applied.OverlaySet && _taskbar != null)
            {
                try { _taskbar.SetOverlayIcon(hwnd, IntPtr.Zero, null); } catch (COMException) { }
            }
        }
        if (applied.BigKey is IconKey big2) _icons.Release(big2);
        if (applied.SmallKey is IconKey small2) _icons.Release(small2);
        applied.BigKey = applied.SmallKey = null;
        applied.Big = applied.Small = IntPtr.Zero;
        applied.OverlaySet = false;
    }

    /// <summary>A handle left by a crashed process (or another instance of us) is invalid; don't restore it.</summary>
    static IntPtr ValidIconOrZero(IntPtr icon)
    {
        if (icon == IntPtr.Zero || !Native.GetIconInfo(icon, out var info)) return IntPtr.Zero;
        if (info.hbmColor != IntPtr.Zero) Native.DeleteObject(info.hbmColor);
        if (info.hbmMask != IntPtr.Zero) Native.DeleteObject(info.hbmMask);
        return icon;
    }

    string? ImagePath(int pid)
    {
        if (!_imagePaths.TryGetValue(pid, out string? path))
            _imagePaths[pid] = path = Native.GetProcessImagePath(pid);
        return path;
    }

    IntPtr CreateIcon(IconKey key)
    {
        uint[] pixels;
        if (key.Kind == IconKind.Badge)
        {
            pixels = IconRecolorer.CreateBadge(key.Size, ColorOf(key.Rgb));
        }
        else
        {
            if (!_sourcePixels.TryGetValue((key.Source, key.Size), out var source))
                _sourcePixels[(key.Source, key.Size)] = source = NativeIcons.ExtractPixels(key.Source, key.Size);
            if (source == null) return IntPtr.Zero;
            pixels = IconRecolorer.Recolor(source, key.Size, ColorOf(key.Rgb));
        }
        return NativeIcons.CreateIcon(pixels, key.Size);
    }

    static PeacockColor ColorOf(int rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    // ---------------------------------------------------------------------------------------
    // Diagnostics
    // ---------------------------------------------------------------------------------------

    void UpdateStatus()
    {
        int colored = _sessions.Values.Count(s => s.Match?.IsMatched == true && s.Report.Color != null);
        int waiting = _sessions.Values.Count(s => s.Report.Color != null && s.Match?.IsMatched != true);
        Summary = _sessions.Count == 0
            ? "Peacock: no VS Code windows reporting"
            : $"Peacock: {colored} coloured" + (waiting > 0 ? $", {waiting} unmatched" : "");

        string status = BuildStatusReport();
        if (status == _lastStatus) return;
        _lastStatus = status;
        try
        {
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(_directory)!, "peacock-status.txt"), status);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        StatusChanged?.Invoke(Summary);
    }

    public string BuildStatusReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Peacock integration ({_style}); session files: {_directory}");
        sb.AppendLine();
        if (_sessions.Count == 0)
        {
            sb.AppendLine("No VS Code windows are reporting. Check that the \"Taskbar Icon Overlay – Peacock Bridge\"");
            sb.AppendLine("extension is installed (locally, not on the SSH host) and enabled, and that");
            sb.AppendLine("\"taskbarIconOverlay.enabled\" is not false.");
        }

        foreach (var s in _sessions.Values.OrderBy(s => s.Report.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var r = s.Report;
            string where = r.RemoteName != null ? $" ({r.RemoteName}{(r.RemoteHost != null ? ": " + r.RemoteHost : "")})" : "";
            sb.AppendLine($"• {r.DisplayName}{where}");
            sb.AppendLine(r.Color is PeacockColor c
                ? $"    colour {c} from {r.ColorSource}"
                : $"    no colour: {r.ColorReason ?? "Peacock has no colour for this workspace"}");
            sb.AppendLine("    " + DescribeMatch(s));
            if (s.FocusNote != null) sb.AppendLine($"    last focus report: {s.FocusNote}");
        }

        foreach (var (file, error) in _fileErrors)
            sb.AppendLine($"• {file}: ignored ({error})");
        return sb.ToString();
    }

    string DescribeMatch(TrackedSession s)
    {
        var m = s.Match;
        switch (m?.Status)
        {
            case MatchStatus.MatchedByTitle:
                return $"window matched by title (0x{m.Window.ToInt64():X})";
            case MatchStatus.MatchedByFocus:
                return $"window identified by focus (0x{m.Window.ToInt64():X})";
            case MatchStatus.Ambiguous:
                int rivals = _sessions.Values.Count(o => o != s && o.Match?.Candidates.Intersect(m.Candidates).Any() == true);
                string who = m.Candidates.Count > 1
                    ? $"{m.Candidates.Count} windows show this title"
                    : $"{rivals + 1} VS Code sessions claim the same window title";
                return $"AMBIGUOUS: {who}, so none is coloured. Switch to the window once (Alt+Tab or click) so it can be identified.";
            case MatchStatus.NotFound:
                return "no window title shows this workspace name yet";
            case MatchStatus.NoTitleInfo:
                return "window.title does not include ${rootName}; switch to the window once so it can be identified";
            default:
                return "not matched yet";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounce.Stop();
        _safety.Stop();
        _debounce.Dispose();
        _safety.Dispose();
        foreach (var hook in _hooks.Where(h => h != IntPtr.Zero)) Native.UnhookWinEvent(hook);
        _hooks.Clear();
        _watcher?.Dispose();
        foreach (var (hwnd, applied) in _applied) Restore(hwnd, applied, !Native.IsWindow(hwnd));
        _applied.Clear();
        _icons.Dispose();
        _taskbarListener?.DestroyHandle();
        if (_taskbarObj != null) Marshal.ReleaseComObject(_taskbarObj);
        _taskbarObj = null;
        _taskbar = null;
    }

    /// <summary>Hidden top-level window: "TaskbarCreated" is only broadcast to top-level windows.</summary>
    sealed class TaskbarCreatedListener : NativeWindow
    {
        readonly uint _message = Native.RegisterWindowMessage("TaskbarCreated");
        readonly Action _onCreated;

        public TaskbarCreatedListener(Action onCreated)
        {
            _onCreated = onCreated;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (_message != 0 && m.Msg == (int)_message) _onCreated();
            base.WndProc(ref m);
        }
    }
}
