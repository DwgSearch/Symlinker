namespace Symlinker
{
    using System;
    using System.Diagnostics;
    using System.Globalization;
    using System.Windows;
    using System.Windows.Navigation;

    using Res = Symlinker.Properties.Resources;

    /// <summary>The About box: name, version, links, credits and the update check.</summary>
    public partial class AboutWindow : Window
    {
        private readonly AppSettings settings = AppSettings.Load();

        public AboutWindow()
        {
            InitializeComponent();
            SourceInitialized += (sender, e) => TitleBarTheme.Apply(this);

            var version = AppInfo.CurrentVersion;
            versionText.Text = string.Format(
                CultureInfo.CurrentUICulture, Res.AboutVersion, $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}");

            githubLink.NavigateUri = new Uri(AppInfo.GitHubUrl);
            giteeLink.NavigateUri = new Uri(AppInfo.GiteeUrl);

            // A newer version found earlier (by the startup check) is shown right away.
            if (settings.HasUpdate(AppInfo.CurrentVersion) && Uri.TryCreate(settings.LatestKnownUrl, UriKind.Absolute, out var known))
            {
                ShowAvailable(settings.LatestKnownTag, known);
            }
        }

        internal static void OpenPage(Uri uri)
        {
            if (uri.Scheme != Uri.UriSchemeHttps)
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
            }
        }

        private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            OpenPage(e.Uri);
            e.Handled = true;
        }

        private void ShowAvailable(string tag, Uri page)
        {
            var current = AppInfo.CurrentVersion;
            updateStatus.Text = string.Format(
                CultureInfo.CurrentUICulture, Res.UpdateAvailable, tag, $"{current.Major}.{current.Minor}.{Math.Max(current.Build, 0)}");
            downloadLink.NavigateUri = page;
            downloadRow.Visibility = Visibility.Visible;
        }

        private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            checkButton.IsEnabled = false;
            downloadRow.Visibility = Visibility.Collapsed;
            updateStatus.Text = Res.UpdateChecking;
            try
            {
                var result = await UpdateChecker.CheckAsync();
                var current = AppInfo.CurrentVersion;
                var currentText = $"{current.Major}.{current.Minor}.{Math.Max(current.Build, 0)}";
                switch (result.State)
                {
                    case UpdateState.Available:
                        ShowAvailable(result.Latest.Tag, new Uri(result.Latest.Url));
                        break;
                    case UpdateState.UpToDate:
                        updateStatus.Text = string.Format(CultureInfo.CurrentUICulture, Res.UpdateUpToDate, currentText);
                        break;
                    default:
                        updateStatus.Text = Res.UpdateFailed;
                        break;
                }

                settings.Record(result, DateTime.UtcNow);
                settings.Save();
            }
            finally
            {
                checkButton.IsEnabled = true;
            }
        }
    }
}
