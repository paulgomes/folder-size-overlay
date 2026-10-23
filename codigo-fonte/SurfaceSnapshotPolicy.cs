using System;

namespace PastaGBIcons
{
    internal static class SurfaceSnapshotPolicy
    {
        internal static bool SameDisplay(DesktopSnapshot a, DesktopSnapshot b)
        {
            return a != null && b != null && a.Visible == b.Visible && a.HostHandle == b.HostHandle &&
                a.Dpi == b.Dpi && a.TextColor == b.TextColor && a.HostScreenBounds == b.HostScreenBounds &&
                (!a.Visible || (!String.IsNullOrEmpty(a.GeometryStamp) &&
                    String.Equals(a.GeometryStamp, b.GeometryStamp, StringComparison.Ordinal)));
        }

        internal static bool SameDemand(DesktopSnapshot a, DesktopSnapshot b)
        {
            if (a == null || b == null || a.Visible != b.Visible || a.HostHandle != b.HostHandle ||
                a.Folders.Count != b.Folders.Count) return false;
            for (int i = 0; i < a.Folders.Count; i++)
                if (!String.Equals(a.Folders[i].Path, b.Folders[i].Path, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        internal static bool IsFresh(DateTime capturedUtc, DateTime nowUtc, double seconds)
        {
            double age = (nowUtc - capturedUtc).TotalSeconds;
            return age >= 0 && age < seconds;
        }
    }
}
