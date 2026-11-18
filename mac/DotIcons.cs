using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ClaudeCodeIndicator;

/// <summary>Menu-bar icons: <c>&lt;state&gt;.png</c> if one ships with the app, else a colored dot.</summary>
internal static class DotIcons
{
    private const int Size = 36;            // 18 pt at 2x (Retina), the menu-bar icon size

    public static WindowIcon Load(IndicatorState state)
    {
        string name = $"{state.ToString().ToLowerInvariant()}.png";
        foreach (string dir in AppResources.Directories())
        {
            string path = Path.Combine(dir, name);
            if (!File.Exists(path)) continue;
            try { return new WindowIcon(path); }
            catch { /* bad file -> generated fallback */ }
        }
        return new WindowIcon(Draw(IndicatorStates.Rgb(state)));
    }

    /// <summary>An anti-aliased filled circle with a faint dark rim, like the Windows dot.</summary>
    private static Bitmap Draw((byte R, byte G, byte B) color)
    {
        var bmp = new WriteableBitmap(new PixelSize(Size, Size), new Vector(192, 192),
                                      PixelFormat.Bgra8888, AlphaFormat.Premul);
        const double c = (Size - 1) / 2.0, radius = Size * 13 / 32.0;
        var row = new byte[Size * 4];
        using var fb = bmp.Lock();
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                double d = Math.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                double cover = Math.Clamp(radius + 0.5 - d, 0, 1);           // edge anti-aliasing
                double rim = Math.Clamp(1.5 - Math.Abs(radius - 0.75 - d), 0, 1) * 0.27; // darken toward the edge
                double r = color.R * (1 - rim), g = color.G * (1 - rim), b = color.B * (1 - rim);
                int i = x * 4;
                row[i + 0] = (byte)Math.Round(b * cover);  // premultiplied BGRA
                row[i + 1] = (byte)Math.Round(g * cover);
                row[i + 2] = (byte)Math.Round(r * cover);
                row[i + 3] = (byte)Math.Round(255 * cover);
            }
            Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
        }
        return bmp;
    }
}

/// <summary>Where files that ship with the app live: Contents/Resources in the .app, else next to the exe.</summary>
internal static class AppResources
{
    public static string[] Directories() => new[]
    {
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources")),
        AppContext.BaseDirectory,
    };

    public static string? Find(string relativePath)
    {
        foreach (string dir in Directories())
        {
            string p = Path.Combine(dir, relativePath);
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
