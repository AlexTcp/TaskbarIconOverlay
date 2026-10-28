using TaskbarIconOverlay.Peacock;
using Xunit;

namespace TaskbarIconOverlay.Tests;

public class ForegroundHistoryTests
{
    static readonly IntPtr A = new(1), B = new(2), Browser = new(3);

    static ForegroundHistory Started(long at = 0)
    {
        var h = new ForegroundHistory();
        h.Start(at, Browser, isCandidate: false);
        return h;
    }

    [Fact]
    public void Focus_report_shortly_after_a_switch_resolves_to_that_window()
    {
        var h = Started();
        h.Record(10_000, A, true);
        var r = h.Resolve(reportMs: 10_060, nowMs: 10_500);
        Assert.Equal(FocusResolutionKind.Resolved, r.Kind);
        Assert.Equal(A, r.Window);
    }

    [Fact]
    public void Is_pending_until_focus_has_settled()
    {
        var h = Started();
        h.Record(10_000, A, true);
        Assert.Equal(FocusResolutionKind.Pending, h.Resolve(10_060, 10_100).Kind);
    }

    [Fact]
    public void Focus_moving_right_after_the_report_is_ambiguous()
    {
        var h = Started();
        h.Record(10_000, A, true);
        h.Record(10_150, B, true);
        Assert.Equal(FocusResolutionKind.Ambiguous, h.Resolve(10_060, 11_000).Kind);
    }

    [Fact]
    public void Rapid_switch_between_two_vscode_windows_is_ambiguous()
    {
        // A late report from A could be mistaken for B.
        var h = Started();
        h.Record(10_000, A, true);
        h.Record(10_100, B, true);
        Assert.Equal(FocusResolutionKind.Ambiguous, h.Resolve(10_200, 11_000).Kind);
    }

    [Fact]
    public void Non_candidate_switches_in_between_do_not_cause_ambiguity()
    {
        var h = Started();
        h.Record(5_000, A, true);
        h.Record(9_800, Browser, false);
        h.Record(10_000, B, true);
        var r = h.Resolve(10_050, 11_000);
        Assert.Equal(B, r.Window);
    }

    [Fact]
    public void Report_while_a_non_vscode_window_is_foreground_is_ambiguous()
    {
        var h = Started();
        h.Record(10_000, Browser, false);
        Assert.Equal(FocusResolutionKind.Ambiguous, h.Resolve(10_050, 11_000).Kind);
    }

    [Fact]
    public void Window_foreground_long_before_the_report_resolves()
    {
        // e.g. the extension activates in a window that has been focused for a while.
        var h = Started();
        h.Record(1_000, A, true);
        Assert.Equal(A, h.Resolve(20_000, 21_000).Window);
    }

    [Fact]
    public void Reports_from_before_observation_started_are_unknown()
    {
        var h = Started(at: 10_000);
        Assert.Equal(FocusResolutionKind.Unknown, h.Resolve(9_000, 11_000).Kind);
        Assert.Equal(FocusResolutionKind.Unknown, h.Resolve(10_500, 11_000).Kind);
    }

    [Fact]
    public void Initial_foreground_window_counts_once_lag_window_is_covered()
    {
        var h = new ForegroundHistory();
        h.Start(10_000, A, isCandidate: true);
        Assert.Equal(A, h.Resolve(12_000, 13_000).Window);
    }
}
