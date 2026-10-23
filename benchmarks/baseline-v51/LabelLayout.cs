using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;

namespace PastaGBIcons
{
    public static class LabelLayout
    {
        public static bool TryPlace(Rectangle icon, Rectangle ownCell, IList<Rectangle> occupied, IList<Rectangle> labels,
            Rectangle host, int dpi, out Rectangle result)
        { return TryPlace(icon, ownCell, occupied, labels, host, dpi, 1f, out result); }

        public static bool TryPlace(Rectangle icon, Rectangle ownCell, IList<Rectangle> occupied, IList<Rectangle> labels,
            Rectangle host, int dpi, float textScale, out Rectangle result)
        {
            result = Rectangle.Empty;
            if (icon.Width <= 0 || icon.Height <= 0 || host.Width <= 0 || host.Height <= 0) return false;
            double scale = Math.Max(0.75, Math.Min(4d, dpi / 96d));
            double text = scale * NormalizeTextScale(textScale);
            int width = Math.Max((int)Math.Ceiling(58 * text), Math.Min((int)Math.Ceiling(116 * text), icon.Width));
            int height = (int)Math.Ceiling(19 * text);
            int gap = Math.Max(1, (int)Math.Ceiling(scale));
            var candidate = new Rectangle(icon.Left + (icon.Width - width) / 2, icon.Bottom + gap, width, height);
            if (!host.Contains(candidate)) return false;
            if (occupied != null)
                foreach (Rectangle other in occupied)
                    if (other != ownCell && candidate.IntersectsWith(other)) return false;
            if (labels != null)
                foreach (Rectangle other in labels)
                    if (candidate.IntersectsWith(other)) return false;
            result = candidate;
            return true;
        }

        // List/details views have no second row reserved for a size. Only use
        // measured spare space in the verified name cell, never other columns.
        public static bool TryPlaceInline(Rectangle nameText, Rectangle ownCell, Rectangle nameCell,
            IList<Rectangle> occupied, IList<Rectangle> labels, Rectangle viewport, int dpi, out Rectangle result)
        { return TryPlaceInline(nameText, ownCell, nameCell, occupied, labels, viewport, dpi, 1f, out result); }

        public static bool TryPlaceInline(Rectangle nameText, Rectangle ownCell, Rectangle nameCell,
            IList<Rectangle> occupied, IList<Rectangle> labels, Rectangle viewport, int dpi, float textScale, out Rectangle result)
        {
            result = Rectangle.Empty;
            if (nameText.IsEmpty || ownCell.IsEmpty || nameCell.IsEmpty || viewport.IsEmpty ||
                !ownCell.Contains(nameText) || !ownCell.Contains(nameCell) || !nameCell.Contains(nameText)) return false;
            double scale = Math.Max(0.75, Math.Min(4d, dpi / 96d));
            double text = scale * NormalizeTextScale(textScale);
            int gap = Math.Max(3, (int)Math.Ceiling(6 * scale));
            int inset = Math.Max(1, (int)Math.Ceiling(2 * scale));
            int left = nameText.Right + gap;
            int right = Math.Min(nameCell.Right - inset, viewport.Right);
            int width = Math.Min((int)Math.Ceiling(96 * text), right - left);
            int height = Math.Min((int)Math.Ceiling(18 * text), nameCell.Height - inset * 2);
            if (width < (int)Math.Ceiling(58 * text) || height < (int)Math.Ceiling(12 * text)) return false;
            var candidate = new Rectangle(left, nameCell.Top + (nameCell.Height - height) / 2, width, height);
            if (!nameCell.Contains(candidate) || !viewport.Contains(candidate) || candidate.IntersectsWith(nameText)) return false;
            if (occupied != null)
                foreach (Rectangle other in occupied)
                    if (other != ownCell && candidate.IntersectsWith(other)) return false;
            if (labels != null)
                foreach (Rectangle other in labels)
                    if (candidate.IntersectsWith(other)) return false;
            result = candidate;
            return true;
        }

        // The probe must prove that sizeCell is the EMPTY System.Size cell for
        // this folder. This method never guesses a column index or overwrites
        // a native value. A missing nameText is acceptable only because the
        // separately verified size cell identifies the safe column itself.
        public static bool TryPlaceInSizeCell(Rectangle nameText, Rectangle ownCell, Rectangle sizeCell,
            IList<Rectangle> occupied, IList<Rectangle> labels, Rectangle viewport, int dpi, out Rectangle result)
        { return TryPlaceInSizeCell(nameText, ownCell, sizeCell, occupied, labels, viewport, dpi, 1f, out result); }

        public static bool TryPlaceInSizeCell(Rectangle nameText, Rectangle ownCell, Rectangle sizeCell,
            IList<Rectangle> occupied, IList<Rectangle> labels, Rectangle viewport, int dpi, float textScale, out Rectangle result)
        {
            result = Rectangle.Empty;
            if (ownCell.IsEmpty || sizeCell.IsEmpty || viewport.IsEmpty ||
                !ownCell.Contains(sizeCell)) return false;
            if (!nameText.IsEmpty && (!ownCell.Contains(nameText) || nameText.IntersectsWith(sizeCell)))
                return false;
            double scale = Math.Max(0.75, Math.Min(4d, dpi / 96d));
            double text = scale * NormalizeTextScale(textScale);
            int horizontalInset = Math.Max(2, (int)Math.Ceiling(3 * scale));
            int verticalInset = Math.Max(1, (int)Math.Ceiling(scale));
            int width = Math.Min((int)Math.Ceiling(96 * text), sizeCell.Width - 2 * horizontalInset);
            int height = Math.Min((int)Math.Ceiling(18 * text), sizeCell.Height - 2 * verticalInset);
            if (width < (int)Math.Ceiling(58 * text) || height < (int)Math.Ceiling(12 * text)) return false;
            // Preserve the native size column's right edge, even when columns
            // are horizontally scrolled. Do not move a value into another cell.
            Rectangle candidate = new Rectangle(sizeCell.Right - horizontalInset - width,
                sizeCell.Top + (sizeCell.Height - height) / 2, width, height);
            if (!sizeCell.Contains(candidate) || !viewport.Contains(candidate) ||
                (!nameText.IsEmpty && candidate.IntersectsWith(nameText)) ||
                HasConflict(candidate, ownCell, occupied, labels)) return false;
            result = candidate;
            return true;
        }

        // Icons/thumbnail modes only. This fallback may cover the lower part of
        // the folder artwork, but never its proven native name or another item.
        // Do not use it in tiles/content rows with additional native metadata.
        public static bool TryPlaceIconBadge(Rectangle nameText, Rectangle ownCell,
            IList<Rectangle> occupied, IList<Rectangle> labels, Rectangle viewport, int dpi, out Rectangle result)
        { return TryPlaceIconBadge(nameText, ownCell, occupied, labels, viewport, dpi, 1f, out result); }

        public static bool TryPlaceIconBadge(Rectangle nameText, Rectangle ownCell,
            IList<Rectangle> occupied, IList<Rectangle> labels, Rectangle viewport, int dpi, float textScale, out Rectangle result)
        {
            result = Rectangle.Empty;
            if (nameText.IsEmpty || ownCell.IsEmpty || viewport.IsEmpty ||
                !ownCell.Contains(nameText)) return false;
            double scale = Math.Max(0.75, Math.Min(4d, dpi / 96d));
            double text = scale * NormalizeTextScale(textScale);
            int inset = Math.Max(2, (int)Math.Ceiling(2 * scale));
            int gap = Math.Max(2, (int)Math.Ceiling(2 * scale));
            int width = Math.Min((int)Math.Ceiling(88 * text), ownCell.Width - 2 * inset);
            int height = (int)Math.Ceiling(16 * text);
            if (width < (int)Math.Ceiling(58 * text)) return false;
            Rectangle candidate = new Rectangle(ownCell.Left + (ownCell.Width - width) / 2,
                nameText.Top - gap - height, width, height);
            if (candidate.Top < ownCell.Top + inset || !ownCell.Contains(candidate) ||
                !viewport.Contains(candidate) || candidate.IntersectsWith(nameText) ||
                HasConflict(candidate, ownCell, occupied, labels)) return false;
            result = candidate;
            return true;
        }

        // Non-finite/corrupt stored preferences must not produce negative or
        // overflowing rectangles. The same normalized scale is used by drawing.
        public static float NormalizeTextScale(float value)
        {
            if (Single.IsNaN(value) || Single.IsInfinity(value)) return 1f;
            return Math.Max(0.85f, Math.Min(1.30f, value));
        }

        private static bool HasConflict(Rectangle candidate, Rectangle ownCell,
            IList<Rectangle> occupied, IList<Rectangle> labels)
        {
            if (occupied != null)
                foreach (Rectangle other in occupied)
                    if (other != ownCell && candidate.IntersectsWith(other)) return true;
            if (labels != null)
                foreach (Rectangle other in labels)
                    if (candidate.IntersectsWith(other)) return true;
            return false;
        }
    }

    public static class SizeCaption
    {
        private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
        private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB", "EB" };
        public static string Format(long bytes)
        {
            if (bytes < 0) throw new ArgumentOutOfRangeException("bytes");
            if (bytes == 0) return "0 B";
            double value = bytes;
            int unit = 0;
            while (value >= 1000d && unit < Units.Length - 1) { value /= 1000d; unit++; }
            // Avoid displaying a rounded '1000 MB' at the unit boundary.
            if (unit < Units.Length - 1 && Math.Round(value, 1, MidpointRounding.AwayFromZero) >= 1000d)
            { value /= 1000d; unit++; }
            return value.ToString(unit == 0 ? "0" : "0.#", Pt) + " " + Units[unit];
        }
    }
}
