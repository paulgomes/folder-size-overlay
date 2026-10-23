using System;
using System.Collections.Generic;
using System.Drawing;

namespace PastaGBIcons
{
    // Snapshots are assembled by the worker and must not be mutated after publication.
    public sealed class DesktopSnapshot
    {
        public IntPtr HostHandle;
        public bool Visible;
        public Rectangle HostScreenBounds;
        public int Dpi = 96;
        public uint TextColor = 0xFFFFFFFF;
        // Content-addressed geometry, stable across identical provider captures.
        // The renderer compares it with the CURRENT snapshot before/after drawing.
        public string GeometryStamp;
        public List<DesktopFolderIcon> Folders = new List<DesktopFolderIcon>();
        public List<Rectangle> OccupiedBounds = new List<Rectangle>();
        public string Status = "Aguardando a área de trabalho.";

        internal void ComputeGeometryStamp(IList<VisualRowAnchor> extraGeometry)
        {
            List<VisualRowAnchor> rows = new List<VisualRowAnchor>();
            foreach (Rectangle bounds in OccupiedBounds)
                rows.Add(new VisualRowAnchor { Name = "\0occupied", Bounds = bounds });
            foreach (DesktopFolderIcon folder in Folders)
            {
                // Windows physical paths cannot contain NUL, so each role and
                // path/name component has an unambiguous internal separator.
                string identity = (folder.Path ?? String.Empty) + "\0" + (folder.Name ?? String.Empty);
                rows.Add(new VisualRowAnchor { Name = "\0folder:" + identity,
                    Bounds = folder.Bounds, Editor = folder.IsEditing });
                rows.Add(new VisualRowAnchor { Name = "\0item:" + identity,
                    Bounds = folder.ItemBounds, Editor = folder.IsEditing });
            }
            if (extraGeometry != null) rows.AddRange(extraGeometry);
            GeometryStamp = VisualSnapshotPolicy.CreateStamp(HostHandle, HostScreenBounds, Dpi, rows);
        }
    }

    public sealed class DesktopFolderIcon
    {
        public string Path;
        public string Name;
        public Rectangle Bounds;
        // Full provider item rectangle, used to recognize this folder's own
        // OccupiedBounds entry when Bounds has a refined text bottom.
        public Rectangle ItemBounds;
        public bool IsSelected;
        public bool IsEditing;
    }

}
