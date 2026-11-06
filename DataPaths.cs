namespace Symlinker
{
    using System;
    using System.IO;

    /// <summary>
    /// Where favorites and settings are stored: a "data" folder next to the program, or, if that folder
    /// cannot be written (for example under C:\Program Files), %AppData%\Symlinker.
    /// </summary>
    public static class DataPaths
    {
        private static readonly Lazy<string> ChosenFolder = new(() => Choose(ProgramFolder, AppDataFolder));

        /// <summary>The "data" folder next to the program.</summary>
        public static string ProgramFolder => Path.Combine(AppContext.BaseDirectory, "data");

        public static string AppDataFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Symlinker");

        /// <summary>The folder files are written to (decided once, when first needed).</summary>
        public static string Folder => ChosenFolder.Value;

        /// <summary>Full path of a data file for writing.</summary>
        public static string FilePath(string fileName) => Path.Combine(Folder, fileName);

        /// <summary>
        /// Full path of a data file for reading. A file kept in %AppData% by an earlier version is found
        /// when the chosen folder does not have one yet; the next save then goes to the chosen folder.
        /// </summary>
        public static string ReadPath(string fileName) => Resolve(fileName, Folder, AppDataFolder);

        /// <summary>Uses <paramref name="preferred"/> if a file can really be created in it, otherwise <paramref name="fallback"/>.</summary>
        public static string Choose(string preferred, string fallback) => IsWritable(preferred) ? preferred : fallback;

        public static string Resolve(string fileName, string chosen, string legacy)
        {
            var path = Path.Combine(chosen, fileName);
            if (File.Exists(path))
            {
                return path;
            }

            var old = Path.Combine(legacy, fileName);
            return File.Exists(old) ? old : path;
        }

        /// <summary>Creates the folder if needed and proves it is writable by creating and deleting a small file.</summary>
        public static bool IsWritable(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                var probe = Path.Combine(folder, ".write-test-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
            {
                return false;
            }
        }
    }
}
