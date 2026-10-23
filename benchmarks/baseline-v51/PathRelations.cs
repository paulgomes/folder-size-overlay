using System;
using System.Collections.Generic;

namespace PastaGBIcons
{
    internal static class PathRelations
    {
        internal static string Normalize(string value)
        {
            if (String.IsNullOrEmpty(value)) return String.Empty;
            string result = value.Replace('/', '\\');
            if (result.StartsWith("\\\\?\\", StringComparison.Ordinal)) result = result.Substring(4);
            while (result.Length > 3 && result.EndsWith("\\", StringComparison.Ordinal))
                result = result.Substring(0, result.Length - 1);
            return result;
        }

        internal static bool Contains(string parent, string path)
        {
            return ContainsNormalized(Normalize(parent), Normalize(path));
        }

        private static bool ContainsNormalized(string parent, string path)
        {
            if (parent.Length == 0 || path.Length == 0) return false;
            if (String.Equals(parent, path, StringComparison.OrdinalIgnoreCase)) return true;
            // Compare the component boundary in place. Appending a separator
            // allocated another string on every overlap/notification check.
            return path.Length > parent.Length && path.StartsWith(parent, StringComparison.OrdinalIgnoreCase) &&
                (parent[parent.Length - 1] == '\\' || path[parent.Length] == '\\');
        }

        internal static bool Overlaps(string first, string second)
        {
            first = Normalize(first); second = Normalize(second);
            return ContainsNormalized(first, second) || ContainsNormalized(second, first);
        }

        internal static bool Affects(string path, IEnumerable<string> dependencies, string changed)
        {
            if (Overlaps(path, changed)) return true;
            if (dependencies != null)
                foreach (string dependency in dependencies)
                    if (Overlaps(dependency, changed)) return true;
            return false;
        }
    }
}
