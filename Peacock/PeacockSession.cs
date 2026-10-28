using System.Text.Json;

namespace TaskbarIconOverlay.Peacock;

/// <summary>How a window title names its workspace (from VS Code's window.title template).</summary>
internal enum TitleNameMode
{
    /// <summary>Title contains ${rootName}, e.g. "proj [SSH: host]".</summary>
    RootName,
    /// <summary>Title contains ${rootNameShort}, e.g. "proj".</summary>
    RootNameShort,
    /// <summary>Title does not name the workspace; only focus correlation can match.</summary>
    None,
}

/// <summary>
/// One VS Code window as reported by the companion extension. Mirrors SessionReport in
/// vscode-extension/src/report.ts (schema version 1).
/// </summary>
internal sealed record PeacockSession
{
    public const int SchemaVersion = 1;
    public const string FileSuffix = ".session.json";

    public required string SessionId { get; init; }
    /// <summary>The window's local extension host process.</summary>
    public int Pid { get; init; }
    public int ParentPid { get; init; }
    public long ProcessStartMs { get; init; }
    public string AppName { get; init; } = "";
    /// <summary>VS Code's ${rootName}: identical to what the window title shows.</summary>
    public string? WorkspaceName { get; init; }
    public string WorkspaceKind { get; init; } = "none";
    public string? WorkspaceId { get; init; }
    public string? RemoteName { get; init; }
    public string? RemoteHost { get; init; }
    public TitleNameMode TitleNameMode { get; init; } = TitleNameMode.RootName;
    public string TitleSeparator { get; init; } = " - ";
    public PeacockColor? Color { get; init; }
    public string? ColorSource { get; init; }
    public string? ColorReason { get; init; }
    public bool Focused { get; init; }
    public long FocusedAtMs { get; init; }
    public long UpdatedAtMs { get; init; }

    public string DisplayName => WorkspaceName ?? "(no workspace)";

    public static PeacockSession? Parse(string json, out string? error)
    {
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "not a JSON object";
                return null;
            }

            int schema = GetInt(root, "schemaVersion");
            if (schema != SchemaVersion)
            {
                error = $"unsupported schemaVersion {schema} (expected {SchemaVersion}); update the extension or TaskbarIconOverlay";
                return null;
            }

            string? sessionId = GetString(root, "sessionId");
            int pid = GetInt(root, "pid");
            if (string.IsNullOrWhiteSpace(sessionId) || pid <= 0)
            {
                error = "missing sessionId or pid";
                return null;
            }

            string? colorText = GetString(root, "color");
            PeacockColor? color = PeacockColor.TryParse(colorText, out var parsed) ? parsed : null;

            return new PeacockSession
            {
                SessionId = sessionId,
                Pid = pid,
                ParentPid = GetInt(root, "ppid"),
                ProcessStartMs = GetLong(root, "processStartMs"),
                AppName = GetString(root, "appName") ?? "",
                WorkspaceName = NullIfEmpty(GetString(root, "workspaceName")),
                WorkspaceKind = GetString(root, "workspaceKind") ?? "none",
                WorkspaceId = GetString(root, "workspaceId"),
                RemoteName = NullIfEmpty(GetString(root, "remoteName")),
                RemoteHost = NullIfEmpty(GetString(root, "remoteHost")),
                TitleNameMode = GetString(root, "titleNameMode") switch
                {
                    "rootNameShort" => TitleNameMode.RootNameShort,
                    "none" => TitleNameMode.None,
                    _ => TitleNameMode.RootName,
                },
                TitleSeparator = NullIfEmpty(GetString(root, "titleSeparator")) ?? " - ",
                Color = color,
                ColorSource = GetString(root, "colorSource"),
                ColorReason = color == null && colorText != null
                    ? $"invalid colour \"{colorText}\""
                    : GetString(root, "colorReason"),
                Focused = root.TryGetProperty("focused", out var f) && f.ValueKind == JsonValueKind.True,
                FocusedAtMs = GetLong(root, "focusedAtMs"),
                UpdatedAtMs = GetLong(root, "updatedAtMs"),
            };
        }
        catch (JsonException ex)
        {
            error = "invalid JSON: " + ex.Message;
            return null;
        }
    }

    static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static long GetLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : 0;

    static int GetInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
}
