namespace Symlinker
{
    using System.Collections.Generic;
    using System.IO;

    public enum LinkMode
    {
        /// <summary>Symbolic links for everything (needs administrator rights or Developer Mode).</summary>
        Symbolic,

        /// <summary>Junctions for folders and hard links for files (no special rights; hard links stay on one drive).</summary>
        JunctionAndHardLink,
    }

    public enum PlanStatus
    {
        Ready,
        AlreadyLinked,
        Exists,
        SourceMissing,
        DestinationMissing,
        NameConflict,
        Overlap,
        HardLinkCrossVolume,
        InvalidSource,
    }

    /// <summary>How the two lists are combined into links.</summary>
    public enum PairingMode
    {
        /// <summary>Every source is linked into every destination folder.</summary>
        All,

        /// <summary>Only the (destination, source) pairs the user chose are linked.</summary>
        Manual,
    }

    /// <summary>One hand-picked pairing: <see cref="Source"/> will be linked into <see cref="Destination"/>.</summary>
    public sealed class LinkPair
    {
        public string Destination { get; set; } = string.Empty;

        public string Source { get; set; } = string.Empty;
    }

    /// <summary>One link that will be created: <see cref="Source"/> linked into <see cref="Destination"/>.</summary>
    public sealed record PlanEntry(string Destination, string Source, string LinkPath, LinkKind Kind, bool IsFolder, PlanStatus Status)
    {
        public bool IsRunnable => Status is PlanStatus.Ready or PlanStatus.AlreadyLinked or PlanStatus.Exists;
    }

    public static class LinkPlanner
    {
        /// <summary>
        /// Every source is linked into every destination folder (M sources x N destinations), using the
        /// source's own name. Nothing on disk is changed here; each entry only carries a pre-check status.
        /// </summary>
        public static List<PlanEntry> Build(IEnumerable<string> destinations, IEnumerable<string> sources, LinkMode mode)
        {
            var destinationList = PathGrouping.Unique(destinations);
            var sourceList = PathGrouping.Unique(sources);
            var pairs = new List<(string Destination, string Source)>();
            foreach (var destination in destinationList)
            {
                foreach (var source in sourceList)
                {
                    pairs.Add((destination, source));
                }
            }

            return Build(pairs, mode);
        }

        /// <summary>
        /// Links exactly the given (destination, source) pairs, in order, using each source's own name.
        /// Duplicate pairs are ignored.
        /// </summary>
        public static List<PlanEntry> Build(IEnumerable<(string Destination, string Source)> pairs, LinkMode mode)
        {
            var entries = new List<PlanEntry>();
            var used = new HashSet<string>(PathGrouping.Comparer);
            var seenPairs = new HashSet<(string, string)>(new PairComparer());

            foreach (var (destination, source) in pairs)
            {
                if (!seenPairs.Add((destination, source)))
                {
                    continue;
                }

                var isFolder = Directory.Exists(source);
                var isFile = !isFolder && File.Exists(source);
                var name = Path.GetFileName(source);
                var linkPath = name.Length == 0 ? string.Empty : Path.Combine(destination, name);
                var kind = mode == LinkMode.Symbolic ? LinkKind.Symbolic
                    : isFolder ? LinkKind.Junction
                    : LinkKind.Hard;

                entries.Add(new PlanEntry(
                    destination, source, linkPath, kind, isFolder,
                    Check(destination, source, linkPath, isFolder, isFile, kind, used)));
            }

            return entries;
        }

        private sealed class PairComparer : IEqualityComparer<(string, string)>
        {
            public bool Equals((string, string) x, (string, string) y) =>
                PathGrouping.Comparer.Equals(x.Item1, y.Item1) && PathGrouping.Comparer.Equals(x.Item2, y.Item2);

            public int GetHashCode((string, string) obj) =>
                System.HashCode.Combine(PathGrouping.Comparer.GetHashCode(obj.Item1), PathGrouping.Comparer.GetHashCode(obj.Item2));
        }

        public static LinkResult Run(PlanEntry entry, ConflictPolicy policy) =>
            entry.IsRunnable
                ? LinkService.Create(entry.LinkPath, entry.Source, entry.Kind, policy)
                : new LinkResult(LinkOutcome.Blocked, entry.Status.ToString());

        private static PlanStatus Check(
            string destination, string source, string linkPath, bool isFolder, bool isFile, LinkKind kind, HashSet<string> used)
        {
            if (linkPath.Length == 0)
            {
                return PlanStatus.InvalidSource;
            }

            if (!Directory.Exists(destination))
            {
                return PlanStatus.DestinationMissing;
            }

            if (!isFolder && !isFile)
            {
                return PlanStatus.SourceMissing;
            }

            if (!used.Add(linkPath))
            {
                return PlanStatus.NameConflict;
            }

            var link = LinkService.Normalize(linkPath);
            var target = LinkService.Normalize(source);
            var separator = Path.DirectorySeparatorChar;
            if (string.Equals(link, target, LinkService.PathComparison)
                || target.StartsWith(link + separator, LinkService.PathComparison)
                || link.StartsWith(target + separator, LinkService.PathComparison))
            {
                return PlanStatus.Overlap;
            }

            if (kind == LinkKind.Hard
                && !string.Equals(Path.GetPathRoot(link), Path.GetPathRoot(target), LinkService.PathComparison))
            {
                return PlanStatus.HardLinkCrossVolume;
            }

            if (LinkService.TryGetAttributes(linkPath, out var attributes))
            {
                return LinkService.IsSameLink(linkPath, attributes, source) ? PlanStatus.AlreadyLinked : PlanStatus.Exists;
            }

            return PlanStatus.Ready;
        }
    }
}
