namespace Symlinker
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>One place to read the latest release from.</summary>
    public sealed record ReleaseSource(string Name, string ApiUrl, string ReleasesPage);

    public sealed record UpdateInfo(string Tag, Version Version, string Url, string Source);

    public enum UpdateState
    {
        UpToDate,
        Available,
        Failed,
    }

    public sealed record UpdateResult(UpdateState State, UpdateInfo Latest);

    /// <summary>
    /// Looks up the latest published release on GitHub and Gitee and compares it with the running version.
    /// Only the release metadata is read; nothing is downloaded or installed.
    /// </summary>
    public static class UpdateChecker
    {
        private static readonly Lazy<HttpClient> Http = new(() =>
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Symlinker-update-check");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            return client;
        });

        /// <summary>"v1.2.3", "1.2" or "release-1.2.3" -> 1.2.3. Returns null if no version number is found.</summary>
        public static Version ParseVersion(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                return null;
            }

            var match = Regex.Match(tag, @"(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:\.(\d+))?");
            if (!match.Success)
            {
                return null;
            }

            int Part(int group) => match.Groups[group].Success && int.TryParse(match.Groups[group].Value, out var n) ? n : 0;
            return new Version(Part(1), Part(2), Part(3), Part(4));
        }

        public static bool IsNewer(Version latest, Version current)
        {
            static Version Normalize(Version v) => new(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
            return Normalize(latest) > Normalize(current);
        }

        /// <summary>
        /// Reads one "latest release" JSON document (GitHub and Gitee use the same field names).
        /// Drafts and pre-releases are ignored. The page link is only used if it points at github.com or gitee.com.
        /// </summary>
        public static UpdateInfo ParseRelease(string json, string fallbackUrl, string source)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                if (Flag(root, "draft") || Flag(root, "prerelease"))
                {
                    return null;
                }

                var tag = Text(root, "tag_name");
                var version = ParseVersion(tag);
                if (version == null)
                {
                    return null;
                }

                var url = Text(root, "html_url");
                return new UpdateInfo(tag, version, IsTrustedPage(url) ? url : fallbackUrl, source);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Asks every source and takes the newest release found. <paramref name="fetch"/> returns the JSON text
        /// of a source (or throws); it is a parameter so the logic can be tested without a network.
        /// </summary>
        public static async Task<UpdateResult> CheckAsync(
            IReadOnlyList<ReleaseSource> sources,
            Version current,
            Func<ReleaseSource, CancellationToken, Task<string>> fetch,
            CancellationToken cancellationToken = default)
        {
            async Task<UpdateInfo> One(ReleaseSource source)
            {
                try
                {
                    var json = await fetch(source, cancellationToken).ConfigureAwait(false);
                    return ParseRelease(json, source.ReleasesPage, source.Name);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException)
                {
                    return null;
                }
            }

            var found = (await Task.WhenAll(sources.Select(One)).ConfigureAwait(false))
                .Where(info => info != null)
                .OrderByDescending(info => info.Version)
                .ToList();

            if (found.Count == 0)
            {
                return new UpdateResult(UpdateState.Failed, null);
            }

            var latest = found[0];
            return new UpdateResult(IsNewer(latest.Version, current) ? UpdateState.Available : UpdateState.UpToDate, latest);
        }

        /// <summary>Checks GitHub and Gitee over the network for the running version.</summary>
        public static Task<UpdateResult> CheckAsync(CancellationToken cancellationToken = default) =>
            CheckAsync(AppInfo.ReleaseSources, AppInfo.CurrentVersion, FetchAsync, cancellationToken);

        private static async Task<string> FetchAsync(ReleaseSource source, CancellationToken cancellationToken)
        {
            using var response = await Http.Value.GetAsync(source.ApiUrl, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        private static bool IsTrustedPage(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("gitee.com", StringComparison.OrdinalIgnoreCase));

        private static string Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static bool Flag(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    }
}
