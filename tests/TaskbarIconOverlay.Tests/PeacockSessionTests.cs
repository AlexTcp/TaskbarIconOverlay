using TaskbarIconOverlay.Peacock;
using Xunit;

namespace TaskbarIconOverlay.Tests;

public class PeacockSessionTests
{
    static string Fixture() => File.ReadAllText(Path.Combine(FindRepoRoot(), "tests", "fixtures", "session-v1.json"));

    internal static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "TaskbarIconOverlay.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root");
    }

    [Fact]
    public void Parses_the_shared_extension_fixture()
    {
        var s = PeacockSession.Parse(Fixture(), out string? error);
        Assert.Null(error);
        Assert.NotNull(s);
        Assert.Equal("4242-a1b2c3d4", s!.SessionId);
        Assert.Equal(4242, s.Pid);
        Assert.Equal(1000, s.ParentPid);
        Assert.Equal("api [SSH: build-01]", s.WorkspaceName);
        Assert.Equal("ssh-remote", s.RemoteName);
        Assert.Equal("build-01", s.RemoteHost);
        Assert.Equal(TitleNameMode.RootName, s.TitleNameMode);
        Assert.Equal(new PeacockColor(0x83, 0x25, 0x61), s.Color);
        Assert.Equal("peacock.remoteColor", s.ColorSource);
        Assert.True(s.Focused);
        Assert.Equal(1793190005000, s.FocusedAtMs);
    }

    [Fact]
    public void Missing_colour_is_reported_with_reason()
    {
        var s = PeacockSession.Parse("""{"schemaVersion":1,"sessionId":"x","pid":5,"color":null,"colorReason":"No Peacock colour"}""", out _);
        Assert.Null(s!.Color);
        Assert.Equal("No Peacock colour", s.ColorReason);
    }

    [Fact]
    public void Invalid_colour_is_ignored_with_reason()
    {
        var s = PeacockSession.Parse("""{"schemaVersion":1,"sessionId":"x","pid":5,"color":"bogus"}""", out _);
        Assert.Null(s!.Color);
        Assert.Contains("bogus", s.ColorReason);
    }

    [Theory]
    [InlineData("""{"schemaVersion":2,"sessionId":"x","pid":5}""", "schemaVersion")]
    [InlineData("""{"schemaVersion":1,"pid":5}""", "sessionId")]
    [InlineData("""{"schemaVersion":1,"sessionId":"x"}""", "pid")]
    [InlineData("""[1,2]""", "object")]
    [InlineData("""{"schemaVersion":1,"sessi""", "JSON")]
    public void Rejects_bad_reports(string json, string errorFragment)
    {
        Assert.Null(PeacockSession.Parse(json, out string? error));
        Assert.Contains(errorFragment, error);
    }

    [Theory]
    [InlineData("rootNameShort", (int)TitleNameMode.RootNameShort)]
    [InlineData("none", (int)TitleNameMode.None)]
    [InlineData("rootName", (int)TitleNameMode.RootName)]
    [InlineData("future-mode", (int)TitleNameMode.RootName)]
    public void Parses_title_mode(string mode, int expectedMode)
    {
        var expected = (TitleNameMode)expectedMode;
        var s = PeacockSession.Parse($$"""{"schemaVersion":1,"sessionId":"x","pid":5,"titleNameMode":"{{mode}}"}""", out _);
        Assert.Equal(expected, s!.TitleNameMode);
    }
}

public class PeacockColorTests
{
    [Theory]
    [InlineData("#ff0000", 255, 0, 0)]
    [InlineData("FF0000", 255, 0, 0)]
    [InlineData("#f00", 255, 0, 0)]
    [InlineData(" #42b883 ", 0x42, 0xb8, 0x83)]
    [InlineData("#42b883ff", 0x42, 0xb8, 0x83)]
    public void Parses_hex(string text, int r, int g, int b)
    {
        Assert.True(PeacockColor.TryParse(text, out var c));
        Assert.Equal(new PeacockColor((byte)r, (byte)g, (byte)b), c);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("red")] // named colours are normalized by the extension, not here
    [InlineData("#12345")]
    [InlineData("#gggggg")]
    public void Rejects_non_hex(string? text) => Assert.False(PeacockColor.TryParse(text, out _));

    [Fact]
    public void Contrasting_colour_is_light_for_dark_and_dark_for_light()
    {
        Assert.Equal(new PeacockColor(255, 255, 255), new PeacockColor(0, 0, 0).ContrastingColor());
        Assert.Equal(new PeacockColor(255, 255, 255), new PeacockColor(0x83, 0x25, 0x61).ContrastingColor());
        Assert.Equal(new PeacockColor(30, 30, 30), new PeacockColor(255, 255, 0).ContrastingColor());
    }

    [Fact]
    public void Hsl_round_trips()
    {
        foreach (var c in new[] { new PeacockColor(0x83, 0x25, 0x61), new PeacockColor(0, 0x7a, 0xcc), new PeacockColor(128, 128, 128) })
        {
            var (h, s, l) = c.ToHsl();
            Assert.Equal(c, PeacockColor.FromHsl(h, s, l));
        }
    }
}

public class AncestorTests
{
    [Fact]
    public void Walks_parent_chain_to_the_main_process()
    {
        // extension host 300 -> main 200 -> explorer 100
        var parents = new Dictionary<int, int> { [300] = 200, [200] = 100, [100] = 4 };
        var result = PeacockManager.Ancestors(300, 200, parents);
        Assert.Contains(200, result);
        Assert.Contains(100, result);
        Assert.DoesNotContain(300, result);
    }

    [Fact]
    public void Survives_cycles_from_pid_reuse_and_missing_entries()
    {
        var parents = new Dictionary<int, int> { [300] = 200, [200] = 300 };
        var result = PeacockManager.Ancestors(300, 200, parents);
        Assert.Contains(200, result);
        Assert.Contains(PeacockManager.Ancestors(999, 50, new Dictionary<int, int>()), p => p == 50);
    }
}
