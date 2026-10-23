using System;
using System.Collections.Generic;
using System.Drawing;
using PastaGBIcons;

internal static class LabelLayoutReviewTests
{
    private static int passed;
    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception("FAILED: " + name);
        passed++;
    }
    private static bool Place(Rectangle icon, Rectangle own, IList<Rectangle> occupied,
        IList<Rectangle> labels, Rectangle host, int dpi, out Rectangle result)
    {
        return LabelLayout.TryPlace(icon, own, occupied, labels, host, dpi, out result);
    }
    private static void Size(long bytes, string expected)
    {
        Check(SizeCaption.Format(bytes) == expected, "caption " + bytes + " -> " + expected);
    }
    public static int Main()
    {
        try
        {
            Size(0, "0 B");
            Size(1, "1 B");
            Size(999, "999 B");
            Size(1000, "1 KB");
            Size(1550, "1,6 KB");
            Size(999949, "999,9 KB");
            Size(999950, "1 MB");
            Size(1000000, "1 MB");
            Size(999950000, "1 GB");
            Size(1000000000, "1 GB");
            Size(3260000000, "3,3 GB");
            Size(1000000000000, "1 TB");
            Size(Int64.MaxValue, "9,2 EB");
            bool negativeRejected = false;
            try { SizeCaption.Format(-1); }
            catch (ArgumentOutOfRangeException) { negativeRejected = true; }
            Check(negativeRejected, "negative byte totals rejected");

            Rectangle host = new Rectangle(0, 0, 1920, 1080);
            Rectangle icon = new Rectangle(40, 35, 80, 75);
            Rectangle label;
            Check(Place(icon, icon, new[] { icon }, null, host, 96, out label), "ordinary icon can receive caption");
            Check(label.Top > icon.Bottom && !label.IntersectsWith(icon), "caption stays below existing name");
            Check(host.Contains(label), "caption inside desktop");
            Check(Math.Abs((label.Left + label.Width / 2.0) - (icon.Left + icon.Width / 2.0)) <= 0.5,
                "caption horizontally centered");
            Rectangle first = label;
            Check(!Place(icon, icon, new[] { icon, new Rectangle(40, 120, 80, 60) }, null, host, 96, out label),
                "next row icon prevents overlap");
            Check(label.IsEmpty, "rejected placement returns empty bounds");
            Check(!Place(icon, icon, new[] { icon }, new[] { first }, host, 96, out label),
                "existing caption prevents duplicate overlap");

            Rectangle fullCell = new Rectangle(40, 35, 80, 105);
            Check(Place(icon, fullCell, new[] { fullCell }, null, host, 96, out label),
                "confirmed name bottom can use own cell padding");
            Check(label.IntersectsWith(fullCell), "refined caption actually occupies own padding");
            Check(!Place(icon, fullCell, new[] { fullCell, new Rectangle(70, 115, 80, 45) }, null, host, 96, out label),
                "own cell exception never exempts a different neighbor");

            Rectangle nearBottom = new Rectangle(40, 1000, 80, 68);
            Check(!Place(nearBottom, nearBottom, null, null, host, 96, out label),
                "caption outside bottom edge is suppressed");
            Rectangle narrowAtLeft = new Rectangle(0, 35, 40, 75);
            Check(!Place(narrowAtLeft, narrowAtLeft, null, null, host, 96, out label),
                "caption outside left edge is suppressed");
            Check(!Place(Rectangle.Empty, Rectangle.Empty, null, null, host, 96, out label),
                "empty icon rejected");
            Check(!Place(icon, icon, null, null, Rectangle.Empty, 96, out label),
                "empty desktop rejected");

            Rectangle virtualHost = new Rectangle(-1920, -1080, 3840, 2160);
            Rectangle negativeIcon = new Rectangle(-1780, -920, 80, 75);
            Check(Place(negativeIcon, negativeIcon, new[] { negativeIcon }, null, virtualHost, 96, out label),
                "negative monitor coordinates are valid");
            Check(label.Left < 0 && label.Top < 0 && virtualHost.Contains(label),
                "negative-origin caption remains on that monitor");

            Rectangle scaledIcon = new Rectangle(80, 70, 160, 150);
            Check(Place(scaledIcon, scaledIcon, new[] { scaledIcon }, null, host, 192, out label),
                "200 percent DPI icon can receive caption");
            Check(label.Height > first.Height && label.Width > first.Width && label.Top > scaledIcon.Bottom,
                "caption scales and keeps below scaled name");

            // Explorer list/details: only verified spare NAME-column space is usable.
            Rectangle viewport = new Rectangle(0, 0, 800, 600);
            Rectangle row = new Rectangle(30, 80, 740, 24);
            Rectangle nameCell = new Rectangle(30, 80, 250, 24);
            Rectangle nameText = new Rectangle(50, 83, 80, 18);
            Rectangle metadataColumn = new Rectangle(280, 80, 170, 24);
            Check(LabelLayout.TryPlaceInline(nameText, row, nameCell,
                new[] { row, metadataColumn }, null, viewport, 96, out label),
                "inline caption uses available name-column space");
            Check(nameCell.Contains(label) && label.Left > nameText.Right && !label.IntersectsWith(nameText),
                "inline caption neither covers name nor crosses name column");
            Check(!label.IntersectsWith(metadataColumn), "inline caption never covers metadata column");
            Check(viewport.Contains(label), "inline caption remains inside viewport");
            Rectangle inlineFirst = label;
            Check(!LabelLayout.TryPlaceInline(new Rectangle(50, 83, 198, 18), row, nameCell,
                new[] { row }, null, viewport, 96, out label),
                "long name suppresses caption rather than crossing column boundary");
            Check(label.IsEmpty, "rejected inline placement returns empty bounds");
            Check(!LabelLayout.TryPlaceInline(nameText, row, Rectangle.Empty,
                new[] { row }, null, viewport, 96, out label),
                "unproven name cell cannot receive inline caption");
            Check(!LabelLayout.TryPlaceInline(new Rectangle(250, 83, 80, 18), row, nameCell,
                new[] { row }, null, viewport, 96, out label),
                "name outside reported cell is rejected");
            Check(!LabelLayout.TryPlaceInline(nameText, row, nameCell,
                new[] { row }, new[] { inlineFirst }, viewport, 96, out label),
                "overlapping inline caption is rejected");
            Check(!LabelLayout.TryPlaceInline(nameText, row, nameCell,
                new[] { row, new Rectangle(150, 80, 120, 24) }, null, viewport, 96, out label),
                "inline caption respects another visible obstacle within viewport");
            Rectangle clippedViewport = new Rectangle(0, 0, 200, 600);
            Check(LabelLayout.TryPlaceInline(nameText, row, nameCell,
                new[] { row }, null, clippedViewport, 96, out label) && clippedViewport.Contains(label),
                "horizontal viewport clipping limits caption to visible spare space");
            Check(!LabelLayout.TryPlaceInline(nameText, row, nameCell,
                new[] { row }, null, new Rectangle(0, 0, 185, 600), 96, out label),
                "insufficient visible width suppresses caption");
            Check(!LabelLayout.TryPlaceInline(nameText, row, nameCell,
                new[] { row }, null, new Rectangle(0, 90, 800, 510), 96, out label),
                "partially scrolled row cannot paint above viewport");
            Rectangle scaledRow = new Rectangle(60, 160, 1480, 48);
            Rectangle scaledNameCell = new Rectangle(60, 160, 500, 48);
            Rectangle scaledNameText = new Rectangle(100, 166, 160, 36);
            Check(LabelLayout.TryPlaceInline(scaledNameText, scaledRow, scaledNameCell,
                new[] { scaledRow }, null, host, 192, out label),
                "inline placement supports 200 percent DPI geometry");
            Check(scaledNameCell.Contains(label) && label.Left > scaledNameText.Right && label.Height > inlineFirst.Height,
                "scaled inline caption stays in name column with adequate height");

            // Explorer details: an EMPTY, proven System.Size cell may be used.
            Rectangle sizeCell = new Rectangle(640, 80, 130, 24);
            Check(LabelLayout.TryPlaceInSizeCell(nameText, row, sizeCell,
                new[] { row, metadataColumn }, null, viewport, 96, out label),
                "proven empty size column can receive a caption");
            Check(sizeCell.Contains(label) && viewport.Contains(label) && !label.IntersectsWith(nameText),
                "size-column caption stays in its column and leaves the name visible");
            Check(!label.IntersectsWith(metadataColumn), "size-column caption does not cover neighboring metadata");
            Rectangle sizeFirst = label;
            Check(LabelLayout.TryPlaceInSizeCell(Rectangle.Empty, row, sizeCell,
                new[] { row }, null, viewport, 96, out label),
                "proven size column does not require separate name geometry");
            Check(!LabelLayout.TryPlaceInSizeCell(nameText, row, Rectangle.Empty,
                new[] { row }, null, viewport, 96, out label) && label.IsEmpty,
                "unproven size column cannot be guessed");
            Check(!LabelLayout.TryPlaceInSizeCell(nameText, row, new Rectangle(760, 80, 130, 24),
                new[] { row }, null, viewport, 96, out label),
                "a size cell outside its row is rejected");
            Check(!LabelLayout.TryPlaceInSizeCell(new Rectangle(650, 83, 80, 18), row, sizeCell,
                new[] { row }, null, viewport, 96, out label),
                "a claimed size cell that overlaps the name is rejected");
            Check(!LabelLayout.TryPlaceInSizeCell(nameText, row, new Rectangle(740, 80, 30, 24),
                new[] { row }, null, viewport, 96, out label),
                "a narrow size column does not force text across its boundary");
            Check(!LabelLayout.TryPlaceInSizeCell(nameText, row, sizeCell,
                new[] { row, sizeFirst }, null, viewport, 96, out label),
                "native text or another obstacle inside the size cell blocks placement");
            Check(!LabelLayout.TryPlaceInSizeCell(nameText, row, sizeCell,
                new[] { row }, new[] { sizeFirst }, viewport, 96, out label),
                "a duplicate size-column label is rejected");
            Check(!LabelLayout.TryPlaceInSizeCell(nameText, row, sizeCell,
                new[] { row }, null, new Rectangle(0, 0, 720, 600), 96, out label),
                "horizontal scrolling cannot shift a size into a different column");
            Check(!LabelLayout.TryPlaceInSizeCell(nameText, row, sizeCell,
                new[] { row }, null, new Rectangle(0, 90, 800, 510), 96, out label),
                "size-column text cannot paint above a scrolled viewport");
            Rectangle scaledSizeCell = new Rectangle(1280, 160, 260, 48);
            Check(LabelLayout.TryPlaceInSizeCell(scaledNameText, scaledRow, scaledSizeCell,
                new[] { scaledRow }, null, host, 192, out label) &&
                scaledSizeCell.Contains(label) && label.Height > sizeFirst.Height,
                "size-column placement scales within its column at 200 percent DPI");

            // Icon fallback: artwork may be covered, but the proven name may not.
            Rectangle badgeCell = new Rectangle(40, 35, 100, 105);
            Rectangle badgeName = new Rectangle(50, 115, 80, 18);
            Check(LabelLayout.TryPlaceIconBadge(badgeName, badgeCell,
                new[] { badgeCell }, null, viewport, 96, out label),
                "an icon can receive a compact badge above its proven name");
            Check(badgeCell.Contains(label) && viewport.Contains(label) &&
                label.Bottom < badgeName.Top && !label.IntersectsWith(badgeName),
                "badge remains inside its icon cell and never covers its native name");
            Rectangle badgeFirst = label;
            Check(!LabelLayout.TryPlaceIconBadge(Rectangle.Empty, badgeCell,
                new[] { badgeCell }, null, viewport, 96, out label) && label.IsEmpty,
                "badge cannot guess where an unproven name starts");
            Check(!LabelLayout.TryPlaceIconBadge(new Rectangle(50, 40, 80, 18), badgeCell,
                new[] { badgeCell }, null, viewport, 96, out label),
                "insufficient room above the name suppresses the badge");
            Check(!LabelLayout.TryPlaceIconBadge(new Rectangle(120, 115, 80, 18), badgeCell,
                new[] { badgeCell }, null, viewport, 96, out label),
                "name outside the icon cell is rejected");
            Check(!LabelLayout.TryPlaceIconBadge(new Rectangle(45, 115, 40, 18),
                new Rectangle(40, 35, 50, 105), null, null, viewport, 96, out label),
                "narrow icon cell cannot force a badge into another item");
            Check(!LabelLayout.TryPlaceIconBadge(badgeName, badgeCell,
                new[] { badgeCell, badgeFirst }, null, viewport, 96, out label),
                "badge respects other occupied content");
            Check(!LabelLayout.TryPlaceIconBadge(badgeName, badgeCell,
                new[] { badgeCell }, new[] { badgeFirst }, viewport, 96, out label),
                "badge cannot overlap an existing label");
            Check(!LabelLayout.TryPlaceIconBadge(badgeName, badgeCell,
                new[] { badgeCell }, null, new Rectangle(0, 105, 800, 495), 96, out label),
                "scrolled icon badge cannot paint outside viewport");
            Rectangle largeBadgeCell = new Rectangle(80, 70, 200, 210);
            Rectangle largeBadgeName = new Rectangle(100, 230, 160, 36);
            Check(LabelLayout.TryPlaceIconBadge(largeBadgeName, largeBadgeCell,
                new[] { largeBadgeCell }, null, host, 192, out label) &&
                largeBadgeCell.Contains(label) && label.Bottom < largeBadgeName.Top && label.Height > badgeFirst.Height,
                "icon badge scales while preserving its name at 200 percent DPI");
            Rectangle negativeBadgeCell = new Rectangle(-1800, -900, 100, 105);
            Rectangle negativeBadgeName = new Rectangle(-1790, -820, 80, 18);
            Check(LabelLayout.TryPlaceIconBadge(negativeBadgeName, negativeBadgeCell,
                new[] { negativeBadgeCell }, null, virtualHost, 96, out label) &&
                negativeBadgeCell.Contains(label) && label.Left < 0 && label.Top < 0,
                "icon badge supports negative monitor coordinates");
            Console.WriteLine("PASS: " + passed + " layout/format checks against production LabelLayout.cs (net8 logic only; no Windows UI execution).");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
}
