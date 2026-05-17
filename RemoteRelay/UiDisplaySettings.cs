using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using RemoteRelay.Common;

namespace RemoteRelay;

public static class SourcePaletteBuilder
{
    public static Dictionary<string, Color> Build(AppSettings settings)
    {
        var palette = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);

        var orderedSources = settings.Sources.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        if (orderedSources.Count > 0)
        {
            double saturation = 0.65;
            double lightness = 0.5;

            var themePalette = settings.ThemePalette ?? "Default";

            if (string.Equals(themePalette, "Pastel", StringComparison.OrdinalIgnoreCase))
            {
                saturation = 0.4;
                lightness = 0.8;
            }
            else if (string.Equals(themePalette, "Dark", StringComparison.OrdinalIgnoreCase))
            {
                saturation = 0.7;
                lightness = 0.3;
            }
            else if (string.Equals(themePalette, "Vibrant", StringComparison.OrdinalIgnoreCase))
            {
                saturation = 0.9;
                lightness = 0.6;
            }

            for (var index = 0; index < orderedSources.Count; index++)
            {
                var sourceName = orderedSources[index];
                var hue = 360.0 * index / orderedSources.Count;
                palette[sourceName] = FromHsl(hue / 360.0, saturation, lightness);
            }
        }

        if (settings.SourceColorPalette != null && settings.SourceColorPalette.Count > 0)
        {
            foreach (var kvp in settings.SourceColorPalette)
            {
                if (!string.IsNullOrWhiteSpace(kvp.Value))
                {
                    palette[kvp.Key] = TryParseColour(kvp.Value);
                }
            }
        }

        return palette;
    }

    public static Color Resolve(string source, IDictionary<string, Color> palette, string? themePalette)
    {
        if (palette.TryGetValue(source, out var colour))
        {
            return colour;
        }

        return GenerateFallbackColour(source, themePalette ?? "Default");
    }

    public static Color GetReadableForeground(Color background)
    {
        var luminance = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255d;
        return luminance >= 0.6 ? Colors.Black : Colors.White;
    }

    public static Color Dim(Color source, double saturationFactor = 0.5, double lightnessFactor = 0.55)
    {
        var (h, s, l) = ToHsl(source);
        s = Math.Clamp(s * saturationFactor, 0, 1);
        l = Math.Clamp(l * lightnessFactor, 0, 1);
        return FromHsl(h, s, l);
    }

    private static (double H, double S, double L) ToHsl(Color color)
    {
        var r = color.R / 255d;
        var g = color.G / 255d;
        var b = color.B / 255d;

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2d;

        if (Math.Abs(max - min) < double.Epsilon)
        {
            return (0, 0, l);
        }

        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);

        double h;
        if (max == r)
        {
            h = (g - b) / d + (g < b ? 6 : 0);
        }
        else if (max == g)
        {
            h = (b - r) / d + 2;
        }
        else
        {
            h = (r - g) / d + 4;
        }

        return (h / 6d, s, l);
    }

    private static Color TryParseColour(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Colors.LightGray;
        }

        try
        {
            return Color.Parse(value);
        }
        catch
        {
            return Colors.LightGray;
        }
    }

    private static Color GenerateFallbackColour(string seed, string themePalette)
    {
        unchecked
        {
            var hash = seed.Aggregate(17, (current, c) => current * 31 + c);
            var hue = (hash % 360 + 360) % 360;

            double saturation = 0.6;
            double lightness = 0.5;

            if (string.Equals(themePalette, "Pastel", StringComparison.OrdinalIgnoreCase))
            {
                saturation = 0.4;
                lightness = 0.8;
            }
            else if (string.Equals(themePalette, "Dark", StringComparison.OrdinalIgnoreCase))
            {
                saturation = 0.7;
                lightness = 0.3;
            }
            else if (string.Equals(themePalette, "Vibrant", StringComparison.OrdinalIgnoreCase))
            {
                saturation = 0.9;
                lightness = 0.6;
            }

            return FromHsl(hue / 360d, saturation, lightness);
        }
    }

    private static Color FromHsl(double h, double s, double l)
    {
        double r;
        double g;
        double b;

        if (s == 0)
        {
            r = g = b = l;
        }
        else
        {
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            r = HueToRgb(p, q, h + 1.0 / 3);
            g = HueToRgb(p, q, h);
            b = HueToRgb(p, q, h - 1.0 / 3);
        }

        return Color.FromArgb(255, (byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0)
        {
            t += 1;
        }

        if (t > 1)
        {
            t -= 1;
        }

        if (t < 1.0 / 6)
        {
            return p + (q - p) * 6 * t;
        }

        if (t < 1.0 / 2)
        {
            return q;
        }

        if (t < 2.0 / 3)
        {
            return p + (q - p) * (2.0 / 3 - t) * 6;
        }

        return p;
    }
}
