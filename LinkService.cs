namespace Symlinker
{
    using System;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;

    /// <summary>The kind of link to create.</summary>
    public enum LinkKind
    {
        Symbolic,
        Hard,
        Junction,
    }

    /// <summary>What to do when something already exists at the link path.</summary>
    public enum ConflictPolicy
    {
        Skip,
        Overwrite,
        Backup,
    }

    public enum LinkOutcome
    {
        Created,
        CreatedAfterBackup,
        CreatedAfterOverwrite,
        SkippedExists,
        SkippedAlreadyLinked,
        TargetMissing,
        InvalidKind,
        PathsOverlap,
        InvalidPath,
        RefusedNonEmptyDirectory,
        NeedPrivilege,
        Blocked,
        Failed,
    }

    /// <summary>Result of one link creation. <see cref="Detail"/> holds a backup path or an error message.</summary>
    public sealed record LinkResult(LinkOutcome Outcome, string Detail = "")
    {
        public bool IsCreated => Outcome is LinkOutcome.Created or LinkOutcome.CreatedAfterBackup or LinkOutcome.CreatedAfterOverwrite;

        public bool IsSkipped => Outcome is LinkOutcome.SkippedExists or LinkOutcome.SkippedAlreadyLinked;

        public bool IsFailure => !IsCreated && !IsSkipped;
    }

    /// <summary>Creates links on disk and applies the chosen <see cref="ConflictPolicy"/>.</summary>
    public static class LinkService
    {
        internal static StringComparison PathComparison =>
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateHardLinkW")]
        private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        /// <summary>
        /// Combines <paramref name="root"/> and a relative path. Fails if the relative path is empty,
        /// rooted, contains invalid characters, or would leave the root (for example through "..").
        /// </summary>
        public static bool TryCombine(string root, string relative, out string fullPath)
        {
            fullPath = string.Empty;
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relative))
            {
                return false;
            }

            relative = relative.Trim().TrimStart('\\', '/');
            if (relative.Length == 0 || Path.IsPathRooted(relative) || relative.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || relative.Contains('"'))
            {
                return false;
            }

            try
            {
                var rootFull = Normalize(root);
                var combined = Path.GetFullPath(Path.Combine(rootFull + Path.DirectorySeparatorChar, relative));
                if (!combined.StartsWith(rootFull + Path.DirectorySeparatorChar, PathComparison))
                {
                    return false;
                }

                fullPath = combined;
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }

        public static LinkResult Create(string linkPath, string targetPath, LinkKind kind, ConflictPolicy policy)
        {
            try
            {
                var targetIsDirectory = Directory.Exists(targetPath);
                var targetIsFile = !targetIsDirectory && File.Exists(targetPath);
                if (!targetIsDirectory && !targetIsFile)
                {
                    return new LinkResult(LinkOutcome.TargetMissing);
                }

                if ((kind == LinkKind.Hard && !targetIsFile) || (kind == LinkKind.Junction && !targetIsDirectory))
                {
                    return new LinkResult(LinkOutcome.InvalidKind);
                }

                // Never let overwrite/backup touch the target itself or one of its parent folders, and never
                // create a link inside the folder it points to (that would be an endless loop).
                var link = Normalize(linkPath);
                var target = Normalize(targetPath);
                if (string.Equals(link, target, PathComparison)
                    || target.StartsWith(link + Path.DirectorySeparatorChar, PathComparison)
                    || link.StartsWith(target + Path.DirectorySeparatorChar, PathComparison))
                {
                    return new LinkResult(LinkOutcome.PathsOverlap);
                }

                var outcome = LinkOutcome.Created;
                var detail = string.Empty;

                if (TryGetAttributes(linkPath, out var attributes))
                {
                    if (IsSameLink(linkPath, attributes, targetPath))
                    {
                        return new LinkResult(LinkOutcome.SkippedAlreadyLinked);
                    }

                    switch (policy)
                    {
                        case ConflictPolicy.Skip:
                            return new LinkResult(LinkOutcome.SkippedExists);

                        case ConflictPolicy.Backup:
                            detail = MoveToBackup(linkPath, attributes);
                            outcome = LinkOutcome.CreatedAfterBackup;
                            break;

                        case ConflictPolicy.Overwrite:
                            if (!TryDeleteExisting(linkPath, attributes))
                            {
                                return new LinkResult(LinkOutcome.RefusedNonEmptyDirectory);
                            }

                            outcome = LinkOutcome.CreatedAfterOverwrite;
                            break;
                    }
                }

                var parent = Path.GetDirectoryName(linkPath);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                switch (kind)
                {
                    case LinkKind.Symbolic:
                        if (targetIsDirectory)
                        {
                            Directory.CreateSymbolicLink(linkPath, targetPath);
                        }
                        else
                        {
                            File.CreateSymbolicLink(linkPath, targetPath);
                        }

                        break;

                    case LinkKind.Hard:
                        if (!OperatingSystem.IsWindows())
                        {
                            throw new PlatformNotSupportedException("Hard links are only supported on Windows.");
                        }

                        if (!CreateHardLink(linkPath, targetPath, IntPtr.Zero))
                        {
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                        }

                        break;

                    case LinkKind.Junction:
                        CreateJunction(linkPath, targetPath);
                        break;
                }

                return new LinkResult(outcome, detail);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception
                                       or ArgumentException or NotSupportedException or InvalidOperationException)
            {
                // ERROR_PRIVILEGE_NOT_HELD: symbolic links need administrator rights or Windows Developer Mode.
                if ((ex.HResult & 0xFFFF) == 1314)
                {
                    return new LinkResult(LinkOutcome.NeedPrivilege, ex.Message);
                }

                return new LinkResult(LinkOutcome.Failed, ex.Message);
            }
        }

        internal static string Normalize(string path) =>
            Path.GetFullPath(path).TrimEnd('\\', '/');

        /// <summary>File.GetAttributes does not follow links, so this also sees dangling links.</summary>
        internal static bool TryGetAttributes(string path, out FileAttributes attributes)
        {
            try
            {
                attributes = File.GetAttributes(path);
                return true;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                attributes = default;
                return false;
            }
        }

        internal static bool IsSameLink(string linkPath, FileAttributes attributes, string targetPath)
        {
            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                return false;
            }

            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
                ? new DirectoryInfo(linkPath)
                : new FileInfo(linkPath);
            var linkTarget = info.LinkTarget;
            if (string.IsNullOrEmpty(linkTarget))
            {
                return false;
            }

            var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(linkPath)) ?? string.Empty;
            var resolved = Path.GetFullPath(linkTarget, baseDirectory);
            return string.Equals(Normalize(resolved), Normalize(targetPath), PathComparison);
        }

        private static string MoveToBackup(string path, FileAttributes attributes)
        {
            var trimmed = path.TrimEnd('\\', '/');
            var backup = trimmed + ".bak";
            for (var i = 1; TryGetAttributes(backup, out _); i++)
            {
                backup = trimmed + ".bak" + i;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.Move(trimmed, backup);
            }
            else
            {
                File.Move(trimmed, backup);
            }

            return backup;
        }

        /// <summary>
        /// Deletes an existing link, file or empty folder. A non-empty real folder is never deleted
        /// (returns false) so that "overwrite" cannot destroy data; use the backup policy for those.
        /// </summary>
        private static bool TryDeleteExisting(string path, FileAttributes attributes)
        {
            var trimmed = path.TrimEnd('\\', '/');
            var isDirectory = (attributes & FileAttributes.Directory) != 0;

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                // Removes the link only, never the contents it points to.
                if (isDirectory)
                {
                    Directory.Delete(trimmed, false);
                }
                else
                {
                    File.Delete(trimmed);
                }

                return true;
            }

            if (isDirectory)
            {
                if (Directory.EnumerateFileSystemEntries(trimmed).Any())
                {
                    return false;
                }

                Directory.Delete(trimmed, false);
                return true;
            }

            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(trimmed, attributes & ~FileAttributes.ReadOnly);
            }

            File.Delete(trimmed);
            return true;
        }

        private static void CreateJunction(string linkPath, string targetPath)
        {
            // Junctions have no managed API, so mklink is used. cmd expands %VAR% even inside quotes.
            if (linkPath.Contains('"') || targetPath.Contains('"') || linkPath.Contains('%') || targetPath.Contains('%'))
            {
                throw new ArgumentException("Paths for junctions cannot contain quote or percent characters.");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("cmd.exe could not be started.");
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEndAsync();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new IOException((error.Result + output.Result).Trim());
            }
        }
    }
}
