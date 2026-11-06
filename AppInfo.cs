namespace Symlinker
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;

    /// <summary>Where the project lives, and which release feeds the update check reads.</summary>
    public static class AppInfo
    {
        public const string RepoName = "Symlinker";
        public const string GitHubOwner = "DwgSearch";
        public const string GiteeOwner = "h1985";

        public static string GitHubUrl => $"https://github.com/{GitHubOwner}/{RepoName}";

        public static string GiteeUrl => $"https://gitee.com/{GiteeOwner}/{RepoName}";

        /// <summary>The running version (from the project's Version property).</summary>
        public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

        /// <summary>Both sites are asked; the newest release found wins.</summary>
        public static IReadOnlyList<ReleaseSource> ReleaseSources { get; } = new[]
        {
            new ReleaseSource("GitHub", $"https://api.github.com/repos/{GitHubOwner}/{RepoName}/releases/latest", $"https://github.com/{GitHubOwner}/{RepoName}/releases"),
            new ReleaseSource("Gitee", $"https://gitee.com/api/v5/repos/{GiteeOwner}/{RepoName}/releases/latest", $"https://gitee.com/{GiteeOwner}/{RepoName}/releases"),
        };
    }
}
