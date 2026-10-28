namespace TaskbarIconOverlay.Peacock;

/// <summary>Converts between HICONs and straight-alpha ARGB pixel arrays.</summary>
internal static class NativeIcons
{
    /// <summary>Extracts the first icon of an executable at exactly <paramref name="size"/> pixels.</summary>
    public static uint[]? ExtractPixels(string exePath, int size)
    {
        var handles = new IntPtr[1];
        var ids = new uint[1];
        uint count = Native.PrivateExtractIcons(exePath, 0, size, size, handles, ids, 1, 0);
        if (count == 0 || count == uint.MaxValue || handles[0] == IntPtr.Zero) return null;
        try
        {
            return GetPixels(handles[0], size);
        }
        finally
        {
            Native.DestroyIcon(handles[0]);
        }
    }

    public static uint[]? GetPixels(IntPtr hIcon, int size)
    {
        if (!Native.GetIconInfo(hIcon, out var info)) return null;
        IntPtr dc = Native.GetDC(IntPtr.Zero);
        try
        {
            if (info.hbmColor == IntPtr.Zero) return null; // monochrome icon
            Native.GetObject(info.hbmColor, System.Runtime.InteropServices.Marshal.SizeOf<Native.BITMAP>(), out var bm);
            if (bm.bmWidth != size || Math.Abs(bm.bmHeight) != size) return null;

            var color = new uint[size * size];
            var header = Native.BITMAPINFOHEADER.Top32(size, size);
            if (Native.GetDIBits(dc, info.hbmColor, 0, (uint)size, color, ref header, 0) == 0) return null;

            bool hasAlpha = color.Any(p => (p >> 24) != 0);
            if (!hasAlpha && info.hbmMask != IntPtr.Zero)
            {
                // Legacy icon without an alpha channel: derive alpha from the AND mask.
                var mask = new uint[size * size];
                var maskHeader = Native.BITMAPINFOHEADER.Top32(size, size);
                if (Native.GetDIBits(dc, info.hbmMask, 0, (uint)size, mask, ref maskHeader, 0) != 0)
                {
                    for (int i = 0; i < color.Length; i++)
                        color[i] = (mask[i] & 0x00FFFFFF) != 0 ? 0 : color[i] | 0xFF000000;
                }
            }
            return color;
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, dc);
            if (info.hbmColor != IntPtr.Zero) Native.DeleteObject(info.hbmColor);
            if (info.hbmMask != IntPtr.Zero) Native.DeleteObject(info.hbmMask);
        }
    }

    /// <summary>Creates a 32-bit alpha icon. The caller owns the handle (DestroyIcon).</summary>
    public static IntPtr CreateIcon(uint[] argb, int size)
    {
        var header = Native.BITMAPINFOHEADER.Top32(size, size);
        IntPtr color = Native.CreateDIBSection(IntPtr.Zero, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (color == IntPtr.Zero) return IntPtr.Zero;
        // With a 32-bit colour bitmap the alpha channel decides transparency; an all-zero
        // AND mask is correct.
        int stride = ((size + 31) / 32) * 4;
        IntPtr mask = Native.CreateBitmap(size, size, 1, 1, new byte[stride * size]);
        try
        {
            var asInt = System.Runtime.InteropServices.MemoryMarshal.Cast<uint, int>(argb).ToArray();
            System.Runtime.InteropServices.Marshal.Copy(asInt, 0, bits, asInt.Length);
            var info = new Native.ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
            return Native.CreateIconIndirect(ref info);
        }
        finally
        {
            Native.DeleteObject(color);
            if (mask != IntPtr.Zero) Native.DeleteObject(mask);
        }
    }
}
