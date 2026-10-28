namespace TaskbarIconOverlay.Peacock;

internal enum FocusResolutionKind
{
    /// <summary>Not enough time has passed after the focus report to be sure; ask again later.</summary>
    Pending,
    /// <summary>The foreground window at that time is known with confidence.</summary>
    Resolved,
    /// <summary>Focus moved between candidate windows too quickly to attribute the report.</summary>
    Ambiguous,
    /// <summary>The report predates what this process has observed (e.g. it just started).</summary>
    Unknown,
}

internal readonly record struct FocusResolution(FocusResolutionKind Kind, IntPtr Window)
{
    public static FocusResolution Pending => new(FocusResolutionKind.Pending, IntPtr.Zero);
    public static FocusResolution Ambiguous => new(FocusResolutionKind.Ambiguous, IntPtr.Zero);
    public static FocusResolution Unknown => new(FocusResolutionKind.Unknown, IntPtr.Zero);
}

/// <summary>
/// Remembers recent Windows foreground changes so a VS Code window's "I got focus at T"
/// report can be attributed to a native window handle. The VS Code API exposes no HWND, but
/// both sides see the same wall clock, and only one window can be foreground at a time.
///
/// A report at time T is attributed to window W only if:
///  - W was foreground at T and stayed foreground until T + <see cref="SettleMs"/>, and
///  - at most one candidate (VS Code-like) window became foreground in [T - <see cref="MaxLagMs"/>, T],
///    so the report cannot belong to a window that was focused a moment earlier.
/// </summary>
internal sealed class ForegroundHistory
{
    /// <summary>Upper bound on the delay between a window gaining focus and the extension noticing.</summary>
    public const long MaxLagMs = 1000;
    /// <summary>How long focus must stay put after the report.</summary>
    public const long SettleMs = 300;
    const int Capacity = 128;

    readonly record struct Change(long TimeMs, IntPtr Window, bool IsCandidate);

    readonly List<Change> _changes = new();
    long _observedSinceMs = long.MaxValue;
    IntPtr _initialWindow;
    bool _initialIsCandidate;

    /// <summary>Starts observation with the current foreground window (time of change unknown).</summary>
    public void Start(long nowMs, IntPtr foreground, bool isCandidate)
    {
        _changes.Clear();
        _observedSinceMs = nowMs;
        _initialWindow = foreground;
        _initialIsCandidate = isCandidate;
    }

    public void Record(long timeMs, IntPtr window, bool isCandidate)
    {
        if (_observedSinceMs == long.MaxValue) Start(timeMs, IntPtr.Zero, false);
        if (_changes.Count > 0 && _changes[^1].Window == window) return;
        _changes.Add(new Change(timeMs, window, isCandidate));
        if (_changes.Count > Capacity) _changes.RemoveAt(0);
    }

    public FocusResolution Resolve(long reportMs, long nowMs)
    {
        if (nowMs < reportMs + SettleMs) return FocusResolution.Pending;
        if (reportMs - MaxLagMs < _observedSinceMs) return FocusResolution.Unknown;
        if (_changes.Count == Capacity && _changes[0].TimeMs > reportMs - MaxLagMs) return FocusResolution.Unknown;

        IntPtr atReport = _initialWindow;
        bool atReportIsCandidate = _initialIsCandidate;
        int candidateChangesInLag = 0;
        foreach (var c in _changes)
        {
            if (c.TimeMs > reportMs + SettleMs) break;
            if (c.TimeMs > reportMs) return FocusResolution.Ambiguous; // focus moved right after the report
            atReport = c.Window;
            atReportIsCandidate = c.IsCandidate;
            if (c.IsCandidate && c.TimeMs >= reportMs - MaxLagMs) candidateChangesInLag++;
        }

        if (atReport == IntPtr.Zero || !atReportIsCandidate) return FocusResolution.Ambiguous;
        if (candidateChangesInLag > 1) return FocusResolution.Ambiguous;
        return new FocusResolution(FocusResolutionKind.Resolved, atReport);
    }
}
