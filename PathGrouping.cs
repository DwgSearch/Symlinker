namespace Symlinker
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;

    public enum PathKind
    {
        Folder,
        File,
        Missing,
    }

    /// <summary>One path shown in a tree (a file, or a folder treated as a single item).</summary>
    public sealed class PathLeaf
    {
        public string FullPath { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public PathKind Kind { get; init; }

        public bool IsMissing => Kind == PathKind.Missing;
    }

    /// <summary>Paths that share the same parent folder.</summary>
    public sealed class PathGroup
    {
        public string Header { get; init; } = string.Empty;

        /// <summary>Label for the group of items that sit directly in a drive root; set by the UI (localized).</summary>
        public static string RootLabel { get; set; } = "(drive root)";

        public string DisplayHeader => Header.Length == 0 ? RootLabel : Header;

        public List<PathLeaf> Children { get; init; } = new();
    }

    public static class PathGrouping
    {
        public static StringComparer Comparer =>
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        /// <summary>
        /// Turns user input (possibly pasted with quotes) into a clean absolute path, or an empty string
        /// when the text is not a fully qualified path.
        /// </summary>
        public static string Clean(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            path = path.Trim().Trim('"').Trim();
            if (path.Length == 0 || !Path.IsPathFullyQualified(path))
            {
                return string.Empty;
            }

            try
            {
                var full = Path.GetFullPath(path);
                var root = Path.GetPathRoot(full) ?? string.Empty;
                if (full.Length > root.Length)
                {
                    full = full.TrimEnd('\\', '/');
                }

                return full;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return string.Empty;
            }
        }

        /// <summary>Cleans every path, drops invalid and duplicate ones, keeps the original order.</summary>
        public static List<string> Unique(IEnumerable<string> paths)
        {
            var seen = new HashSet<string>(Comparer);
            var result = new List<string>();
            foreach (var path in paths)
            {
                var full = Clean(path);
                if (full.Length > 0 && seen.Add(full))
                {
                    result.Add(full);
                }
            }

            return result;
        }

        public static PathKind KindOf(string fullPath) =>
            Directory.Exists(fullPath) ? PathKind.Folder
            : File.Exists(fullPath) ? PathKind.File
            : PathKind.Missing;

        public static string NameOf(string fullPath)
        {
            var name = Path.GetFileName(fullPath);
            return name.Length == 0 ? fullPath : name;
        }

        /// <summary>Groups paths by parent folder: the "directory tree classification" shown in the UI.</summary>
        public static List<PathGroup> Group(IEnumerable<string> paths)
        {
            var groups = new Dictionary<string, PathGroup>(Comparer);
            foreach (var full in Unique(paths))
            {
                var header = Path.GetDirectoryName(full) ?? string.Empty;
                if (!groups.TryGetValue(header, out var group))
                {
                    group = new PathGroup { Header = header };
                    groups[header] = group;
                }

                group.Children.Add(new PathLeaf { FullPath = full, Name = NameOf(full), Kind = KindOf(full) });
            }

            foreach (var group in groups.Values)
            {
                group.Children.Sort((a, b) => Comparer.Compare(a.Name, b.Name));
            }

            return groups.Values.OrderBy(g => g.Header, Comparer).ToList();
        }
    }
}
