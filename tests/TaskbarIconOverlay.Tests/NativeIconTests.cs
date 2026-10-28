using TaskbarIconOverlay.Peacock;
using Xunit;

namespace TaskbarIconOverlay.Tests;

/// <summary>Exercises real Win32 icon APIs (runs on any Windows machine, no desktop interaction).</summary>
public class NativeIconTests
{
    [Fact]
    public void Created_icon_round_trips_pixels_including_alpha()
    {
        const int size = 32;
        var pixels = IconRecolorer.CreateBadge(size, new PeacockColor(0x83, 0x25, 0x61));
        IntPtr icon = NativeIcons.CreateIcon(pixels, size);
        Assert.NotEqual(IntPtr.Zero, icon);
        try
        {
            var back = NativeIcons.GetPixels(icon, size);
            Assert.NotNull(back);
            Assert.Equal(pixels, back);
        }
        finally
        {
            Native.DestroyIcon(icon);
        }
    }

    [Fact]
    public void Extracts_and_recolours_an_application_icon()
    {
        // Any Windows executable with an icon works; VS Code itself may not be installed on CI.
        string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var source = NativeIcons.ExtractPixels(exe, 32);
        Assert.NotNull(source);
        Assert.Contains(source!, p => (p >> 24) == 0);   // has transparency
        Assert.Contains(source!, p => (p >> 24) == 255); // has opaque pixels

        var recolored = IconRecolorer.Recolor(source!, 32, new PeacockColor(0xe0, 0x20, 0x20));
        IntPtr icon = NativeIcons.CreateIcon(recolored, 32);
        Assert.NotEqual(IntPtr.Zero, icon);
        Native.DestroyIcon(icon);
    }
}
