using TaskbarIconOverlay.Peacock;
using Xunit;

namespace TaskbarIconOverlay.Tests;

public class IconRecolorerTests
{
    const int Size = 16;
    static readonly PeacockColor VsCodeBlue = new(0x00, 0x7a, 0xcc);
    static readonly PeacockColor Red = new(0xe0, 0x20, 0x20);

    static uint Argb(byte a, PeacockColor c) => ((uint)a << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    /// <summary>A fake logo: transparent border, blue body in two shades, one white pixel.</summary>
    static uint[] FakeLogo()
    {
        var px = new uint[Size * Size];
        for (int y = 2; y < 14; y++)
        for (int x = 2; x < 14; x++)
            px[y * Size + x] = Argb(255, x < 8 ? VsCodeBlue : new PeacockColor(0x1f, 0x9c, 0xf0));
        px[3 * Size + 3] = Argb(128, VsCodeBlue); // anti-aliased edge
        px[8 * Size + 8] = Argb(255, new PeacockColor(255, 255, 255));
        return px;
    }

    static double Hue(uint p) => PeacockColor.RgbToHsl((byte)(p >> 16), (byte)(p >> 8), (byte)p).H;

    [Fact]
    public void Replaces_the_blue_with_the_target_hue_and_keeps_transparency()
    {
        var src = FakeLogo();
        var dst = IconRecolorer.Recolor(src, Size, Red);

        for (int i = 0; i < src.Length; i++)
            Assert.Equal(src[i] >> 24, dst[i] >> 24);
        Assert.Equal(0u, dst[0]);
        double hue = Hue(dst[5 * Size + 5]);
        Assert.True(hue < 5 || hue > 355, $"expected red hue, got {hue}");
        Assert.Equal(src[8 * Size + 8], dst[8 * Size + 8]); // white highlight untouched
    }

    [Theory]
    [InlineData(0x6e, 0x37, 0x34)]
    [InlineData(0x29, 0x29, 0x29)]
    [InlineData(0xe6, 0xe6, 0xe6)]
    [InlineData(0xb8, 0x73, 0x33)]
    public void Main_shade_becomes_exactly_the_target_colour(int r, int g, int b)
    {
        // Large light panel (the main shade) plus a smaller dark fold, like VS Code's logo.
        var src = new uint[Size * Size];
        for (int y = 2; y < 14; y++)
        for (int x = 2; x < 14; x++)
            src[y * Size + x] = Argb(255, x < 5 ? new PeacockColor(0x00, 0x77, 0xb9) : new PeacockColor(0x24, 0xac, 0xf2));
        var target = new PeacockColor((byte)r, (byte)g, (byte)b);

        var dst = IconRecolorer.Recolor(src, Size, target);

        Assert.Equal(Argb(255, target), dst[5 * Size + 10]);
        Assert.NotEqual(dst[5 * Size + 10], dst[5 * Size + 3]); // fold keeps a distinct shade
    }

    [Fact]
    public void Keeps_the_logos_relative_shading()
    {
        var dst = IconRecolorer.Recolor(FakeLogo(), Size, Red);
        double darkSide = PeacockColor.RgbToHsl((byte)(dst[5 * Size + 3] >> 16), (byte)(dst[5 * Size + 3] >> 8), (byte)dst[5 * Size + 3]).L;
        double lightSide = PeacockColor.RgbToHsl((byte)(dst[5 * Size + 12] >> 16), (byte)(dst[5 * Size + 12] >> 8), (byte)dst[5 * Size + 12]).L;
        Assert.True(lightSide > darkSide);
    }

    [Fact]
    public void Gray_target_produces_gray_icon()
    {
        var dst = IconRecolorer.Recolor(FakeLogo(), Size, new PeacockColor(128, 128, 128));
        uint p = dst[5 * Size + 5];
        Assert.Equal((byte)(p >> 16), (byte)(p >> 8));
        Assert.Equal((byte)(p >> 8), (byte)p);
    }

    [Fact]
    public void Black_target_gets_a_light_outline_so_it_stays_visible()
    {
        var dst = IconRecolorer.Recolor(FakeLogo(), Size, new PeacockColor(0, 0, 0));
        uint outline = dst[1 * Size + 5]; // just outside the body
        Assert.True((outline >> 24) > 0);
        Assert.True((byte)(outline >> 16) > 200);
    }

    [Fact]
    public void Icon_without_dominant_hue_is_tinted_entirely()
    {
        var gray = Enumerable.Repeat(Argb(255, new PeacockColor(100, 100, 100)), Size * Size).ToArray();
        var dst = IconRecolorer.Recolor(gray, Size, Red);
        Assert.True(Hue(dst[0]) < 5 || Hue(dst[0]) > 355);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void Badge_is_a_round_disc_with_contrasting_ring(int size)
    {
        var badge = IconRecolorer.CreateBadge(size, new PeacockColor(0x83, 0x25, 0x61));
        Assert.Equal(0u, badge[0] >> 24); // corner transparent
        uint center = badge[(size / 2) * size + size / 2];
        Assert.Equal(255u, center >> 24);
        Assert.Equal(new PeacockColor(0x83, 0x25, 0x61), new PeacockColor((byte)(center >> 16), (byte)(center >> 8), (byte)center));
        uint ring = badge[(size / 2) * size]; // left edge, middle row
        Assert.True((byte)(ring >> 16) > 0x83); // blended toward white
    }

    [Fact]
    public void Light_badge_gets_dark_ring()
    {
        int size = 16;
        var badge = IconRecolorer.CreateBadge(size, new PeacockColor(255, 255, 0));
        uint ring = badge[(size / 2) * size];
        Assert.True((byte)(ring >> 8) < 200);
    }
}

public class IconCacheTests
{
    sealed class FakeIcons
    {
        public int Created;
        public readonly List<IntPtr> Destroyed = new();
        public IntPtr Create(IconKey key) => new(++Created);
        public void Destroy(IntPtr h) => Destroyed.Add(h);
    }

    static IconKey Key(int rgb, int size = 32) => new("code.exe", size, rgb, IconKind.Recolored);

    [Fact]
    public void Same_colour_and_size_reuses_the_handle()
    {
        var fake = new FakeIcons();
        using var cache = new IconCache(fake.Create, fake.Destroy);
        IntPtr a = cache.Acquire(Key(0xff0000));
        IntPtr b = cache.Acquire(Key(0xff0000));
        Assert.Equal(a, b);
        Assert.Equal(1, fake.Created);
        Assert.NotEqual(a, cache.Acquire(Key(0xff0000, size: 16)));
        Assert.NotEqual(a, cache.Acquire(Key(0x00ff00)));
    }

    [Fact]
    public void Handles_in_use_are_never_destroyed()
    {
        var fake = new FakeIcons();
        var cache = new IconCache(fake.Create, fake.Destroy, idleCapacity: 0);
        IntPtr inUse = cache.Acquire(Key(1));
        cache.Acquire(Key(2));
        cache.Release(Key(2));
        Assert.Single(fake.Destroyed);
        Assert.DoesNotContain(inUse, fake.Destroyed);
    }

    [Fact]
    public void Idle_handles_are_kept_up_to_capacity_then_destroyed_oldest_first()
    {
        var fake = new FakeIcons();
        var cache = new IconCache(fake.Create, fake.Destroy, idleCapacity: 2);
        var handles = Enumerable.Range(1, 3).Select(i => cache.Acquire(Key(i))).ToList();
        foreach (int i in Enumerable.Range(1, 3)) cache.Release(Key(i));
        Assert.Equal(new[] { handles[0] }, fake.Destroyed);
        Assert.Equal(2, cache.Count);

        // Re-acquiring a cached idle colour does not create a new icon.
        cache.Acquire(Key(3));
        Assert.Equal(3, fake.Created);
    }

    [Fact]
    public void Dispose_destroys_everything_and_failed_creation_is_not_cached()
    {
        var fake = new FakeIcons();
        var cache = new IconCache(k => k.Rgb == 0 ? IntPtr.Zero : fake.Create(k), fake.Destroy);
        Assert.Equal(IntPtr.Zero, cache.Acquire(Key(0)));
        cache.Acquire(Key(1));
        cache.Dispose();
        Assert.Single(fake.Destroyed);
        Assert.Equal(0, cache.Count);
    }
}
