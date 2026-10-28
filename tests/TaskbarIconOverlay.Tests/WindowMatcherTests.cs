using TaskbarIconOverlay.Peacock;
using Xunit;

namespace TaskbarIconOverlay.Tests;

public class WindowMatcherTests
{
    const int MainPid = 1000;
    static readonly HashSet<int> Main = new() { MainPid };

    static MatchRequest Session(string id, string? name, IntPtr bound = default, TitleNameMode mode = TitleNameMode.RootName, HashSet<int>? pids = null) =>
        new(id, name, mode, " - ", pids ?? Main, bound);

    static WindowInfo Window(int handle, string title, int pid = MainPid) => new(new IntPtr(handle), pid, title);

    static MatchResult Single(IReadOnlyList<MatchResult> results, string id) => results.Single(r => r.SessionId == id);

    [Fact]
    public void Matches_local_workspace_by_title_segment()
    {
        var results = WindowMatcher.Match(
            new[] { Session("a", "proj") },
            new[] { Window(1, "main.ts - proj - Visual Studio Code"), Window(2, "other - Visual Studio Code") });

        Assert.Equal(MatchStatus.MatchedByTitle, results[0].Status);
        Assert.Equal(new IntPtr(1), results[0].Window);
    }

    [Fact]
    public void Same_folder_name_on_different_ssh_hosts_matches_distinct_windows()
    {
        var results = WindowMatcher.Match(
            new[] { Session("a", "api [SSH: alpha]"), Session("b", "api [SSH: beta]"), Session("c", "api") },
            new[]
            {
                Window(1, "x.py - api [SSH: beta] - Visual Studio Code"),
                Window(2, "api [SSH: alpha] - Visual Studio Code"),
                Window(3, "y.py - api - Visual Studio Code"),
            });

        Assert.Equal(new IntPtr(2), Single(results, "a").Window);
        Assert.Equal(new IntPtr(1), Single(results, "b").Window);
        Assert.Equal(new IntPtr(3), Single(results, "c").Window);
        Assert.All(results, r => Assert.Equal(MatchStatus.MatchedByTitle, r.Status));
    }

    [Fact]
    public void Remote_suffix_never_matches_a_different_host_loosely()
    {
        var results = WindowMatcher.Match(
            new[] { Session("a", "api [SSH: alpha]") },
            new[] { Window(1, "api [SSH: beta] - Visual Studio Code") });

        Assert.Equal(MatchStatus.NotFound, results[0].Status);
        Assert.Equal(IntPtr.Zero, results[0].Window);
    }

    [Fact]
    public void Identical_titles_are_ambiguous_and_get_no_colour()
    {
        // Same folder name, same host, different paths: titles are identical.
        var results = WindowMatcher.Match(
            new[] { Session("a", "api [SSH: alpha]"), Session("b", "api [SSH: alpha]") },
            new[] { Window(1, "api [SSH: alpha] - Visual Studio Code"), Window(2, "api [SSH: alpha] - Visual Studio Code") });

        Assert.All(results, r =>
        {
            Assert.Equal(MatchStatus.Ambiguous, r.Status);
            Assert.Equal(IntPtr.Zero, r.Window);
            Assert.Equal(2, r.Candidates.Count);
        });
    }

    [Fact]
    public void A_window_without_a_session_sharing_the_title_makes_the_match_ambiguous()
    {
        // e.g. a second VS Code profile where the extension is not installed.
        var results = WindowMatcher.Match(
            new[] { Session("a", "proj") },
            new[] { Window(1, "proj - Visual Studio Code"), Window(2, "proj - Visual Studio Code") });

        Assert.Equal(MatchStatus.Ambiguous, results[0].Status);
    }

    [Fact]
    public void Focus_binding_resolves_ambiguity_and_the_rest_follows_by_elimination()
    {
        var results = WindowMatcher.Match(
            new[] { Session("a", "api [SSH: alpha]", bound: new IntPtr(2)), Session("b", "api [SSH: alpha]") },
            new[] { Window(1, "api [SSH: alpha] - Visual Studio Code"), Window(2, "api [SSH: alpha] - Visual Studio Code") });

        Assert.Equal(MatchStatus.MatchedByFocus, Single(results, "a").Status);
        Assert.Equal(new IntPtr(2), Single(results, "a").Window);
        Assert.Equal(MatchStatus.MatchedByTitle, Single(results, "b").Status);
        Assert.Equal(new IntPtr(1), Single(results, "b").Window);
    }

    [Fact]
    public void Binding_to_a_closed_window_is_ignored()
    {
        var results = WindowMatcher.Match(
            new[] { Session("a", "proj", bound: new IntPtr(99)) },
            new[] { Window(1, "proj - Visual Studio Code") });

        Assert.Equal(MatchStatus.MatchedByTitle, results[0].Status);
        Assert.Equal(new IntPtr(1), results[0].Window);
    }

    [Fact]
    public void Windows_of_another_vscode_instance_are_not_candidates()
    {
        // VS Code and VS Code Insiders open the same folder: different main processes.
        var results = WindowMatcher.Match(
            new[] { Session("stable", "proj", pids: new HashSet<int> { 1000 }), Session("insiders", "proj", pids: new HashSet<int> { 2000 }) },
            new[] { Window(1, "proj - Visual Studio Code", pid: 1000), Window(2, "proj - Visual Studio Code - Insiders", pid: 2000) });

        Assert.Equal(new IntPtr(1), Single(results, "stable").Window);
        Assert.Equal(new IntPtr(2), Single(results, "insiders").Window);
    }

    [Fact]
    public void Dirty_indicator_and_editor_changes_do_not_break_matching()
    {
        foreach (string title in new[] { "● main.ts - proj - Visual Studio Code", "proj - Visual Studio Code", "README.md - proj - Profile - Visual Studio Code" })
        {
            var results = WindowMatcher.Match(new[] { Session("a", "proj") }, new[] { Window(1, title) });
            Assert.Equal(MatchStatus.MatchedByTitle, results[0].Status);
        }
    }

    [Fact]
    public void Workspace_name_must_be_a_whole_segment_not_a_substring()
    {
        var results = WindowMatcher.Match(
            new[] { Session("a", "api") },
            new[] { Window(1, "api-gateway - Visual Studio Code"), Window(2, "my api - Visual Studio Code") });

        Assert.Equal(MatchStatus.NotFound, results[0].Status);
    }

    [Fact]
    public void Multi_root_workspace_name_matches()
    {
        var results = WindowMatcher.Match(
            new[] { Session("a", "Platform (Workspace) [SSH: alpha]") },
            new[] { Window(1, "main.go - Platform (Workspace) [SSH: alpha] - Visual Studio Code") });

        Assert.Equal(new IntPtr(1), results[0].Window);
    }

    [Fact]
    public void Root_name_short_title_template_matches_without_suffix_when_unique()
    {
        var results = WindowMatcher.Match(
            new[] { Session("a", "api [SSH: alpha]", mode: TitleNameMode.RootNameShort) },
            new[] { Window(1, "api - Visual Studio Code") });

        Assert.Equal(new IntPtr(1), results[0].Window);
    }

    [Fact]
    public void Root_name_short_with_same_name_on_two_hosts_is_ambiguous()
    {
        var results = WindowMatcher.Match(
            new[]
            {
                Session("a", "api [SSH: alpha]", mode: TitleNameMode.RootNameShort),
                Session("b", "api [SSH: beta]", mode: TitleNameMode.RootNameShort),
            },
            new[] { Window(1, "api - Visual Studio Code"), Window(2, "api - Visual Studio Code") });

        Assert.All(results, r => Assert.Equal(MatchStatus.Ambiguous, r.Status));
    }

    [Fact]
    public void Loose_match_tolerates_one_side_missing_the_suffix()
    {
        var results = WindowMatcher.Match(
            new[] { Session("a", "api [SSH: alpha]") },
            new[] { Window(1, "api - Visual Studio Code") });

        Assert.Equal(MatchStatus.MatchedByTitle, results[0].Status);
    }

    [Fact]
    public void Loose_match_does_not_steal_a_window_another_session_matched_exactly()
    {
        var results = WindowMatcher.Match(
            new[] { Session("exact", "api"), Session("loose", "api [SSH: alpha]") },
            new[] { Window(1, "api - Visual Studio Code") });

        Assert.Equal(new IntPtr(1), Single(results, "exact").Window);
        Assert.Equal(MatchStatus.Ambiguous, Single(results, "loose").Status);
    }

    [Fact]
    public void Title_without_workspace_name_requires_focus()
    {
        var results = WindowMatcher.Match(
            new[] { Session("a", "proj", mode: TitleNameMode.None) },
            new[] { Window(1, "main.ts") });
        Assert.Equal(MatchStatus.NoTitleInfo, results[0].Status);

        var bound = WindowMatcher.Match(
            new[] { Session("a", "proj", bound: new IntPtr(1), mode: TitleNameMode.None) },
            new[] { Window(1, "main.ts") });
        Assert.Equal(MatchStatus.MatchedByFocus, bound[0].Status);
    }

    [Fact]
    public void Empty_window_session_is_not_matched()
    {
        var results = WindowMatcher.Match(new[] { Session("a", null) }, new[] { Window(1, "Visual Studio Code") });
        Assert.Equal(MatchStatus.NoTitleInfo, results[0].Status);
    }

    [Fact]
    public void Custom_title_separator_is_respected()
    {
        var request = new MatchRequest("a", "proj", TitleNameMode.RootName, " | ", Main, IntPtr.Zero);
        var results = WindowMatcher.Match(new[] { request }, new[] { Window(1, "main.ts | proj | Visual Studio Code") });
        Assert.Equal(new IntPtr(1), results[0].Window);
    }

    [Fact]
    public void Two_sessions_bound_to_the_same_window_do_not_both_get_it()
    {
        var results = WindowMatcher.Match(
            new[] { Session("a", "x", bound: new IntPtr(1)), Session("b", "y", bound: new IntPtr(1)) },
            new[] { Window(1, "x - Visual Studio Code") });

        Assert.Single(results, r => r.IsMatched);
    }

    [Theory]
    [InlineData("api [SSH: alpha]", "api")]
    [InlineData("Platform (Workspace) [SSH: h]", "Platform (Workspace)")]
    [InlineData("api", "api")]
    [InlineData("[weird]", "[weird]")]
    public void StripRemoteSuffix(string input, string expected) =>
        Assert.Equal(expected, WindowMatcher.StripRemoteSuffix(input));
}
