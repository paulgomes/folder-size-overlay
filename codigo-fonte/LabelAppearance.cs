using System;
using System.Drawing;

namespace PastaGBIcons
{
    public enum LabelTheme { Automatic = 0, Light = 1, Dark = 2 }

    // No native calls: this palette is also verified by the pure logic tests.
    internal struct LabelPalette
    {
        internal Color Ink, Shade, Fill, Border;
        internal bool SolidBackground;

        internal static LabelPalette Create(LabelTheme theme, uint hostTextColor,
            bool highContrast, Color systemText, Color systemBackground)
        {
            if (highContrast)
                return new LabelPalette { Ink = systemText, Fill = systemBackground,
                    Border = systemText, Shade = Color.Transparent, SolidBackground = true };
            Color host = Color.FromArgb((int)(hostTextColor & 255),
                (int)((hostTextColor >> 8) & 255), (int)((hostTextColor >> 16) & 255));
            bool dark = theme == LabelTheme.Dark || (theme == LabelTheme.Automatic &&
                host.R * 299 + host.G * 587 + host.B * 114 >= 128000);
            return new LabelPalette {
                // Automatic honors the actual provider foreground, including
                // customized colors, instead of turning all text into gray.
                Ink = theme == LabelTheme.Automatic ? host : dark ?
                    Color.FromArgb(235, 235, 235) : Color.FromArgb(55, 55, 55),
                Shade = dark ? Color.FromArgb(95, 0, 0, 0) : Color.FromArgb(32, 255, 255, 255),
                Fill = dark ? Color.FromArgb(240, 37, 37, 37) : Color.FromArgb(245, 247, 247, 247),
                Border = dark ? Color.FromArgb(100, 205, 205, 205) : Color.FromArgb(80, 90, 90, 90),
                // Explicit themes use their own small background so choosing a
                // dark label on a light host (or vice versa) remains readable.
                SolidBackground = theme != LabelTheme.Automatic
            };
        }
    }
}
