namespace Symlinker
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>A saved ("favorite") set: the destination folders, the real items and the options.</summary>
    public class LinkSet
    {
        public string Name { get; set; } = string.Empty;

        public List<string> Destinations { get; set; } = new();

        public List<string> Sources { get; set; } = new();

        public LinkMode Mode { get; set; } = LinkMode.Symbolic;

        /// <summary>All = every source into every destination; Manual = only <see cref="Pairs"/>.</summary>
        public PairingMode Pairing { get; set; } = PairingMode.All;

        public List<LinkPair> Pairs { get; set; } = new();

        public ConflictPolicy Policy { get; set; } = ConflictPolicy.Skip;
    }

    /// <summary>Reads and writes favorites in sets.json (see <see cref="DataPaths"/> for the folder).</summary>
    public static class LinkSetStore
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        public static string DefaultPath => DataPaths.FilePath("sets.json");

        public static List<LinkSet> Load(string path = null)
        {
            path ??= DataPaths.ReadPath("sets.json");
            if (!File.Exists(path))
            {
                return new List<LinkSet>();
            }

            try
            {
                return JsonSerializer.Deserialize<List<LinkSet>>(File.ReadAllText(path), Options) ?? new List<LinkSet>();
            }
            catch (JsonException)
            {
                // Keep the unreadable file so the next save cannot silently destroy it.
                try
                {
                    File.Copy(path, path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
                }
                catch (IOException)
                {
                }

                return new List<LinkSet>();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new List<LinkSet>();
            }
        }

        public static void Save(IEnumerable<LinkSet> sets, string path = null)
        {
            path ??= DefaultPath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(sets.ToList(), Options));
            File.Move(temp, path, true);
        }
    }
}
