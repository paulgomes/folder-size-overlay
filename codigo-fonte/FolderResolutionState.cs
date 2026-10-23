using System;

namespace PastaGBIcons
{
    // One Shell item's resolution state, owned by the discovery STA. A failed
    // attempt remains pending; only a confirmed folder path or confirmed
    // non-folder attributes complete it for the lifetime of the metadata index.
    internal sealed class FolderResolutionState
    {
        private DateTime retryAfterUtc;
        internal bool Resolved { get; private set; }
        internal string Path { get; private set; }

        internal bool TryBegin(DateTime nowUtc)
        {
            if (Resolved || nowUtc < retryAfterUtc) return false;
            retryAfterUtc = nowUtc.AddSeconds(1);
            return true;
        }

        internal void AcceptFolder(string path)
        {
            if (String.IsNullOrEmpty(path)) throw new ArgumentException("A confirmed folder needs a path.", "path");
            Path = path;
            Resolved = true;
        }

        internal void ExcludeNonFolder()
        {
            Path = null;
            Resolved = true;
        }
    }
}
