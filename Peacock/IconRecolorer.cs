namespace TaskbarIconOverlay.Peacock;

/// <summary>
/// Pixel operations on straight-alpha ARGB images (0xAARRGGBB, row-major, top-down).
/// </summary>
internal static class IconRecolorer
{
    const double MinSaturation = 0.25;
    const double HueTolerance = 45;

    /// <summary>
    /// Replaces the icon's dominant hue (VS Code's blue) with <paramref name="target"/>, keeping
    /// the silhouette, transparency and the logo's light/dark shading. Pixels in other hues are
    /// left untouched. Icons with no dominant hue are tinted entirely.
    /// </summary>
    public static uint[] Recolor(uint[] source, int size, PeacockColor target)
    {
        var (targetH, targetS, targetL) = target.ToHsl();
        int n = source.Length;
        var hsl = new (double H, double S, double L)[n];
        var selected = new bool[n];

        // Dominant hue: circular mean weighted by alpha × saturation.
        double sx = 0, sy = 0, weightSum = 0;
        for (int i = 0; i < n; i++)
        {
            uint p = source[i];
            byte a = (byte)(p >> 24);
            hsl[i] = PeacockColor.RgbToHsl((byte)(p >> 16), (byte)(p >> 8), (byte)p);
            if (a < 32 || hsl[i].S < MinSaturation || hsl[i].L < 0.05 || hsl[i].L > 0.97) continue;
            double w = a / 255.0 * hsl[i].S;
            double rad = hsl[i].H * Math.PI / 180;
            sx += Math.Cos(rad) * w;
            sy += Math.Sin(rad) * w;
            weightSum += w;
        }

        bool hasDominantHue = weightSum > 0;
        double dominant = Math.Atan2(sy, sx) * 180 / Math.PI;
        for (int i = 0; i < n; i++)
        {
            byte a = (byte)(source[i] >> 24);
            if (a == 0) continue;
            selected[i] = hasDominantHue
                ? hsl[i].S >= 0.1 && HueDistance(hsl[i].H, dominant) <= HueTolerance
                : true;
        }

        // Reference saturation/lightness of the selected region, and its lightness spread.
        double refS = 0, refL = 0, w2 = 0;
        for (int i = 0; i < n; i++)
        {
            if (!selected[i]) continue;
            double w = (byte)(source[i] >> 24) / 255.0;
            refS += hsl[i].S * w;
            refL += hsl[i].L * w;
            w2 += w;
        }
        var result = (uint[])source.Clone();
        if (w2 == 0) return result;
        refS /= w2;
        refL /= w2;

        double spread = 0;
        for (int i = 0; i < n; i++)
            if (selected[i]) spread = Math.Max(spread, Math.Abs(hsl[i].L - refL));
        // Keep the logo's shading but compress it so it stays within [0, 1] around the target.
        double room = Math.Min(targetL, 1 - targetL);
        double scale = spread > 1e-6 ? Math.Min(1, Math.Max(room, 0.04) / spread) : 1;

        for (int i = 0; i < n; i++)
        {
            if (!selected[i]) continue;
            double s = targetS == 0 ? 0 : Math.Clamp(refS > 1e-6 ? hsl[i].S / refS * targetS : targetS, 0, 1);
            double l = Math.Clamp(targetL + (hsl[i].L - refL) * scale, 0, 1);
            var c = PeacockColor.FromHsl(targetH, s, l);
            result[i] = (source[i] & 0xFF000000) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
        }

        // Near-black or near-white colours would vanish against a dark or light taskbar;
        // a 1px contrasting outline keeps the silhouette visible on both.
        double luminance = target.RelativeLuminance;
        if (luminance < 0.03 || luminance > 0.8)
            AddOutline(result, size, target.ContrastingColor(), 0xB0);

        return result;
    }

    /// <summary>
    /// A filled, anti-aliased disc in <paramref name="color"/> with a contrasting ring,
    /// readable at 16px on light and dark taskbars. Used for the overlay badge.
    /// </summary>
    public static uint[] CreateBadge(int size, PeacockColor color)
    {
        var pixels = new uint[size * size];
        var ring = color.ContrastingColor();
        double center = size / 2.0;
        double outer = size / 2.0;
        double ringWidth = Math.Max(1.25, size / 10.0);
        double inner = outer - ringWidth;
        const int ss = 4; // supersampling per axis

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int fill = 0, edge = 0;
            for (int sy = 0; sy < ss; sy++)
            for (int sxi = 0; sxi < ss; sxi++)
            {
                double px = x + (sxi + 0.5) / ss - center;
                double py = y + (sy + 0.5) / ss - center;
                double d = Math.Sqrt(px * px + py * py);
                if (d <= inner) fill++;
                else if (d <= outer) edge++;
            }
            int covered = fill + edge;
            if (covered == 0) continue;
            double alpha = covered / (double)(ss * ss);
            double t = edge / (double)covered;
            byte r = (byte)Math.Round(color.R * (1 - t) + ring.R * t);
            byte g = (byte)Math.Round(color.G * (1 - t) + ring.G * t);
            byte b = (byte)Math.Round(color.B * (1 - t) + ring.B * t);
            pixels[y * size + x] = ((uint)Math.Round(alpha * 255) << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
        }
        return pixels;
    }

    /// <summary>Adds a 1px outline around opaque regions, only where the image is transparent.</summary>
    public static void AddOutline(uint[] pixels, int size, PeacockColor color, byte alpha)
    {
        var original = (uint[])pixels.Clone();
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int i = y * size + x;
            if ((original[i] >> 24) >= 0x80) continue;
            bool touches = false;
            for (int dy = -1; dy <= 1 && !touches; dy++)
            for (int dx = -1; dx <= 1 && !touches; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= size || ny >= size) continue;
                touches = (original[ny * size + nx] >> 24) >= 0x80;
            }
            if (!touches) continue;
            pixels[i] = BlendUnder(original[i], color, alpha);
        }
    }

    /// <summary>Composites <paramref name="top"/> over a background of the given colour/alpha.</summary>
    static uint BlendUnder(uint top, PeacockColor under, byte underAlpha)
    {
        double ta = (top >> 24) / 255.0, ua = underAlpha / 255.0;
        double oa = ta + ua * (1 - ta);
        if (oa <= 0) return 0;
        double Mix(double t, double u) => (t * ta + u * ua * (1 - ta)) / oa;
        byte r = (byte)Math.Round(Mix((byte)(top >> 16), under.R));
        byte g = (byte)Math.Round(Mix((byte)(top >> 8), under.G));
        byte b = (byte)Math.Round(Mix((byte)top, under.B));
        return ((uint)Math.Round(oa * 255) << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
    }

    static double HueDistance(double a, double b)
    {
        double d = Math.Abs(((a - b) % 360 + 360) % 360);
        return d > 180 ? 360 - d : d;
    }
}
