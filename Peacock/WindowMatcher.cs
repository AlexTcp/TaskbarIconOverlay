namespace TaskbarIconOverlay.Peacock;

/// <summary>A top-level taskbar window belonging to a VS Code-family process.</summary>
internal sealed record WindowInfo(IntPtr Handle, int ProcessId, string Title);

/// <summary>What the matcher needs to know about one reporting VS Code window.</summary>
internal sealed record MatchRequest(
    string SessionId,
    string? WorkspaceName,
    TitleNameMode TitleNameMode,
    string TitleSeparator,
    /// <summary>Processes that may own this session's window: ancestors of its extension host.</summary>
    IReadOnlySet<int> AllowedProcessIds,
    /// <summary>Window bound earlier by focus correlation, or IntPtr.Zero.</summary>
    IntPtr BoundWindow);

internal enum MatchStatus
{
    /// <summary>Exactly one window carries this workspace name in its title.</summary>
    MatchedByTitle,
    /// <summary>Bound by correlating the extension's focus report with the foreground window.</summary>
    MatchedByFocus,
    /// <summary>Several windows could be this one; nothing is applied until it is focused.</summary>
    Ambiguous,
    /// <summary>No open window shows this workspace name.</summary>
    NotFound,
    /// <summary>window.title does not include the workspace name; focus the window to bind it.</summary>
    NoTitleInfo,
}

internal sealed record MatchResult(string SessionId, MatchStatus Status, IntPtr Window, IReadOnlyList<IntPtr> Candidates)
{
    public bool IsMatched => Status is MatchStatus.MatchedByTitle or MatchStatus.MatchedByFocus;
}

/// <summary>
/// Maps reporting VS Code sessions to taskbar windows. The rule is: never guess. A session
/// gets a window only when (a) it was bound by focus correlation, or (b) its workspace name
/// appears as a whole title segment in exactly one window, and that window is not claimed by
/// any other unresolved session.
///
/// The workspace name the extension reports is vscode.workspace.name, which VS Code computes
/// with the same labelService.getWorkspaceLabel() call as the ${rootName} title variable, so
/// remote windows include the " [SSH: host]" suffix in both. That suffix is what tells
/// identically named folders on different SSH hosts apart.
/// </summary>
internal static class WindowMatcher
{
    /// <summary>VS Code prefixes the title with this when the active editor is dirty.</summary>
    public const string DirtyMarker = "● ";

    public static IReadOnlyList<MatchResult> Match(IEnumerable<MatchRequest> requests, IEnumerable<WindowInfo> windows)
    {
        var requestList = requests.ToList();
        var windowList = windows.ToList();
        var byHandle = windowList.ToDictionary(w => w.Handle);
        var results = new Dictionary<string, MatchResult>();
        var taken = new HashSet<IntPtr>();

        // 1. Focus bindings are authoritative while the window still exists.
        foreach (var r in requestList)
        {
            if (r.BoundWindow == IntPtr.Zero || taken.Contains(r.BoundWindow)) continue;
            if (byHandle.TryGetValue(r.BoundWindow, out var w) && r.AllowedProcessIds.Contains(w.ProcessId))
            {
                taken.Add(w.Handle);
                results[r.SessionId] = new MatchResult(r.SessionId, MatchStatus.MatchedByFocus, w.Handle, new[] { w.Handle });
            }
        }

        var pending = requestList.Where(r => !results.ContainsKey(r.SessionId)).ToList();
        foreach (var r in pending.Where(r => r.TitleNameMode == TitleNameMode.None || string.IsNullOrEmpty(r.WorkspaceName)))
            results[r.SessionId] = new MatchResult(r.SessionId, MatchStatus.NoTitleInfo, IntPtr.Zero, Array.Empty<IntPtr>());
        pending.RemoveAll(r => results.ContainsKey(r.SessionId));

        // 2. Exact title matches.
        var exact = pending.ToDictionary(
            r => r.SessionId,
            r => windowList.Where(w => !taken.Contains(w.Handle) && r.AllowedProcessIds.Contains(w.ProcessId) && TitleMatches(r, w.Title, loose: false))
                           .Select(w => w.Handle).ToList());
        AssignUnique(pending, exact, taken, results);

        // 3. Loose matches (one side lacks the "[SSH: host]" suffix) for sessions with no exact
        //    candidate, never using a window that some session matched exactly.
        var exactlyClaimed = exact.Values.SelectMany(c => c).ToHashSet();
        var loosePending = pending.Where(r => !results.ContainsKey(r.SessionId) && exact[r.SessionId].Count == 0).ToList();
        var loose = loosePending.ToDictionary(
            r => r.SessionId,
            r => windowList.Where(w => !taken.Contains(w.Handle) && !exactlyClaimed.Contains(w.Handle)
                                       && r.AllowedProcessIds.Contains(w.ProcessId) && TitleMatches(r, w.Title, loose: true))
                           .Select(w => w.Handle).ToList());
        AssignUnique(loosePending, loose, taken, results);

        foreach (var r in pending.Where(r => !results.ContainsKey(r.SessionId)))
        {
            // For diagnostics, report every window this session could plausibly be, including
            // ones another session claimed, so "contested" is distinguishable from "missing".
            var candidates = exact[r.SessionId].Count > 0
                ? exact[r.SessionId]
                : windowList.Where(w => r.AllowedProcessIds.Contains(w.ProcessId) && TitleMatches(r, w.Title, loose: true))
                            .Select(w => w.Handle).ToList();
            results[r.SessionId] = new MatchResult(
                r.SessionId,
                candidates.Count > 0 ? MatchStatus.Ambiguous : MatchStatus.NotFound,
                IntPtr.Zero,
                candidates);
        }

        return requestList.Select(r => results[r.SessionId]).ToList();
    }

    /// <summary>Repeatedly assigns sessions with exactly one candidate that no other open session wants.</summary>
    static void AssignUnique(
        List<MatchRequest> sessions,
        Dictionary<string, List<IntPtr>> candidates,
        HashSet<IntPtr> taken,
        Dictionary<string, MatchResult> results)
    {
        bool progress = true;
        while (progress)
        {
            progress = false;
            var open = sessions.Where(s => !results.ContainsKey(s.SessionId)).ToList();
            foreach (var s in open)
            {
                var mine = candidates[s.SessionId].Where(h => !taken.Contains(h)).ToList();
                if (mine.Count != 1) continue;
                var window = mine[0];
                bool contested = open.Any(o => o.SessionId != s.SessionId
                                               && !results.ContainsKey(o.SessionId)
                                               && candidates[o.SessionId].Contains(window));
                if (contested) continue;
                taken.Add(window);
                results[s.SessionId] = new MatchResult(s.SessionId, MatchStatus.MatchedByTitle, window, new[] { window });
                progress = true;
            }
        }
    }

    /// <summary>True when the title shows this session's workspace as a whole " - "-separated segment.</summary>
    public static bool TitleMatches(MatchRequest request, string title, bool loose)
    {
        if (string.IsNullOrEmpty(request.WorkspaceName) || request.TitleNameMode == TitleNameMode.None)
            return false;
        string name = request.WorkspaceName;
        string expected = request.TitleNameMode == TitleNameMode.RootNameShort ? StripRemoteSuffix(name) : name;

        foreach (string segment in TitleSegments(title, request.TitleSeparator))
        {
            if (segment == expected) return true;
            if (!loose) continue;
            // Loose: tolerate exactly one side missing the "[...]" remote suffix, but never
            // two different suffixes ("proj [SSH: a]" must not match "proj [SSH: b]").
            bool segmentHasSuffix = HasRemoteSuffix(segment), nameHasSuffix = HasRemoteSuffix(name);
            if (segmentHasSuffix != nameHasSuffix && StripRemoteSuffix(segment) == StripRemoteSuffix(name))
                return true;
        }
        return false;
    }

    public static IEnumerable<string> TitleSegments(string title, string separator)
    {
        if (string.IsNullOrEmpty(title)) yield break;
        if (title.StartsWith(DirtyMarker, StringComparison.Ordinal))
            title = title[DirtyMarker.Length..];
        if (string.IsNullOrEmpty(separator)) separator = " - ";
        foreach (string part in title.Split(separator))
        {
            string trimmed = part.Trim();
            if (trimmed.StartsWith(DirtyMarker, StringComparison.Ordinal))
                trimmed = trimmed[DirtyMarker.Length..].Trim();
            if (trimmed.Length > 0) yield return trimmed;
        }
    }

    public static bool HasRemoteSuffix(string name) =>
        name.EndsWith(']') && name.LastIndexOf(" [", StringComparison.Ordinal) > 0;

    /// <summary>"proj [SSH: host]" → "proj".</summary>
    public static string StripRemoteSuffix(string name)
    {
        if (!HasRemoteSuffix(name)) return name;
        return name[..name.LastIndexOf(" [", StringComparison.Ordinal)];
    }
}
