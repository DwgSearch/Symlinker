namespace Symlinker
{
    using System;
    using System.IO;
    using System.Text.Json;

    /// <summary>Small program settings, stored in %AppData%\Symlinker\settings.json.</summary>
    public class AppSettings
    {
        public DateTime LastUpdateCheckUtc { get; set; }

        /// <summary>The newest release found by the last successful check, empty if the program was up to date.</summary>
        public string LatestKnownTag { get; set; } = string.Empty;

        public string LatestKnownUrl { get; set; } = string.Empty;

        /// <summary>The newest version the user was already told about, so the same one is not announced twice.</summary>
        public string LastAnnouncedVersion { get; set; } = string.Empty;

        /// <summary>True when the last successful check found a release newer than <paramref name="current"/>.</summary>
        public bool HasUpdate(Version current) =>
            UpdateChecker.ParseVersion(LatestKnownTag) is { } known && UpdateChecker.IsNewer(known, current);

        /// <summary>The automatic check runs at most once every 24 hours.</summary>
        public bool IsCheckDue(DateTime utcNow) => utcNow - LastUpdateCheckUtc >= TimeSpan.FromHours(24);

        /// <summary>Remembers the outcome of a check. A failed check changes nothing, so it is retried next time.</summary>
        public void Record(UpdateResult result, DateTime utcNow)
        {
            if (result.State == UpdateState.Failed)
            {
                return;
            }

            LastUpdateCheckUtc = utcNow;
            LatestKnownTag = result.State == UpdateState.Available ? result.Latest.Tag : string.Empty;
            LatestKnownUrl = result.State == UpdateState.Available ? result.Latest.Url : string.Empty;
        }

        public static string DefaultPath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Symlinker",
            "settings.json");

        public static AppSettings Load(string path = null)
        {
            path ??= DefaultPath;
            try
            {
                return File.Exists(path)
                    ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings()
                    : new AppSettings();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return new AppSettings();
            }
        }

        public void Save(string path = null)
        {
            path ??= DefaultPath;
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, path, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Settings are a convenience; failing to save them must never stop the program.
            }
        }
    }
}
