using System.Globalization;

namespace TaskbarIconOverlay.Peacock;

/// <summary>An opaque sRGB colour. The companion extension already normalizes Peacock's
/// formats (named colours, rgb(), hsl(), ...) to "#rrggbb"; this side only accepts hex.</summary>
internal readonly record struct PeacockColor(byte R, byte G, byte B)
{
    public static bool TryParse(string? text, out PeacockColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string hex = text.Trim().TrimStart('#');
        if (hex.Length == 3)
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        if (hex.Length != 6 && hex.Length != 8) return false;
        if (!uint.TryParse(hex[..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
            return false;
        color = new PeacockColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    public int ToRgbInt() => (R << 16) | (G << 8) | B;

    public override string ToString() => $"#{R:x2}{G:x2}{B:x2}";

    /// <summary>WCAG relative luminance, 0 (black) to 1 (white).</summary>
    public double RelativeLuminance
    {
        get
        {
            static double Lin(byte c)
            {
                double v = c / 255.0;
                return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Lin(R) + 0.7152 * Lin(G) + 0.0722 * Lin(B);
        }
    }

    /// <summary>White or near-black, whichever contrasts more with this colour.</summary>
    public PeacockColor ContrastingColor()
    {
        var white = new PeacockColor(255, 255, 255);
        var dark = new PeacockColor(30, 30, 30);
        return ContrastRatio(this, white) >= ContrastRatio(this, dark) ? white : dark;
    }

    public static double ContrastRatio(PeacockColor a, PeacockColor b)
    {
        double la = a.RelativeLuminance, lb = b.RelativeLuminance;
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    public (double H, double S, double L) ToHsl() => RgbToHsl(R, G, B);

    public static (double H, double S, double L) RgbToHsl(byte r, byte g, byte b)
    {
        double rn = r / 255.0, gn = g / 255.0, bn = b / 255.0;
        double max = Math.Max(rn, Math.Max(gn, bn)), min = Math.Min(rn, Math.Min(gn, bn));
        double l = (max + min) / 2;
        if (max == min) return (0, 0, l);
        double d = max - min;
        double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (max == rn) h = (gn - bn) / d + (gn < bn ? 6 : 0);
        else if (max == gn) h = (bn - rn) / d + 2;
        else h = (rn - gn) / d + 4;
        return (h * 60, s, l);
    }

    public static PeacockColor FromHsl(double h, double s, double l)
    {
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);
        if (s == 0)
        {
            byte v = ToByte(l);
            return new PeacockColor(v, v, v);
        }
        double hn = ((h % 360) + 360) % 360 / 360;
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        double Hue(double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
        return new PeacockColor(ToByte(Hue(hn + 1.0 / 3)), ToByte(Hue(hn)), ToByte(Hue(hn - 1.0 / 3)));
    }

    static byte ToByte(double unit) => (byte)Math.Clamp((int)Math.Round(unit * 255), 0, 255);
}
